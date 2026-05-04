using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NdcHostEmulator.Web.Services;

public sealed class TcpTerminal : IAsyncDisposable
{
    private const int MaxLogEntries = 500;
    private const int PollDelayMs = 100;

    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
    private readonly List<LogEntry> _logBuffer = new(MaxLogEntries);

    private TcpListener? _listener;
    private TcpClient? _currentClient;
    private NetworkStream? _clientStream;
    private CancellationTokenSource? _readCancellationSource;
    private CancellationTokenSource? _listenerCancellationSource;
    private TaskCompletionSource<bool>? _awaitingResponse;

    public int Port { get; }
    public string Name { get; set; }
    public string FilesDirectory { get; set; }
    public bool IsListening { get; private set; }
    public bool IsClientConnected { get; private set; }
    public string CurrentClientInfo { get; private set; } = string.Empty;

    public event Action<LogEntry>? OnLogEntry;
    public event Action? OnConnectionChanged;

    public TcpTerminal(string name, int port, string filesDirectory, ILogger logger)
    {
        Name = name;
        Port = port;
        FilesDirectory = filesDirectory;
        _logger = logger;
    }

    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100)
    {
        lock (_lock)
        {
            var skip = Math.Max(0, _logBuffer.Count - count);
            return _logBuffer.Skip(skip).ToList().AsReadOnly();
        }
    }

    public Task StartAsync()
    {
        if (IsListening) return Task.CompletedTask;

        if (!Directory.Exists(FilesDirectory))
            Directory.CreateDirectory(FilesDirectory);

        _listenerCancellationSource = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();

        IsListening = true;
        AddLog("SYSTEM", $"Server started on port {Port}");
        OnConnectionChanged?.Invoke();

        _ = Task.Run(() => AcceptClientsLoop(_listenerCancellationSource.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsListening) return;

        _listenerCancellationSource?.Cancel();
        CleanupConnection();

        try { _listener?.Stop(); } catch { }

        _listener = null;
        IsListening = false;

        AddLog("SYSTEM", "Server stopped");
        OnConnectionChanged?.Invoke();

        await Task.CompletedTask;
    }

    public async Task DisconnectClient()
    {
        if (!IsClientConnected) return;

        try
        {
            lock (_lock)
            {
                if (_clientStream is { CanWrite: true })
                    _clientStream.Write(Encoding.UTF8.GetBytes("[SERVER] Connection closed\n"));
            }
            await Task.Delay(100);
        }
        catch { }

        CleanupConnection();
        AddLog("SYSTEM", "Client disconnected by server");
        OnConnectionChanged?.Invoke();
    }

    public async Task SendFile(string filePath)
    {
        if (!IsClientConnected)
            throw new InvalidOperationException("No client connected.");

        var cancelToken = _readCancellationSource?.Token ?? CancellationToken.None;
        var fileData = await File.ReadAllTextAsync(filePath);
        var commands = fileData.Split("[FIELD]").Where(x => x.Length > 0).ToArray();

        for (int i = 0; i < commands.Length; i++)
        {
            var byteData = Encoding.UTF8.GetBytes(commands[i]);
            byte[] header = [(byte)(byteData.Length / 256), (byte)(byteData.Length % 256)];

            await _writeSemaphore.WaitAsync();
            try
            {
                if (_clientStream is { CanWrite: true })
                {
                    await _clientStream.WriteAsync(header);
                    await _clientStream.WriteAsync(byteData);
                }
                else throw new InvalidOperationException("Connection lost during file send.");
            }
            finally { _writeSemaphore.Release(); }

            AddLog("OUTGOING", $"Command {i + 1}/{commands.Length} -- {byteData.Length} bytes");
            AddLog("DATA", FormatWithNamedChars(Encoding.UTF8.GetString(byteData)));
            await WaitForResponseAsync($"cmd {i + 1}/{commands.Length}", cancelToken);
        }

        AddLog("OUTGOING", $"File '{Path.GetFileName(filePath)}' sent: {commands.Length} commands");
    }

    public async Task SendContent(string content)
    {
        if (!IsClientConnected)
            throw new InvalidOperationException("No client connected.");

        var cancelToken = _readCancellationSource?.Token ?? CancellationToken.None;
        var commands = content.Split("[FIELD]").Where(x => x.Length > 0).ToArray();

        for (int i = 0; i < commands.Length; i++)
        {
            var byteData = Encoding.UTF8.GetBytes(commands[i]);
            byte[] header = [(byte)(byteData.Length / 256), (byte)(byteData.Length % 256)];

            await _writeSemaphore.WaitAsync();
            try
            {
                if (_clientStream is { CanWrite: true })
                {
                    await _clientStream.WriteAsync(header);
                    await _clientStream.WriteAsync(byteData);
                }
                else throw new InvalidOperationException("Connection lost during send.");
            }
            finally { _writeSemaphore.Release(); }

            AddLog("OUTGOING", $"Command {i + 1}/{commands.Length} -- {byteData.Length} bytes");
            AddLog("DATA", FormatWithNamedChars(Encoding.UTF8.GetString(byteData)));
            await WaitForResponseAsync($"cmd {i + 1}/{commands.Length}", cancelToken);
        }
    }

    public async Task SendText(string text)
    {
        if (!IsClientConnected)
            throw new InvalidOperationException("No client connected.");

        var bytes = Encoding.UTF8.GetBytes(text);
        await _writeSemaphore.WaitAsync();
        try
        {
            if (_clientStream is { CanWrite: true })
                await _clientStream.WriteAsync(bytes);
            else
                throw new InvalidOperationException("Connection lost.");
        }
        finally { _writeSemaphore.Release(); }

        var cancelToken = _readCancellationSource?.Token ?? CancellationToken.None;
        AddLog("OUTGOING", $"Text sent ({bytes.Length} bytes): {FormatWithNamedChars(text)}");
        await WaitForResponseAsync("text", cancelToken);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeSemaphore.Dispose();
    }

    private async Task AcceptClientsLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                CleanupConnection();

                _currentClient = client;
                CurrentClientInfo = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                _clientStream = client.GetStream();
                IsClientConnected = true;

                AddLog("CONNECT", $"Client connected: {CurrentClientInfo}");
                OnConnectionChanged?.Invoke();

                _readCancellationSource = new CancellationTokenSource();
                _ = Task.Run(() => ReadIncomingData(_readCancellationSource.Token), CancellationToken.None);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    AddLog("ERROR", $"Accept error: {ex.Message}");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }
    }

    private async Task ReadIncomingData(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (IsClientConnected && !cancellationToken.IsCancellationRequested)
            {
                NetworkStream? stream;
                TcpClient? client;
                lock (_lock) { stream = _clientStream; client = _currentClient; }

                if (stream != null && stream.DataAvailable)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0) { HandleClientDisconnected(); break; }

                    var messages = ParseMessages(buffer, bytesRead);
                    AddLog("INCOMING", $"Incoming TCP data ({bytesRead} bytes, {messages.Count} msg):");
                    foreach (var msg in messages)
                        AddLog("DATA", FormatWithNamedChars(Encoding.UTF8.GetString(msg)));

                    Interlocked.Exchange(ref _awaitingResponse, null)?.TrySetResult(true);
                }
                else
                {
                    if (client != null && !IsSocketConnected(client)) { HandleClientDisconnected(); break; }
                    await Task.Delay(PollDelayMs, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AddLog("ERROR", $"Read error: {ex.Message}"); HandleClientDisconnected(); }
    }

    private async Task WaitForResponseAsync(string context, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Interlocked.Exchange(ref _awaitingResponse, tcs);
        AddLog("SYSTEM", $"Waiting for response ({context})...");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try { await tcs.Task.WaitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            Interlocked.CompareExchange(ref _awaitingResponse, null, tcs);
            AddLog("SYSTEM", cancellationToken.IsCancellationRequested
                ? $"Connection lost while waiting for response ({context})"
                : $"Timeout waiting for response ({context})");
        }
    }

    private static bool IsSocketConnected(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch { return false; }
    }

    private void HandleClientDisconnected()
    {
        if (!IsClientConnected) return;
        var clientInfo = CurrentClientInfo;
        CleanupConnection();
        AddLog("DISCONNECT", $"Client {clientInfo} disconnected");
        OnConnectionChanged?.Invoke();
    }

    private void CleanupConnection()
    {
        try { _readCancellationSource?.Cancel(); } catch { }
        lock (_lock)
        {
            try { _clientStream?.Close(); _currentClient?.Close(); } catch { }
            finally
            {
                _clientStream = null;
                _currentClient = null;
                IsClientConnected = false;
                CurrentClientInfo = string.Empty;
            }
        }
    }

    private void AddLog(string type, string message)
    {
        var entry = new LogEntry(DateTime.Now, type, message);
        lock (_lock)
        {
            if (_logBuffer.Count >= MaxLogEntries) _logBuffer.RemoveAt(0);
            _logBuffer.Add(entry);
        }
        _logger.LogInformation("[Port {Port}][{Type}] {Message}", Port, type, message);
        try { OnLogEntry?.Invoke(entry); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error invoking OnLogEntry"); }
    }

    private static List<byte[]> ParseMessages(byte[] buffer, int count)
    {
        var result = new List<byte[]>();
        int pos = 0;
        while (pos + 2 <= count)
        {
            int msgLen = buffer[pos] * 256 + buffer[pos + 1];
            pos += 2;
            if (msgLen <= 0 || pos + msgLen > count) break;
            result.Add(buffer[pos..(pos + msgLen)]);
            pos += msgLen;
        }
        return result;
    }

    private static string FormatWithNamedChars(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c < 0x20) sb.Append((char)(0x2400 + c));
            else if (c == 0x7F) sb.Append('␡');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

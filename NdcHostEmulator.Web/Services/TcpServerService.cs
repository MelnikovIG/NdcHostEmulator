using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NdcHostEmulator.Web.Services;

/// <summary>
/// Represents a single log entry produced by the TCP server.
/// </summary>
/// <param name="Timestamp">When the event occurred.</param>
/// <param name="Type">Category of the log entry (CONNECT, DISCONNECT, INCOMING, OUTGOING, ERROR, etc.).</param>
/// <param name="Message">Human-readable description of the event.</param>
public record LogEntry(DateTime Timestamp, string Type, string Message);

/// <summary>
/// Background service that manages a TCP listener, accepts a single client connection,
/// reads incoming data, and provides methods to send data to the connected client.
/// This duplicates the logic from the console application for use in Blazor Server.
/// </summary>
public sealed class TcpServerService : BackgroundService
{
    private const int MaxLogEntries = 500;
    private const int PollDelayMs = 100;

    private readonly IConfiguration _configuration;
    private readonly ILogger<TcpServerService> _logger;
    private readonly object _lock = new();

    private readonly List<LogEntry> _logBuffer = new(MaxLogEntries);

    private TcpListener? _listener;
    private TcpClient? _currentClient;
    private NetworkStream? _clientStream;
    private CancellationTokenSource? _readCancellationSource;
    private CancellationTokenSource? _listenerCancellationSource;

    private string _filesDirectory = "./Files";

    /// <summary>
    /// Indicates whether the TCP listener is currently active and accepting connections.
    /// </summary>
    public bool IsListening { get; private set; }

    /// <summary>
    /// Indicates whether a client is currently connected.
    /// </summary>
    public bool IsClientConnected { get; private set; }

    /// <summary>
    /// Information about the currently connected client (e.g. IP:Port).
    /// </summary>
    public string CurrentClientInfo { get; private set; } = string.Empty;

    /// <summary>
    /// The port the TCP listener is currently bound to.
    /// </summary>
    public int CurrentPort { get; private set; }

    /// <summary>
    /// The directory where NDC scenario files are stored.
    /// </summary>
    public string FilesDirectory => _filesDirectory;

    /// <summary>
    /// Raised when a new log entry is added.
    /// </summary>
    public event Action<LogEntry>? OnLogEntry;

    /// <summary>
    /// Raised when the connection status changes (client connected/disconnected, listener started/stopped).
    /// </summary>
    public event Action? OnConnectionChanged;

    /// <summary>
    /// Initializes a new instance of <see cref="TcpServerService"/>.
    /// </summary>
    /// <param name="configuration">Application configuration for reading TcpServer settings.</param>
    /// <param name="logger">Logger instance.</param>
    public TcpServerService(IConfiguration configuration, ILogger<TcpServerService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Returns the most recent log entries from the ring buffer.
    /// </summary>
    /// <param name="count">Maximum number of entries to return. Defaults to 100.</param>
    /// <returns>A read-only list of recent log entries, ordered oldest to newest.</returns>
    public IReadOnlyList<LogEntry> GetRecentLogs(int count = 100)
    {
        lock (_lock)
        {
            var skip = Math.Max(0, _logBuffer.Count - count);
            return _logBuffer.Skip(skip).ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Starts or restarts the TCP listener on the specified port.
    /// If the listener is already running, it will be stopped first.
    /// </summary>
    /// <param name="port">The port to listen on (1-65535).</param>
    public async Task StartListening(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");

        await StopListening();

        _filesDirectory = _configuration["TcpServer:FilesDirectory"] ?? "./Files";

        if (!Directory.Exists(_filesDirectory))
            Directory.CreateDirectory(_filesDirectory);

        _listenerCancellationSource = new CancellationTokenSource();

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();

        CurrentPort = port;
        IsListening = true;

        SaveLastPort(port);
        AddLog("SYSTEM", $"Server started on port {port}");
        OnConnectionChanged?.Invoke();

        _ = Task.Run(() => AcceptClientsLoop(_listenerCancellationSource.Token));
    }

    /// <summary>
    /// Stops the TCP listener and disconnects any connected client.
    /// </summary>
    public async Task StopListening()
    {
        if (!IsListening)
            return;

        _listenerCancellationSource?.Cancel();
        CleanupConnection();

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Listener may already be disposed
        }

        _listener = null;
        IsListening = false;
        CurrentPort = 0;

        AddLog("SYSTEM", "Server stopped");
        OnConnectionChanged?.Invoke();

        await Task.CompletedTask;
    }

    /// <summary>
    /// Updates the files directory at runtime without restarting the server.
    /// </summary>
    /// <param name="path">The new directory path for NDC scenario files.</param>
    public void SetFilesDirectory(string path)
    {
        _filesDirectory = path;
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        AddLog("SYSTEM", $"Files directory changed to: {path}");
    }

    /// <summary>
    /// Disconnects the currently connected client without stopping the listener.
    /// </summary>
    public async Task DisconnectClient()
    {
        if (!IsClientConnected)
            return;

        try
        {
            lock (_lock)
            {
                if (_clientStream is { CanWrite: true })
                {
                    var msg = Encoding.UTF8.GetBytes("[SERVER] Connection closed\n");
                    _clientStream.Write(msg);
                }
            }

            await Task.Delay(100);
        }
        catch
        {
            // Client may already be gone
        }

        CleanupConnection();
        AddLog("SYSTEM", "Client disconnected by server");
        OnConnectionChanged?.Invoke();
    }

    /// <summary>
    /// Reads a file, splits it by [FIELD] delimiter, and sends each segment
    /// with a 2-byte big-endian length header.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the file to send.</param>
    public async Task SendFile(string filePath)
    {
        if (!IsClientConnected)
            throw new InvalidOperationException("No client connected.");

        var fileData = await File.ReadAllTextAsync(filePath);
        var commands = fileData.Split("[FIELD]").Where(x => x.Length > 0).ToArray();
        var totalSentBytes = 0;

        for (int i = 0; i < commands.Length; i++)
        {
            var byteData = Encoding.UTF8.GetBytes(commands[i]);
            byte[] header = { (byte)(byteData.Length / 256), (byte)(byteData.Length % 256) };

            lock (_lock)
            {
                if (_clientStream is { CanWrite: true })
                {
                    _clientStream.Write(header);
                    _clientStream.Write(byteData);
                    totalSentBytes += byteData.Length;
                }
                else
                {
                    throw new InvalidOperationException("Connection lost during file send.");
                }
            }

            AddLog("OUTGOING", $"Command {i + 1}/{commands.Length} -- {byteData.Length} bytes");
            AddLog("UTF8", Encoding.UTF8.GetString(byteData));
        }

        var fileName = Path.GetFileName(filePath);
        AddLog("OUTGOING", $"File '{fileName}' sent: {commands.Length} commands, {totalSentBytes} bytes total");
    }

    /// <summary>
    /// Sends a UTF-8 encoded text string to the connected client.
    /// </summary>
    /// <param name="text">The text to send.</param>
    public async Task SendText(string text)
    {
        if (!IsClientConnected)
            throw new InvalidOperationException("No client connected.");

        var bytes = Encoding.UTF8.GetBytes(text);

        lock (_lock)
        {
            if (_clientStream is { CanWrite: true })
            {
                _clientStream.Write(bytes);
            }
            else
            {
                throw new InvalidOperationException("Connection lost.");
            }
        }

        AddLog("OUTGOING", $"Text sent ({bytes.Length} bytes): {EscapeControlCharacters(text)}");
        await Task.CompletedTask;
    }

    /// <summary>
    /// Parses a hex string and sends the resulting bytes to the connected client.
    /// </summary>
    /// <param name="hexInput">Hex string, e.g. "48 65 6C 6C 6F". Spaces, dashes, and "0x" prefixes are stripped.</param>
    public async Task SendHex(string hexInput)
    {
        if (!IsClientConnected)
            throw new InvalidOperationException("No client connected.");

        var cleaned = hexInput.Replace(" ", "").Replace("-", "").Replace("0x", "");
        if (cleaned.Length % 2 != 0)
            throw new FormatException("Invalid HEX format: odd number of characters.");

        var bytes = new byte[cleaned.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(cleaned.Substring(i * 2, 2), 16);

        lock (_lock)
        {
            if (_clientStream is { CanWrite: true })
            {
                _clientStream.Write(bytes);
            }
            else
            {
                throw new InvalidOperationException("Connection lost.");
            }
        }

        AddLog("OUTGOING", $"HEX sent ({bytes.Length} bytes): {BitConverter.ToString(bytes).Replace("-", " ")}");
        await Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configPort = _configuration.GetValue<int>("TcpServer:Port", 4070);
        var savedPort = LoadLastPort();
        var port = savedPort != 0 ? savedPort : configPort;

        await StartListening(port);

        // Keep the service alive until shutdown is requested
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        await StopListening();
    }

    private async Task AcceptClientsLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);

                // Disconnect previous client if any
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
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

                lock (_lock)
                {
                    stream = _clientStream;
                    client = _currentClient;
                }

                if (stream != null && stream.DataAvailable)
                {
                    int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                    if (bytesRead == 0)
                    {
                        HandleClientDisconnected();
                        break;
                    }

                    var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    AddLog("INCOMING", $"Incoming data ({bytesRead} bytes):");

                    if (IsPrintableText(data))
                    {
                        AddLog("DATA", $"   {EscapeControlCharacters(data)}");
                    }
                    else
                    {
                        AddLog("HEX", BitConverter.ToString(buffer, 0, bytesRead).Replace("-", " "));
                        AddLog("UTF8", Encoding.UTF8.GetString(buffer, 0, bytesRead));
                    }
                }
                else
                {
                    if (client != null && !IsSocketConnected(client))
                    {
                        HandleClientDisconnected();
                        break;
                    }

                    await Task.Delay(PollDelayMs, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            AddLog("ERROR", $"Read error: {ex.Message}");
            HandleClientDisconnected();
        }
    }

    private static bool IsSocketConnected(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private void HandleClientDisconnected()
    {
        if (!IsClientConnected)
            return;

        var clientInfo = CurrentClientInfo;
        CleanupConnection();

        AddLog("DISCONNECT", $"Client {clientInfo} disconnected");
        OnConnectionChanged?.Invoke();
    }

    private void CleanupConnection()
    {
        try
        {
            _readCancellationSource?.Cancel();
        }
        catch
        {
            // Ignore
        }

        lock (_lock)
        {
            try
            {
                _clientStream?.Close();
                _currentClient?.Close();
            }
            catch
            {
                // Ignore
            }
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
            if (_logBuffer.Count >= MaxLogEntries)
                _logBuffer.RemoveAt(0);

            _logBuffer.Add(entry);
        }

        _logger.LogInformation("[{Type}] {Message}", type, message);

        try
        {
            OnLogEntry?.Invoke(entry);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error invoking OnLogEntry event handler");
        }
    }

    private static int LoadLastPort()
    {
        try
        {
            var statePath = Path.Combine(AppContext.BaseDirectory, "state.json");
            if (!File.Exists(statePath))
                return 0;

            var json = File.ReadAllText(statePath);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("LastPort", out var portProp) &&
                portProp.TryGetInt32(out var port))
                return port;
        }
        catch
        {
            // Ignore
        }

        return 0;
    }

    private void SaveLastPort(int port)
    {
        try
        {
            var statePath = Path.Combine(AppContext.BaseDirectory, "state.json");

            var dict = new Dictionary<string, object>();
            if (File.Exists(statePath))
            {
                var existing = JsonDocument.Parse(File.ReadAllText(statePath));
                dict = existing.RootElement.EnumerateObject()
                    .ToDictionary(p => p.Name, p => (object)p.Value.ToString()!);
            }

            dict["LastPort"] = port;

            File.WriteAllText(statePath,
                JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save last port to state.json");
        }
    }

    private static bool IsPrintableText(string text)
    {
        foreach (char c in text)
        {
            if ((c < 32 && c != '\n' && c != '\r' && c != '\t') || (c > 126 && c < 160))
                return false;
        }

        return true;
    }

    private static string EscapeControlCharacters(string text) =>
        text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("\0", "\\0");
}

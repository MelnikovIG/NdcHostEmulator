using System.Collections.Concurrent;
using System.Text.Json;

namespace NdcHostEmulator.Web.Services;

public sealed class TerminalManager : BackgroundService
{
    private readonly ILogger<TerminalManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<int, TcpTerminal> _terminals = new();

    public IReadOnlyCollection<TcpTerminal> Terminals => _terminals.Values.ToArray();

    public event Action? OnTerminalsChanged;
    public event Action<int, LogEntry>? OnAnyLog;
    public event Action<int>? OnAnyConnectionChanged;

    public TerminalManager(ILogger<TerminalManager> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public TcpTerminal? GetByPort(int port) => _terminals.GetValueOrDefault(port);

    public async Task<TcpTerminal> AddTerminal(string name, int port, string filesDirectory)
    {
        if (_terminals.ContainsKey(port))
            throw new InvalidOperationException($"Terminal on port {port} already exists.");

        var terminal = CreateTerminal(name, port, filesDirectory);

        if (!_terminals.TryAdd(port, terminal))
        {
            await terminal.DisposeAsync();
            throw new InvalidOperationException($"Failed to add terminal on port {port}.");
        }

        await terminal.StartAsync();
        SaveSettings();
        OnTerminalsChanged?.Invoke();
        return terminal;
    }

    public async Task RemoveTerminal(int port)
    {
        if (!_terminals.TryRemove(port, out var terminal)) return;
        await terminal.DisposeAsync();
        SaveSettings();
        OnTerminalsChanged?.Invoke();
    }

    public Task UpdateTerminal(int port, string name, string filesDirectory)
    {
        if (_terminals.TryGetValue(port, out var terminal))
        {
            terminal.Name = name;
            terminal.FilesDirectory = filesDirectory;
            SaveSettings();
            OnTerminalsChanged?.Invoke();
        }
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var cfg in LoadSettings())
        {
            try { await AddTerminal(cfg.Name, cfg.Port, cfg.FilesDirectory); }
            catch (Exception ex) { _logger.LogError(ex, "Failed to start terminal on port {Port}", cfg.Port); }
        }

        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { }

        foreach (var terminal in _terminals.Values)
            await terminal.DisposeAsync();
    }

    private TcpTerminal CreateTerminal(string name, int port, string filesDirectory)
    {
        var terminal = new TcpTerminal(name, port, filesDirectory,
            _loggerFactory.CreateLogger<TcpTerminal>());
        terminal.OnLogEntry += entry => OnAnyLog?.Invoke(port, entry);
        terminal.OnConnectionChanged += () => OnAnyConnectionChanged?.Invoke(port);
        return terminal;
    }

    private record TerminalConfig(string Name, int Port, string FilesDirectory);

    private static string StatePath => Path.Combine(AppContext.BaseDirectory, "state.json");

    private List<TerminalConfig> LoadSettings()
    {
        try
        {
            if (!File.Exists(StatePath))
                return [new("ATM-1", 4070, "./Files")];

            var json = File.ReadAllText(StatePath);
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Terminals", out var arr))
            {
                var list = new List<TerminalConfig>();
                foreach (var t in arr.EnumerateArray())
                {
                    var name = t.TryGetProperty("Name", out var n) ? n.GetString() ?? "Terminal" : "Terminal";
                    var port = t.TryGetProperty("Port", out var p) && p.TryGetInt32(out var pv) ? pv : 4070;
                    var dir  = t.TryGetProperty("FilesDirectory", out var d) ? d.GetString() ?? "./Files" : "./Files";
                    list.Add(new(name, port, dir));
                }
                return list.Count > 0 ? list : [new("ATM-1", 4070, "./Files")];
            }

            // Migrate from old single-terminal format
            var oldPort = 4070;
            var oldDir = "./Files";
            if (doc.RootElement.TryGetProperty("LastPort", out var lp) && lp.TryGetInt32(out var lpv)) oldPort = lpv;
            if (doc.RootElement.TryGetProperty("FilesDirectory", out var ld) && ld.GetString() is { } ldv) oldDir = ldv;
            return [new("ATM-1", oldPort, oldDir)];
        }
        catch { return [new("ATM-1", 4070, "./Files")]; }
    }

    private void SaveSettings()
    {
        try
        {
            var state = new
            {
                Terminals = _terminals.Values
                    .OrderBy(t => t.Port)
                    .Select(t => new { t.Name, t.Port, t.FilesDirectory })
                    .ToArray()
            };
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save settings"); }
    }
}

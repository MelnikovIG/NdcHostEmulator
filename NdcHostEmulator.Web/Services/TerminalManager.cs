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

    private record StateFile(TerminalConfig[] Terminals);

    private static string StatePath => Path.Combine(AppContext.BaseDirectory, "state.json");

    private List<TerminalConfig> LoadSettings()
    {
        try
        {
            if (!File.Exists(StatePath))
                return [new("ATM-1", 4070, "./Files")];

            var json = File.ReadAllText(StatePath);
            var state = JsonSerializer.Deserialize<StateFile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var list = state?.Terminals?.ToList() ?? [];
            return list.Count > 0 ? list : [new("ATM-1", 4070, "./Files")];
        }
        catch { return [new("ATM-1", 4070, "./Files")]; }
    }

    private void SaveSettings()
    {
        try
        {
            var state = new StateFile(
                _terminals.Values
                    .OrderBy(t => t.Port)
                    .Select(t => new TerminalConfig(t.Name, t.Port, t.FilesDirectory))
                    .ToArray());
            File.WriteAllText(StatePath,
                JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to save settings"); }
    }
}

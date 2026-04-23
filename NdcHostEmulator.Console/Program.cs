using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

TcpClient? _currentClient = null;
bool _isRunning = true;
bool _isClientConnected = false;
string _currentClientInfo = string.Empty;
NetworkStream? _clientStream = null;
string _filesDirectory = "./Files";
CancellationTokenSource _readCancellationSource = new();
bool _isReadingIncoming = false;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

_filesDirectory = config["FilesDirectory"] ?? _filesDirectory;
var defaultPort = LoadLastPort();

AnsiConsole.MarkupLine($"[grey]📄 FilesDirectory: [cyan]{_filesDirectory}[/][/]");

ShowAvailableFiles();

var port = AnsiConsole.Prompt(
    new TextPrompt<int>("[green]Введите порт для прослушивания (1-65535):[/]")
        .DefaultValue(defaultPort)
        .Validate(p => p >= 1 && p <= 65535
            ? ValidationResult.Success()
            : ValidationResult.Error("[red]Порт должен быть от 1 до 65535[/]")));

SaveLastPort(port);

var listener = new TcpListener(IPAddress.Any, port);
listener.Start();

AnsiConsole.MarkupLine($"[green]✓ Сервер запущен на порту {port}[/]");

while (_isRunning)
{
    try
    {
        ShowWaitingForConnection();

        _currentClient = await listener.AcceptTcpClientAsync();
        _currentClientInfo = _currentClient.Client.RemoteEndPoint?.ToString() ?? "Unknown";
        _clientStream = _currentClient.GetStream();
        _isClientConnected = true;

        LogMessage("CONNECT", $"✅ Подключен: {_currentClientInfo}", ConsoleColor.Green);

        _readCancellationSource = new CancellationTokenSource();
        _isReadingIncoming = true;
        _ = Task.Run(() => ReadIncomingDataAsync(_readCancellationSource.Token));

        await ShowControlMenuAsync();

        CleanupConnection();
    }
    catch (Exception ex) when (_isRunning)
    {
        LogMessage("ERROR", $"❌ Ошибка сервера: {ex.Message}", ConsoleColor.Red);
        CleanupConnection();
    }
}

listener.Stop();
AnsiConsole.MarkupLine("[yellow]Сервер остановлен.[/]");

int LoadLastPort()
{
    try
    {
        var statePath = Path.Combine(AppContext.BaseDirectory, "state.json");
        if (!File.Exists(statePath)) return 4070;

        var json = File.ReadAllText(statePath);
        var doc = System.Text.Json.JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("LastPort", out var portProp) &&
            portProp.TryGetInt32(out var port))
            return port;
    }
    catch { /* ignore */ }

    return 4070;
}

void SaveLastPort(int port)
{
    try
    {
        var statePath = Path.Combine(AppContext.BaseDirectory, "state.json");

        var dict = new Dictionary<string, object>();
        if (File.Exists(statePath))
        {
            var existing = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath));
            dict = existing.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => (object)p.Value.ToString()!);
        }

        dict["LastPort"] = port;

        File.WriteAllText(statePath,
            System.Text.Json.JsonSerializer.Serialize(dict,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex)
    {
        LogMessage("SYSTEM", $"⚠️ Не удалось сохранить порт: {ex.Message}", ConsoleColor.DarkYellow);
    }
}

void ShowWaitingForConnection()
{
    AnsiConsole.WriteLine();
    var rule = new Rule("[yellow]⏳ Ожидание подключения...[/]") { Style = Style.Parse("yellow") };
    AnsiConsole.Write(rule);
    AnsiConsole.WriteLine();
}

async Task HandleClientDisconnectedAsync()
{
    if (!_isClientConnected) return;

    _isClientConnected = false;
    _readCancellationSource.Cancel();
    LogMessage("DISCONNECT", $"🔌 Клиент {_currentClientInfo} отключился", ConsoleColor.Yellow);
}

void CleanupConnection()
{
    try
    {
        _readCancellationSource.Cancel();
        _clientStream?.Close();
        _currentClient?.Close();
    }
    catch { }
    finally
    {
        _currentClient = null;
        _clientStream = null;
        _isReadingIncoming = false;
        _isClientConnected = false;
    }
}

async Task ReadIncomingDataAsync(CancellationToken cancellationToken)
{
    var buffer = new byte[4096];

    try
    {
        while (_isClientConnected && !cancellationToken.IsCancellationRequested)
        {
            if (_clientStream != null && _clientStream.DataAvailable)
            {
                int bytesRead = await _clientStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);

                if (bytesRead == 0)
                {
                    await HandleClientDisconnectedAsync();
                    break;
                }

                var messages = ParseMessages(buffer, bytesRead);
                LogMessage("INCOMING", $"📥 Входящие TCP данные ({bytesRead} байт, {messages.Count} сообщ.):", ConsoleColor.Blue);
                foreach (var msg in messages)
                {
                    var msgText = Encoding.UTF8.GetString(msg);
                    LogMessage("DATA", $"{EscapeControlCharacters(msgText)}", ConsoleColor.Blue);
                }
            }
            else
            {
                if (_currentClient != null && !IsSocketConnected(_currentClient))
                {
                    await HandleClientDisconnectedAsync();
                    break;
                }

                await Task.Delay(100, cancellationToken);
            }
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        LogMessage("ERROR", $"❌ Ошибка чтения: {ex.Message}", ConsoleColor.Red);
        await HandleClientDisconnectedAsync();
    }
    finally
    {
        _isReadingIncoming = false;
    }
}

bool IsSocketConnected(TcpClient client)
{
    try
    {
        var socket = client.Client;
        return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
    }
    catch { return false; }
}

async Task ShowControlMenuAsync()
{
    while (_isRunning && _isClientConnected)
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Выберите действие:[/]")
                .PageSize(12)
                .AddChoices(
                    "📁 Выбрать и отправить файл",
                    "📤 Отправить произвольный текст",
                    "🔄 Обновить список файлов",
                    "📊 Статус соединения",
                    "⏸️ Пауза логгирования",
                    "▶️ Возобновить логгирование",
                    "📝 Показать логи",
                    "🧹 Очистить логи",
                    "❌ Отключить клиента",
                    "🚪 Выйти из программы"));

        if (!_isClientConnected)
        {
            LogMessage("SYSTEM", "⚠️ Соединение было разорвано клиентом", ConsoleColor.Yellow);
            return;
        }

        switch (choice)
        {
            case "📁 Выбрать и отправить файл":     await SendFileAsync(); break;
            case "📤 Отправить произвольный текст":  await SendCustomTextAsync(); break;
            case "🔄 Обновить список файлов":        ShowAvailableFiles(); break;
            case "📊 Статус соединения":             ShowConnectionStatus(); break;
            case "⏸️ Пауза логгирования":           PauseLogging(); break;
            case "▶️ Возобновить логгирование":     ResumeLogging(); break;
            case "📝 Показать логи":                ShowLogs(); break;
            case "🧹 Очистить логи":                ClearLogs(); break;
            case "❌ Отключить клиента":
                await DisconnectClientAsync();
                return;
            case "🚪 Выйти из программы":
                _isRunning = false;
                return;
        }
    }
}

async Task SendMessageAsync(byte[] data)
{
    if (_clientStream is not { CanWrite: true })
        throw new InvalidOperationException("Соединение разорвано");

    byte[] header = [(byte)(data.Length / 256), (byte)(data.Length % 256)];
    await _clientStream.WriteAsync(header);
    await _clientStream.WriteAsync(data);

    LogMessage("OUTGOING", $"📤 {data.Length} байт: {EscapeControlCharacters(Encoding.UTF8.GetString(data))}", ConsoleColor.Green);
}

async Task SendCommandsAsync(string text)
{
    var commands = text.Split("[FIELD]", StringSplitOptions.RemoveEmptyEntries);
    for (int i = 0; i < commands.Length; i++)
    {
        LogMessage("OUTGOING", $"📤 Команда {i + 1}/{commands.Length}", ConsoleColor.Green);
        await SendMessageAsync(Encoding.UTF8.GetBytes(UnescapeControlCharacters(commands[i])));
    }
}

async Task SendFileAsync()
{
    try
    {
        var files = GetAvailableFiles();
        if (files.Count == 0)
        {
            LogMessage("ERROR", $"❌ В папке '{_filesDirectory}' нет файлов", ConsoleColor.Red);
            return;
        }

        var fileChoices = files
            .Select(f => $"{f.Name} <{GetFileType(f.Extension.ToLower())}, {f.Length:N0} байт>")
            .Append("❌ Отмена")
            .ToList();

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[green]Выберите файл для отправки:[/]")
                .PageSize(15)
                .AddChoices(fileChoices));

        if (selected == "❌ Отмена") return;

        var selectedFile = files[fileChoices.IndexOf(selected)];
        await SendCommandsAsync(await File.ReadAllTextAsync(selectedFile.FullName));
    }
    catch (Exception ex)
    {
        LogMessage("ERROR", $"❌ Ошибка отправки файла: {ex.Message}", ConsoleColor.Red);
    }
}

async Task SendCustomTextAsync()
{
    try
    {
        var text = AnsiConsole.Prompt(new TextPrompt<string>("[green]Введите текст:[/]").AllowEmpty());
        if (string.IsNullOrEmpty(text)) return;
        await SendCommandsAsync(text);
    }
    catch (Exception ex) { LogMessage("ERROR", $"❌ {ex.Message}", ConsoleColor.Red); }
}

void ShowConnectionStatus()
{
    AnsiConsole.Write(new Panel($"""
        Клиент:       {_currentClientInfo}
        Статус:       {(_isClientConnected ? "[green]Подключен[/]" : "[red]Отключен[/]")}
        Логгирование: {(_isReadingIncoming ? "[green]Активно[/]" : "[red]Остановлено[/]")}
        Поток:        {(_clientStream?.CanWrite == true ? "[green]Доступен[/]" : "[red]Недоступен[/]")}
        Время:        {DateTime.Now:HH:mm:ss}
        """)
    {
        Header = new PanelHeader("[blue]Статус соединения[/]"),
        Border = BoxBorder.Rounded,
        Padding = new Padding(1, 1, 1, 1)
    });
}

void PauseLogging()
{
    if (_isReadingIncoming)
    {
        _readCancellationSource.Cancel();
        _isReadingIncoming = false;
        LogMessage("SYSTEM", "⏸️ Логгирование приостановлено", ConsoleColor.Yellow);
    }
    else LogMessage("SYSTEM", "Логгирование уже приостановлено", ConsoleColor.Gray);
}

void ResumeLogging()
{
    if (!_isReadingIncoming && _isClientConnected)
    {
        _readCancellationSource = new CancellationTokenSource();
        _isReadingIncoming = true;
        _ = Task.Run(() => ReadIncomingDataAsync(_readCancellationSource.Token));
        LogMessage("SYSTEM", "▶️ Логгирование возобновлено", ConsoleColor.Green);
    }
    else LogMessage("SYSTEM", "Логгирование уже активно", ConsoleColor.Gray);
}

void ShowLogs()
{
    try
    {
        if (!File.Exists("tcp_sender.log")) { LogMessage("SYSTEM", "Файл логов не найден", ConsoleColor.Gray); return; }

        var logs = File.ReadAllLines("tcp_sender.log");
        AnsiConsole.WriteLine("Последние 20 логов");
        foreach (var log in logs.TakeLast(20))
        {
            AnsiConsole.WriteLine(log);
        }
    }
    catch (Exception ex) { LogMessage("ERROR", $"❌ {ex.Message}", ConsoleColor.Red); }
}

void ClearLogs()
{
    if (AnsiConsole.Confirm("[red]Очистить все логи?[/]", false))
    {
        try
        {
            if (File.Exists("tcp_sender.log"))
            {
                File.Delete("tcp_sender.log");
                LogMessage("SYSTEM", "🧹 Логи очищены", ConsoleColor.Green);
            }
        }
        catch (Exception ex) { LogMessage("ERROR", $"❌ {ex.Message}", ConsoleColor.Red); }
    }
}

async Task DisconnectClientAsync()
{
    try
    {
        if (_currentClient is { Connected: true } && _clientStream != null)
        {
            _readCancellationSource.Cancel();
            var msg = Encoding.UTF8.GetBytes("[SERVER] Соединение закрыто\n");
            await _clientStream.WriteAsync(msg);
            await Task.Delay(100);
            _clientStream.Close();
            _currentClient.Close();
            LogMessage("SYSTEM", "🔌 Соединение закрыто сервером", ConsoleColor.Yellow);
        }
    }
    catch (Exception ex) { LogMessage("ERROR", $"❌ {ex.Message}", ConsoleColor.Red); }
    finally { _isClientConnected = false; }
}

void ShowAvailableFiles()
{
    if (!Directory.Exists(_filesDirectory))
    {
        Directory.CreateDirectory(_filesDirectory);
        LogMessage("SYSTEM", $"📁 Папка '{_filesDirectory}' создана", ConsoleColor.Blue);
    }

    var files = GetAvailableFiles();
    if (files.Count == 0)
    {
        AnsiConsole.MarkupLine($"[yellow]В папке '{_filesDirectory}' нет файлов[/]");
        AnsiConsole.MarkupLine($"[grey]Путь: {Path.GetFullPath(_filesDirectory)}[/]");
        return;
    }

    var table = new Table();
    table.Border(TableBorder.Rounded);
    table.Title($"[blue]📁 Файлы в '{_filesDirectory}'[/]");
    table.AddColumn(new TableColumn("[green]#[/]").Centered());
    table.AddColumn("[green]Имя файла[/]");
    table.AddColumn(new TableColumn("[green]Размер[/]").RightAligned());
    table.AddColumn("[green]Тип[/]");
    table.AddColumn("[green]Изменён[/]");

    for (int i = 0; i < files.Count; i++)
    {
        var f = files[i];
        var type = GetFileType(f.Extension.ToLower());
        table.AddRow(
            $"[yellow]{i + 1}[/]", f.Name,
            $"[cyan]{f.Length:N0}[/] байт",
            $"[{GetFileTypeColor(type)}]{type}[/]",
            $"[grey]{f.LastWriteTime:dd.MM.yy HH:mm}[/]");
    }

    AnsiConsole.Write(table);
    LogMessage("SYSTEM", $"📊 Всего файлов: {files.Count}", ConsoleColor.Blue);
}

List<FileInfo> GetAvailableFiles() =>
    Directory.Exists(_filesDirectory)
        ? Directory.GetFiles(_filesDirectory).Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList()
        : new List<FileInfo>();

string GetFileType(string ext) => ext switch
{
    ".txt" => "Текст", ".json" => "JSON", ".xml" => "XML", ".csv" => "CSV",
    ".bin" => "Бинарный", ".dat" => "Данные", ".log" => "Лог",
    ".cfg" or ".config" => "Конфиг",
    ".jpg" or ".jpeg" or ".png" or ".gif" => "Изображение",
    ".pdf" => "PDF", ".zip" or ".rar" => "Архив",
    ".exe" => "Программа", ".dll" => "Библиотека",
    _ => "Другой"
};

string GetFileTypeColor(string type) => type switch
{
    "Текст" => "green", "JSON" => "yellow", "XML" => "orange1", "CSV" => "cyan",
    "Бинарный" => "magenta", "Данные" => "blue", "Лог" => "grey", "Конфиг" => "purple",
    "Изображение" => "springgreen1", "PDF" => "red", "Архив" => "darkorange",
    "Программа" => "red1", "Библиотека" => "darkcyan", _ => "white"
};

void LogMessage(string type, string message, ConsoleColor color)
{
    var ts = DateTime.Now.ToString("HH:mm:ss.fff");
    var colorName = color.ToString().ToLower();

    try
    {
        AnsiConsole.MarkupLine(
            $"[grey]{Markup.Escape($"[{ts}] ")}[/][{colorName}]{Markup.Escape($"[{type}] ")}[/] {Markup.Escape(message)}");
    }
    catch (Exception e)
    {
        System.Console.WriteLine(e);
        throw;
    }

    try
    {
        File.AppendAllText("tcp_sender.log", $"[{ts}] [{type}] {message}{Environment.NewLine}", Encoding.UTF8);
    }
    catch { }
}

bool IsPrintableText(string text)
{
    foreach (char c in text)
        if ((c < 32 && c != '\n' && c != '\r' && c != '\t') || (c > 126 && c < 160))
            return false;
    return true;
}

List<byte[]> ParseMessages(byte[] buffer, int count)
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

string EscapeControlCharacters(string text)
{
    var sb = new System.Text.StringBuilder(text.Length);
    foreach (char c in text)
    {
        if (c <= '\x1F')
            sb.Append((char)(0x2400 + c));
        else if (c == '\x7F')
            sb.Append('␡');
        else if (c > '\x7F')
            sb.Append($"\\x{(int)c:X2}");
        else
            sb.Append(c);
    }
    return sb.ToString();
}

string UnescapeControlCharacters(string text)
{
    var sb = new System.Text.StringBuilder(text.Length);
    foreach (char c in text)
    {
        if (c >= '␀' && c <= '␟')
            sb.Append((char)(c - 0x2400));
        else if (c == '␡')
            sb.Append('\x7F');
        else
            sb.Append(c);
    }
    return sb.ToString();
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace NdcHostEmulator;

class Program
{
    private static TcpClient? _currentClient;
    private static bool _isRunning = true;
    private static bool _isClientConnected = false;
    private static string _currentClientInfo = string.Empty;
    private static NetworkStream? _clientStream;
    private static string _filesDirectory = "./Files";
    private static CancellationTokenSource _readCancellationSource = new();
    private static bool _isReadingIncoming = false;

    static async Task Main(string[] args)
    {
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

                LogMessage($"✅ Подключен: {_currentClientInfo}", "CONNECT", ConsoleColor.Green);

                _readCancellationSource = new CancellationTokenSource();
                _isReadingIncoming = true;
                _ = Task.Run(() => ReadIncomingDataAsync(_readCancellationSource.Token));

                await ShowControlMenuAsync();

                CleanupConnection();
            }
            catch (Exception ex) when (_isRunning)
            {
                LogMessage($"❌ Ошибка сервера: {ex.Message}", "ERROR", ConsoleColor.Red);
                CleanupConnection();
            }
        }

        listener.Stop();
        AnsiConsole.MarkupLine("[yellow]Сервер остановлен.[/]");
    }

    static int LoadLastPort()
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

    static void SaveLastPort(int port)
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
            LogMessage($"⚠️ Не удалось сохранить порт: {ex.Message}", "SYSTEM", ConsoleColor.DarkYellow);
        }
    }

    static void ShowWaitingForConnection()
    {
        AnsiConsole.WriteLine();
        var rule = new Rule("[yellow]⏳ Ожидание подключения...[/]") { Style = Style.Parse("yellow") };
        AnsiConsole.Write(rule);
        AnsiConsole.WriteLine();
    }

    static async Task HandleClientDisconnectedAsync()
    {
        if (!_isClientConnected) return;

        _isClientConnected = false;
        _readCancellationSource.Cancel();
        LogMessage($"🔌 Клиент {_currentClientInfo} отключился", "DISCONNECT", ConsoleColor.Yellow);
    }

    static void CleanupConnection()
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

    static async Task ReadIncomingDataAsync(CancellationToken cancellationToken)
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

                    var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    LogMessage($"📥 Входящие данные ({bytesRead} байт):", "INCOMING", ConsoleColor.Yellow);

                    if (IsPrintableText(data))
                        LogMessage($"   {EscapeControlCharacters(data)}", "DATA", ConsoleColor.Gray);
                    else
                    {
                        LogMessage($"   Бинарные данные: {bytesRead} байт", "DATA", ConsoleColor.Gray);
                        LogMessage($"   Hex: {BitConverter.ToString(buffer, 0, bytesRead).Replace("-", " ")}", "HEX", ConsoleColor.DarkCyan);
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
            LogMessage($"❌ Ошибка чтения: {ex.Message}", "ERROR", ConsoleColor.Red);
            await HandleClientDisconnectedAsync();
        }
        finally
        {
            _isReadingIncoming = false;
        }
    }

    static bool IsSocketConnected(TcpClient client)
    {
        try
        {
            var socket = client.Client;
            return !(socket.Poll(1, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch { return false; }
    }

    static async Task ShowControlMenuAsync()
    {
        while (_isRunning && _isClientConnected)
        {
            AnsiConsole.WriteLine();
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Выберите действие:[/]")
                    .PageSize(12)
                    .AddChoices(
                        "📁 Выбрать и отправить файл",
                        "📤 Отправить произвольный текст",
                        "📤 Отправить HEX данные",
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
                LogMessage("⚠️ Соединение было разорвано клиентом", "SYSTEM", ConsoleColor.Yellow);
                return;
            }

            switch (choice)
            {
                case "📁 Выбрать и отправить файл":     await SendFileAsync(); break;
                case "📤 Отправить произвольный текст":  await SendCustomTextAsync(); break;
                case "📤 Отправить HEX данные":          await SendHexDataAsync(); break;
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

    static async Task SendFileAsync()
    {
        try
        {
            var files = GetAvailableFiles();
            if (files.Count == 0)
            {
                LogMessage($"❌ В папке '{_filesDirectory}' нет файлов", "ERROR", ConsoleColor.Red);
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

            AnsiConsole.Write(new Panel($"""
                [bold]Имя файла:[/] {selectedFile.Name}
                [bold]Размер:[/]    {selectedFile.Length:N0} байт
                [bold]Тип:[/]       {GetFileType(selectedFile.Extension.ToLower())}
                [bold]Изменён:[/]   {selectedFile.LastWriteTime:dd.MM.yyyy HH:mm:ss}
                """)
            {
                Header = new PanelHeader("[blue]Информация о файле[/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            });

            var fileData = await File.ReadAllTextAsync(selectedFile.FullName);
            var commands = fileData.Split("[FIELD]").Where(x => x.Length > 0).ToArray();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var totalSentBytes = 0;

            AnsiConsole.Status().Start("Отправка файла...", ctx =>
            {
                ctx.Spinner(Spinner.Known.Dots);
                ctx.SpinnerStyle(Style.Parse("green"));

                for (int i = 0; i < commands.Length; i++)
                {
                    var byteData = Encoding.UTF8.GetBytes(commands[i]);
                    byte[] header = { (byte)(byteData.Length / 256), (byte)(byteData.Length % 256) };

                    if (_clientStream is { CanWrite: true })
                    {
                        _clientStream.Write(header);
                        _clientStream.Write(byteData);
                        totalSentBytes += byteData.Length;

                        LogMessage($"📤 Команда {i + 1}/{commands.Length} — {byteData.Length} байт", "OUTGOING", ConsoleColor.Green);
                        var speed = stopwatch.Elapsed.TotalSeconds > 0 ? totalSentBytes / stopwatch.Elapsed.TotalSeconds : 0;
                        ctx.Status($"Отправлено: {totalSentBytes:N0} байт ({speed:N0} байт/сек)");
                    }
                    else throw new Exception("Соединение разорвано");
                }
            });

            stopwatch.Stop();
            var elapsed = stopwatch.Elapsed.TotalSeconds;
            var finalSpeed = elapsed > 0 ? totalSentBytes / elapsed : 0;

            AnsiConsole.Write(new Panel($"""
                [green]✓ Файл успешно отправлен![/]

                [bold]Имя файла:[/] {selectedFile.Name}
                [bold]Размер:[/]    {totalSentBytes:N0} байт
                [bold]Время:[/]     {elapsed:F2} сек
                [bold]Скорость:[/]  {finalSpeed:N0} байт/сек
                [bold]Клиент:[/]    {_currentClientInfo}
                """)
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(1, 1, 1, 1)
            });
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Ошибка отправки файла: {ex.Message}", "ERROR", ConsoleColor.Red);
        }
    }

    static async Task SendCustomTextAsync()
    {
        try
        {
            var text = AnsiConsole.Prompt(new TextPrompt<string>("[green]Введите текст:[/]").AllowEmpty());
            if (string.IsNullOrEmpty(text)) return;

            var bytes = Encoding.UTF8.GetBytes(text);
            if (_clientStream is { CanWrite: true })
            {
                await _clientStream.WriteAsync(bytes);
                LogMessage($"📤 Отправлен текст ({bytes.Length} байт): {EscapeControlCharacters(text)}", "OUTGOING", ConsoleColor.Green);
            }
        }
        catch (Exception ex) { LogMessage($"❌ {ex.Message}", "ERROR", ConsoleColor.Red); }
    }

    static async Task SendHexDataAsync()
    {
        try
        {
            var hexInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Введите HEX (например: 48 65 6C 6C 6F):[/]").AllowEmpty());
            if (string.IsNullOrEmpty(hexInput)) return;

            hexInput = hexInput.Replace(" ", "").Replace("-", "").Replace("0x", "");
            if (hexInput.Length % 2 != 0) { LogMessage("❌ Неверный формат HEX", "ERROR", ConsoleColor.Red); return; }

            var bytes = new byte[hexInput.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hexInput.Substring(i * 2, 2), 16);

            if (_clientStream is { CanWrite: true })
            {
                await _clientStream.WriteAsync(bytes);
                LogMessage($"📤 HEX отправлен ({bytes.Length} байт): {BitConverter.ToString(bytes).Replace("-", " ")}", "OUTGOING", ConsoleColor.Green);
            }
        }
        catch (Exception ex) { LogMessage($"❌ {ex.Message}", "ERROR", ConsoleColor.Red); }
    }

    static void ShowConnectionStatus()
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

    static void PauseLogging()
    {
        if (_isReadingIncoming)
        {
            _readCancellationSource.Cancel();
            _isReadingIncoming = false;
            LogMessage("⏸️ Логгирование приостановлено", "SYSTEM", ConsoleColor.Yellow);
        }
        else LogMessage("Логгирование уже приостановлено", "SYSTEM", ConsoleColor.Gray);
    }

    static void ResumeLogging()
    {
        if (!_isReadingIncoming && _isClientConnected)
        {
            _readCancellationSource = new CancellationTokenSource();
            _isReadingIncoming = true;
            _ = Task.Run(() => ReadIncomingDataAsync(_readCancellationSource.Token));
            LogMessage("▶️ Логгирование возобновлено", "SYSTEM", ConsoleColor.Green);
        }
        else LogMessage("Логгирование уже активно", "SYSTEM", ConsoleColor.Gray);
    }

    static void ShowLogs()
    {
        try
        {
            if (!File.Exists("tcp_sender.log")) { LogMessage("Файл логов не найден", "SYSTEM", ConsoleColor.Gray); return; }

            var logs = File.ReadAllLines("tcp_sender.log");
            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.Title("[blue]📋 Последние 20 логов[/]");
            table.AddColumn("Время"); table.AddColumn("Тип"); table.AddColumn("Сообщение");

            foreach (var line in logs.TakeLast(20))
            {
                var parts = line.Split(']', 3);
                if (parts.Length >= 3)
                    table.AddRow(
                        $"[grey]{parts[0].TrimStart('[')}[/]",
                        $"[cyan]{parts[1].TrimStart('[').Trim()}[/]",
                        parts[2].Trim());
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex) { LogMessage($"❌ {ex.Message}", "ERROR", ConsoleColor.Red); }
    }

    static void ClearLogs()
    {
        if (AnsiConsole.Confirm("[red]Очистить все логи?[/]", false))
        {
            try
            {
                if (File.Exists("tcp_sender.log"))
                {
                    File.Delete("tcp_sender.log");
                    LogMessage("🧹 Логи очищены", "SYSTEM", ConsoleColor.Green);
                }
            }
            catch (Exception ex) { LogMessage($"❌ {ex.Message}", "ERROR", ConsoleColor.Red); }
        }
    }

    static async Task DisconnectClientAsync()
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
                LogMessage("🔌 Соединение закрыто сервером", "SYSTEM", ConsoleColor.Yellow);
            }
        }
        catch (Exception ex) { LogMessage($"❌ {ex.Message}", "ERROR", ConsoleColor.Red); }
        finally { _isClientConnected = false; }
    }

    static void ShowAvailableFiles()
    {
        if (!Directory.Exists(_filesDirectory))
        {
            Directory.CreateDirectory(_filesDirectory);
            LogMessage($"📁 Папка '{_filesDirectory}' создана", "SYSTEM", ConsoleColor.Blue);
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
        LogMessage($"📊 Всего файлов: {files.Count}", "SYSTEM", ConsoleColor.Blue);
    }

    static List<FileInfo> GetAvailableFiles() =>
        Directory.Exists(_filesDirectory)
            ? Directory.GetFiles(_filesDirectory).Select(f => new FileInfo(f)).OrderBy(f => f.Name).ToList()
            : new List<FileInfo>();

    static string GetFileType(string ext) => ext switch
    {
        ".txt" => "Текст", ".json" => "JSON", ".xml" => "XML", ".csv" => "CSV",
        ".bin" => "Бинарный", ".dat" => "Данные", ".log" => "Лог",
        ".cfg" or ".config" => "Конфиг",
        ".jpg" or ".jpeg" or ".png" or ".gif" => "Изображение",
        ".pdf" => "PDF", ".zip" or ".rar" => "Архив",
        ".exe" => "Программа", ".dll" => "Библиотека",
        _ => "Другой"
    };

    static string GetFileTypeColor(string type) => type switch
    {
        "Текст" => "green", "JSON" => "yellow", "XML" => "orange1", "CSV" => "cyan",
        "Бинарный" => "magenta", "Данные" => "blue", "Лог" => "grey", "Конфиг" => "purple",
        "Изображение" => "springgreen1", "PDF" => "red", "Архив" => "darkorange",
        "Программа" => "red1", "Библиотека" => "darkcyan", _ => "white"
    };

    static void LogMessage(string message, string type, ConsoleColor color)
    {
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var colorName = color.ToString().ToLower();
        AnsiConsole.MarkupLine(
            $"[grey]{Markup.Escape($"[{ts}] ")}[/][{colorName}]{Markup.Escape($"[{type}] ")}[/] {message}");

        try { File.AppendAllText("tcp_sender.log", $"[{ts}] [{type}] {message}{Environment.NewLine}", Encoding.UTF8); }
        catch { }
    }

    static bool IsPrintableText(string text)
    {
        foreach (char c in text)
            if ((c < 32 && c != '\n' && c != '\r' && c != '\t') || (c > 126 && c < 160))
                return false;
        return true;
    }

    static string EscapeControlCharacters(string text) =>
        text.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t").Replace("\0", "\\0");
}
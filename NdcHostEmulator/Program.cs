using System.Net;
using System.Net.Sockets;
using System.Text;
using Spectre.Console;

namespace NdcHostEmulator;

class Program
{
    private static TcpClient _currentClient;
    private static bool _isRunning = true;
    private static string _currentClientInfo = string.Empty;
    private static NetworkStream _clientStream;
    private static string _filesDirectory = "./Files";
    private static StreamReader _clientReader;
    private static CancellationTokenSource _readCancellationSource = new();
    private static bool _isReadingIncoming = false;

    static async Task Main(string[] args)
    {
        AnsiConsole.Clear();
        AnsiConsole.Write(new FigletText("TCP File Logger").Color(Color.Blue));
        AnsiConsole.Write(new Rule("[yellow]Логирование входящих/исходящих данных[/]").LeftJustified());

        // Показываем список файлов в директории
        ShowAvailableFiles();

        // Выбор порта
        var port = AnsiConsole.Prompt(
            new TextPrompt<int>("[green]Введите порт для прослушивания (1-65535):[/]")
                .DefaultValue(4070)
                .Validate(p => p >= 1 && p <= 65535 ? ValidationResult.Success() 
                    : ValidationResult.Error("[red]Порт должен быть от 1 до 65535[/]")));

        // Запуск сервера
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        AnsiConsole.MarkupLine($"[green]✓ Сервер запущен на порту {port}[/]");
        AnsiConsole.MarkupLine("[yellow]Ожидание подключения...[/]");

        // Основной цикл сервера
        while (_isRunning)
        {
            try
            {
                // Принимаем только одно соединение
                if (_currentClient == null || !_currentClient.Connected)
                {
                    _currentClient = await listener.AcceptTcpClientAsync();
                    _currentClientInfo = _currentClient.Client.RemoteEndPoint?.ToString() ?? "Unknown";
                    _clientStream = _currentClient.GetStream();
                    _clientReader = new StreamReader(_clientStream, Encoding.UTF8);
                        
                    LogMessage($"✅ Подключен: {_currentClientInfo}", "CONNECT", ConsoleColor.Green);
                        
                    // Запускаем чтение входящих данных
                    _readCancellationSource = new CancellationTokenSource();
                    _isReadingIncoming = true;
                    _ = Task.Run(() => ReadIncomingDataAsync(_readCancellationSource.Token));
                        
                    // Показываем меню управления
                    _ = Task.Run(() => ShowControlMenuAsync());
                }
                else
                {
                    // Если клиент уже подключен, ждем
                    await Task.Delay(1000);
                }
            }
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка сервера: {ex.Message}", "ERROR", ConsoleColor.Red);
            }
        }

        listener.Stop();
    }

    static async Task ReadIncomingDataAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
            
        try
        {
            while (_currentClient != null && 
                   _currentClient.Connected && 
                   !cancellationToken.IsCancellationRequested)
            {
                // Проверяем, есть ли данные для чтения
                if (_clientStream != null && _clientStream.DataAvailable)
                {
                    int bytesRead = await _clientStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                        
                    if (bytesRead > 0)
                    {
                        var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        LogMessage($"📥 Входящие данные ({bytesRead} байт):", "INCOMING", ConsoleColor.Yellow);
                            
                        // Выводим данные в удобном формате
                        if (IsPrintableText(data))
                        {
                            LogMessage($"   {EscapeControlCharacters(data)}", "DATA", ConsoleColor.Gray);
                        }
                        else
                        {
                            LogMessage($"   Бинарные данные: {bytesRead} байт", "DATA", ConsoleColor.Gray);
                            LogMessage($"   Hex: {BitConverter.ToString(buffer, 0, bytesRead).Replace("-", " ")}", "HEX", ConsoleColor.DarkCyan);
                            LogMessage($"   UTF8: {Encoding.UTF8.GetString(buffer[..bytesRead])}", "UTF8", ConsoleColor.DarkCyan);
                        }
                    }
                }
                else
                {
                    // Небольшая задержка чтобы не нагружать CPU
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемое исключение при отмене
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Ошибка чтения входящих данных: {ex.Message}", "ERROR", ConsoleColor.Red);
        }
        finally
        {
            _isReadingIncoming = false;
        }
    }

    static bool IsPrintableText(string text)
    {
        foreach (char c in text)
        {
            if (c < 32 && c != '\n' && c != '\r' && c != '\t')
                return false;
            if (c > 126 && c < 160)
                return false;
        }
        return true;
    }

    static string EscapeControlCharacters(string text)
    {
        return text
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            .Replace("\0", "\\0");
    }

    static void ShowAvailableFiles()
    {
        // Создаем директорию если её нет
        if (!Directory.Exists(_filesDirectory))
        {
            Directory.CreateDirectory(_filesDirectory);
            LogMessage($"📁 Папка '{_filesDirectory}' создана (пустая)", "SYSTEM", ConsoleColor.Blue);
        }

        var files = GetAvailableFiles();
            
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]В папке '{_filesDirectory}' нет файлов[/]");
            AnsiConsole.MarkupLine($"[grey]Поместите файлы в папку: {Path.GetFullPath(_filesDirectory)}[/]");
            return;
        }

        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.Title($"[blue]📁 Доступные файлы в папке '{_filesDirectory}'[/]");
            
        table.AddColumn(new TableColumn("[green]#[/]").Centered());
        table.AddColumn("[green]Имя файла[/]");
        table.AddColumn(new TableColumn("[green]Размер[/]").RightAligned());
        table.AddColumn("[green]Тип[/]");
        table.AddColumn("[green]Дата изменения[/]");

        int index = 1;
        foreach (var file in files)
        {
            var fileType = GetFileType(file.Extension.ToLower());
            var typeColor = GetFileTypeColor(fileType);
                
            table.AddRow(
                $"[yellow]{index}[/]",
                file.Name,
                $"[cyan]{file.Length:N0}[/] байт",
                $"[{typeColor}]{fileType}[/]",
                $"[grey]{file.LastWriteTime:dd.MM.yy HH:mm}[/]");
            index++;
        }

        AnsiConsole.Write(table);
        LogMessage($"📊 Всего файлов: {files.Count}", "SYSTEM", ConsoleColor.Blue);
    }

    static List<FileInfo> GetAvailableFiles()
    {
        var files = new List<FileInfo>();
            
        if (Directory.Exists(_filesDirectory))
        {
            foreach (var filePath in Directory.GetFiles(_filesDirectory))
            {
                files.Add(new FileInfo(filePath));
            }
        }
            
        return files;
    }

    static string GetFileType(string extension)
    {
        return extension switch
        {
            ".txt" => "Текст",
            ".json" => "JSON",
            ".xml" => "XML",
            ".csv" => "CSV",
            ".bin" => "Бинарный",
            ".dat" => "Данные",
            ".log" => "Лог",
            ".cfg" or ".config" => "Конфиг",
            ".jpg" or ".jpeg" => "Изображение",
            ".png" => "Изображение",
            ".gif" => "Изображение",
            ".pdf" => "PDF",
            ".zip" or ".rar" => "Архив",
            ".exe" => "Программа",
            ".dll" => "Библиотека",
            _ => "Другой"
        };
    }

    static string GetFileTypeColor(string fileType)
    {
        return fileType switch
        {
            "Текст" => "green",
            "JSON" => "yellow",
            "XML" => "orange1",
            "CSV" => "cyan",
            "Бинарный" => "magenta",
            "Данные" => "blue",
            "Лог" => "grey",
            "Конфиг" => "purple",
            "Изображение" => "springgreen1",
            "PDF" => "red",
            "Архив" => "darkorange",
            "Программа" => "red1",
            "Библиотека" => "darkcyan",
            _ => "white"
        };
    }

    static async Task ShowControlMenuAsync()
    {
        while (_isRunning && _currentClient != null && _currentClient.Connected)
        {
            AnsiConsole.WriteLine();
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Выберите действие:[/]")
                    .PageSize(12)
                    .AddChoices(new[]
                    {
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
                        "🚪 Выйти из программы"
                    }));

            switch (choice)
            {
                case "📁 Выбрать и отправить файл":
                    await SendFileAsync();
                    break;
                        
                case "📤 Отправить произвольный текст":
                    await SendCustomTextAsync();
                    break;
                        
                case "📤 Отправить HEX данные":
                    await SendHexDataAsync();
                    break;
                        
                case "🔄 Обновить список файлов":
                    ShowAvailableFiles();
                    break;
                        
                case "📊 Статус соединения":
                    ShowConnectionStatus();
                    break;
                        
                case "⏸️ Пауза логгирования":
                    PauseLogging();
                    break;
                        
                case "▶️ Возобновить логгирование":
                    ResumeLogging();
                    break;
                        
                case "📝 Показать логи":
                    ShowLogs();
                    break;
                        
                case "🧹 Очистить логи":
                    ClearLogs();
                    break;
                        
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

            // Создаем список файлов для выбора
            var fileChoices = new List<string>();
            foreach (var file in files)
            {
                var fileType = GetFileType(file.Extension.ToLower());
                fileChoices.Add($"{file.Name} <{fileType}, {file.Length:N0} байт>");
            }
            fileChoices.Add("❌ Отмена");

            var selected = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[green]Выберите файл для отправки:[/]")
                    .PageSize(15)
                    .AddChoices(fileChoices));

            if (selected == "❌ Отмена")
            {
                LogMessage("Отмена отправки файла", "SYSTEM", ConsoleColor.Gray);
                return;
            }

            // Получаем индекс выбранного файла
            int fileIndex = fileChoices.IndexOf(selected);
            var selectedFile = files[fileIndex];

            // Показываем информацию о файле
            var panel = new Panel($"""
                                   [bold]Имя файла:[/] {selectedFile.Name}
                                   [bold]Размер:[/] {selectedFile.Length:N0} байт
                                   [bold]Тип:[/] {GetFileType(selectedFile.Extension.ToLower())}
                                   [bold]Дата изменения:[/] {selectedFile.LastWriteTime:dd.MM.yyyy HH:mm:ss}
                                   """)
            {
                Header = new PanelHeader("[blue]Информация о файле[/]"),
                Border = BoxBorder.Rounded,
                Padding = new Padding(1, 1, 1, 1)
            };

            AnsiConsole.Write(panel);

            // Подтверждение отправки
            if (!AnsiConsole.Confirm("[yellow]Отправить этот файл?[/]", true))
            {
                LogMessage("Отмена отправки файла", "SYSTEM", ConsoleColor.Gray);
                return;
            }

            // Отправка файла
            var fileData = await File.ReadAllTextAsync(selectedFile.FullName);
            var fileDataCommands = fileData.Split("[FIELD]")
                .Where(x => x.Length > 0)
                .ToArray();
            
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var totalSentBytes = 0;
            
            AnsiConsole.Status()
                .Start("Отправка файла...", ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                    ctx.SpinnerStyle(Style.Parse("green"));

                    var i = 0;
                    foreach (var fileDataCommand in fileDataCommands)
                    {
                        var byteData = Encoding.UTF8.GetBytes(fileDataCommand);
                        
                        byte[] headerBytes = new byte[2]
                        {
                            (byte)(byteData.Length / 256),
                            (byte)(byteData.Length % 256)
                        };

                        if (_clientStream != null && _clientStream.CanWrite)
                        {
                            _clientStream.Write(headerBytes); // header bytes
                            _clientStream.Write(byteData); //data
                            
                            totalSentBytes += byteData.Length;
                            
                            // Логируем отправку
                            LogMessage($"📤 Отправлено {byteData.Length} байт (комманда {i++} из {fileDataCommands.Length})", "OUTGOING", ConsoleColor.Green);
                                    
                            // Обновляем статус
                            var elapsed = stopwatch.Elapsed.TotalSeconds;
                            var speed = elapsed > 0 ? totalSentBytes / elapsed : 0;
                            ctx.Status($"Отправлено: {totalSentBytes:N0} байт ({speed:N0} байт/сек)");
                        }
                        else
                        {
                            throw new Exception("Соединение разорвано");
                        }
                    }
                });

            stopwatch.Stop();

            var elapsedTime = stopwatch.Elapsed.TotalSeconds;
            var speed = elapsedTime > 0 ? totalSentBytes / elapsedTime : 0;
                    
            LogMessage($"✅ Файл отправлен: {selectedFile.Name} ({totalSentBytes} байт, {speed:N0} байт/сек)", "SYSTEM", ConsoleColor.Green);
                    
            var resultPanel = new Panel($"""
                                         [green]✓ Файл успешно отправлен![/]

                                         [bold]Имя файла:[/] {selectedFile.Name}
                                         [bold]Размер:[/] {totalSentBytes:N0} байт
                                         [bold]Время:[/] {elapsedTime:F2} секунд
                                         [bold]Скорость:[/] {speed:N0} байт/сек
                                         [bold]Клиент:[/] {_currentClientInfo}
                                         """)
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green),
                Padding = new Padding(1, 1, 1, 1)
            };
                    
            AnsiConsole.Write(resultPanel);
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
            var text = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Введите текст для отправки:[/]")
                    .AllowEmpty());

            if (string.IsNullOrEmpty(text))
            {
                LogMessage("Текст не введен", "SYSTEM", ConsoleColor.Gray);
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(text);
                
            if (_clientStream != null && _clientStream.CanWrite)
            {
                await _clientStream.WriteAsync(bytes, 0, bytes.Length);
                    
                LogMessage($"📤 Отправлен текст ({bytes.Length} байт):", "OUTGOING", ConsoleColor.Green);
                LogMessage($"   {EscapeControlCharacters(text)}", "DATA", ConsoleColor.Gray);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Ошибка отправки текста: {ex.Message}", "ERROR", ConsoleColor.Red);
        }
    }

    static async Task SendHexDataAsync()
    {
        try
        {
            var hexInput = AnsiConsole.Prompt(
                new TextPrompt<string>("[green]Введите HEX данные (например: 48 65 6C 6C 6F):[/]")
                    .AllowEmpty());

            if (string.IsNullOrEmpty(hexInput))
            {
                LogMessage("HEX данные не введены", "SYSTEM", ConsoleColor.Gray);
                return;
            }

            // Преобразуем HEX строку в байты
            hexInput = hexInput.Replace(" ", "").Replace("-", "").Replace("0x", "");
                
            if (hexInput.Length % 2 != 0)
            {
                LogMessage("❌ Неверный формат HEX данных (должно быть четное количество символов)", "ERROR", ConsoleColor.Red);
                return;
            }

            var bytes = new byte[hexInput.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hexInput.Substring(i * 2, 2), 16);
            }

            if (_clientStream != null && _clientStream.CanWrite)
            {
                await _clientStream.WriteAsync(bytes, 0, bytes.Length);
                    
                LogMessage($"📤 Отправлены HEX данные ({bytes.Length} байт):", "OUTGOING", ConsoleColor.Green);
                LogMessage($"   Hex: {BitConverter.ToString(bytes).Replace("-", " ")}", "HEX", ConsoleColor.DarkCyan);
                    
                var text = Encoding.UTF8.GetString(bytes);
                if (IsPrintableText(text))
                {
                    LogMessage($"   Текст: {EscapeControlCharacters(text)}", "TEXT", ConsoleColor.Gray);
                }
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Ошибка отправки HEX данных: {ex.Message}", "ERROR", ConsoleColor.Red);
        }
    }

    static void ShowConnectionStatus()
    {
        var panel = new Panel($"""
                               Клиент: {_currentClientInfo}
                               Статус: {(_currentClient?.Connected == true ? "[green]Подключен[/]" : "[red]Отключен[/]")}
                               Логгирование: {(_isReadingIncoming ? "[green]Активно[/]" : "[red]Остановлено[/]")}
                               Поток: {(_clientStream?.CanWrite == true ? "[green]Доступен[/]" : "[red]Недоступен[/]")}
                               Время: {DateTime.Now:HH:mm:ss}
                               """)
        {
            Header = new PanelHeader("[blue]Статус соединения[/]"),
            Border = BoxBorder.Rounded,
            Padding = new Padding(1, 1, 1, 1)
        };

        AnsiConsole.Write(panel);
    }

    static void PauseLogging()
    {
        if (_isReadingIncoming)
        {
            _readCancellationSource.Cancel();
            _isReadingIncoming = false;
            LogMessage("⏸️ Логгирование входящих данных приостановлено", "SYSTEM", ConsoleColor.Yellow);
        }
        else
        {
            LogMessage("Логгирование уже приостановлено", "SYSTEM", ConsoleColor.Gray);
        }
    }

    static void ResumeLogging()
    {
        if (!_isReadingIncoming && _currentClient?.Connected == true)
        {
            _readCancellationSource = new CancellationTokenSource();
            _isReadingIncoming = true;
            _ = Task.Run(() => ReadIncomingDataAsync(_readCancellationSource.Token));
            LogMessage("▶️ Логгирование входящих данных возобновлено", "SYSTEM", ConsoleColor.Green);
        }
        else
        {
            LogMessage("Логгирование уже активно", "SYSTEM", ConsoleColor.Gray);
        }
    }

    static void ShowLogs()
    {
        try
        {
            if (File.Exists("tcp_sender.log"))
            {
                var logs = File.ReadAllLines("tcp_sender.log");
                    
                var table = new Table();
                table.Border(TableBorder.Rounded);
                table.Title("[blue]📋 Последние логи[/]");
                    
                table.AddColumn("Время");
                table.AddColumn("Тип");
                table.AddColumn("Сообщение");

                // Показываем последние 20 записей
                var startIndex = Math.Max(0, logs.Length - 20);
                for (int i = startIndex; i < logs.Length; i++)
                {
                    var parts = logs[i].Split(']', 3);
                    if (parts.Length >= 3)
                    {
                        var time = parts[0].TrimStart('[');
                        var type = parts[1].TrimStart('[').Trim();
                        var message = parts[2].Trim();
                            
                        table.AddRow(
                            $"[grey]{time}[/]",
                            $"[cyan]{type}[/]",
                            message);
                    }
                }

                AnsiConsole.Write(table);
                LogMessage($"Показано {Math.Min(20, logs.Length)} из {logs.Length} записей лога", "SYSTEM", ConsoleColor.Blue);
            }
            else
            {
                LogMessage("Файл логов не найден", "SYSTEM", ConsoleColor.Gray);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Ошибка чтения логов: {ex.Message}", "ERROR", ConsoleColor.Red);
        }
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
            catch (Exception ex)
            {
                LogMessage($"❌ Ошибка очистки логов: {ex.Message}", "ERROR", ConsoleColor.Red);
            }
        }
    }

    static async Task DisconnectClientAsync()
    {
        try
        {
            if (_currentClient != null && _currentClient.Connected)
            {
                // Останавливаем чтение входящих данных
                if (_isReadingIncoming)
                {
                    _readCancellationSource.Cancel();
                }

                // Отправляем сообщение о разрыве соединения
                var disconnectMsg = Encoding.UTF8.GetBytes("[SERVER] Соединение закрыто\n");
                await _clientStream.WriteAsync(disconnectMsg, 0, disconnectMsg.Length);
                    
                await Task.Delay(100);
                    
                _clientStream?.Close();
                _currentClient?.Close();
                    
                LogMessage("🔌 Соединение закрыто сервером", "SYSTEM", ConsoleColor.Yellow);
            }
        }
        catch (Exception ex)
        {
            LogMessage($"❌ Ошибка при отключении: {ex.Message}", "ERROR", ConsoleColor.Red);
        }
        finally
        {
            _currentClient = null;
            _clientStream = null;
            _isReadingIncoming = false;
            LogMessage("⏳ Ожидание нового подключения...", "SYSTEM", ConsoleColor.Blue);
        }
    }

    static void LogMessage(string message, string type, ConsoleColor color)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logEntry = $"[{timestamp}] [{type}] {message}";

        var colorName = color.ToString().ToLower();

        var escapedTimestamp = Markup.Escape($"[{timestamp}] ");
        var escapedType = Markup.Escape($"[{type}] ");
        AnsiConsole.MarkupLine($"[grey]{escapedTimestamp}[/][{colorName}]{escapedType}[/] {message}");

        // Запись в файл лога
        try
        {
            File.AppendAllText("tcp_sender.log", logEntry + Environment.NewLine, Encoding.UTF8);
        }
        catch
        {
            // Игнорируем ошибки записи в лог
        }
    }
}
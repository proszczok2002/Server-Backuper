using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using ServerBackuper.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace ServerBackuper.App;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths = new();
    private readonly JsonFileStore _store = new();
    private readonly DispatcherTimer _timer;
    private BackupSettings _settings = new();
    private List<BackupHistoryEntry> _history = [];
    private long _historyVersion = -1;
    private string _lastAnnouncement = string.Empty;
    private string _lastOperationSignature = string.Empty;
    private string _lastStateOperationVersion = string.Empty;
    private string _lastLogPath = string.Empty;
    private DateTime _lastLogWriteUtc = DateTime.MinValue;
    private long _lastLogLength = -1;
    private readonly Queue<string> _operationLines = new();
    private bool _testInProgress;
    private Forms.NotifyIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayNoticeShown;

    public MainWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshViews();
        Loaded += (_, _) => InitializeApplication();
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
        Closed += (_, _) =>
        {
            _timer.Stop();
            DisposeTrayIcon();
        };
    }

    private void InitializeApplication()
    {
        try
        {
            _paths.EnsureCreated();
            InitializeTrayIcon();
            LoadSettings();
            RefreshViews(true);
            _timer.Start();
            RunNowButton.Focus();
        }
        catch (Exception ex)
        {
            ShowError("Nie można uruchomić panelu: " + ex.Message);
        }
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null) return;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Otwórz panel", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray)));
        menu.Items.Add("Zakończ panel", null,
            (_, _) => Dispatcher.BeginInvoke(new Action(ExitPanel)));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "Server Backuper",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        HideToTray();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (_trayIcon is null || _trayNoticeShown) return;
        _trayNoticeShown = true;
        _trayIcon.ShowBalloonTip(3000, "Server Backuper",
            "Panel działa w zasobniku. Kliknij ikonę dwukrotnie, aby go otworzyć.",
            Forms.ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitPanel()
    {
        _exitRequested = true;
        DisposeTrayIcon();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon is null) return;
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void RefreshViews(bool forceLogRefresh = false)
    {
        RefreshState(false);
        RefreshLog(forceLogRefresh);
    }

    private void LoadSettings()
    {
        _settings = _store.LoadOrCreate(_paths.SettingsFile, () => new BackupSettings());
        SourceRootBox.Text = _settings.SourceRoot;
        LocalRootBox.Text = _settings.LocalBackupRoot;
        NasRootBox.Text = _settings.NasBackupRoot;
        NasEnabledBox.IsChecked = _settings.NasEnabled;
        SevenZipBox.Text = _settings.SevenZipPath;
        ArchivePasswordBox.Password = TryUnprotect(_settings.ArchivePasswordProtected);
        ScheduleEnabledBox.IsChecked = _settings.Schedule.Enabled;
        IntervalHoursBox.Text = _settings.Schedule.IntervalHours.ToString(CultureInfo.InvariantCulture);
        var first = ScheduleCalculator.ParseFirstRun(_settings.Schedule);
        FirstDateBox.Text = first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        FirstTimeBox.Text = first.ToString("HH:mm", CultureInfo.InvariantCulture);
        KeepLocalBox.Text = _settings.Retention.KeepLocal.ToString(CultureInfo.InvariantCulture);
        KeepNasBox.Text = _settings.Retention.KeepNas.ToString(CultureInfo.InvariantCulture);
        TelegramEnabledBox.IsChecked = _settings.Telegram.Enabled;
        TokenBox.Password = TryUnprotect(_settings.Telegram.BotTokenProtected);
        ChatIdBox.Text = _settings.Telegram.ChatId;
        NotifyStartBox.IsChecked = _settings.Telegram.NotifyStart;
        NotifySuccessBox.IsChecked = _settings.Telegram.NotifySuccess;
        NotifyFailureBox.IsChecked = _settings.Telegram.NotifyFailure;
        UpdateNasControls();
    }

    private bool SaveSettings(bool showConfirmation = true)
    {
        try
        {
            if (!int.TryParse(IntervalHoursBox.Text, out var intervalHours))
                throw new InvalidDataException("Odstęp musi być liczbą godzin.");
            ScheduleCalculator.ValidateInterval(intervalHours);
            if (!int.TryParse(KeepLocalBox.Text, out var keepLocal) || keepLocal < 1)
                throw new InvalidDataException("Liczba kopii lokalnych musi wynosić co najmniej 1.");
            var nasEnabled = NasEnabledBox.IsChecked == true;
            var keepNas = _settings.Retention.KeepNas;
            if (nasEnabled && (!int.TryParse(KeepNasBox.Text, out keepNas) || keepNas < 1))
                throw new InvalidDataException("Liczba kopii na NAS musi wynosić co najmniej 1.");
            if (!DateTime.TryParseExact(FirstDateBox.Text.Trim() + " " + FirstTimeBox.Text.Trim(),
                    "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var firstRun))
                throw new InvalidDataException("Data i godzina muszą mieć format RRRR-MM-DD oraz GG:mm.");
            if (string.IsNullOrWhiteSpace(SourceRootBox.Text)) throw new InvalidDataException("Wpisz katalog źródłowy.");
            if (NetworkStorageDetector.IsNetworkPath(SourceRootBox.Text.Trim()))
                throw new InvalidDataException(
                    "Katalog źródłowy nie może być ścieżką UNC ani mapowanym dyskiem sieciowym. Wskaż lokalny katalog po migracji.");
            if (string.IsNullOrWhiteSpace(LocalRootBox.Text)) throw new InvalidDataException("Wpisz katalog lokalnych kopii.");
            if (nasEnabled && !NasRootBox.Text.Trim().StartsWith(@"\\", StringComparison.Ordinal))
                throw new InvalidDataException(@"Ścieżka NAS musi być ścieżką UNC, np. \\NAS\Backupy.");
            if (ArchivePasswordBox.Password.Length < 12)
                throw new InvalidDataException("Hasło archiwum musi mieć co najmniej 12 znaków.");

            var enabled = ScheduleEnabledBox.IsChecked == true;
            var firstText = firstRun.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            var scheduleChanged = _settings.Schedule.Enabled != enabled
                || _settings.Schedule.IntervalHours != intervalHours
                || _settings.Schedule.FirstRunLocal != firstText;

            _settings.SourceRoot = SourceRootBox.Text.Trim();
            _settings.LocalBackupRoot = LocalRootBox.Text.Trim();
            _settings.NasBackupRoot = NasRootBox.Text.Trim();
            _settings.NasEnabled = nasEnabled;
            _settings.SevenZipPath = SevenZipBox.Text.Trim();
            _settings.ArchivePasswordProtected = SecretProtector.Protect(ArchivePasswordBox.Password);
            _settings.Schedule.Enabled = enabled;
            _settings.Schedule.IntervalHours = intervalHours;
            _settings.Schedule.FirstRunLocal = firstText;
            if (scheduleChanged) _settings.Schedule.Revision = Guid.NewGuid().ToString("N");
            _settings.Retention.KeepLocal = keepLocal;
            _settings.Retention.KeepNas = keepNas;
            _settings.Telegram.Enabled = TelegramEnabledBox.IsChecked == true;
            _settings.Telegram.BotTokenProtected = SecretProtector.Protect(TokenBox.Password);
            _settings.Telegram.ChatId = ChatIdBox.Text.Trim();
            _settings.Telegram.NotifyStart = NotifyStartBox.IsChecked == true;
            _settings.Telegram.NotifySuccess = NotifySuccessBox.IsChecked == true;
            _settings.Telegram.NotifyFailure = NotifyFailureBox.IsChecked == true;
            _store.Save(_paths.SettingsFile, _settings);
            if (showConfirmation) ShowInformation("Ustawienia zostały zapisane.");
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            return false;
        }
    }

    private void RefreshState(bool announce)
    {
        if (_testInProgress) return;
        if (!File.Exists(_paths.StateFile))
        {
            const string message = "Agent nie jest uruchomiony. Testy można wykonać, ale backup wymaga instalacji agenta.";
            ServiceStatusText.Text = message;
            AppendOperationLine("Stan agenta", 0, message);
            if (announce || _lastAnnouncement != message) Announce(message);
            return;
        }
        try
        {
            var state = _store.LoadOrCreate(_paths.StateFile, () => new ServiceState());
            var stale = !state.LastHeartbeatUtc.HasValue ||
                DateTime.UtcNow - state.LastHeartbeatUtc.Value > TimeSpan.FromSeconds(15);
            ServiceStatusText.Text = stale ? "Agent nie odpowiada" : state.ServiceStatus;
            CurrentStageText.Text = string.IsNullOrWhiteSpace(state.CurrentStage) ? "Brak" : state.CurrentStage;
            ProgressText.Text = state.ProgressPercent + " procent";
            BackupProgressBar.Value = Math.Clamp(state.ProgressPercent, 0, 100);
            LastSuccessText.Text = FormatDate(state.LastSuccessUtc?.ToLocalTime());
            NextRunText.Text = state.NextScheduledRunLocal?.ToString("yyyy-MM-dd HH:mm") ?? "Brak";
            LastMessageText.Text = string.IsNullOrWhiteSpace(state.LastMessage) ? "Brak" : state.LastMessage;
            if (state.OperationLog is { Count: > 0 })
                UpdateStateOperationLog(state.OperationLog);
            else
                AppendOperationLine(state.CurrentStage, state.ProgressPercent, state.LastMessage);
            RunNowButton.IsEnabled = !state.BackupInProgress;
            CancelButton.IsEnabled = state.BackupInProgress;
            StartRestoreButton.IsEnabled = !state.BackupInProgress && !_testInProgress;
            var historyVersion = state.History.Count == 0 ? 0 :
                state.History[0].StartedUtc.Ticks ^ state.History.Count;
            if (historyVersion != _historyVersion)
            {
                _historyVersion = historyVersion;
                _history = state.History;
                UpdateHistoryList();
            }
            if (announce) Announce(state.LastMessage);
            else if (state.LastMessage != _lastAnnouncement) Announce(state.LastMessage);
        }
        catch (IOException) { }
        catch (Exception ex) { ServiceStatusText.Text = "Błąd odczytu stanu: " + ex.Message; }
    }

    private void UpdateStateOperationLog(IReadOnlyList<OperationProgressEntry> entries)
    {
        var ordered = entries
            .OrderBy(entry => entry.TimestampUtc)
            .TakeLast(500)
            .ToList();
        if (ordered.Count == 0) return;

        var newest = ordered[^1];
        var version = $"{entries.Count}|{newest.TimestampUtc.Ticks}|{newest.Stage}|{newest.Percent}|{newest.Message}";
        if (version == _lastStateOperationVersion) return;
        _lastStateOperationVersion = version;

        _operationLines.Clear();
        foreach (var entry in ordered)
        {
            var localTime = entry.TimestampUtc.ToLocalTime();
            _operationLines.Enqueue(
                $"{localTime:yyyy-MM-dd HH:mm:ss} | {Math.Clamp(entry.Percent, 0, 100)}% | " +
                $"{entry.Stage} | {entry.Message}");
        }

        var hadFocus = LiveOperationLogBox.IsKeyboardFocusWithin;
        var selectionStart = LiveOperationLogBox.SelectionStart;
        var selectionLength = LiveOperationLogBox.SelectionLength;
        LiveOperationLogBox.Text = string.Join(Environment.NewLine, _operationLines);
        if (hadFocus && selectionLength > 0)
        {
            selectionStart = Math.Min(selectionStart, LiveOperationLogBox.Text.Length);
            selectionLength = Math.Min(selectionLength, LiveOperationLogBox.Text.Length - selectionStart);
            LiveOperationLogBox.Select(selectionStart, selectionLength);
        }
        else { LiveOperationLogBox.CaretIndex = LiveOperationLogBox.Text.Length; LiveOperationLogBox.ScrollToEnd(); }
    }

    private void AppendOperationLine(string? stage, int percent, string? message)
    {
        var normalizedStage = string.IsNullOrWhiteSpace(stage) ? "Stan" : stage.Trim();
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Brak komunikatu" : message.Trim();
        var safePercent = Math.Clamp(percent, 0, 100);
        var signature = $"{normalizedStage}|{safePercent}|{normalizedMessage}";
        if (signature == _lastOperationSignature) return;
        _lastOperationSignature = signature;

        _operationLines.Enqueue($"{DateTime.Now:HH:mm:ss} | {safePercent}% | {normalizedStage} | {normalizedMessage}");
        while (_operationLines.Count > 500) _operationLines.Dequeue();

        var hadFocus = LiveOperationLogBox.IsKeyboardFocusWithin;
        var selectionStart = LiveOperationLogBox.SelectionStart;
        var selectionLength = LiveOperationLogBox.SelectionLength;
        LiveOperationLogBox.Text = string.Join(Environment.NewLine, _operationLines);
        if (hadFocus)
        {
            selectionStart = Math.Min(selectionStart, LiveOperationLogBox.Text.Length);
            selectionLength = Math.Min(selectionLength, LiveOperationLogBox.Text.Length - selectionStart);
            LiveOperationLogBox.Select(selectionStart, selectionLength);
        }
        else
        {
            LiveOperationLogBox.CaretIndex = LiveOperationLogBox.Text.Length;
            LiveOperationLogBox.ScrollToEnd();
        }
    }

    private void UpdateHistoryList()
    {
        var selected = HistoryList.SelectedIndex;
        HistoryList.ItemsSource = _history.Select(entry => new HistoryItem(entry)).ToList();
        if (selected >= 0 && selected < HistoryList.Items.Count) HistoryList.SelectedIndex = selected;
    }

    private bool IsAgentResponsive()
    {
        if (!File.Exists(_paths.StateFile)) return false;
        try
        {
            var state = _store.LoadOrCreate(_paths.StateFile, () => new ServiceState());
            return state.LastHeartbeatUtc.HasValue
                && DateTime.UtcNow - state.LastHeartbeatUtc.Value <= TimeSpan.FromSeconds(15);
        }
        catch { return false; }
    }

    private void RunNow_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAgentResponsive())
        {
            ShowError("Agent backupu nie odpowiada. Najpierw uruchom instalator agenta.");
            return;
        }
        if (!SaveSettings(false)) return;
        var answer = MessageBox.Show(
            "Uruchomić backup teraz? Wybrane kontenery zostaną na krótko zatrzymane.",
            "Potwierdzenie backupu", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        new CommandQueue(_paths, _store).Enqueue(CommandType.RunBackup);
        ShowInformation("Polecenie uruchomienia backupu zostało przekazane do usługi.");
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAgentResponsive())
        {
            ShowError("Agent backupu nie odpowiada, więc nie ma czego anulować.");
            return;
        }
        new CommandQueue(_paths, _store).Enqueue(CommandType.CancelBackup);
        ShowInformation("Polecenie bezpiecznego anulowania operacji zostało przekazane do agenta.");
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();

    private void BeginTest(string message)
    {
        _testInProgress = true;
        TestNasButton.IsEnabled = false;
        TestTelegramButton.IsEnabled = false;
        InspectRestoreButton.IsEnabled = false;
        StartRestoreButton.IsEnabled = false;
        OperationProgress.Visibility = Visibility.Visible;
        AppendOperationLine("Operacja panelu", 0, message);
        Announce(message);
    }

    private void EndTest()
    {
        _testInProgress = false;
        OperationProgress.Visibility = Visibility.Collapsed;
        UpdateNasControls();
        TestTelegramButton.IsEnabled = true;
        InspectRestoreButton.IsEnabled = true;
        _lastStateOperationVersion = string.Empty;
        StartRestoreButton.IsEnabled = true;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshViews(true);
        Announce("Stan i dziennik odświeżono. Ostatni komunikat: " + LastMessageText.Text);
    }

    private async void TestNas_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(false)) return;
        if (!_settings.NasEnabled)
        {
            ShowInformation("Najpierw zaznacz opcję kopiowania backupu na NAS.");
            return;
        }
        BeginTest("Trwa test zapisu i usuwania pliku na NAS.");
        try
        {
            await BackupEngine.TestNasAsync(_settings.NasBackupRoot, CancellationToken.None);
            ShowInformation("Test NAS zakończony sukcesem. Zapis, odczyt i usunięcie pliku działają.");
        }
        catch (Exception ex) { ShowError("Test NAS nieudany: " + ex.Message); }
        finally { EndTest(); }
    }

    private void NasEnabled_Changed(object sender, RoutedEventArgs e) => UpdateNasControls();

    private void UpdateNasControls()
    {
        var enabled = NasEnabledBox.IsChecked == true;
        NasLabel.IsEnabled = enabled;
        NasRootBox.IsEnabled = enabled;
        BrowseNasButton.IsEnabled = enabled;
        KeepNasLabel.IsEnabled = enabled;
        KeepNasBox.IsEnabled = enabled;
        KeepNasHelp.IsEnabled = enabled;
        TestNasButton.IsEnabled = enabled && !_testInProgress;
    }

    private async void TestTelegram_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveSettings(false)) return;
        BeginTest("Trwa wysyłanie wiadomości testowej Telegram.");
        try
        {
            var telegram = new TelegramSettings
            {
                Enabled = true,
                BotTokenProtected = _settings.Telegram.BotTokenProtected,
                ChatId = _settings.Telegram.ChatId
            };
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var result = await new TelegramNotifier(client, new TextLog(_paths)).SendAsync(
                telegram, $"Test powiadomień Server Backuper na {Environment.MachineName}.", CancellationToken.None);
            if (result.Success) ShowInformation(result.Message);
            else ShowError(result.Message);
        }
        catch (Exception ex) { ShowError("Test Telegram nieudany: " + ex.Message); }
        finally { EndTest(); }
    }

    private async Task<RestorePreview> InspectSelectedArchiveAsync()
    {
        var archive = RestoreArchiveBox.Text.Trim();
        var password = RestorePasswordBox.Password;
        if (string.IsNullOrWhiteSpace(archive)) throw new InvalidDataException("Wybierz archiwum backupu.");
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidDataException("Wpisz hasło archiwum.");
        var runner = new ProcessRunner(new TextLog(_paths));
        var engine = new RestoreEngine(runner, new DockerDiscovery(runner), new TextLog(_paths));
        return await engine.InspectAsync(archive, password, SevenZipBox.Text.Trim(), CancellationToken.None);
    }

    private async void InspectRestore_Click(object sender, RoutedEventArgs e)
    {
        BeginTest("Trwa sprawdzanie integralności i manifestu archiwum.");
        try
        {
            var preview = await InspectSelectedArchiveAsync();
            RestorePreviewBox.Text =
                $"Format backupu: {preview.FormatVersion}{Environment.NewLine}" +
                $"Utworzono: {preview.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"Pierwotna lokalizacja: {preview.OriginalSourceRoot}{Environment.NewLine}" +
                $"Projekty Compose: {preview.ComposeFileCount}{Environment.NewLine}" +
                $"Kontenery: {preview.ContainerCount}{Environment.NewLine}" +
                $"Wolumeny Docker: {preview.VolumeCount}{Environment.NewLine}" +
                $"Logiczne dumpy baz: {preview.DatabaseDumpCount}";
            ShowInformation("Archiwum jest poprawne. Szczegóły zostały pokazane w polu informacji.");
        }
        catch (Exception ex) { ShowError("Nie można użyć archiwum: " + ex.Message); }
        finally { EndTest(); }
    }

    private async void StartRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!IsAgentResponsive())
        {
            ShowError("Agent backupu nie odpowiada. Przywracanie wymaga uruchomionego agenta.");
            return;
        }

        RestorePreview preview;
        BeginTest("Trwa końcowa kontrola archiwum przed przywracaniem.");
        try { preview = await InspectSelectedArchiveAsync(); }
        catch (Exception ex) { ShowError("Nie można użyć archiwum: " + ex.Message); return; }
        finally { EndTest(); }

        var target = RestoreTargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(target)) { ShowError("Wybierz pusty katalog docelowy."); return; }
        var fullTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(fullTarget)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(fullTarget, driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Nie można przywracać bezpośrednio do głównego katalogu dysku.");
            return;
        }
        if (Directory.Exists(fullTarget) && Directory.EnumerateFileSystemEntries(fullTarget).Any())
        {
            ShowError("Katalog docelowy nie jest pusty. Wybierz pusty katalog, aby chronić istniejące dane.");
            return;
        }

        var answer = MessageBox.Show(
            $"Przywrócić backup do:{Environment.NewLine}{fullTarget}{Environment.NewLine}{Environment.NewLine}" +
            $"Projekty Compose: {preview.ComposeFileCount}, wolumeny: {preview.VolumeCount}, " +
            $"dumpy baz: {preview.DatabaseDumpCount}.{Environment.NewLine}{Environment.NewLine}" +
            "Zawartość wymienionych wolumenów Dockera zostanie zastąpiona. Czy kontynuować?",
            "Potwierdzenie przywracania", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        new CommandQueue(_paths, _store).Enqueue(new CommandRequest
        {
            Type = CommandType.RestoreBackup,
            ArchivePath = RestoreArchiveBox.Text.Trim(),
            RestoreTargetRoot = fullTarget,
            RestorePasswordProtected = SecretProtector.Protect(RestorePasswordBox.Password),
            StartContainersAfterRestore = RestoreStartContainersBox.IsChecked == true
        });
        RestorePasswordBox.Clear();
        MainTabs.SelectedIndex = 0;
        ShowInformation("Przywracanie zostało przekazane do agenta. Postęp jest na karcie Stan.");
    }

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => BrowseFolder(SourceRootBox);
    private void BrowseLocal_Click(object sender, RoutedEventArgs e) => BrowseFolder(LocalRootBox);
    private void BrowseNas_Click(object sender, RoutedEventArgs e) => BrowseFolder(NasRootBox);
    private void BrowseRestoreTarget_Click(object sender, RoutedEventArgs e) => BrowseFolder(RestoreTargetBox);

    private void BrowseRestoreArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Archiwa Server Backuper|*.7z|Wszystkie pliki|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) RestoreArchiveBox.Text = dialog.FileName;
    }

    private void BrowseSevenZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "7-Zip|7z.exe|Programy EXE|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) SevenZipBox.Text = dialog.FileName;
    }

    private void BrowseFolder(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog { Multiselect = false, Title = "Wybierz katalog" };
        if (Directory.Exists(target.Text)) dialog.InitialDirectory = target.Text;
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryItem item) return;
        var entry = item.Entry;
        var warnings = entry.Warnings is { Count: > 0 }
            ? Environment.NewLine + string.Join(Environment.NewLine,
                entry.Warnings.Select(warning => "- " + warning))
            : " Brak";
        HistoryDetailsBox.Text =
            $"Wynik: {(entry.Success ? "Sukces" : "Błąd")}{Environment.NewLine}" +
            $"Rozpoczęcie: {entry.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Zakończenie: {entry.FinishedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
            $"Tryb: {entry.Trigger}{Environment.NewLine}" +
            $"Komunikat: {entry.Message}{Environment.NewLine}" +
            $"Plik lokalny: {entry.LocalArchivePath}{Environment.NewLine}" +
            $"Plik NAS: {entry.NasArchivePath}{Environment.NewLine}" +
            $"Rozmiar: {entry.ArchiveSizeBytes / 1024d / 1024d / 1024d:F2} GB{Environment.NewLine}" +
            $"Ostrze\u017Cenia:{warnings}";
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e)
    {
        RefreshLog(true);
        Announce("Dziennik odświeżono.");
    }

    private void RefreshLog(bool force = false)
    {
        try
        {
            var logPath = _paths.LogFile;
            if (!File.Exists(logPath))
            {
                if (force || _lastLogPath != logPath || _lastLogLength != 0)
                    LogBox.Text = "Dziennik jest jeszcze pusty.";
                _lastLogPath = logPath;
                _lastLogWriteUtc = DateTime.MinValue;
                _lastLogLength = 0;
                return;
            }

            var info = new FileInfo(logPath);
            if (!force && _lastLogPath == logPath && _lastLogWriteUtc == info.LastWriteTimeUtc
                && _lastLogLength == info.Length) return;

            var lines = File.ReadLines(logPath).TakeLast(1000);
            LogBox.Text = string.Join(Environment.NewLine, lines);
            _lastLogPath = logPath;
            _lastLogWriteUtc = info.LastWriteTimeUtc;
            _lastLogLength = info.Length;
        }
        catch (Exception ex) { LogBox.Text = "Nie można odczytać dziennika: " + ex.Message; }
    }

    private void Announce(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _lastAnnouncement = message;
        LiveStatus.Text = message;
        System.Windows.Automation.AutomationProperties.SetName(LiveStatus, message);
        var peer = FrameworkElementAutomationPeer.FromElement(LiveStatus)
            ?? new FrameworkElementAutomationPeer(LiveStatus);
        peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ShowError(string message)
    {
        Announce("Błąd: " + message);
        MessageBox.Show(message, "Server Backuper — błąd", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ShowInformation(string message)
    {
        Announce(message);
        MessageBox.Show(message, "Server Backuper", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string TryUnprotect(string value)
    {
        try { return SecretProtector.Unprotect(value); }
        catch { return string.Empty; }
    }

    private static string FormatDate(DateTime? date) => date?.ToString("yyyy-MM-dd HH:mm:ss") ?? "Brak";

    private sealed record HistoryItem(BackupHistoryEntry Entry)
    {
        public override string ToString() =>
            $"{Entry.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} — {(Entry.Success ? "Sukces" : "Błąd")} — {Entry.Message}";
    }
}

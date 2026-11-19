using ServerBackuper.Core;

namespace ServerBackuper.Service;

public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AppPaths _paths = new();
    private readonly JsonFileStore _store = new();
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly object _stateLock = new();
    private readonly TextLog _log;
    private readonly CommandQueue _commands;
    private readonly BackupEngine _engine;
    private readonly RestoreEngine _restore;
    private readonly TelegramNotifier _telegram;
    private ServiceState _state = new();
    private Task? _activeJob;
    private CancellationTokenSource? _activeCancellation;

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
        _paths.EnsureCreated();
        _log = new TextLog(_paths);
        _commands = new CommandQueue(_paths, _store);
        var runner = new ProcessRunner(_log);
        _engine = new BackupEngine(runner, new DockerDiscovery(runner), _log);
        _restore = new RestoreEngine(runner, new DockerDiscovery(runner), _log);
        _telegram = new TelegramNotifier(_httpClient, _log);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _state = _store.LoadOrCreate(_paths.StateFile, () => new ServiceState());
        UpdateState(state =>
        {
            state.ServiceStatus = "Usługa działa";
            state.LastMessage = "Usługa backupu została uruchomiona.";
        });
        _log.Write("Usługa została uruchomiona.");
        UpdateState(state => AddOperationEntry(state, "Stan agenta", state.ProgressPercent, state.LastMessage));
        var nextRecoveryAttempt = DateTime.MinValue;

        while (!stoppingToken.IsCancellationRequested)
        {
            UpdateState(state => state.LastHeartbeatUtc = DateTime.UtcNow);
            try
            {
                if (_activeJob is { IsCompleted: true })
                {
                    try { await _activeJob; } catch (Exception ex) { _log.Write("Błąd zadania: " + ex); }
                    _activeJob = null;
                    _activeCancellation?.Dispose();
                    _activeCancellation = null;
                }

                if (_activeJob is null && _state.RecoveryContainerIds.Count > 0
                    && DateTime.UtcNow >= nextRecoveryAttempt)
                {
                    nextRecoveryAttempt = DateTime.UtcNow.AddSeconds(30);
                    await TryRecoverAsync(stoppingToken);
                }

                await ProcessCommandsAsync(stoppingToken);
                var settings = _store.LoadOrCreate(_paths.SettingsFile, () => new BackupSettings());
                ProcessSchedule(settings, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Błąd pętli usługi");
                _log.Write("Błąd pętli usługi: " + ex);
                UpdateState(state => state.LastMessage = "Błąd usługi: " + ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        if (_activeJob is not null)
        {
            _activeCancellation?.Cancel();
            try { await _activeJob; } catch { }
        }
        _httpClient.Dispose();
        UpdateState(state => state.ServiceStatus = "Usługa zatrzymana");
    }

    private async Task ProcessCommandsAsync(CancellationToken stoppingToken)
    {
        foreach (var (path, request) in _commands.ReadPending())
        {
            try
            {
                switch (request.Type)
                {
                    case CommandType.RunBackup:
                        if (_activeJob is null) StartJob("Ręcznie", stoppingToken);
                        else UpdateState(state => state.LastMessage = "Backup już trwa.");
                        break;
                    case CommandType.CancelBackup:
                        _activeCancellation?.Cancel();
                        UpdateState(state => state.LastMessage = "Zażądano bezpiecznego anulowania backupu.");
                        break;
                    case CommandType.TestNas:
                        await TestNasAsync(stoppingToken);
                        break;
                    case CommandType.TestTelegram:
                        await TestTelegramAsync(stoppingToken);
                        break;
                    case CommandType.RestoreBackup:
                        if (_activeJob is null) StartRestore(request, stoppingToken);
                        else UpdateState(state => state.LastMessage = "Inna operacja już trwa.");
                        break;
                }
            }
            finally { CommandQueue.TryDelete(path); }
        }
    }

    private void ProcessSchedule(BackupSettings settings, CancellationToken stoppingToken)
    {
        DateTime? due;
        DateTime? next;
        try
        {
            due = ScheduleCalculator.GetLatestDue(settings.Schedule, DateTime.Now);
            next = ScheduleCalculator.GetNext(settings.Schedule, DateTime.Now);
        }
        catch (Exception ex)
        {
            UpdateState(state => state.LastMessage = "Niepoprawny harmonogram: " + ex.Message);
            return;
        }

        if (_state.LastScheduleRevision != settings.Schedule.Revision)
        {
            UpdateState(state =>
            {
                state.LastScheduleRevision = settings.Schedule.Revision;
                state.LastScheduledSlotLocal = null;
            });
        }
        UpdateState(state => state.NextScheduledRunLocal = next);

        if (_activeJob is null && _state.RecoveryContainerIds.Count == 0 && due.HasValue
            && (!_state.LastScheduledSlotLocal.HasValue || due.Value > _state.LastScheduledSlotLocal.Value))
        {
            UpdateState(state => state.LastScheduledSlotLocal = due.Value);
            StartJob("Harmonogram", stoppingToken);
        }
    }

    private void StartJob(string trigger, CancellationToken serviceToken)
    {
        if (_state.RecoveryContainerIds.Count > 0)
        {
            UpdateState(state => state.LastMessage = "Najpierw trzeba odzyskać wcześniej zatrzymane kontenery.");
            return;
        }
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        _activeJob = RunBackupAsync(trigger, _activeCancellation.Token);
    }

    private void StartRestore(CommandRequest request, CancellationToken serviceToken)
    {
        if (_state.RecoveryContainerIds.Count > 0)
        {
            UpdateState(state => state.LastMessage =
                "Najpierw trzeba odzyskać wcześniej zatrzymane kontenery.");
            return;
        }
        _activeCancellation = CancellationTokenSource.CreateLinkedTokenSource(serviceToken);
        _activeJob = RunRestoreAsync(request, _activeCancellation.Token);
    }

    private async Task RunRestoreAsync(CommandRequest request, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var settings = _store.LoadOrCreate(_paths.SettingsFile, () => new BackupSettings());
        UpdateState(state =>
        {
            state.BackupInProgress = true;
            state.LastAttemptUtc = started;
            state.CurrentStage = "Przygotowanie przywracania";
            state.ProgressPercent = 0;
            state.LastMessage = "Przywracanie zostało rozpoczęte.";
        });

        UpdateState(state => AddOperationEntry(state, state.CurrentStage, state.ProgressPercent, state.LastMessage));
        if (settings.Telegram.Enabled && settings.Telegram.NotifyStart)
            await TrySendTelegramAsync(settings.Telegram,
                $"Przywracanie backupu rozpoczęte na {Environment.MachineName}.", token);

        BackupResult result;
        try
        {
            var progress = new ImmediateProgress<JobProgress>(ReportProgress);
            result = await _restore.RunAsync(request, settings, progress,
                ids => UpdateState(state => state.RecoveryContainerIds = ids.ToList()), token);
        }
        catch (OperationCanceledException)
        {
            result = new BackupResult { Success = false, Message = "Przywracanie zostało anulowane." };
        }
        catch (Exception ex)
        {
            _log.Write("PRZYWRACANIE NIEUDANE: " + ex);
            result = new BackupResult { Success = false, Message = ex.Message };
        }

        var finished = DateTime.UtcNow;
        UpdateState(state =>
        {
            state.BackupInProgress = false;
            state.CurrentStage = result.Success ? "Przywracanie zakończone" : "Błąd przywracania";
            state.ProgressPercent = result.Success ? 100 : 0;
            state.LastMessage = result.Message;
            foreach (var warning in result.Warnings)
                AddOperationEntry(state, "Ostrze\u017Cenie", state.ProgressPercent, warning);
            AddOperationEntry(state, state.CurrentStage, state.ProgressPercent, state.LastMessage);
            state.History.Insert(0, new BackupHistoryEntry
            {
                StartedUtc = started,
                FinishedUtc = finished,
                Success = result.Success,
                Trigger = "Przywracanie",
                Message = result.Message,
                LocalArchivePath = request.ArchivePath,
                NasArchivePath = request.RestoreTargetRoot,
                ArchiveSizeBytes = File.Exists(request.ArchivePath)
                    ? new FileInfo(request.ArchivePath).Length : 0,
                Warnings = result.Warnings.ToList()
            });
            if (state.History.Count > 100) state.History.RemoveRange(100, state.History.Count - 100);
        });
        foreach (var warning in result.Warnings)
            _log.Write("OSTRZE\u017BENIE PRZYWRACANIA: " + warning);
        _log.Write(result.Message);

        if (settings.Telegram.Enabled && ((result.Success && settings.Telegram.NotifySuccess)
            || (!result.Success && settings.Telegram.NotifyFailure)))
        {
            var message = result.Success
                ? $"Przywracanie zakończone na {Environment.MachineName}. Cel: {request.RestoreTargetRoot}."
                : $"BŁĄD PRZYWRACANIA na {Environment.MachineName}: {result.Message}";
            await TrySendTelegramAsync(settings.Telegram, message, CancellationToken.None);
        }
    }

    private async Task RunBackupAsync(string trigger, CancellationToken token)
    {
        var started = DateTime.UtcNow;
        var settings = _store.LoadOrCreate(_paths.SettingsFile, () => new BackupSettings());
        UpdateState(state =>
        {
            state.BackupInProgress = true;
            state.LastAttemptUtc = started;
            state.CurrentStage = "Rozpoczynanie";
            state.ProgressPercent = 0;
            state.LastMessage = "Backup został rozpoczęty.";
        });

        UpdateState(state => AddOperationEntry(state, state.CurrentStage, state.ProgressPercent, state.LastMessage));
        if (settings.Telegram.Enabled && settings.Telegram.NotifyStart)
            await TrySendTelegramAsync(settings.Telegram,
                $"Backup rozpoczęty na {Environment.MachineName}. Tryb: {trigger}.", token);

        BackupResult result;
        try
        {
            var progress = new ImmediateProgress<JobProgress>(ReportProgress);
            result = await _engine.RunAsync(settings, progress,
                ids => UpdateState(state => state.RecoveryContainerIds = ids.ToList()), token);
        }
        catch (OperationCanceledException)
        {
            result = new BackupResult { Success = false, Message = "Backup został anulowany." };
        }
        catch (Exception ex)
        {
            _log.Write("BACKUP NIEUDANY: " + ex);
            result = new BackupResult { Success = false, Message = ex.Message };
        }

        var finished = DateTime.UtcNow;
        UpdateState(state =>
        {
            state.BackupInProgress = false;
            state.CurrentStage = result.Success ? "Zakończono" : "Błąd";
            state.ProgressPercent = result.Success ? 100 : 0;
            state.LastMessage = result.Message;
            foreach (var warning in result.Warnings)
                AddOperationEntry(state, "Ostrze\u017Cenie", state.ProgressPercent, warning);
            AddOperationEntry(state, state.CurrentStage, state.ProgressPercent, state.LastMessage);
            if (result.Success) state.LastSuccessUtc = finished;
            state.History.Insert(0, new BackupHistoryEntry
            {
                StartedUtc = started,
                FinishedUtc = finished,
                Success = result.Success,
                Trigger = trigger,
                Message = result.Message,
                LocalArchivePath = result.LocalArchivePath,
                NasArchivePath = result.NasArchivePath,
                ArchiveSizeBytes = result.ArchiveSizeBytes,
                Warnings = result.Warnings.ToList()
            });
            if (state.History.Count > 100) state.History.RemoveRange(100, state.History.Count - 100);
        });
        foreach (var warning in result.Warnings)
            _log.Write("OSTRZE\u017BENIE BACKUPU: " + warning);
        _log.Write(result.Message);

        if (settings.Telegram.Enabled && ((result.Success && settings.Telegram.NotifySuccess)
            || (!result.Success && settings.Telegram.NotifyFailure)))
        {
            var duration = finished - started;
            var size = result.ArchiveSizeBytes / 1024d / 1024d / 1024d;
            var retentionSummary = settings.NasEnabled
                ? $"Lokalnie zachowuję {settings.Retention.KeepLocal}, na NAS {settings.Retention.KeepNas}."
                : $"Lokalnie zachowuję {settings.Retention.KeepLocal}; kopiowanie na NAS jest wyłączone.";
            var message = result.Success
                ? $"Backup zakończony na {Environment.MachineName}. Czas: {duration:hh\\:mm\\:ss}, rozmiar: {size:F2} GB. {retentionSummary}"
                : $"BŁĄD BACKUPU na {Environment.MachineName}: {result.Message}";
            await TrySendTelegramAsync(settings.Telegram, message, CancellationToken.None);
        }
    }

    private async Task TryRecoverAsync(CancellationToken token)
    {
        try
        {
            UpdateState(state => state.LastMessage = "Odzyskiwanie kontenerów po przerwanym backupie.");
            await _engine.RecoverContainersAsync(_state.RecoveryContainerIds, token);
            UpdateState(state =>
            {
                state.RecoveryContainerIds.Clear();
                state.LastMessage = "Kontenery zostały odzyskane.";
            });
        }
        catch (Exception ex)
        {
            _log.Write("Odzyskiwanie kontenerów nie powiodło się: " + ex.Message);
            UpdateState(state => state.LastMessage = "Nie można odzyskać kontenerów: " + ex.Message);
        }
    }

    private async Task TestNasAsync(CancellationToken token)
    {
        try
        {
            var settings = _store.LoadOrCreate(_paths.SettingsFile, () => new BackupSettings());
            if (!settings.NasEnabled)
            {
                UpdateState(state => state.LastMessage = "Test NAS pominięty: kopiowanie na NAS jest wyłączone.");
                return;
            }
            await BackupEngine.TestNasAsync(settings.NasBackupRoot, token);
            UpdateState(state => state.LastMessage = "Test NAS zakończony sukcesem.");
        }
        catch (Exception ex) { UpdateState(state => state.LastMessage = "Test NAS nieudany: " + ex.Message); }
    }

    private async Task TestTelegramAsync(CancellationToken token)
    {
        var settings = _store.LoadOrCreate(_paths.SettingsFile, () => new BackupSettings()).Telegram;
        settings.Enabled = true;
        var result = await _telegram.SendAsync(settings,
            $"Test powiadomień Server Backuper na {Environment.MachineName}.", token);
        UpdateState(state => state.LastMessage = result.Message);
    }

    private async Task TrySendTelegramAsync(TelegramSettings settings, string message, CancellationToken token)
    {
        try
        {
            var result = await _telegram.SendAsync(settings, message, token);
            if (!result.Success) _log.Write("Powiadomienie Telegram: " + result.Message);
        }
        catch (Exception ex) { _log.Write("Powiadomienie Telegram: " + ex.Message); }
    }

    private void ReportProgress(JobProgress item)
    {
        UpdateState(state =>
        {
            state.CurrentStage = item.Stage;
            state.ProgressPercent = Math.Clamp(item.Percent, 0, 100);
            state.LastMessage = item.Message;
            AddOperationEntry(state, state.CurrentStage, state.ProgressPercent, state.LastMessage);
        });
        _log.Write($"{Math.Clamp(item.Percent, 0, 100)}% | {item.Stage} | {item.Message}");
    }

    private static void AddOperationEntry(ServiceState state, string stage, int percent, string message)
    {
        state.OperationLog ??= [];
        var safePercent = Math.Clamp(percent, 0, 100);
        var previous = state.OperationLog.LastOrDefault();
        if (previous is not null && previous.Stage == stage && previous.Percent == safePercent
            && previous.Message == message) return;

        state.OperationLog.Add(new OperationProgressEntry
        {
            TimestampUtc = DateTime.UtcNow,
            Stage = stage,
            Percent = safePercent,
            Message = message
        });
        if (state.OperationLog.Count > 500)
            state.OperationLog.RemoveRange(0, state.OperationLog.Count - 500);
    }

    private void UpdateState(Action<ServiceState> update)
    {
        lock (_stateLock)
        {
            update(_state);
            _store.Save(_paths.StateFile, _state);
        }
    }
}

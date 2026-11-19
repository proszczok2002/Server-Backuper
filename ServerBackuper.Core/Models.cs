using System.Text.Json.Serialization;

namespace ServerBackuper.Core;

public sealed class BackupSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string SourceRoot { get; set; } = @"C:\Kontenery";
    public string LocalBackupRoot { get; set; } = @"C:\Backupy";
    public string NasBackupRoot { get; set; } = string.Empty;
    public bool NasEnabled { get; set; } = true;
    public string SevenZipPath { get; set; } = string.Empty;
    public string ArchivePasswordProtected { get; set; } = string.Empty;
    public bool RequireNasBeforeBackup { get; set; } = true;
    public string HelperImage { get; set; } = "alpine:3.22";
    public int ContainerStopTimeoutSeconds { get; set; } = 60;
    public ScheduleSettings Schedule { get; set; } = ScheduleSettings.CreateDefault();
    public RetentionSettings Retention { get; set; } = new();
    public TelegramSettings Telegram { get; set; } = new();
}

public sealed class ScheduleSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalHours { get; set; } = 48;
    public string FirstRunLocal { get; set; } = string.Empty;
    public string Revision { get; set; } = Guid.NewGuid().ToString("N");

    public static ScheduleSettings CreateDefault()
    {
        var now = DateTime.Now;
        var first = now.Date.AddHours(5);
        if (first <= now) first = first.AddDays(1);
        return new ScheduleSettings { FirstRunLocal = first.ToString("yyyy-MM-dd'T'HH:mm:ss") };
    }
}

public sealed class RetentionSettings
{
    public int KeepLocal { get; set; } = 2;
    public int KeepNas { get; set; } = 3;
    public int PartialFileMaxAgeDays { get; set; } = 3;
}

public sealed class TelegramSettings
{
    public bool Enabled { get; set; }
    public string BotTokenProtected { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool NotifyStart { get; set; } = true;
    public bool NotifySuccess { get; set; } = true;
    public bool NotifyFailure { get; set; } = true;
}

public sealed class ServiceState
{
    public DateTime? LastHeartbeatUtc { get; set; }
    public string ServiceStatus { get; set; } = "Uruchamianie";
    public bool BackupInProgress { get; set; }
    public string CurrentStage { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public string LastMessage { get; set; } = string.Empty;
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public DateTime? NextScheduledRunLocal { get; set; }
    public string LastScheduleRevision { get; set; } = string.Empty;
    public DateTime? LastScheduledSlotLocal { get; set; }
    public List<string> RecoveryContainerIds { get; set; } = [];
    public List<BackupHistoryEntry> History { get; set; } = [];
    public List<OperationProgressEntry> OperationLog { get; set; } = [];
}

public sealed class OperationProgressEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string Stage { get; set; } = string.Empty;
    public int Percent { get; set; }
    public string Message { get; set; } = string.Empty;
}

public sealed class BackupHistoryEntry
{
    public DateTime StartedUtc { get; set; }
    public DateTime FinishedUtc { get; set; }
    public bool Success { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string LocalArchivePath { get; set; } = string.Empty;
    public string NasArchivePath { get; set; } = string.Empty;
    public long ArchiveSizeBytes { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed record JobProgress(string Stage, int Percent, string Message);

public sealed class BackupResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string LocalArchivePath { get; init; } = string.Empty;
    public string NasArchivePath { get; init; } = string.Empty;
    public long ArchiveSizeBytes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public enum CommandType { RunBackup, CancelBackup, TestNas, TestTelegram, RestoreBackup }

public sealed class CommandRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public CommandType Type { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string ArchivePath { get; set; } = string.Empty;
    public string RestoreTargetRoot { get; set; } = string.Empty;
    public string RestorePasswordProtected { get; set; } = string.Empty;
    public bool StartContainersAfterRestore { get; set; } = true;
}

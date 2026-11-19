namespace ServerBackuper.Core;

public sealed class AppPaths
{
    public const string DataDirectoryEnvironmentVariable = "SERVERBACKUPER_DATA_DIR";

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride
            ?? Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServerBackuper");
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string StateFile => Path.Combine(Root, "state.json");
    public string CommandsDirectory => Path.Combine(Root, "commands");
    public string LogsDirectory => Path.Combine(Root, "logs");
    public string LogFile => Path.Combine(LogsDirectory, $"server-backuper-{DateTime.Now:yyyy-MM}.log");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CommandsDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}

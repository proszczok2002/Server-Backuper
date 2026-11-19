namespace ServerBackuper.Core;

public sealed class TextLog(AppPaths paths)
{
    private readonly object _sync = new();

    public void Write(string message)
    {
        paths.EnsureCreated();
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        lock (_sync) File.AppendAllText(paths.LogFile, line);
    }
}

namespace ServerBackuper.Core;

public static class RetentionManager
{
    public static IReadOnlyList<string> SelectArchivesToDelete(string directory, int keepCount)
    {
        if (keepCount < 1) throw new ArgumentOutOfRangeException(nameof(keepCount));
        if (!Directory.Exists(directory)) return [];

        return Directory.EnumerateFiles(directory, "Kopia serwera *.7z", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith(".partial.7z", StringComparison.OrdinalIgnoreCase))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(keepCount)
            .Select(file => file.FullName)
            .ToArray();
    }

    public static IReadOnlyList<string> Apply(string directory, int keepCount)
    {
        var deleted = new List<string>();
        foreach (var path in SelectArchivesToDelete(directory, keepCount))
        {
            File.Delete(path);
            deleted.Add(path);
        }
        return deleted;
    }

    public static IReadOnlyList<string> DeleteOldPartialFiles(string directory, int maxAgeDays, DateTime utcNow)
    {
        if (!Directory.Exists(directory)) return [];
        var threshold = utcNow.AddDays(-Math.Max(1, maxAgeDays));
        var deleted = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.partial*", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) >= threshold) continue;
            File.Delete(path);
            deleted.Add(path);
        }
        return deleted;
    }
}

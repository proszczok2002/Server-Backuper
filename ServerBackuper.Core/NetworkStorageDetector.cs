namespace ServerBackuper.Core;

public static class NetworkStorageDetector
{
    private static readonly string[] NetworkMarkers =
        ["cifs", "smb", "nfs", "sshfs", "rclone", "davfs", "webdav"];

    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal)
            || trimmed.StartsWith("//", StringComparison.Ordinal)) return true;

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(trimmed));
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsNetworkVolume(string? driver, IReadOnlyDictionary<string, string>? options)
    {
        if (ContainsNetworkMarker(driver)) return true;
        if (options is null) return false;

        foreach (var option in options)
        {
            if (ContainsNetworkMarker(option.Key) || ContainsNetworkMarker(option.Value)) return true;
            var value = option.Value?.Trim() ?? string.Empty;
            if (value.StartsWith(@"\\", StringComparison.Ordinal)
                || value.StartsWith("//", StringComparison.Ordinal)
                || value.StartsWith("smb://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("nfs://", StringComparison.OrdinalIgnoreCase)) return true;
            if (option.Key.Equals("device", StringComparison.OrdinalIgnoreCase)
                && value.Contains(":/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool ContainsNetworkMarker(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && NetworkMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

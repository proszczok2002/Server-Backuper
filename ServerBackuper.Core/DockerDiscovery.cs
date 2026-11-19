using System.Text.Json;

namespace ServerBackuper.Core;

public sealed record DockerMount(string Type, string Source, string Name);
public sealed record DockerContainer(
    string Id, string Name, bool Running, string ComposeWorkingDirectory,
    IReadOnlyList<DockerMount> Mounts, string Image = "", string ComposeProject = "");
public sealed record DockerVolumeCheck(string Name, bool IsNetwork, string Reason);

public sealed class DockerSelection
{
    public IReadOnlyList<DockerContainer> Containers { get; init; } = [];
    public IReadOnlyList<string> RunningContainerIds { get; init; } = [];
    public IReadOnlyList<string> VolumeNames { get; init; } = [];
}

public sealed class DockerDiscovery(IProcessRunner runner)
{
    public async Task<IReadOnlyList<DockerVolumeCheck>> CheckVolumesAsync(
        IEnumerable<string> volumeNames, CancellationToken cancellationToken)
    {
        var result = new List<DockerVolumeCheck>();
        foreach (var volumeName in volumeNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var inspect = await runner.RunAsync("docker.exe", ["volume", "inspect", volumeName], null,
                cancellationToken);
            if (inspect.ExitCode != 0)
            {
                result.Add(new(volumeName, true,
                    "nie udało się potwierdzić, że wolumen jest lokalny"));
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(inspect.StandardOutput);
                var root = document.RootElement[0];
                var driver = root.TryGetProperty("Driver", out var driverElement)
                    ? driverElement.GetString() ?? string.Empty : string.Empty;
                var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("Options", out var optionsElement)
                    && optionsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in optionsElement.EnumerateObject())
                        options[property.Name] = property.Value.ToString();
                }
                var network = NetworkStorageDetector.IsNetworkVolume(driver, options);
                result.Add(new(volumeName, network,
                    network ? "sterownik lub opcje wskazują na CIFS/SMB/NFS/SSHFS/WebDAV" : "wolumen lokalny"));
            }
            catch (Exception)
            {
                result.Add(new(volumeName, true,
                    "Docker zwrócił nieprawidłowe dane inspekcji; pominięto bezpiecznie"));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<DockerContainer>> GetAllAsync(CancellationToken cancellationToken)
    {
        var idsResult = await runner.RunAsync("docker.exe", ["ps", "-aq"], null, cancellationToken);
        EnsureSuccess(idsResult, "Nie można pobrać listy kontenerów.");
        var ids = idsResult.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var result = new List<DockerContainer>();
        foreach (var id in ids)
        {
            var inspect = await runner.RunAsync("docker.exe", ["inspect", id.Trim()], null, cancellationToken);
            EnsureSuccess(inspect, $"Nie można odczytać kontenera {id}.");
            result.Add(ParseContainer(inspect.StandardOutput));
        }
        return result;
    }

    public static DockerSelection SelectForSource(IReadOnlyList<DockerContainer> all, string sourceRoot)
    {
        var selected = all.Where(container => IsUnder(container.ComposeWorkingDirectory, sourceRoot)
            || container.Mounts.Any(mount => mount.Type.Equals("bind", StringComparison.OrdinalIgnoreCase)
                && IsUnder(mount.Source, sourceRoot))).ToList();

        var volumes = selected.SelectMany(c => c.Mounts)
            .Where(m => m.Type.Equals("volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

        var sharedUsers = all.Where(c => c.Mounts.Any(m => m.Type.Equals("volume", StringComparison.OrdinalIgnoreCase)
            && volumes.Contains(m.Name, StringComparer.OrdinalIgnoreCase))).ToList();
        selected = selected.Concat(sharedUsers).DistinctBy(c => c.Id).ToList();

        return new DockerSelection
        {
            Containers = selected,
            RunningContainerIds = selected.Where(c => c.Running).Select(c => c.Id).ToArray(),
            VolumeNames = volumes
        };
    }

    private static DockerContainer ParseContainer(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0];
        var id = root.GetProperty("Id").GetString() ?? string.Empty;
        var name = (root.GetProperty("Name").GetString() ?? string.Empty).TrimStart('/');
        var running = root.GetProperty("State").GetProperty("Running").GetBoolean();
        var workingDirectory = string.Empty;
        var projectName = string.Empty;
        if (root.GetProperty("Config").TryGetProperty("Labels", out var labels)
            && labels.ValueKind == JsonValueKind.Object)
        {
            if (labels.TryGetProperty("com.docker.compose.project.working_dir", out var working))
                workingDirectory = working.GetString() ?? string.Empty;
            if (labels.TryGetProperty("com.docker.compose.project", out var project))
                projectName = project.GetString() ?? string.Empty;
        }

        var mounts = new List<DockerMount>();
        foreach (var mount in root.GetProperty("Mounts").EnumerateArray())
        {
            var type = mount.GetProperty("Type").GetString() ?? string.Empty;
            var source = mount.TryGetProperty("Source", out var sourceElement)
                ? sourceElement.GetString() ?? string.Empty : string.Empty;
            var volumeName = mount.TryGetProperty("Name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty : string.Empty;
            mounts.Add(new DockerMount(type, source, volumeName));
        }
        var image = root.GetProperty("Config").TryGetProperty("Image", out var imageElement)
            ? imageElement.GetString() ?? string.Empty : string.Empty;
        return new DockerContainer(id, name, running, workingDirectory, mounts, image, projectName);
    }

    private static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException(message + " " + result.StandardError.Trim());
    }
}

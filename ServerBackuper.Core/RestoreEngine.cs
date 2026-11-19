using System.Text.Json;

namespace ServerBackuper.Core;

public sealed record RestorePreview(
    int FormatVersion,
    DateTime CreatedUtc,
    string OriginalSourceRoot,
    int ComposeFileCount,
    int ContainerCount,
    int VolumeCount,
    int DatabaseDumpCount);

public sealed class RestoreEngine(
    IProcessRunner runner,
    DockerDiscovery dockerDiscovery,
    TextLog log)
{
    private const string MetadataDirectory = ".serverbackuper-metadata";

    public async Task<RestorePreview> InspectAsync(string archivePath, string password,
        string sevenZipPath, CancellationToken token)
    {
        ValidateInputs(archivePath, password);
        var sevenZip = ResolveSevenZip(sevenZipPath);
        EnsureSuccess(await runner.RunAsync(sevenZip,
            ["t", "-bb1", "-bso1", "-bsp1", "-p" + password, archivePath], null, token),
            "Archiwum nie przeszło testu integralności albo hasło jest nieprawidłowe.");
        await ValidateArchivePathsAsync(sevenZip, archivePath, password, token);

        var manifestResult = await runner.RunAsync(sevenZip,
            ["e", "-so", "-p" + password, archivePath,
                MetadataDirectory + "/backup-manifest.json"], null, token);
        if (manifestResult.ExitCode != 0 || string.IsNullOrWhiteSpace(manifestResult.StandardOutput))
        {
            manifestResult = await runner.RunAsync(sevenZip,
                ["e", "-so", "-p" + password, archivePath, "backup-manifest.json"], null, token);
        }
        EnsureSuccess(manifestResult, "Archiwum nie zawiera poprawnego manifestu Server Backuper.");
        var manifest = ParseManifest(manifestResult.StandardOutput);
        return new RestorePreview(
            manifest.FormatVersion,
            manifest.CreatedUtc,
            manifest.SourceRoot,
            manifest.ComposeFiles.Count,
            manifest.Containers.Count,
            manifest.Volumes.Count,
            manifest.DatabaseDumps.Count);
    }

    public async Task<BackupResult> RunAsync(CommandRequest request, BackupSettings settings,
        IProgress<JobProgress> progress, Action<IReadOnlyList<string>> recoveryChanged,
        CancellationToken token)
    {
        var password = SecretProtector.Unprotect(request.RestorePasswordProtected);
        var preview = await InspectAsync(request.ArchivePath, password, settings.SevenZipPath, token);
        var targetRoot = Path.GetFullPath(request.RestoreTargetRoot).TrimEnd(Path.DirectorySeparatorChar);
        ValidateTarget(targetRoot);
        var parent = Path.GetDirectoryName(targetRoot)
            ?? throw new InvalidOperationException("Wybierz katalog docelowy, a nie główny katalog dysku.");
        Directory.CreateDirectory(parent);
        var stageRoot = targetRoot + ".serverbackuper-staging-" + Guid.NewGuid().ToString("N");
        var warnings = new List<string>();
        IReadOnlyList<string> previouslyRunning = [];
        var recoveryRequired = false;
        var targetCreated = false;

        try
        {
            progress.Report(new("Weryfikacja archiwum", 5,
                $"Poprawne archiwum z {preview.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}."));
            Directory.CreateDirectory(stageRoot);
            var sevenZip = ResolveSevenZip(settings.SevenZipPath);
            progress.Report(new("Rozpakowywanie", 15, "Rozpakowywanie backupu do katalogu tymczasowego."));
            EnsureSuccess(await runner.RunAsync(sevenZip,
                ["x", "-y", "-aoa", "-p" + password, "-o" + stageRoot, request.ArchivePath],
                null, token), "Nie udało się rozpakować archiwum.");

            var manifest = LoadExtractedManifest(stageRoot);
            var metadataRoot = Directory.Exists(Path.Combine(stageRoot, MetadataDirectory))
                ? Path.Combine(stageRoot, MetadataDirectory) : stageRoot;

            if (manifest.Volumes.Count > 0)
            {
                progress.Report(new("Przygotowanie Dockera", 45,
                    "Sprawdzanie kontenerów i wolumenów przed odtworzeniem."));
                EnsureSuccess(await runner.RunAsync("docker.exe", ["info"], null, token),
                    "Docker Engine nie odpowiada.");
                var containers = await dockerDiscovery.GetAllAsync(token);
                previouslyRunning = containers.Where(c => c.Running &&
                    (IsUnder(c.ComposeWorkingDirectory, targetRoot)
                     || c.Mounts.Any(m => m.Type.Equals("volume", StringComparison.OrdinalIgnoreCase)
                         && manifest.Volumes.Any(v => v.Name.Equals(m.Name,
                             StringComparison.OrdinalIgnoreCase)))))
                    .Select(c => c.Id).ToArray();
                if (previouslyRunning.Count > 0)
                {
                    recoveryChanged(previouslyRunning);
                    recoveryRequired = true;
                    var stopArgs = new List<string> { "stop", "--timeout",
                        settings.ContainerStopTimeoutSeconds.ToString() };
                    stopArgs.AddRange(previouslyRunning);
                    EnsureSuccess(await runner.RunAsync("docker.exe", stopArgs, null, token),
                        "Nie udało się zatrzymać kontenerów przed odtworzeniem.");
                }
                await RestoreVolumesAsync(manifest, metadataRoot, settings.HelperImage, progress, token);
            }

            progress.Report(new("Odtwarzanie plików", 70,
                $"Odtwarzanie danych do {targetRoot}."));
            RemoveMetadata(stageRoot);
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, false);
            Directory.Move(stageRoot, targetRoot);
            targetCreated = true;

            if (request.StartContainersAfterRestore)
            {
                await StartComposeProjectsAsync(manifest, targetRoot, progress, warnings, token);
                await FinishNextcloudRestoreAsync(targetRoot, progress, warnings, token);
            }
            if (previouslyRunning.Count > 0)
            {
                var startArgs = new List<string> { "start" };
                startArgs.AddRange(previouslyRunning);
                EnsureSuccess(await runner.RunAsync("docker.exe", startArgs, null, token),
                    "Nie udało się ponownie uruchomić wcześniej działających kontenerów.");
            }

            recoveryRequired = false;
            recoveryChanged([]);
            progress.Report(new("Przywracanie zakończone", 100,
                "Pliki, projekty Compose i wolumeny zostały odtworzone."));
            return new BackupResult
            {
                Success = true,
                Message = warnings.Count == 0
                    ? "Przywracanie zakończone sukcesem."
                    : "Przywracanie zakończone z ostrzeżeniami.",
                LocalArchivePath = request.ArchivePath,
                NasArchivePath = targetRoot,
                Warnings = warnings
            };
        }
        finally
        {
            if (recoveryRequired && previouslyRunning.Count > 0)
            {
                try
                {
                    var startArgs = new List<string> { "start" };
                    startArgs.AddRange(previouslyRunning);
                    await runner.RunAsync("docker.exe", startArgs, null, CancellationToken.None);
                    recoveryChanged([]);
                }
                catch (Exception ex) { log.Write("Nie udało się wznowić kontenerów po restore: " + ex.Message); }
            }
            if (!targetCreated && Directory.Exists(stageRoot))
            {
                try { Directory.Delete(stageRoot, true); }
                catch (Exception ex) { log.Write("Nie można usunąć katalogu staging restore: " + ex.Message); }
            }
        }
    }

    private async Task RestoreVolumesAsync(RestoreManifest manifest, string metadataRoot,
        string helperImage, IProgress<JobProgress> progress, CancellationToken token)
    {
        var image = await runner.RunAsync("docker.exe", ["image", "inspect", helperImage], null, token);
        if (image.ExitCode != 0)
            EnsureSuccess(await runner.RunAsync("docker.exe", ["pull", helperImage], null, token),
                "Nie udało się pobrać obrazu pomocniczego do odtwarzania wolumenów.");
        var volumesRoot = Path.Combine(metadataRoot, "DockerVolumes");
        for (var index = 0; index < manifest.Volumes.Count; index++)
        {
            var volume = manifest.Volumes[index];
            var archive = Path.Combine(volumesRoot, volume.File);
            if (!File.Exists(archive)) throw new FileNotFoundException(
                $"Brakuje eksportu wolumenu {volume.Name}.", archive);
            progress.Report(new("Odtwarzanie wolumenów", 48 + (int)(17d * (index + 1)
                / Math.Max(1, manifest.Volumes.Count)), $"Wolumen {volume.Name}."));
            EnsureSuccess(await runner.RunAsync("docker.exe",
                ["volume", "create", volume.Name], null, token),
                $"Nie udało się utworzyć wolumenu {volume.Name}.");
            var helperName = "server-backuper-restore-" + Guid.NewGuid().ToString("N")[..12];
            var script = $"rm -rf /target/* /target/.[!.]* /target/..?*; " +
                $"tar -xf '/backup/{volume.File}' -C /target";
            try
            {
                EnsureSuccess(await runner.RunAsync("docker.exe",
                    ["run", "--rm", "--name", helperName,
                        "--mount", $"type=volume,src={volume.Name},dst=/target",
                        "--mount", $"type=bind,src={volumesRoot},dst=/backup,readonly",
                        helperImage, "sh", "-c", script], null, token),
                    $"Nie udało się odtworzyć wolumenu {volume.Name}.");
            }
            catch
            {
                try { await runner.RunAsync("docker.exe", ["rm", "-f", helperName],
                    null, CancellationToken.None); } catch { }
                throw;
            }
        }
    }

    private async Task StartComposeProjectsAsync(RestoreManifest manifest, string targetRoot,
        IProgress<JobProgress> progress, List<string> warnings, CancellationToken token)
    {
        for (var index = 0; index < manifest.ComposeFiles.Count; index++)
        {
            var composePath = Path.GetFullPath(Path.Combine(targetRoot, manifest.ComposeFiles[index]));
            if (!IsUnder(composePath, targetRoot) || !File.Exists(composePath))
            {
                warnings.Add("Pominięto brakujący plik Compose: " + manifest.ComposeFiles[index]);
                continue;
            }
            progress.Report(new("Uruchamianie projektów", 88,
                $"Docker Compose {index + 1} z {manifest.ComposeFiles.Count}."));
            var result = await runner.RunAsync("docker.exe",
                ["compose", "-f", composePath, "up", "-d"], Path.GetDirectoryName(composePath), token);
            if (result.ExitCode != 0)
                warnings.Add("Nie udało się uruchomić projektu " + manifest.ComposeFiles[index]);
        }
    }

    private async Task FinishNextcloudRestoreAsync(string targetRoot,
        IProgress<JobProgress> progress, List<string> warnings, CancellationToken token)
    {
        IReadOnlyList<DockerContainer> containers;
        try { containers = await dockerDiscovery.GetAllAsync(token); }
        catch (Exception ex) { warnings.Add("Nie można sprawdzić Nextcloud po restore: " + ex.Message); return; }
        foreach (var container in containers.Where(c =>
            c.Image.Contains("nextcloud", StringComparison.OrdinalIgnoreCase)
            && IsUnder(c.ComposeWorkingDirectory, targetRoot)))
        {
            progress.Report(new("Finalizacja Nextcloud", 96,
                $"Aktualizacja znacznika danych w {container.Name}."));
            var completed = false;
            for (var attempt = 1; attempt <= 12 && !completed; attempt++)
            {
                var fingerprint = await runner.RunAsync("docker.exe",
                    ["exec", "--user", "www-data", container.Id, "php", "occ",
                        "maintenance:data-fingerprint"], null, token);
                if (fingerprint.ExitCode == 0)
                {
                    var maintenance = await runner.RunAsync("docker.exe",
                        ["exec", "--user", "www-data", container.Id, "php", "occ",
                            "maintenance:mode", "--off"], null, token);
                    completed = maintenance.ExitCode == 0;
                }
                if (!completed && attempt < 12) await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
            if (!completed)
                warnings.Add("Nextcloud nie był gotowy albo nie udało się wyłączyć maintenance mode w "
                    + container.Name);
        }
    }

    private static RestoreManifest LoadExtractedManifest(string stageRoot)
    {
        var modern = Path.Combine(stageRoot, MetadataDirectory, "backup-manifest.json");
        var legacy = Path.Combine(stageRoot, "backup-manifest.json");
        var path = File.Exists(modern) ? modern : legacy;
        if (!File.Exists(path)) throw new InvalidDataException("Brakuje manifestu backupu.");
        return ParseManifest(File.ReadAllText(path));
    }

    private static RestoreManifest ParseManifest(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<RestoreManifest>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Manifest jest pusty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Manifest backupu jest uszkodzony.", ex); }
    }

    private static void RemoveMetadata(string stageRoot)
    {
        var modern = Path.Combine(stageRoot, MetadataDirectory);
        if (Directory.Exists(modern)) Directory.Delete(modern, true);
        var legacyVolumes = Path.Combine(stageRoot, "DockerVolumes");
        if (Directory.Exists(legacyVolumes)) Directory.Delete(legacyVolumes, true);
        var legacyDumps = Path.Combine(stageRoot, "DatabaseDumps");
        if (Directory.Exists(legacyDumps)) Directory.Delete(legacyDumps, true);
        var legacyManifest = Path.Combine(stageRoot, "backup-manifest.json");
        if (File.Exists(legacyManifest)) File.Delete(legacyManifest);
    }

    private static void ValidateInputs(string archivePath, string password)
    {
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Nie znaleziono archiwum.", archivePath);
        if (string.IsNullOrWhiteSpace(password)) throw new InvalidDataException("Wpisz hasło archiwum.");
    }

    private async Task ValidateArchivePathsAsync(string sevenZip, string archivePath,
        string password, CancellationToken token)
    {
        var list = await runner.RunAsync(sevenZip,
            ["l", "-ba", "-slt", "-p" + password, archivePath], null, token);
        EnsureSuccess(list, "Nie można odczytać listy plików archiwum.");
        foreach (var line in list.StandardOutput.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Path = ", StringComparison.Ordinal)) continue;
            var entry = line[7..].Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(entry)
                || entry.Split(Path.DirectorySeparatorChar).Any(part => part == ".."))
                throw new InvalidDataException("Archiwum zawiera niebezpieczną ścieżkę: " + entry);
        }
    }

    private static void ValidateTarget(string targetRoot)
    {
        var root = Path.GetPathRoot(targetRoot)?.TrimEnd(Path.DirectorySeparatorChar);
        if (string.Equals(root, targetRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Nie można przywracać bezpośrednio do głównego katalogu dysku.");
        if (Directory.Exists(targetRoot)
            && Directory.EnumerateFileSystemEntries(targetRoot).Any())
            throw new InvalidOperationException("Katalog docelowy musi być pusty. To zabezpiecza istniejące dane.");
    }

    private static bool IsUnder(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var normalizedCandidate = Path.GetFullPath(candidate);
            return normalizedCandidate.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string ResolveSevenZip(string configured)
    {
        var candidates = new[]
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("Nie znaleziono programu 7-Zip.");
    }

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(message + " " + result.StandardError.Trim());
    }

    private sealed class RestoreManifest
    {
        public int FormatVersion { get; set; } = 1;
        public DateTime CreatedUtc { get; set; }
        public string SourceRoot { get; set; } = string.Empty;
        public List<string> ComposeFiles { get; set; } = [];
        public List<RestoreContainer> Containers { get; set; } = [];
        public List<RestoreVolume> Volumes { get; set; } = [];
        public List<string> DatabaseDumps { get; set; } = [];
    }

    private sealed class RestoreContainer
    {
        public string Name { get; set; } = string.Empty;
        public string Image { get; set; } = string.Empty;
    }

    private sealed class RestoreVolume
    {
        public string Name { get; set; } = string.Empty;
        public string File { get; set; } = string.Empty;
    }
}

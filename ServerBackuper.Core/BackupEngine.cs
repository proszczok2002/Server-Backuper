using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerBackuper.Core;

public sealed class BackupEngine(
    IProcessRunner runner,
    DockerDiscovery dockerDiscovery,
    TextLog log)
{
    public async Task<BackupResult> RunAsync(
        BackupSettings settings,
        IProgress<JobProgress> progress,
        Action<IReadOnlyList<string>> recoveryChanged,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        string localFinal = string.Empty;
        string nasFinal = string.Empty;
        string? workRoot = null;
        IReadOnlyList<string> runningIds = [];
        var maintenanceContainerIds = new List<string>();
        var recoveryRequired = false;

        try
        {
            progress.Report(new("Kontrola wstępna", 2, "Sprawdzanie konfiguracji i dostępu."));
            ValidateSettings(settings);
            var sevenZip = ResolveSevenZip(settings.SevenZipPath);
            var password = SecretProtector.Unprotect(settings.ArchivePasswordProtected);
            if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("Nie ustawiono hasła archiwum.");

            Directory.CreateDirectory(settings.LocalBackupRoot);
            if (settings.NasEnabled && settings.RequireNasBeforeBackup)
                await TestNasAsync(settings.NasBackupRoot, cancellationToken);
            var dockerInfo = await runner.RunAsync("docker.exe", ["info"], null, cancellationToken);
            EnsureSuccess(dockerInfo, "Docker Engine nie odpowiada.");

            progress.Report(new("Wykrywanie Dockera", 6, "Wykrywanie kontenerów i wolumenów."));
            var allContainers = await dockerDiscovery.GetAllAsync(cancellationToken);
            var selection = DockerDiscovery.SelectForSource(allContainers, settings.SourceRoot);
            runningIds = selection.RunningContainerIds;
            var skippedNetworkVolumes = Array.Empty<DockerVolumeCheck>();
            if (selection.VolumeNames.Count > 0)
            {
                var volumeChecks = await dockerDiscovery.CheckVolumesAsync(selection.VolumeNames, cancellationToken);
                skippedNetworkVolumes = volumeChecks.Where(volume => volume.IsNetwork).ToArray();
                if (skippedNetworkVolumes.Length > 0)
                {
                    var skippedNames = skippedNetworkVolumes.Select(volume => volume.Name).ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
                    selection = new DockerSelection
                    {
                        Containers = selection.Containers,
                        RunningContainerIds = selection.RunningContainerIds,
                        VolumeNames = selection.VolumeNames.Where(name => !skippedNames.Contains(name)).ToArray()
                    };
                    var message = "Pominięto sieciowe lub niezweryfikowane wolumeny Docker: "
                        + string.Join(", ", skippedNames.OrderBy(name => name));
                    warnings.Add(message);
                    progress.Report(new("Pomijanie zasobów sieciowych", 7, message));
                }
            }

            if (selection.VolumeNames.Count > 0)
            {
                var image = await runner.RunAsync("docker.exe", ["image", "inspect", settings.HelperImage], null, cancellationToken);
                if (image.ExitCode != 0)
                {
                    progress.Report(new("Przygotowanie obrazu", 8, $"Pobieranie {settings.HelperImage}."));
                    EnsureSuccess(await runner.RunAsync("docker.exe", ["pull", settings.HelperImage], null, cancellationToken),
                        "Nie udało się pobrać obrazu pomocniczego.");
                }
            }

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var baseName = $"Kopia serwera {timestamp}";
            localFinal = UniqueArchivePath(settings.LocalBackupRoot, baseName + ".7z");
            var localPartial = Path.Combine(settings.LocalBackupRoot,
                Path.GetFileNameWithoutExtension(localFinal) + "." + Guid.NewGuid().ToString("N") + ".partial.7z");
            workRoot = Path.Combine(settings.LocalBackupRoot, ".serverbackuper-work", Guid.NewGuid().ToString("N"));
            var metadataRoot = Path.Combine(workRoot, ".serverbackuper-metadata");
            var volumesRoot = Path.Combine(metadataRoot, "DockerVolumes");
            var databaseDumpsRoot = Path.Combine(metadataRoot, "DatabaseDumps");
            Directory.CreateDirectory(volumesRoot);
            Directory.CreateDirectory(databaseDumpsRoot);

            try
            {
                if (runningIds.Count > 0)
                {
                    recoveryChanged(runningIds);
                    recoveryRequired = true;
                }
                await EnableNextcloudMaintenanceAsync(selection.Containers, maintenanceContainerIds,
                    progress, cancellationToken);
                await ExportDatabaseDumpsAsync(selection.Containers, databaseDumpsRoot,
                    progress, cancellationToken);
                if (runningIds.Count > 0)
                {
                progress.Report(new("Zatrzymywanie kontenerów", 12, $"Zatrzymywanie {runningIds.Count} kontenerów."));
                    var stopArgs = new List<string> { "stop", "--timeout", settings.ContainerStopTimeoutSeconds.ToString() };
                stopArgs.AddRange(runningIds);
                EnsureSuccess(await runner.RunAsync("docker.exe", stopArgs, null, cancellationToken),
                    "Nie udało się zatrzymać kontenerów.");
                }

                await ExportVolumesAsync(selection.VolumeNames, settings.HelperImage, metadataRoot,
                    volumesRoot, progress, cancellationToken);
                await WriteManifestAsync(metadataRoot, settings.SourceRoot, selection, skippedNetworkVolumes,
                    cancellationToken);

                var performance = DetectPerformanceProfile();
                progress.Report(new("Tworzenie archiwum", 45,
                    "Pakowanie bez kompresji — szybkość zależy głównie od dysku."));
                log.Write($"Wykryta specyfikacja: {performance.LogicalProcessors} procesorów logicznych, "
                    + $"około {performance.AvailableMemoryBytes / 1024d / 1024d / 1024d:F1} GB pamięci dostępnej dla procesu. "
                    + "7-Zip: tryb bez kompresji -mx=0, metoda Copy; wydajność ogranicza głównie odczyt i zapis dysku.");
                var packingTimer = Stopwatch.StartNew();
                var common7z = SevenZipArguments(password);
                var sourceArgs = new List<string> { "a" };
                sourceArgs.AddRange(common7z);
                sourceArgs.Add(localPartial);
                sourceArgs.Add("-xr!$RECYCLE.BIN");
                sourceArgs.Add("-xr!System Volume Information");
                sourceArgs.Add("-xr!.serverbackuper-work");
                sourceArgs.Add("-snl");
                sourceArgs.Add(@".\*");
                EnsureSuccess(await runner.RunAsync(sevenZip, sourceArgs, settings.SourceRoot, cancellationToken),
                    "7-Zip nie utworzył archiwum źródła.");

                progress.Report(new("Dodawanie wolumenów", 64, "Dodawanie eksportów Docker volumes."));
                var metadataArgs = new List<string> { "a" };
                metadataArgs.AddRange(common7z);
                metadataArgs.Add(localPartial);
                metadataArgs.Add(".serverbackuper-metadata");
                EnsureSuccess(await runner.RunAsync(sevenZip, metadataArgs, workRoot, cancellationToken),
                    "7-Zip nie dodał eksportów wolumenów.");
                packingTimer.Stop();
                log.Write($"Pakowanie 7-Zip bez kompresji trwało {FormatDuration(packingTimer.Elapsed)}.");

                progress.Report(new("Test lokalny", 72, "Testowanie integralności lokalnego archiwum."));
                var localTestTimer = Stopwatch.StartNew();
                EnsureSuccess(await runner.RunAsync(sevenZip,
                    ["t", "-bb1", "-bso1", "-bsp1", "-p" + password, localPartial], null, cancellationToken),
                    "Lokalne archiwum nie przeszło testu integralności.");
                localTestTimer.Stop();
                log.Write($"Test lokalnego archiwum trwał {FormatDuration(localTestTimer.Elapsed)}.");
                File.Move(localPartial, localFinal);
            }
            finally
            {
                await RecoverAfterBackupAsync(runningIds, maintenanceContainerIds, recoveryRequired,
                    progress, recoveryChanged, warnings);
            }

            if (workRoot is not null)
            {
                try { Directory.Delete(workRoot, true); workRoot = null; }
                catch (Exception ex) { warnings.Add("Nie można od razu usunąć roboczych eksportów: " + ex.Message); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(new("Retencja lokalna", 80, "Usuwanie nadmiarowych lokalnych kopii."));
            ApplyRetentionForRoot("lokalna", settings.LocalBackupRoot, settings.Retention.KeepLocal,
                settings.Retention.PartialFileMaxAgeDays, warnings);

            if (settings.NasEnabled)
            {
                progress.Report(new("Kopiowanie na NAS", 82, "Kopiowanie sprawdzonego archiwum na NAS."));
                Directory.CreateDirectory(settings.NasBackupRoot);
                nasFinal = Path.Combine(settings.NasBackupRoot, Path.GetFileName(localFinal));
                var nasPartial = Path.Combine(settings.NasBackupRoot,
                    Path.GetFileNameWithoutExtension(localFinal) + "." + Guid.NewGuid().ToString("N") + ".partial.7z");
                var archiveSize = new FileInfo(localFinal).Length;
                var copyTimer = Stopwatch.StartNew();
                var localHash = await CopyFileAsync(localFinal, nasPartial, progress, cancellationToken);
                copyTimer.Stop();
                LogThroughput("Kopiowanie na NAS", archiveSize, copyTimer.Elapsed);

                progress.Report(new("Weryfikacja NAS", 92,
                    "Ponowny odczyt kopii z NAS i porównywanie sum SHA-256."));
                var verifyTimer = Stopwatch.StartNew();
                var nasHash = await ComputeSha256Async(nasPartial, cancellationToken);
                verifyTimer.Stop();
                LogThroughput("Weryfikacja SHA-256 NAS", archiveSize, verifyTimer.Elapsed);
                if (!CryptographicOperations.FixedTimeEquals(localHash, nasHash))
                    throw new InvalidDataException("Suma SHA-256 kopii NAS nie zgadza się z archiwum lokalnym.");
                File.Move(nasPartial, nasFinal);
                progress.Report(new("Kopia NAS gotowa", 96,
                    "Suma SHA-256 kopii NAS jest zgodna z archiwum lokalnym."));
                log.Write("Kopia NAS przeszła pełną weryfikację SHA-256 i otrzymała nazwę końcową.");

                progress.Report(new("Retencja NAS", 97, "Usuwanie nadmiarowych poprawnych kopii na NAS."));
                ApplyRetentionForRoot("NAS", settings.NasBackupRoot, settings.Retention.KeepNas,
                    settings.Retention.PartialFileMaxAgeDays, warnings);
            }
            else
            {
                progress.Report(new("NAS wyłączony", 97,
                    "Kopia na NAS została pominięta zgodnie z ustawieniami."));
            }

            var size = new FileInfo(localFinal).Length;
            var completionMessage = settings.NasEnabled
                ? "Backup lokalny jest sprawdzony, a kopia NAS została zapisana bez ponownej weryfikacji."
                : "Backup lokalny jest poprawny. Kopiowanie na NAS jest wyłączone.";
            progress.Report(new("Zakończono", 100, completionMessage));
            return new BackupResult
            {
                Success = true,
                Message = warnings.Count == 0 ? "Backup zakończony sukcesem." : "Backup zakończony z ostrzeżeniami.",
                LocalArchivePath = localFinal,
                NasArchivePath = nasFinal,
                ArchiveSizeBytes = size,
                Warnings = warnings
            };
        }
        finally
        {
            if (workRoot is not null && Directory.Exists(workRoot))
            {
                try { Directory.Delete(workRoot, true); }
                catch (Exception ex) { log.Write("Nie można usunąć katalogu roboczego: " + ex.Message); }
            }
        }
    }

    public async Task RecoverContainersAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0) return;
        var args = new List<string> { "start" };
        args.AddRange(ids);
        EnsureSuccess(await runner.RunAsync("docker.exe", args, null, cancellationToken),
            "Nie udało się odzyskać kontenerów po przerwanym backupie.");

        var containers = await dockerDiscovery.GetAllAsync(cancellationToken);
        foreach (var container in containers.Where(container =>
            ids.Contains(container.Id, StringComparer.OrdinalIgnoreCase)
            && container.Image.Contains("nextcloud", StringComparison.OrdinalIgnoreCase)))
        {
            var (disabled, error) = await DisableNextcloudMaintenanceAsync(container.Id, 30,
                cancellationToken);
            if (!disabled)
                throw new InvalidOperationException(
                    $"Kontener {container.Name} został uruchomiony, ale nie udało się wyłączyć maintenance mode. {error}");
        }
    }

    private async Task RecoverAfterBackupAsync(IReadOnlyList<string> runningIds,
        IReadOnlyList<string> maintenanceContainerIds, bool recoveryRequired,
        IProgress<JobProgress> progress, Action<IReadOnlyList<string>> recoveryChanged,
        List<string> warnings)
    {
        var failedStarts = new List<string>();
        var failedMaintenance = new List<string>();
        if (recoveryRequired)
        {
            progress.Report(new("Bezpieczne kończenie", 78,
                "Uruchamianie kontenerów działających przed backupem. Anulowanie już tego nie przerwie."));
            foreach (var containerId in runningIds)
            {
                var started = false;
                for (var attempt = 1; attempt <= 3 && !started; attempt++)
                {
                    try
                    {
                        var result = await runner.RunAsync("docker.exe", ["start", containerId], null,
                            CancellationToken.None);
                        started = result.ExitCode == 0;
                    }
                    catch (Exception ex)
                    {
                        log.Write($"Próba {attempt} uruchomienia kontenera {containerId} nie powiodła się: {ex.Message}");
                    }
                    if (!started && attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                if (!started) failedStarts.Add(containerId);
            }
            if (failedStarts.Count > 0)
                warnings.Add("Nie udało się uruchomić kontenerów: " + string.Join(", ", failedStarts));
        }

        foreach (var containerId in maintenanceContainerIds)
        {
            progress.Report(new("Wyłączanie maintenance Nextcloud", 79,
                "Czekanie na gotowość Nextclouda i wyłączanie trybu konserwacji."));
            var (disabled, lastError) = await DisableNextcloudMaintenanceAsync(containerId, 30,
                CancellationToken.None);
            if (!disabled)
            {
                failedMaintenance.Add(containerId);
                warnings.Add("Nie udało się wyłączyć maintenance mode Nextcloud po ponowieniu prób. " + lastError);
                log.Write("Nextcloud pozostał w maintenance mode dla kontenera " + containerId + ". " + lastError);
            }
            else
            {
                progress.Report(new("Nextcloud uruchomiony", 80,
                    "Tryb konserwacji Nextclouda został wyłączony."));
            }
        }
        recoveryChanged(failedStarts.Concat(failedMaintenance)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private async Task<(bool Disabled, string LastError)> DisableNextcloudMaintenanceAsync(
        string containerId, int attempts, CancellationToken token)
    {
        string lastError = string.Empty;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var result = await runner.RunAsync("docker.exe",
                    ["exec", "--user", "www-data", containerId, "php", "occ", "maintenance:mode", "--off"],
                    null, token);
                if (result.ExitCode == 0) return (true, string.Empty);
                lastError = result.StandardError.Trim();
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                lastError = ex.Message;
            }
            if (attempt < attempts) await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        return (false, lastError);
    }

    public static async Task TestNasAsync(string nasRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nasRoot)) throw new InvalidOperationException("Nie wpisano ścieżki NAS.");
        Directory.CreateDirectory(nasRoot);
        var probe = Path.Combine(nasRoot, $".serverbackuper-test-{Guid.NewGuid():N}.tmp");
        const string content = "ServerBackuper test";
        try
        {
            await File.WriteAllTextAsync(probe, content, cancellationToken);
            var readBack = await File.ReadAllTextAsync(probe, cancellationToken);
            if (readBack != content) throw new IOException("Odczyt pliku testowego zwrócił nieprawidłową zawartość.");
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe);
        }
    }

    private async Task EnableNextcloudMaintenanceAsync(IReadOnlyList<DockerContainer> containers,
        List<string> enabledContainerIds, IProgress<JobProgress> progress, CancellationToken token)
    {
        foreach (var container in containers.Where(c => c.Running
            && c.Image.Contains("nextcloud", StringComparison.OrdinalIgnoreCase)))
        {
            progress.Report(new("Nextcloud maintenance", 9,
                $"Włączanie maintenance mode w kontenerze {container.Name}."));
            var result = await runner.RunAsync("docker.exe",
                ["exec", "--user", "www-data", container.Id, "php", "occ", "maintenance:mode", "--on"],
                null, token);
            EnsureSuccess(result, $"Nie udało się włączyć maintenance mode w {container.Name}.");
            enabledContainerIds.Add(container.Id);
        }
    }

    private async Task ExportDatabaseDumpsAsync(IReadOnlyList<DockerContainer> containers,
        string dumpsRoot, IProgress<JobProgress> progress, CancellationToken token)
    {
        foreach (var container in containers.Where(c => c.Running))
        {
            string? script = null;
            if (container.Image.Contains("mariadb", StringComparison.OrdinalIgnoreCase))
            {
                script = """
                    db="${MYSQL_DATABASE:-${MARIADB_DATABASE:-nextcloud}}";
                    user="${MYSQL_USER:-${MARIADB_USER:-root}}";
                    pass="${MYSQL_PASSWORD:-${MARIADB_PASSWORD:-${MYSQL_ROOT_PASSWORD:-${MARIADB_ROOT_PASSWORD:-}}}}";
                    exec mariadb-dump --single-transaction --quick --default-character-set=utf8mb4 -u"$user" ${pass:+-p"$pass"} "$db"
                    """;
            }
            else if (container.Image.Contains("mysql", StringComparison.OrdinalIgnoreCase))
            {
                script = """
                    db="${MYSQL_DATABASE:-nextcloud}"; user="${MYSQL_USER:-root}";
                    pass="${MYSQL_PASSWORD:-${MYSQL_ROOT_PASSWORD:-}}";
                    exec mysqldump --single-transaction --quick --default-character-set=utf8mb4 -u"$user" ${pass:+-p"$pass"} "$db"
                    """;
            }
            else if (container.Image.Contains("postgres", StringComparison.OrdinalIgnoreCase))
            {
                script = """
                    db="${POSTGRES_DB:-${POSTGRES_USER:-postgres}}";
                    exec pg_dump -U "${POSTGRES_USER:-postgres}" "$db"
                    """;
            }
            if (script is null) continue;

            progress.Report(new("Zrzut bazy danych", 10,
                $"Tworzenie logicznego zrzutu bazy z kontenera {container.Name}."));
            var outputPath = Path.Combine(dumpsRoot, SafeName(container.Name) + ".sql");
            var result = await runner.RunToFileAsync("docker.exe",
                ["exec", container.Id, "sh", "-c", script], outputPath, null, token);
            EnsureSuccess(result, $"Nie udało się wykonać zrzutu bazy {container.Name}.");
            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                throw new InvalidDataException($"Zrzut bazy {container.Name} jest pusty.");
        }
    }

    private async Task ExportVolumesAsync(IReadOnlyList<string> volumes, string helperImage,
        string workRoot, string volumesRoot, IProgress<JobProgress> progress, CancellationToken token)
    {
        for (var index = 0; index < volumes.Count; index++)
        {
            var volume = volumes[index];
            var tarName = SafeVolumeFileName(volume);
            var helperName = "server-backuper-" + Guid.NewGuid().ToString("N")[..12];
            progress.Report(new("Eksport wolumenów", 18 + (int)(22d * (index + 1) / Math.Max(1, volumes.Count)),
                $"Eksportowanie wolumenu {index + 1} z {volumes.Count}: {volume}"));
            var args = new[]
            {
                "run", "--rm", "--name", helperName,
                "--mount", $"type=volume,src={volume},dst=/source,readonly",
                "--mount", $"type=bind,src={workRoot},dst=/backup",
                helperImage, "sh", "-c", $"tar -cf '/backup/DockerVolumes/{tarName}' -C /source ."
            };
            try
            {
                EnsureSuccess(await runner.RunAsync("docker.exe", args, null, token),
                    $"Nie udało się wyeksportować wolumenu {volume}.");
            }
            catch
            {
                try { await runner.RunAsync("docker.exe", ["rm", "-f", helperName], null, CancellationToken.None); }
                catch { }
                throw;
            }
            if (!File.Exists(Path.Combine(volumesRoot, tarName)))
                throw new FileNotFoundException("Brakuje eksportu wolumenu.", tarName);
        }
    }

    private static async Task WriteManifestAsync(string workRoot, string sourceRoot,
        DockerSelection selection, IReadOnlyList<DockerVolumeCheck> skippedNetworkVolumes,
        CancellationToken token)
    {
        var manifest = new
        {
            formatVersion = 2,
            createdUtc = DateTime.UtcNow,
            computer = Environment.MachineName,
            sourceRoot,
            composeFiles = DiscoverComposeFiles(sourceRoot),
            containers = selection.Containers.Select(c => new
            {
                c.Id, c.Name, c.Running, c.Image, c.ComposeProject, c.ComposeWorkingDirectory,
                mounts = c.Mounts
            }),
            volumes = selection.VolumeNames.Select(name => new { name, file = SafeVolumeFileName(name) }),
            skippedNetworkVolumes = skippedNetworkVolumes.Select(volume => new { volume.Name, volume.Reason }),
            databaseDumps = Directory.Exists(Path.Combine(workRoot, "DatabaseDumps"))
                ? Directory.GetFiles(Path.Combine(workRoot, "DatabaseDumps"), "*.sql")
                    .Select(Path.GetFileName).ToArray()
                : []
        };
        await File.WriteAllTextAsync(Path.Combine(workRoot, "backup-manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static string[] DiscoverComposeFiles(string sourceRoot)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
            };
            return Directory.EnumerateFiles(sourceRoot, "*.*", options)
                .Where(path =>
                {
                    var name = Path.GetFileName(path);
                    return name.Equals("docker-compose.yml", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("docker-compose.yaml", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("compose.yml", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("compose.yaml", StringComparison.OrdinalIgnoreCase);
                })
                .Select(path => Path.GetRelativePath(sourceRoot, path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch { return []; }
    }

    private static string SafeVolumeFileName(string name)
    {
        var safe = SafeName(name);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return $"{safe}-{hash}.tar";
    }

    private static string SafeName(string name)
    {
        var safe = name;
        foreach (var character in Path.GetInvalidFileNameChars()) safe = safe.Replace(character, '_');
        return string.IsNullOrWhiteSpace(safe) ? "bez-nazwy" : safe;
    }

    private static IReadOnlyList<string> SevenZipArguments(string password) =>
        ["-t7z", "-mx=0", "-m0=Copy", "-mhe=on", "-p" + password, "-bb1"];

    private static PerformanceProfile DetectPerformanceProfile()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return new PerformanceProfile(Math.Max(1, Environment.ProcessorCount), Math.Max(0, memory));
    }

    private static void ValidateSettings(BackupSettings settings)
    {
        if (NetworkStorageDetector.IsNetworkPath(settings.SourceRoot))
            throw new InvalidOperationException(
                "Katalog źródłowy jest zasobem sieciowym albo mapowanym dyskiem sieciowym. Backup został zatrzymany przed zmianą stanu kontenerów.");
        if (!Directory.Exists(settings.SourceRoot)) throw new DirectoryNotFoundException($"Brak źródła: {settings.SourceRoot}");
        if (settings.Retention.KeepLocal < 1)
            throw new InvalidOperationException("Retencja musi zachowywać co najmniej jedną kopię lokalną.");
        if (settings.NasEnabled && settings.Retention.KeepNas < 1)
            throw new InvalidOperationException("Retencja musi zachowywać co najmniej jedną kopię na NAS.");
        if (settings.NasEnabled && string.IsNullOrWhiteSpace(settings.NasBackupRoot))
            throw new InvalidOperationException("Nie wpisano ścieżki NAS.");
        var localPath = Path.GetFullPath(settings.LocalBackupRoot).TrimEnd(Path.DirectorySeparatorChar);
        var sourcePath = Path.GetFullPath(settings.SourceRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (settings.NasEnabled)
        {
            var nasPath = Path.GetFullPath(settings.NasBackupRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (localPath.Equals(nasPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Katalog lokalny i NAS nie mogą być tym samym katalogiem.");
        }
        if (localPath.Equals(sourcePath, StringComparison.OrdinalIgnoreCase)
            || localPath.StartsWith(sourcePath + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Katalog lokalnych kopii nie może znajdować się wewnątrz źródła.");
    }

    private static string ResolveSevenZip(string configured)
    {
        var candidates = new[]
        {
            configured,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "7-Zip", "7z.exe")
        };
        var found = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        if (found is null) throw new FileNotFoundException("Nie znaleziono 7-Zip. Wskaż plik 7z.exe w ustawieniach.");
        return found;
    }

    private static string UniqueArchivePath(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path)) return path;
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}-{Guid.NewGuid():N}.7z");
    }

    private static async Task<byte[]> CopyFileAsync(string source, string destination,
        IProgress<JobProgress> progress, CancellationToken token)
    {
        const int bufferSize = 8 * 1024 * 1024;
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[bufferSize];
        var totalBytes = input.Length;
        long copiedBytes = 0;
        var lastFilePercent = -1;
        var timer = Stopwatch.StartNew();
        var lastReportAt = TimeSpan.Zero;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            sourceHash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copiedBytes += read;
            var filePercent = totalBytes == 0 ? 100 : (int)(copiedBytes * 100 / totalBytes);
            if (filePercent == lastFilePercent && timer.Elapsed - lastReportAt < TimeSpan.FromSeconds(2)) continue;
            lastFilePercent = filePercent;
            lastReportAt = timer.Elapsed;
            var overallPercent = 82 + filePercent * 9 / 100;
            var copiedGb = copiedBytes / 1024d / 1024d / 1024d;
            var totalGb = totalBytes / 1024d / 1024d / 1024d;
            var megabytesPerSecond = copiedBytes / 1024d / 1024d / Math.Max(timer.Elapsed.TotalSeconds, 0.001);
            var secondsRemaining = copiedBytes == 0
                ? 0
                : (totalBytes - copiedBytes) / Math.Max(copiedBytes / timer.Elapsed.TotalSeconds, 1);
            var eta = TimeSpan.FromSeconds(Math.Max(0, secondsRemaining));
            progress.Report(new JobProgress("Kopiowanie na NAS", overallPercent,
                $"Skopiowano {filePercent}% ({copiedGb:F2} z {totalGb:F2} GB), "
                + $"{megabytesPerSecond:F1} MB/s, pozostało około {FormatDuration(eta)}."));
        }
        if (totalBytes == 0)
            progress.Report(new JobProgress("Kopiowanie na NAS", 91, "Skopiowano 100% pustego pliku."));
        await output.FlushAsync(token);
        return sourceHash.GetHashAndReset();
    }

    private static async Task<byte[]> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await SHA256.HashDataAsync(stream, token);
    }

    private void ApplyRetentionForRoot(string locationName, string directory, int keepCount,
        int partialFileMaxAgeDays, List<string> warnings)
    {
        try
        {
            var deletedPartials = RetentionManager.DeleteOldPartialFiles(directory,
                partialFileMaxAgeDays, DateTime.UtcNow);
            var deletedArchives = RetentionManager.Apply(directory, keepCount);
            log.Write($"Retencja {locationName}: zachowano {keepCount}, usunięto "
                + $"{deletedArchives.Count} archiwów i {deletedPartials.Count} starych plików częściowych.");
            foreach (var path in deletedArchives.Concat(deletedPartials))
                log.Write($"Retencja {locationName} usunęła: {path}");
        }
        catch (Exception ex)
        {
            warnings.Add($"Backup jest poprawny, ale retencja {locationName} zgłosiła błąd: {ex.Message}");
        }
    }

    private void LogThroughput(string stage, long bytes, TimeSpan elapsed)
    {
        var megabytesPerSecond = bytes / 1024d / 1024d / Math.Max(elapsed.TotalSeconds, 0.001);
        log.Write($"{stage}: {bytes / 1024d / 1024d / 1024d:F2} GB w {FormatDuration(elapsed)}, "
            + $"średnio {megabytesPerSecond:F1} MB/s.");
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes}:{duration.Seconds:00}";

    private sealed record PerformanceProfile(int LogicalProcessors, long AvailableMemoryBytes);

    private static void EnsureSuccess(ProcessResult result, string message)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException(message + " " + result.StandardError.Trim());
    }
}

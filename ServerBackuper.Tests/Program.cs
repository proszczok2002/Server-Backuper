using ServerBackuper.Core;
using System.Diagnostics;

var tests = new (string Name, Action Test)[]
{
    ("Domyślna konfiguracja", TestDefaults),
    ("Stare ustawienia zachowują włączony NAS", TestLegacySettingsEnableNas),
    ("Backup może działać wyłącznie lokalnie", TestLocalOnlyBackup),
    ("Backup NAS kopiuje i porównuje SHA-256", TestNasBackupRoundTrip),
    ("Harmonogram co 48 godzin", TestSchedule),
    ("Retencja zachowuje najnowsze kopie", TestRetention),
    ("Pliki partial nie podlegają zwykłej retencji", TestPartialRetention),
    ("Atomowy magazyn JSON", TestJsonStore),
    ("Zapis stanu czeka na zwolnienie pliku", TestJsonStoreLockedDestination),
    ("Ochrona sekretów DPAPI", TestSecrets),
    ("Test NAS zapisuje, odczytuje i sprząta plik", TestNasProbe),
    ("Przywracanie odtwarza pliki i usuwa metadane techniczne", TestRestoreRoundTrip),
    ("Wykrywanie projektu działa po zmianie lokalizacji", TestMigratedProjectSelection),
    ("Wybór kontenerów i współdzielonych wolumenów", TestDockerSelection)
    ,("Rozpoznawanie zasobów sieciowych", TestNetworkStorageDetection)
    ,("Sieciowe źródło jest blokowane przed Dockerem", TestNetworkSourcePreflight)
    ,("Anulowanie uruchamia kontenery i wyłącza maintenance", TestCancellationRecovery)
};

var failures = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS: " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL: " + name + " — " + ex.Message); }
}
Console.WriteLine($"Wynik: {tests.Length - failures}/{tests.Length} testów zakończonych sukcesem.");
return failures == 0 ? 0 : 1;

static void TestDefaults()
{
    var value = new BackupSettings();
    Equal(48, value.Schedule.IntervalHours);
    Equal(2, value.Retention.KeepLocal);
    Equal(3, value.Retention.KeepNas);
    True(value.NasEnabled, "NAS powinien być domyślnie włączony dla zgodności ze starszą konfiguracją.");
    Equal(5, ScheduleCalculator.ParseFirstRun(value.Schedule).Hour);
}

static void TestLegacySettingsEnableNas()
{
    WithTempDirectory(root =>
    {
        var path = Path.Combine(root, "settings.json");
        File.WriteAllText(path, "{\"NasBackupRoot\":\"\\\\nas\\\\backup\"}");
        var loaded = new JsonFileStore().LoadOrCreate(path, () => new BackupSettings());
        True(loaded.NasEnabled, "Brak nowego pola w starszym JSON-ie wyłączył NAS.");
    });
}

static void TestLocalOnlyBackup()
{
    WithTempDirectory(root =>
    {
        var source = Path.Combine(root, "source");
        var local = Path.Combine(root, "backup");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "dane.txt"), "test");
        var sevenZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "7-Zip", "7z.exe");
        True(File.Exists(sevenZip), "Brak 7-Zip do testu trybu lokalnego.");

        var runner = new ScriptedRunner((arguments, _) =>
        {
            var args = arguments.ToArray();
            if (args.SequenceEqual(["ps", "-aq"])) return new(0, string.Empty, string.Empty);
            if (args.FirstOrDefault() == "a")
            {
                var archive = args.FirstOrDefault(argument =>
                    argument.EndsWith(".partial.7z", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(archive) && !File.Exists(archive))
                    File.WriteAllText(archive, "testowe archiwum");
            }
            return new(0, string.Empty, string.Empty);
        });
        var paths = new AppPaths(Path.Combine(root, "appdata"));
        paths.EnsureCreated();
        var engine = new BackupEngine(runner, new DockerDiscovery(runner), new TextLog(paths));
        var settings = new BackupSettings
        {
            SourceRoot = source,
            LocalBackupRoot = local,
            NasBackupRoot = string.Empty,
            NasEnabled = false,
            SevenZipPath = sevenZip,
            ArchivePasswordProtected = SecretProtector.Protect("testowe-haslo-123")
        };

        var result = engine.RunAsync(settings, new Progress<JobProgress>(), _ => { }, CancellationToken.None)
            .GetAwaiter().GetResult();
        True(result.Success, "Backup lokalny nie zakończył się powodzeniem.");
        Equal(string.Empty, result.NasArchivePath);
        Equal(1, Directory.GetFiles(local, "Kopia serwera *.7z").Length);
        True(runner.Calls.Any(call => call.Arguments.Contains("-mx=0")),
            "Tryb bez kompresji nie przekazał parametru -mx=0 do 7-Zip.");
        True(runner.Calls.Any(call => call.Arguments.Contains("-m0=Copy")),
            "Tryb bez kompresji nie przekazał metody Copy do 7-Zip.");
    });
}

static void TestNasBackupRoundTrip()
{
    WithTempDirectory(root =>
    {
        var source = Path.Combine(root, "source");
        var local = Path.Combine(root, "local");
        var nas = Path.Combine(root, "nas");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "dane.txt"), "test");
        var sevenZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "7-Zip", "7z.exe");
        True(File.Exists(sevenZip), "Brak 7-Zip do testu NAS.");

        var runner = new ScriptedRunner((arguments, _) =>
        {
            var args = arguments.ToArray();
            if (args.SequenceEqual(["ps", "-aq"])) return new(0, string.Empty, string.Empty);
            if (args.FirstOrDefault() == "a")
            {
                var archive = args.FirstOrDefault(argument =>
                    argument.EndsWith(".partial.7z", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(archive) && !File.Exists(archive))
                    File.WriteAllBytes(archive, Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray());
            }
            return new(0, string.Empty, string.Empty);
        });
        var paths = new AppPaths(Path.Combine(root, "appdata"));
        paths.EnsureCreated();
        var engine = new BackupEngine(runner, new DockerDiscovery(runner), new TextLog(paths));
        var settings = new BackupSettings
        {
            SourceRoot = source,
            LocalBackupRoot = local,
            NasBackupRoot = nas,
            NasEnabled = true,
            RequireNasBeforeBackup = false,
            SevenZipPath = sevenZip,
            ArchivePasswordProtected = SecretProtector.Protect("testowe-haslo-123")
        };

        var result = engine.RunAsync(settings, new Progress<JobProgress>(), _ => { }, CancellationToken.None)
            .GetAwaiter().GetResult();
        True(result.Success, "Backup z NAS nie zakończył się powodzeniem.");
        True(File.Exists(result.LocalArchivePath), "Brak archiwum lokalnego.");
        True(File.Exists(result.NasArchivePath), "Brak archiwum na NAS.");
        True(File.ReadAllBytes(result.LocalArchivePath).SequenceEqual(File.ReadAllBytes(result.NasArchivePath)),
            "Kopia NAS różni się od archiwum lokalnego.");
        var logText = File.ReadAllText(paths.LogFile);
        True(logText.Contains("Wykryta specyfikacja", StringComparison.Ordinal),
            "Brak wykrytej specyfikacji w logu.");
        True(logText.Contains("pełną weryfikację SHA-256", StringComparison.Ordinal),
            "Brak potwierdzenia weryfikacji SHA-256 NAS w logu.");
    });
}

static void TestSchedule()
{
    var schedule = new ScheduleSettings
    {
        Enabled = true,
        IntervalHours = 48,
        FirstRunLocal = "2026-08-22T05:00:00"
    };
    Equal(null, ScheduleCalculator.GetLatestDue(schedule, new DateTime(2026, 8, 22, 4, 59, 0)));
    Equal(new DateTime(2026, 8, 22, 5, 0, 0),
        ScheduleCalculator.GetLatestDue(schedule, new DateTime(2026, 8, 22, 5, 1, 0)));
    Equal(new DateTime(2026, 8, 24, 5, 0, 0),
        ScheduleCalculator.GetNext(schedule, new DateTime(2026, 8, 22, 5, 1, 0)));
}

static void TestRetention()
{
    WithTempDirectory(root =>
    {
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(root, $"Kopia serwera 2026-08-{10 + i:00}_05-00-00.7z");
            File.WriteAllText(path, i.ToString());
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 10 + i, 5, 0, 0, DateTimeKind.Utc));
        }
        var selected = RetentionManager.SelectArchivesToDelete(root, 2);
        Equal(3, selected.Count);
        RetentionManager.Apply(root, 2);
        Equal(2, Directory.GetFiles(root, "Kopia serwera *.7z").Length);
    });
}

static void TestPartialRetention()
{
    WithTempDirectory(root =>
    {
        File.WriteAllText(Path.Combine(root, "Kopia serwera 2026-08-10_05-00-00.partial.7z"), "partial");
        File.WriteAllText(Path.Combine(root, "Kopia serwera 2026-08-11_05-00-00.7z"), "ok");
        Equal(0, RetentionManager.SelectArchivesToDelete(root, 1).Count);
    });
}

static void TestJsonStore()
{
    WithTempDirectory(root =>
    {
        var path = Path.Combine(root, "settings.json");
        var store = new JsonFileStore();
        store.Save(path, new RetentionSettings { KeepLocal = 2, KeepNas = 3 });
        var loaded = store.LoadOrCreate(path, () => new RetentionSettings());
        Equal(2, loaded.KeepLocal);
        Equal(3, loaded.KeepNas);
        True(!File.Exists(path + ".tmp"), "Pozostał plik tymczasowy JSON.");
    });
}

static void TestJsonStoreLockedDestination()
{
    WithTempDirectory(root =>
    {
        var path = Path.Combine(root, "state.json");
        var store = new JsonFileStore();
        store.Save(path, new RetentionSettings { KeepLocal = 1, KeepNas = 1 });

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var save = Task.Run(() =>
            store.Save(path, new RetentionSettings { KeepLocal = 4, KeepNas = 5 }));
        Thread.Sleep(150);
        True(!save.IsCompleted, "Zapis nie zaczekał na zwolnienie pliku stanu.");
        reader.Dispose();
        True(save.Wait(TimeSpan.FromSeconds(5)), "Zapis nie zakończył się po zwolnieniu pliku stanu.");

        var loaded = store.LoadOrCreate(path, () => new RetentionSettings());
        Equal(4, loaded.KeepLocal);
        Equal(5, loaded.KeepNas);
        Equal(0, Directory.GetFiles(root, "*.tmp").Length);
    });
}

static void TestSecrets()
{
    const string secret = "testowe-haslo-123456";
    var encrypted = SecretProtector.Protect(secret);
    True(encrypted != secret, "Sekret nie został zaszyfrowany.");
    Equal(secret, SecretProtector.Unprotect(encrypted));
}

static void TestNasProbe()
{
    WithTempDirectory(root =>
    {
        BackupEngine.TestNasAsync(root, CancellationToken.None).GetAwaiter().GetResult();
        Equal(0, Directory.GetFiles(root, ".serverbackuper-test-*.tmp").Length);
    });
}

static void TestRestoreRoundTrip()
{
    WithTempDirectory(root =>
    {
        const string password = "testowe-haslo-restore-123";
        var sevenZip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "7-Zip", "7z.exe");
        True(File.Exists(sevenZip), "Brak 7-Zip do testu restore.");
        var payload = Path.Combine(root, "payload");
        var metadata = Path.Combine(payload, ".serverbackuper-metadata");
        Directory.CreateDirectory(Path.Combine(payload, "nextcloud"));
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(payload, "nextcloud", "dane.txt"), "ważne dane");
        var manifest = new
        {
            formatVersion = 2,
            createdUtc = DateTime.UtcNow,
            sourceRoot = @"K:\",
            composeFiles = Array.Empty<string>(),
            containers = Array.Empty<object>(),
            volumes = Array.Empty<object>(),
            databaseDumps = Array.Empty<string>()
        };
        File.WriteAllText(Path.Combine(metadata, "backup-manifest.json"),
            System.Text.Json.JsonSerializer.Serialize(manifest));
        var archive = Path.Combine(root, "test.7z");
        RunProcess(sevenZip, ["a", "-t7z", "-mx=1", "-mhe=on", "-p" + password,
            archive, @".\*"], payload);

        var paths = new AppPaths(Path.Combine(root, "appdata"));
        paths.EnsureCreated();
        var runner = new ProcessRunner(new TextLog(paths));
        var engine = new RestoreEngine(runner, new DockerDiscovery(runner), new TextLog(paths));
        var target = Path.Combine(root, "restore", "serwer");
        var request = new CommandRequest
        {
            Type = CommandType.RestoreBackup,
            ArchivePath = archive,
            RestoreTargetRoot = target,
            RestorePasswordProtected = SecretProtector.Protect(password),
            StartContainersAfterRestore = false
        };
        var settings = new BackupSettings { SevenZipPath = sevenZip };
        var result = engine.RunAsync(request, settings, new Progress<JobProgress>(), _ => { },
            CancellationToken.None).GetAwaiter().GetResult();
        True(result.Success, "Restore nie zwrócił sukcesu.");
        Equal("ważne dane", File.ReadAllText(Path.Combine(target, "nextcloud", "dane.txt")));
        True(!Directory.Exists(Path.Combine(target, ".serverbackuper-metadata")),
            "Metadane techniczne trafiły do katalogu docelowego.");
    });
}

static void TestDockerSelection()
{
    var inside = new DockerContainer("1", "inside", true, @"C:\Kontenery\projekt",
        [new DockerMount("volume", "/var/lib/docker/volumes/data", "data")]);
    var shared = new DockerContainer("2", "shared", true, @"D:\Inne",
        [new DockerMount("volume", "/var/lib/docker/volumes/data", "data")]);
    var unrelated = new DockerContainer("3", "other", true, @"D:\Inne",
        [new DockerMount("volume", "/var/lib/docker/volumes/other", "other")]);
    var result = DockerDiscovery.SelectForSource([inside, shared, unrelated], @"C:\Kontenery");
    Equal(2, result.Containers.Count);
    Equal(2, result.RunningContainerIds.Count);
    Equal("data", result.VolumeNames.Single());
}

static void TestMigratedProjectSelection()
{
    var app = new DockerContainer("1", "nextcloud-app", true, @"M:\Serwer\nextcloud",
        [new DockerMount("bind", @"M:\Serwer\nextcloud\nextcloud_data", "")],
        "nextcloud:latest", "nextcloud");
    var db = new DockerContainer("2", "nextcloud-db", true, @"M:\Serwer\nextcloud",
        [new DockerMount("bind", @"M:\Serwer\nextcloud\db_data", "")],
        "mariadb:10.6", "nextcloud");
    var tunnel = new DockerContainer("3", "nextcloud-tunnel", true, @"M:\Serwer\nextcloud",
        [], "cloudflare/cloudflared:latest", "nextcloud");
    var outside = new DockerContainer("4", "outside", true, @"D:\Inne", [],
        "nginx:latest", "outside");
    var result = DockerDiscovery.SelectForSource([app, db, tunnel, outside], @"M:\Serwer");
    Equal(3, result.Containers.Count);
    Equal(3, result.RunningContainerIds.Count);
}

static void TestNetworkStorageDetection()
{
    True(NetworkStorageDetector.IsNetworkPath(@"\\192.168.50.20\Homes\uzytkownik"),
        "Ścieżka UNC nie została rozpoznana jako sieciowa.");
    True(NetworkStorageDetector.IsNetworkVolume("local", new Dictionary<string, string>
    {
        ["type"] = "cifs", ["device"] = "//192.168.50.20/Audiobooks"
    }), "Wolumen CIFS nie został rozpoznany jako sieciowy.");
    True(NetworkStorageDetector.IsNetworkVolume("local", new Dictionary<string, string>
    {
        ["type"] = "nfs", ["device"] = ":/volume1/media"
    }), "Wolumen NFS nie został rozpoznany jako sieciowy.");
    True(!NetworkStorageDetector.IsNetworkVolume("local", new Dictionary<string, string>()),
        "Zwykły lokalny wolumen został błędnie uznany za sieciowy.");
}

static void TestCancellationRecovery()
{
    WithTempDirectory(root =>
    {
        var source = Path.Combine(root, "source");
        var local = Path.Combine(root, "backup");
        Directory.CreateDirectory(source);
        var runner = new ScriptedRunner((arguments, token) =>
        {
            var args = arguments.ToArray();
            if (args.SequenceEqual(["ps", "-aq"])) return new(0, "nextcloud-id\n", "");
            if (args.Length == 2 && args[0] == "inspect")
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        Id = "nextcloud-id", Name = "/nextcloud-app", State = new { Running = true },
                        Config = new
                        {
                            Image = "nextcloud:33.0.4",
                            Labels = new Dictionary<string, string>
                            {
                                ["com.docker.compose.project.working_dir"] = source,
                                ["com.docker.compose.project"] = "nextcloud"
                            }
                        },
                        Mounts = Array.Empty<object>()
                    }
                });
                return new(0, json, "");
            }
            if (args.SequenceEqual(["info"])) return new(0, "ok", "");
            if (args.Length > 1 && args[0] == "exec" && args[^1] == "--on") return new(0, "", "");
            if (args.Length > 0 && args[0] == "stop") throw new OperationCanceledException("testowe anulowanie");
            if (args.SequenceEqual(["start", "nextcloud-id"])) return new(0, "nextcloud-id", "");
            if (args.Length > 1 && args[0] == "exec" && args[^1] == "--off") return new(0, "", "");
            return new(0, "", "");
        });
        var paths = new AppPaths(Path.Combine(root, "appdata"));
        paths.EnsureCreated();
        var engine = new BackupEngine(runner, new DockerDiscovery(runner), new TextLog(paths));
        var settings = new BackupSettings
        {
            SourceRoot = source,
            LocalBackupRoot = local,
            NasBackupRoot = @"\\nas\backup",
            RequireNasBeforeBackup = false,
            ArchivePasswordProtected = SecretProtector.Protect("testowe-haslo-123")
        };
        var recoverySnapshots = new List<IReadOnlyList<string>>();
        try
        {
            engine.RunAsync(settings, new Progress<JobProgress>(), ids => recoverySnapshots.Add(ids.ToArray()),
                new CancellationTokenSource().Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Test nie wywołał anulowania.");
        }
        catch (OperationCanceledException) { }

        True(runner.Calls.Any(call => call.Arguments.SequenceEqual(["start", "nextcloud-id"])
            && !call.TokenCanBeCanceled), "Po anulowaniu nie uruchomiono kontenera niezależnym tokenem.");
        True(runner.Calls.Any(call => call.Arguments.FirstOrDefault() == "exec"
            && call.Arguments.LastOrDefault() == "--off" && !call.TokenCanBeCanceled),
            "Po anulowaniu nie wyłączono maintenance niezależnym tokenem.");
        True(recoverySnapshots.Count >= 2 && recoverySnapshots[^1].Count == 0,
            "Stan odzyskiwania nie został wyczyszczony po udanym sprzątaniu.");
    });
}

static void TestNetworkSourcePreflight()
{
    var runner = new ScriptedRunner((_, _) =>
        throw new InvalidOperationException("Docker nie powinien zostać wywołany."));
    var engine = new BackupEngine(runner, new DockerDiscovery(runner), new TextLog(new AppPaths()));
    var settings = new BackupSettings
    {
        SourceRoot = @"\\192.168.50.30\Kontenery",
        LocalBackupRoot = @"C:\Backupy",
        NasBackupRoot = @"\\nas\backup"
    };
    try
    {
        engine.RunAsync(settings, new Progress<JobProgress>(), _ => { }, CancellationToken.None)
            .GetAwaiter().GetResult();
        throw new InvalidOperationException("Sieciowe źródło nie zostało zablokowane.");
    }
    catch (InvalidOperationException ex)
    {
        True(ex.Message.Contains("sieciowym", StringComparison.OrdinalIgnoreCase),
            "Komunikat nie wyjaśnia blokady źródła sieciowego.");
    }
    Equal(0, runner.Calls.Count);
}

static void WithTempDirectory(Action<string> action)
{
    var root = Path.Combine(Path.GetTempPath(), "ServerBackuperTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try { action(root); }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static void RunProcess(string fileName, IEnumerable<string> arguments, string workingDirectory)
{
    var start = new ProcessStartInfo(fileName)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        WorkingDirectory = workingDirectory
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Nie uruchomiono procesu.");
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0) throw new InvalidOperationException(error + output);
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Oczekiwano: {expected}, otrzymano: {actual}.");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ScriptedRunner(Func<IEnumerable<string>, CancellationToken, ProcessResult> handler) : IProcessRunner
{
    public List<(string[] Arguments, bool TokenCanBeCanceled)> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments,
        string? workingDirectory, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var args = arguments.ToArray();
        Calls.Add((args, cancellationToken.CanBeCanceled));
        return Task.FromResult(handler(args, cancellationToken));
    }

    public Task<ProcessResult> RunToFileAsync(string fileName, IEnumerable<string> arguments,
        string outputPath, string? workingDirectory, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

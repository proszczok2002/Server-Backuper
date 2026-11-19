using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace ServerBackuper.Installer;

internal static class Program
{
    private const string TaskName = "Server Backuper Agent";
    private const string RunValueName = "Server Backuper Agent";
    private const string AgentProcessName = "ServerBackuper.Agent";
    private const string AppProcessName = "ServerBackuper.App";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ServerBackuper";
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ServerBackuper-install.log");
    private static string? _persistentLogPath;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            try { File.WriteAllText(LogPath, string.Empty); } catch { }
            WriteLog("Rozpoczecie instalacji.");
            EnsureAdministrator();
            var result = Install();
            WriteLog("Instalacja zakonczona.");
            MessageBox.Show(
                "Instalacja zosta\u0142a sprawdzona i zako\u0144czona pomy\u015Blnie.\n\n" +
                "[OK] Wszystkie pliki programu zosta\u0142y skopiowane i zweryfikowane.\n" +
                "[OK] Zadanie autostartu działa bez limitu czasu i ma restart po awarii.\n" +
                "[OK] Agent dzia\u0142a i regularnie zapisuje heartbeat.\n" +
                "[OK] Panel Server Backuper zosta\u0142 uruchomiony.\n\n" +
                result,
                "Server Backuper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            WriteLog("BLAD: " + ex);
            MessageBox.Show(
                "Instalacja nie zosta\u0142a uko\u0144czona i nie zostanie oznaczona jako udana.\n\n" + ex.Message +
                "\n\nPe\u0142ny log instalatora:\n" + (_persistentLogPath ?? LogPath),
                "Server Backuper - b\u0142\u0105d instalacji",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static string Install()
    {
        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Nie mo\u017Cna ustali\u0107 po\u0142o\u017Cenia instalatora.");
        var packageRoot = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Nie mo\u017Cna ustali\u0107 katalogu paczki.");
        var sourceAgent = Path.Combine(packageRoot, "Agent");
        var sourceApp = Path.Combine(packageRoot, "App");
        var sourceUninstaller = Path.Combine(packageRoot, "Uninstall-ServerBackuper.exe");
        ValidatePackage(sourceAgent, sourceApp, sourceUninstaller);

        var installRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ServerBackuper");
        var agentRoot = Path.Combine(installRoot, "Agent");
        var appRoot = Path.Combine(installRoot, "App");
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ServerBackuper");
        var agentExe = Path.Combine(agentRoot, "ServerBackuper.Agent.exe");
        var appExe = Path.Combine(appRoot, "ServerBackuper.App.exe");
        var uninstallerExe = Path.Combine(installRoot, "Uninstall-ServerBackuper.exe");

        StopExistingInstallation();
        Directory.CreateDirectory(agentRoot);
        Directory.CreateDirectory(appRoot);
        Directory.CreateDirectory(dataRoot);
        EnablePersistentLogging(dataRoot);
        CopyDirectory(sourceAgent, agentRoot);
        CopyDirectory(sourceApp, appRoot);
        File.Copy(sourceUninstaller, uninstallerExe, true);
        VerifyCopiedPackage(sourceAgent, agentRoot, "agenta");
        VerifyCopiedPackage(sourceApp, appRoot, "panelu");
        VerifyInstalledFile(uninstallerExe, "deinstalatora");
        ConfigureDataPermissions(dataRoot);

        CreateScheduledTask(agentExe);
        RemoveRegistryFallback();
        RegisterUninstaller(uninstallerExe, appExe, installRoot);

        var shortcutCreated = TryCreateDesktopShortcut(appExe, appRoot);
        if (!shortcutCreated)
            throw new InvalidOperationException("Nie uda\u0142o si\u0119 utworzy\u0107 skr\u00F3tu panelu na pulpicie.");

        var verificationStartedUtc = DateTime.UtcNow;
        StartAgent();
        VerifyScheduledTask();
        VerifyAgent(agentExe, dataRoot, verificationStartedUtc);
        StartAndVerifyPanel(appExe);
        VerifyScheduledTask();
        VerifyInstalledFile(agentExe, "pliku wykonywalnego agenta");
        VerifyInstalledFile(appExe, "pliku wykonywalnego panelu");
        VerifyInstalledFile(uninstallerExe, "pliku wykonywalnego deinstalatora");

        return "Autostart: Harmonogram zada\u0144 Windows.\n" +
            "Skr\u00F3t zosta\u0142 utworzony na pulpicie.\n" +
            "Deinstalator jest dostępny w systemowych Zainstalowanych aplikacjach.\n" +
            "Log instalacji: " + (_persistentLogPath ?? LogPath);
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "Instalator nie otrzyma\u0142 uprawnie\u0144 administratora. Zaakceptuj okno kontroli konta UAC.");
    }

    private static void ValidatePackage(string agentRoot, string appRoot, string uninstallerPath)
    {
        if (!File.Exists(Path.Combine(agentRoot, "ServerBackuper.Agent.exe")))
            throw new FileNotFoundException(
                "Brakuje katalogu Agent obok instalatora. Najpierw rozpakuj ca\u0142\u0105 paczk\u0119 ZIP.");
        if (!File.Exists(Path.Combine(appRoot, "ServerBackuper.App.exe")))
            throw new FileNotFoundException(
                "Brakuje katalogu App obok instalatora. Najpierw rozpakuj ca\u0142\u0105 paczk\u0119 ZIP.");
        if (!File.Exists(uninstallerPath))
            throw new FileNotFoundException(
                "Brakuje Uninstall-ServerBackuper.exe obok instalatora. Rozpakuj całą paczkę ZIP.");

        VerifyInstalledFile(Path.Combine(agentRoot, "ServerBackuper.Agent.exe"),
            "agenta w paczce instalacyjnej");
        VerifyInstalledFile(Path.Combine(appRoot, "ServerBackuper.App.exe"),
            "panelu w paczce instalacyjnej");
        VerifyInstalledFile(uninstallerPath, "deinstalatora w paczce instalacyjnej");
    }

    private static void StopExistingInstallation()
    {
        RunCommand("schtasks.exe", ["/End", "/TN", TaskName], false);
        RunCommand("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], false);
        StopProcesses(AgentProcessName);
        StopProcesses(AppProcessName);
    }

    private static void StopProcesses(string name)
    {
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    WriteLog($"Zatrzymywanie procesu {name}, PID {process.Id}.");
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
                catch (Exception ex) { WriteLog("Nie zatrzymano procesu: " + ex.Message); }
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        WriteLog($"Kopiowanie {source} -> {destination}");
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
        }
    }

    private static void VerifyCopiedPackage(string source, string destination, string componentName)
    {
        WriteLog("Weryfikowanie plikow " + componentName + ".");
        var sourceFiles = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0)
            throw new InvalidOperationException($"Paczka {componentName} jest pusta.");

        foreach (var relativePath in sourceFiles)
        {
            var sourcePath = Path.Combine(source, relativePath);
            var destinationPath = Path.Combine(destination, relativePath);
            if (!File.Exists(destinationPath))
                throw new InvalidOperationException($"Nie skopiowano pliku {componentName}: {relativePath}");
            var sourceInfo = new FileInfo(sourcePath);
            var destinationInfo = new FileInfo(destinationPath);
            if (sourceInfo.Length != destinationInfo.Length || !FilesHaveSameHash(sourcePath, destinationPath))
                throw new InvalidOperationException($"Plik {componentName} jest uszkodzony: {relativePath}");
        }
        WriteLog($"Zweryfikowano {sourceFiles.Length} plikow {componentName}.");
    }

    private static bool FilesHaveSameHash(string firstPath, string secondPath)
    {
        using var first = File.OpenRead(firstPath);
        using var second = File.OpenRead(secondPath);
        return SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second));
    }

    private static void VerifyInstalledFile(string path, string description)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new FileNotFoundException($"Brakuje {description}: {path}");
    }

    private static void ConfigureDataPermissions(string dataRoot)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Nie mo\u017Cna odczyta\u0107 identyfikatora konta Windows.");
        RunCommand("icacls.exe", [dataRoot, "/inheritance:r"], true);
        RunCommand("icacls.exe",
            [dataRoot, "/grant:r", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F",
                $"*{sid}:(OI)(CI)M"], true);
    }

    private static void CreateScheduledTask(string agentExe)
    {
        var account = WindowsIdentity.GetCurrent().Name;
        var taskCommand = string.Concat((char)34, agentExe, (char)34);
        var result = RunCommand("schtasks.exe",
            ["/Create", "/TN", TaskName, "/TR", taskCommand, "/SC", "ONLOGON", "/RU", account,
                "/RL", "HIGHEST", "/IT", "/F"], false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Nie uda\u0142o si\u0119 utworzy\u0107 zadania autostartu. " + result.Output);
        ConfigureScheduledTaskSettings();
        VerifyScheduledTask();
    }

    private static void ConfigureScheduledTaskSettings()
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $settingsArguments = @{
                ExecutionTimeLimit = [TimeSpan]::Zero
                RestartCount = 999
                RestartInterval = [TimeSpan]::FromMinutes(1)
                StartWhenAvailable = $true
                AllowStartIfOnBatteries = $true
                DontStopIfGoingOnBatteries = $true
                MultipleInstances = 'IgnoreNew'
            }
            $settings = New-ScheduledTaskSettingsSet @settingsArguments
            Set-ScheduledTask -TaskName 'Server Backuper Agent' -Settings $settings | Out-Null
            """;
        RunPowerShell(script, true);
        WriteLog("Ustawiono zadanie bez limitu czasu oraz restart po awarii co 1 minute.");
    }

    private static void ConfigureRegistryFallback(string agentExe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? throw new InvalidOperationException("Nie mo\u017Cna zapisa\u0107 autostartu w rejestrze.");
        key.SetValue(RunValueName, $"\"{agentExe}\"", RegistryValueKind.String);
    }

    private static void RemoveRegistryFallback()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        key?.DeleteValue(RunValueName, false);
    }

    private static void RegisterUninstaller(string uninstallerExe, string appExe, string installRoot)
    {
        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey, true)
            ?? throw new InvalidOperationException("Nie można utworzyć wpisu deinstalacyjnego w rejestrze.");
        key.SetValue("DisplayName", "Server Backuper", RegistryValueKind.String);
        key.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
        key.SetValue("Publisher", "proszczok2002", RegistryValueKind.String);
        key.SetValue("InstallLocation", installRoot, RegistryValueKind.String);
        key.SetValue("DisplayIcon", appExe, RegistryValueKind.String);
        key.SetValue("UninstallString", string.Concat((char)34, uninstallerExe, (char)34),
            RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var sizeKb = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length) / 1024;
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, sizeKb), RegistryValueKind.DWord);
        WriteLog("Zarejestrowano deinstalator: " + uninstallerExe);
    }

    private static void StartAgent()
    {
        var result = RunCommand("schtasks.exe", ["/Run", "/TN", TaskName], false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Nie uda\u0142o si\u0119 uruchomi\u0107 zadania agenta. " + result.Output);
    }

    private static void VerifyScheduledTask()
    {
        var result = RunCommand("schtasks.exe", ["/Query", "/TN", TaskName], false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Zadanie autostartu nie istnieje po instalacji. " + result.Output);

        const string script = """
            $ErrorActionPreference = 'Stop'
            $task = Get-ScheduledTask -TaskName 'Server Backuper Agent' -ErrorAction Stop
            $executionTimeLimit = [System.Xml.XmlConvert]::ToTimeSpan(
                [string]$task.Settings.ExecutionTimeLimit)
            if ($executionTimeLimit -ne [TimeSpan]::Zero) {
                throw "Limit czasu zadania nie jest wyłączony: $($task.Settings.ExecutionTimeLimit)"
            }
            if ([int]$task.Settings.RestartCount -lt 1) {
                throw 'Restart zadania po awarii nie jest włączony.'
            }
            $restartInterval = [System.Xml.XmlConvert]::ToTimeSpan(
                [string]$task.Settings.RestartInterval)
            if ($restartInterval -le [TimeSpan]::Zero) {
                throw "Interwał restartu zadania jest niepoprawny: $($task.Settings.RestartInterval)"
            }
            "ExecutionTimeLimit=$($task.Settings.ExecutionTimeLimit); " +
                "RestartCount=$($task.Settings.RestartCount); " +
                "RestartInterval=$($task.Settings.RestartInterval)"
            """;
        var settingsResult = RunPowerShell(script, false);
        if (settingsResult.ExitCode != 0)
            throw new InvalidOperationException(
                "Zadanie autostartu ma niepoprawne ustawienia ci\u0105g\u0142ej pracy. " + settingsResult.Output);
    }

    private static CommandResult RunPowerShell(string script, bool required) =>
        RunCommand("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script], required);

    private static void VerifyAgent(string agentExe, string dataRoot, DateTime startedUtc)
    {
        var statePath = Path.Combine(dataRoot, "state.json");
        var timeoutAt = DateTime.UtcNow.AddSeconds(30);
        DateTime? firstHeartbeat = null;
        string lastProblem = "agent nie utworzy\u0142 jeszcze pliku stanu";

        while (DateTime.UtcNow < timeoutAt)
        {
            var processFound = IsExpectedProcessRunning(AgentProcessName, agentExe);
            var heartbeat = TryReadHeartbeat(statePath, out var heartbeatProblem);
            lastProblem = !processFound ? "proces agenta nie dzia\u0142a" : heartbeatProblem;
            if (processFound && heartbeat.HasValue && heartbeat.Value >= startedUtc.AddSeconds(-1)
                && DateTime.UtcNow - heartbeat.Value <= TimeSpan.FromSeconds(10))
            {
                if (!firstHeartbeat.HasValue)
                {
                    firstHeartbeat = heartbeat;
                    WriteLog("Pierwszy heartbeat agenta: " + heartbeat.Value.ToString("O"));
                }
                else if (heartbeat.Value > firstHeartbeat.Value)
                {
                    WriteLog("Agent przeszedl weryfikacje dwoch heartbeatow: " + heartbeat.Value.ToString("O"));
                    return;
                }
            }
            Thread.Sleep(500);
        }

        var startupLogPath = Path.Combine(dataRoot, "logs", "agent-startup.log");
        if (File.Exists(startupLogPath))
        {
            try { WriteLog("Log awarii startu agenta: " + File.ReadAllText(startupLogPath)); }
            catch (Exception ex) { WriteLog("Nie mozna odczytac logu awarii agenta: " + ex.Message); }
        }
        var taskDetails = RunCommand("schtasks.exe", ["/Query", "/TN", TaskName, "/V", "/FO", "LIST"], false);
        WriteLog("Stan zadania po nieudanym starcie: " + taskDetails.Output);
        throw new InvalidOperationException(
            "Agent nie uruchomi\u0142 si\u0119 poprawnie w ci\u0105gu 30 sekund: " + lastProblem +
            ". Szczeg\u00F3\u0142y znajduj\u0105 si\u0119 w logu instalatora.");
    }

    private static bool IsExpectedProcessRunning(string processName, string expectedPath)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && string.Equals(process.MainModule?.FileName, expectedPath,
                            StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }
        }
        return false;
    }

    private static DateTime? TryReadHeartbeat(string statePath, out string problem)
    {
        try
        {
            if (!File.Exists(statePath))
            {
                problem = "brak pliku state.json";
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("LastHeartbeatUtc", out var value)
                || value.ValueKind != JsonValueKind.String
                || !value.TryGetDateTime(out var heartbeat))
            {
                problem = "state.json nie zawiera poprawnego heartbeat";
                return null;
            }
            problem = "heartbeat nie jest jeszcze aktualny";
            return heartbeat.ToUniversalTime();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            problem = "nie mo\u017Cna odczyta\u0107 state.json: " + ex.Message;
            return null;
        }
    }

    private static void StartAndVerifyPanel(string appExe)
    {
        WriteLog("Uruchamianie i weryfikowanie panelu.");
        using var process = Process.Start(new ProcessStartInfo(appExe) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Nie uda\u0142o si\u0119 uruchomi\u0107 panelu.");
        if (process.WaitForExit(3000))
            throw new InvalidOperationException(
                $"Panel zamkn\u0105\u0142 si\u0119 podczas uruchamiania (kod {process.ExitCode}).");
        WriteLog($"Panel dziala, PID {process.Id}.");
    }

    private static bool TryCreateDesktopShortcut(string appExe, string workingDirectory)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Brak Windows Script Host.");
            shell = Activator.CreateInstance(shellType);
            dynamic dynamicShell = shell!;
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var legacyShortcut = Path.Combine(desktop, "Serwer Backuper.lnk");
            if (File.Exists(legacyShortcut)) File.Delete(legacyShortcut);
            var shortcutPath = Path.Combine(desktop, "Server Backuper.lnk");
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = appExe;
            dynamicShortcut.WorkingDirectory = workingDirectory;
            dynamicShortcut.Description = "Panel zarzadzania Server Backuper";
            dynamicShortcut.Save();
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("Nie utworzono skrotu: " + ex);
            return false;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void EnablePersistentLogging(string dataRoot)
    {
        try
        {
            var logsDirectory = Path.Combine(dataRoot, "logs");
            Directory.CreateDirectory(logsDirectory);
            _persistentLogPath = Path.Combine(logsDirectory, "installer.log");
            File.AppendAllText(_persistentLogPath,
                Environment.NewLine + "===== Instalacja " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " =====" + Environment.NewLine);
            if (File.Exists(LogPath)) File.AppendAllText(_persistentLogPath, File.ReadAllText(LogPath));
        }
        catch (Exception ex)
        {
            _persistentLogPath = null;
            WriteLog("Nie mozna utworzyc stalego logu: " + ex.Message);
        }
    }

    private static CommandResult RunCommand(string fileName, IReadOnlyList<string> arguments, bool required)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        WriteLog(fileName + " " + string.Join(" ", arguments.Select(QuoteForLog)));
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Nie uruchomiono programu: " + fileName);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = (outputTask.GetAwaiter().GetResult() + Environment.NewLine +
            errorTask.GetAwaiter().GetResult()).Trim();
        WriteLog($"Kod {process.ExitCode}: {output}");
        if (required && process.ExitCode != 0)
            throw new InvalidOperationException($"Polecenie {fileName} nie powiod\u0142o si\u0119: {output}");
        return new CommandResult(process.ExitCode, output);
    }

    private static string QuoteForLog(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    private static void WriteLog(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        try { File.AppendAllText(LogPath, line); }
        catch { }
        if (_persistentLogPath is not null)
        {
            try { File.AppendAllText(_persistentLogPath, line); }
            catch { }
        }
    }

    private sealed record CommandResult(int ExitCode, string Output);
}

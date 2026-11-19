using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace ServerBackuper.Uninstaller;

internal static class Program
{
    private const string TaskName = "Server Backuper Agent";
    private const string AgentProcessName = "ServerBackuper.Agent";
    private const string AppProcessName = "ServerBackuper.App";
    private const string RunValueName = "Server Backuper Agent";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ServerBackuper";
    private const string RelocatedArgument = "--relocated";
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "ServerBackuper-uninstall.log");
    private static bool _deleteTemporaryExecutable;

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            EnsureAdministrator();
            var installRoot = GetInstallRoot();
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Nie można ustalić ścieżki deinstalatora.");

            if (!args.Contains(RelocatedArgument, StringComparer.OrdinalIgnoreCase)
                && IsInsideDirectory(executable, installRoot))
            {
                RelaunchFromTemporaryLocation(executable);
                return;
            }

            _deleteTemporaryExecutable = args.Contains(RelocatedArgument, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(LogPath, string.Empty);
            WriteLog("Rozpoczęcie pełnej deinstalacji.");

            if (!CanSafelyUninstall(out var safetyMessage))
            {
                WriteLog("Deinstalacja zatrzymana: " + safetyMessage);
                MessageBox.Show(safetyMessage, "Server Backuper — deinstalacja zatrzymana",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var confirmation = MessageBox.Show(
                "Deinstalator usunie z tego komputera:\n\n" +
                "• agenta i panel Server Backuper,\n" +
                "• zadanie autostartu i uruchomione procesy,\n" +
                "• skróty oraz wpisy programu w rejestrze,\n" +
                "• ustawienia, historię i wszystkie logi programu.\n\n" +
                "Archiwa w C:\\Backupy i na NAS NIE zostaną usunięte.\n\n" +
                "Czy na pewno całkowicie odinstalować Server Backuper?",
                "Odinstaluj Server Backuper", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirmation != DialogResult.Yes)
            {
                WriteLog("Użytkownik anulował deinstalację.");
                return;
            }

            var result = Uninstall();
            VerifyRemoval();
            WriteLog("Deinstalacja zakończona i zweryfikowana.");
            MessageBox.Show(
                "Server Backuper został całkowicie usunięty z systemu.\n\n" +
                result + "\n\n" +
                "Archiwa backupów w C:\\Backupy i na NAS pozostały bez zmian.\n\n" +
                "Log deinstalacji: " + LogPath,
                "Server Backuper — odinstalowano", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            WriteLog("BŁĄD: " + ex);
            MessageBox.Show(
                "Nie udało się całkowicie odinstalować Server Backuper.\n\n" + ex.Message +
                "\n\nSzczegóły: " + LogPath,
                "Server Backuper — błąd deinstalacji", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (_deleteTemporaryExecutable) ScheduleTemporaryExecutableDeletion();
        }
    }

    private static string Uninstall()
    {
        var installRoot = GetInstallRoot();
        var dataRoot = GetDataRoot();

        EndAndDeleteTask();
        StopProcesses(AgentProcessName);
        StopProcesses(AppProcessName);
        RemoveShortcuts();
        DeleteDirectoryWithRetries(installRoot);
        DeleteDirectoryWithRetries(dataRoot);
        RemoveRegistryEntries();

        return "[OK] Usunięto procesy, autostart, zadanie, skróty i wpisy rejestru.\n" +
            "[OK] Usunięto pliki programu, ustawienia, historię oraz logi.";
    }

    private static bool CanSafelyUninstall(out string message)
    {
        message = string.Empty;
        var statePath = Path.Combine(GetDataRoot(), "state.json");
        if (!File.Exists(statePath)) return true;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            var root = document.RootElement;
            var backupInProgress = root.TryGetProperty("BackupInProgress", out var progress)
                && progress.ValueKind == JsonValueKind.True;
            var recoveryPending = root.TryGetProperty("RecoveryContainerIds", out var recovery)
                && recovery.ValueKind == JsonValueKind.Array && recovery.GetArrayLength() > 0;
            var agentRunning = Process.GetProcessesByName(AgentProcessName).Length > 0;

            if (recoveryPending)
            {
                message = "Nie można teraz odinstalować programu, ponieważ agent ma zapisane kontenery " +
                    "wymagające odzyskania. Uruchom agenta i poczekaj, aż kontenery zostaną uruchomione " +
                    "oraz tryb maintenance zostanie wyłączony.";
                return false;
            }

            if (backupInProgress && agentRunning)
            {
                message = "Backup lub przywracanie nadal trwa. Najpierw anuluj operację w panelu i poczekaj " +
                    "na bezpieczne uruchomienie kontenerów. Dopiero potem uruchom deinstalator ponownie.";
                return false;
            }

            if (backupInProgress)
            {
                var answer = MessageBox.Show(
                    "Plik stanu informuje o niedokończonej operacji, ale proces agenta nie działa. " +
                    "Kontynuowanie usunie ten stan. Upewnij się, że kontenery działają i Nextcloud nie jest " +
                    "w trybie maintenance.\n\nKontynuować mimo to?",
                    "Niedokończona operacja", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                return answer == DialogResult.Yes;
            }
        }
        catch (Exception ex)
        {
            WriteLog("Nie można sprawdzić state.json: " + ex);
            message = "Nie można bezpiecznie odczytać stanu programu. Deinstalacja została zatrzymana, " +
                "aby nie pozostawić zatrzymanych kontenerów. Szczegóły: " + LogPath;
            return false;
        }

        return true;
    }

    private static void EndAndDeleteTask()
    {
        WriteLog("Zatrzymywanie i usuwanie zadania: " + TaskName);
        RunCommand("schtasks.exe", ["/End", "/TN", TaskName], false);
        RunCommand("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"], false);
    }

    private static void StopProcesses(string processName)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var processes = Process.GetProcessesByName(processName);
            if (processes.Length == 0) return;
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        WriteLog($"Zatrzymywanie {processName}, PID {process.Id}.");
                        process.Kill(true);
                        process.WaitForExit(5000);
                    }
                    catch (Exception ex) { WriteLog("Nie zatrzymano procesu: " + ex.Message); }
                }
            }
            Thread.Sleep(300);
        }

        if (Process.GetProcessesByName(processName).Length > 0)
            throw new InvalidOperationException("Nie można zatrzymać procesu: " + processName);
    }

    private static void RemoveRegistryEntries()
    {
        WriteLog("Usuwanie wpisów rejestru.");
        using (var run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", true))
        {
            run?.DeleteValue(RunValueName, false);
        }

        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
    }

    private static void RemoveShortcuts()
    {
        foreach (var shortcut in GetShortcutPaths()) DeleteFileIfPresent(shortcut);
    }

    private static IEnumerable<string> GetShortcutPaths()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };
        foreach (var folder in folders.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(folder, "Server Backuper.lnk");
            yield return Path.Combine(folder, "Programs", "Server Backuper.lnk");
            yield return Path.Combine(folder, "Serwer Backuper.lnk");
            yield return Path.Combine(folder, "Programs", "Serwer Backuper.lnk");
        }
    }

    private static void DeleteFileIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        WriteLog("Usuwanie pliku: " + path);
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    private static void DeleteDirectoryWithRetries(string path)
    {
        ValidateRemovalPath(path);
        if (!Directory.Exists(path)) return;

        WriteLog("Usuwanie katalogu: " + path);
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, true);
                if (!Directory.Exists(path)) return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                WriteLog($"Próba {attempt} usunięcia katalogu nieudana: {ex.Message}");
            }
            Thread.Sleep(200 * attempt);
        }

        throw new IOException("Nie można usunąć katalogu: " + path, lastError);
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); }
                catch { }
            }
        }
        catch { }
    }

    private static void ValidateRemovalPath(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var expectedInstall = Path.GetFullPath(GetInstallRoot()).TrimEnd(Path.DirectorySeparatorChar);
        var expectedData = Path.GetFullPath(GetDataRoot()).TrimEnd(Path.DirectorySeparatorChar);
        if (!fullPath.Equals(expectedInstall, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(expectedData, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Odmowa usunięcia nieoczekiwanej ścieżki: " + fullPath);
    }

    private static void VerifyRemoval()
    {
        var problems = new List<string>();
        if (Directory.Exists(GetInstallRoot())) problems.Add("pozostał katalog programu");
        if (Directory.Exists(GetDataRoot())) problems.Add("pozostał katalog danych");
        if (Process.GetProcessesByName(AgentProcessName).Length > 0) problems.Add("agent nadal działa");
        if (Process.GetProcessesByName(AppProcessName).Length > 0) problems.Add("panel nadal działa");
        if (RunCommand("schtasks.exe", ["/Query", "/TN", TaskName], false).ExitCode == 0)
            problems.Add("pozostało zadanie Harmonogramu");
        using (var key = Registry.LocalMachine.OpenSubKey(UninstallKey))
        {
            if (key is not null) problems.Add("pozostał wpis deinstalacyjny w rejestrze");
        }
        using (var run = Registry.CurrentUser.OpenSubKey(
                   @"Software\Microsoft\Windows\CurrentVersion\Run"))
        {
            if (run?.GetValue(RunValueName) is not null) problems.Add("pozostał wpis autostartu w rejestrze");
        }
        if (GetShortcutPaths().Any(File.Exists)) problems.Add("pozostał skrót programu");

        if (problems.Count > 0)
            throw new InvalidOperationException("Weryfikacja wykryła problem: " + string.Join(", ", problems) + ".");
    }

    private static void EnsureAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException(
                "Deinstalator nie otrzymał uprawnień administratora. Zaakceptuj okno UAC.");
    }

    private static string GetInstallRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ServerBackuper");

    private static string GetDataRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServerBackuper");

    private static bool IsInsideDirectory(string filePath, string directoryPath)
    {
        var file = Path.GetFullPath(filePath);
        var directory = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return file.StartsWith(directory, StringComparison.OrdinalIgnoreCase);
    }

    private static void RelaunchFromTemporaryLocation(string executable)
    {
        var temporaryExecutable = Path.Combine(Path.GetTempPath(),
            "Uninstall-ServerBackuper-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(executable, temporaryExecutable, true);
        WriteLog("Przeniesienie deinstalatora do katalogu tymczasowego: " + temporaryExecutable);
        var start = new ProcessStartInfo(temporaryExecutable) { UseShellExecute = true };
        start.ArgumentList.Add(RelocatedArgument);
        if (Process.Start(start) is null)
            throw new InvalidOperationException("Nie uruchomiono kopii deinstalatora.");
    }

    private static void ScheduleTemporaryExecutableDeletion()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !Path.GetFullPath(executable).StartsWith(
                    Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) return;
            var quote = (char)34;
            var command = $"ping 127.0.0.1 -n 3 > nul & del /f /q {quote}{executable}{quote}";
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add(command);
            Process.Start(start);
        }
        catch (Exception ex) { WriteLog("Nie zaplanowano usunięcia kopii tymczasowej: " + ex.Message); }
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
        WriteLog(fileName + " " + string.Join(" ", arguments));
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Nie uruchomiono programu: " + fileName);
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = (outputTask.GetAwaiter().GetResult() + Environment.NewLine
            + errorTask.GetAwaiter().GetResult()).Trim();
        WriteLog($"Kod {process.ExitCode}: {output}");
        if (required && process.ExitCode != 0)
            throw new InvalidOperationException($"Polecenie {fileName} nie powiodło się: {output}");
        return new CommandResult(process.ExitCode, output);
    }

    private static void WriteLog(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private sealed record CommandResult(int ExitCode, string Output);
}

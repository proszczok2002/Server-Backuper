using System.Diagnostics;

namespace ServerBackuper.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments,
        string? workingDirectory, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null);

    Task<ProcessResult> RunToFileAsync(string fileName, IEnumerable<string> arguments,
        string outputPath, string? workingDirectory, CancellationToken cancellationToken);
}

public sealed class ProcessRunner(TextLog log) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments,
        string? workingDirectory, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (environment is not null)
            foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;

        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Nie można uruchomić: {fileName}");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            log.Write("Błąd procesu: " + error.Trim());
        return new ProcessResult(process.ExitCode, output, error);
    }

    public async Task<ProcessResult> RunToFileAsync(string fileName, IEnumerable<string> arguments,
        string outputPath, string? workingDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException($"Nie można uruchomić: {fileName}");
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await copyTask;
            await output.FlushAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        var error = await errorTask;
        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(error))
            log.Write("Błąd procesu: " + error.Trim());
        return new ProcessResult(process.ExitCode, string.Empty, error);
    }
}

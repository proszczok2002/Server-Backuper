using ServerBackuper.Core;
using ServerBackuper.Service;

try
{
    using var singleInstance = new Mutex(true, @"Local\ServerBackuper.Agent", out var isFirstInstance);
    if (!isFirstInstance) return;

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = "Server Backuper");
    builder.Services.AddHostedService<Worker>();
    await builder.Build().RunAsync();
}
catch (Exception ex)
{
    TryWriteStartupFailure(ex);
    throw;
}

static void TryWriteStartupFailure(Exception exception)
{
    try
    {
        var paths = new AppPaths();
        paths.EnsureCreated();
        var path = Path.Combine(paths.LogsDirectory, "agent-startup.log");
        File.AppendAllText(path,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} AWARIA STARTU{Environment.NewLine}" +
            exception + Environment.NewLine + Environment.NewLine);
    }
    catch { }
}

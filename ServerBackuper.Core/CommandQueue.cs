namespace ServerBackuper.Core;

public sealed class CommandQueue(AppPaths paths, JsonFileStore store)
{
    public void Enqueue(CommandType type)
    {
        Enqueue(new CommandRequest { Type = type });
    }

    public void Enqueue(CommandRequest command)
    {
        paths.EnsureCreated();
        if (command.Id == Guid.Empty) command.Id = Guid.NewGuid();
        if (command.CreatedUtc == default) command.CreatedUtc = DateTime.UtcNow;
        var name = $"{command.CreatedUtc:yyyyMMddHHmmssfff}-{command.Id:N}.json";
        store.Save(Path.Combine(paths.CommandsDirectory, name), command);
    }

    public IReadOnlyList<(string Path, CommandRequest Request)> ReadPending()
    {
        paths.EnsureCreated();
        var result = new List<(string, CommandRequest)>();
        foreach (var file in Directory.EnumerateFiles(paths.CommandsDirectory, "*.json").OrderBy(x => x))
        {
            try { result.Add((file, store.LoadOrCreate(file, () => new CommandRequest()))); }
            catch { TryDelete(file); }
        }
        return result;
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}

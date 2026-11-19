using System.Text.Json;
using System.Text;

namespace ServerBackuper.Core;

public sealed class JsonFileStore
{
    private readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public T LoadOrCreate<T>(string path, Func<T> factory) where T : class
    {
        if (!File.Exists(path))
        {
            var value = factory();
            Save(path, value);
            return value;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(ReadAllTextShared(path), _options) ?? factory();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Nie można odczytać pliku: {path}", ex);
        }
    }

    public void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Brak katalogu docelowego.");
        Directory.CreateDirectory(directory);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, _options));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
                }
                catch (UnauthorizedAccessException) when (attempt < 10)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Nie można atomowo zapisać pliku: {path}. {ex.Message}", ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { }
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }
}

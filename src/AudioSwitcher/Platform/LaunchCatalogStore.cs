using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed record LaunchCatalogLoad(LaunchCatalog Catalog, bool CanSave, string? Error);

public sealed class LaunchCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public string FilePath { get; }

    public LaunchCatalogStore(string? path = null) => FilePath = Path.GetFullPath(path ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSwitcher", "programs.json"));

    public LaunchCatalogLoad Load()
    {
        try
        {
            // Read directly: File.Exists would hide access failures and permit overwriting unreadable data.
            var catalog = JsonSerializer.Deserialize<LaunchCatalog>(File.ReadAllText(FilePath), JsonOptions)
                ?? throw new InvalidDataException("Каталог содержит пустые данные.");
            ValidateCatalog(catalog);
            return new(catalog, true, null);
        }
        catch (FileNotFoundException) { return new(new(1, []), true, null); }
        catch (DirectoryNotFoundException) { return new(new(1, []), true, null); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(new(1, []), false, $"Не удалось прочитать каталог программ: {e.Message}");
        }
    }

    public void Save(LaunchCatalog catalog)
    {
        ValidateCatalog(catalog);
        var current = Load();
        if (!current.CanSave) throw new InvalidDataException(current.Error + " Для сохранения выполните явный сброс каталога.");
        WriteAtomically(catalog);
    }

    public void Reset()
    {
        // Copy before replacement: a failed reset never removes the original catalog.
        try
        {
            File.Copy(FilePath, FilePath + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "." + Guid.NewGuid().ToString("N") + ".bak", false);
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        WriteAtomically(new(1, []));
    }

    private void WriteAtomically(LaunchCatalog catalog)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".programs-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, catalog, JsonOptions);
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateCatalog(LaunchCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (catalog.SchemaVersion != 1) throw new InvalidDataException("Версия каталога не поддерживается. Исходный файл сохранён.");
        if (catalog.Entries is null) throw new InvalidDataException("В каталоге отсутствует список программ.");
        var ids = new HashSet<Guid>();
        foreach (var entry in catalog.Entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id)) throw new InvalidDataException("Каталог содержит пустые или повторяющиеся идентификаторы.");
            if (!Enum.IsDefined(entry.Kind) || string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.Target)
                || entry.Arguments is null || entry.WorkingDirectory is null)
                throw new InvalidDataException("Каталог содержит некорректную запись программы.");
        }
    }
}

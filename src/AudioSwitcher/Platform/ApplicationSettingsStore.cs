using System.IO;
using System.Text.Json;

namespace AudioSwitcher.Platform;

public enum WindowMoveBehavior { ActivateAndClose, KeepUtilityFocused }
public sealed record ApplicationPreferences(WindowMoveBehavior MoveBehavior = WindowMoveBehavior.KeepUtilityFocused);

public sealed class ApplicationSettingsStore
{
    private readonly string path;
    public ApplicationSettingsStore(string? path = null) => this.path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSwitcher", "settings.json"));
    public ApplicationPreferences Load()
    {
        try
        {
            var value = JsonSerializer.Deserialize<ApplicationPreferences>(File.ReadAllText(path));
            return value != null && Enum.IsDefined(value.MoveBehavior) ? value : new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save(ApplicationPreferences value)
    {
        if (!Enum.IsDefined(value.MoveBehavior)) throw new ArgumentOutOfRangeException(nameof(value));
        WriteAtomically(value);
    }

    private void WriteAtomically(ApplicationPreferences value)
    {
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

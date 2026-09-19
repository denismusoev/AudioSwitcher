using System.IO;
using System.Text.Json;

namespace AudioSwitcher.Platform;

public enum WindowMoveBehavior { ActivateAndClose, KeepUtilityFocused }
public sealed record ApplicationPreferences(WindowMoveBehavior MoveBehavior = WindowMoveBehavior.KeepUtilityFocused);

public sealed class ApplicationSettingsStore
{
    private readonly string path;
    public ApplicationSettingsStore(string? path = null) => this.path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioSwitcher", "settings.json");
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
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

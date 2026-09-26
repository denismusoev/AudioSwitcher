using System.IO;
using System.Security;
using System.Text.Json;

namespace AudioSwitcher.Platform;

public sealed record GameManifestEntry(string Name, string ExecutablePath, string WorkingDirectory, string SourceKey);

public interface IGameManifestScanner
{
    IReadOnlyList<GameManifestEntry> Scan(CancellationToken cancellationToken = default);
}

public sealed class GameManifestScanner : IGameManifestScanner
{
    private const string ManifestFileName = "audioswitcher.game.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly string rootPath;

    public GameManifestScanner(string rootPath = @"E:\Games") => this.rootPath = Path.GetFullPath(rootPath);

    public IReadOnlyList<GameManifestEntry> Scan(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] folders;
        try { folders = Directory.GetDirectories(rootPath); }
        catch (Exception error) when (IsFileError(error)) { return []; }

        var games = new List<GameManifestEntry>();
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifestPath = Path.Combine(folder, ManifestFileName);
                cancellationToken.ThrowIfCancellationRequested();
                var manifest = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(manifestPath), JsonOptions);
                cancellationToken.ThrowIfCancellationRequested();
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Executable)
                    || Path.IsPathRooted(manifest.Executable)) continue;

                var gameFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                var executable = Path.GetFullPath(manifest.Executable, gameFolder);
                var relative = Path.GetRelativePath(gameFolder, executable);
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) continue;

                games.Add(new(manifest.Name.Trim(), executable, Path.GetDirectoryName(executable)!, gameFolder));
            }
            catch (Exception error) when (IsFileError(error)) { }
        }
        return games.OrderBy(game => game.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or SecurityException
        or JsonException or NotSupportedException or ArgumentException;

    private sealed record GameManifest(string? Name, string? Executable);
}

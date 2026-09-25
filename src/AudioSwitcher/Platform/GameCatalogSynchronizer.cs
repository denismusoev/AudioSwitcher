using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class GameCatalogSynchronizer
{
    private readonly LaunchCatalogStore store;
    private readonly IGameManifestScanner scanner;
    private int synchronizing;

    public GameCatalogSynchronizer(LaunchCatalogStore store, IGameManifestScanner scanner)
    {
        this.store = store;
        this.scanner = scanner;
    }

    public LaunchCatalogLoad? Synchronize()
    {
        if (Interlocked.Exchange(ref synchronizing, 1) != 0) return null;
        try
        {
            var games = scanner.Scan();
            return store.Update(current => Merge(current, games));
        }
        finally { Volatile.Write(ref synchronizing, 0); }
    }

    internal static LaunchCatalog Merge(LaunchCatalog current, IReadOnlyList<GameManifestEntry> games)
    {
        var existing = current.Entries
            .Where(entry => entry.Source == LaunchEntrySource.GameManifest)
            .GroupBy(entry => entry.SourceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var entries = current.Entries.Where(entry => entry.Source == LaunchEntrySource.Manual).ToList();
        foreach (var game in games)
        {
            existing.TryGetValue(game.SourceKey, out var previous);
            entries.Add(new(previous?.Id ?? Guid.NewGuid(), game.Name, LaunchKind.Executable, game.ExecutablePath, "",
                game.WorkingDirectory, LaunchEntrySource.GameManifest, game.SourceKey));
        }
        return new(current.SchemaVersion, entries);
    }
}

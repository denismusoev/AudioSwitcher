namespace AudioSwitcher.Core;

public enum LaunchKind { Executable, Shortcut, InternetShortcut }
public enum LaunchEntrySource { Manual, GameManifest }

public sealed record LaunchEntry(Guid Id, string Name, LaunchKind Kind, string Target,
    string Arguments = "", string WorkingDirectory = "", LaunchEntrySource Source = LaunchEntrySource.Manual,
    string SourceKey = "");

public sealed record LaunchCatalog(int SchemaVersion, IReadOnlyList<LaunchEntry> Entries);

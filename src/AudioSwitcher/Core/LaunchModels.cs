namespace AudioSwitcher.Core;

public enum LaunchKind { Executable, Shortcut, InternetShortcut }

public sealed record LaunchEntry(Guid Id, string Name, LaunchKind Kind, string Target,
    string Arguments = "", string WorkingDirectory = "");

public sealed record LaunchCatalog(int SchemaVersion, IReadOnlyList<LaunchEntry> Entries);

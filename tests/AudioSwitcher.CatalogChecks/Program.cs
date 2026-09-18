using AudioSwitcher.Core;
using AudioSwitcher.Platform;
using System.Text.Json;

int passed = 0, failed = 0;
void Check(string name, Action test) { try { test(); Console.WriteLine($"PASS {name}"); passed++; } catch (Exception e) { Console.WriteLine($"FAIL {name}: {e.Message}"); failed++; } }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Reject(Action action) { try { action(); } catch (Exception e) when (e is not TestFailure) { return; } throw new TestFailure("Invalid operation accepted"); }
var root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-CatalogChecks-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var path = Path.Combine(root, "Каталог игр", "programs.json");
    var store = new LaunchCatalogStore(path);
    var exe = Path.Combine(root, "Моя игра.exe"); File.WriteAllText(exe, "fixture, never launched");
    var entry = new LaunchEntry(Guid.NewGuid(), "Моя игра 世界", LaunchKind.Executable, exe, "--name \"two words\" & text", root);
    var catalog = new LaunchCatalog(1, [entry]);
    Check("Missing catalog is empty without creating a file", () => { Equal(true, store.Load().CanSave); Equal(0, store.Load().Catalog.Entries.Count); Equal(false, File.Exists(path)); });
    Check("Unicode and arguments round trip and atomic replacement", () => { store.Save(catalog); Equal(entry, store.Load().Catalog.Entries.Single()); store.Save(catalog with { Entries = [] }); Equal(0, store.Load().Catalog.Entries.Count); Equal(1, Directory.GetFiles(Path.GetDirectoryName(path)!).Length); });
    foreach (var json in new[] { "{", "null", "{\"schemaVersion\":2,\"entries\":[]}", "{\"schemaVersion\":1,\"entries\":null}", "{\"schemaVersion\":1,\"entries\":[null]}", "{\"schemaVersion\":1,\"entries\":[{\"id\":\"" + entry.Id + "\",\"name\":\"X\",\"kind\":999,\"target\":\"x\"}]}", "{\"schemaVersion\":1,\"entries\":[{\"id\":\"" + entry.Id + "\",\"name\":null,\"kind\":\"Executable\",\"target\":\"x\"}]}" })
        Check("Damaged or future catalog cannot be overwritten: " + json, () => { File.WriteAllText(path, json); Equal(false, store.Load().CanSave); Reject(() => store.Save(catalog)); Equal(json, File.ReadAllText(path)); store.Reset(); Equal(0, store.Load().Catalog.Entries.Count); Equal(true, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak").Any(p => File.ReadAllText(p) == json)); });
    Check("Duplicate IDs in file block save and survive reset backup", () => { store.Save(catalog); var document = JsonSerializer.Serialize(new { schemaVersion = 1, entries = new[] { entry, entry } }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }); File.WriteAllText(path, document); Equal(false, store.Load().CanSave); Reject(() => store.Save(catalog)); Equal(document, File.ReadAllText(path)); store.Reset(); Equal(true, store.Load().CanSave); Equal(true, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak").Any(p => File.ReadAllText(p) == document)); });
    Check("Save protects malformed file without prior Load", () => { File.WriteAllText(path, "{damaged"); Reject(() => new LaunchCatalogStore(path).Save(catalog)); Equal("{damaged", File.ReadAllText(path)); store.Reset(); });
    Check("Duplicate and empty IDs reject before changing valid file", () => { store.Save(catalog); var original = File.ReadAllText(path); Reject(() => store.Save(catalog with { Entries = [entry, entry] })); Reject(() => store.Save(catalog with { Entries = [entry with { Id = Guid.Empty }] })); Equal(original, File.ReadAllText(path)); });
    Check("Write failure preserves existing catalog", () => { store.Save(catalog); var original = File.ReadAllText(path); File.SetAttributes(path, FileAttributes.ReadOnly); try { Reject(() => store.Save(catalog with { Entries = [] })); Equal(original, File.ReadAllText(path)); } finally { File.SetAttributes(path, FileAttributes.Normal); } });
    var launcher = new ProgramLaunchService();
    Check("EXE arguments and working directory remain separate", () => { var info = launcher.BuildStartInfo(entry); Equal(exe, info.FileName); Equal(entry.Arguments, info.Arguments); Equal(root, info.WorkingDirectory); Equal(true, info.UseShellExecute); });
    Check("Missing files, wrong extensions and folders reject", () => { Reject(() => launcher.Validate(entry with { Target = exe + "missing" })); Reject(() => launcher.Validate(entry with { Kind = LaunchKind.Shortcut })); Reject(() => launcher.Validate(entry with { WorkingDirectory = root + "missing" })); Reject(() => launcher.Validate(entry with { Kind = (LaunchKind)123 })); });
    var lnk = Path.Combine(root, "game.lnk"); File.WriteAllText(lnk, "fixture");
    Check("Shortcut delegates its own arguments to shell", () => { var shortcut = entry with { Kind = LaunchKind.Shortcut, Target = lnk, Arguments = "", WorkingDirectory = "" }; Equal(lnk, launcher.BuildStartInfo(shortcut).FileName); Reject(() => launcher.Validate(shortcut with { Arguments = "external override" })); });
    var url = Path.Combine(root, "game.url");
    foreach (var target in new[] { "https://example.com/game", "http://example.com/game", "steam://rungameid/123", "com.epicgames.launcher://apps/game?action=launch" })
        Check("Allowed URL " + target, () => { File.WriteAllText(url, "[InternetShortcut]\nURL=" + target); Equal(target, launcher.BuildStartInfo(entry with { Kind = LaunchKind.InternetShortcut, Target = url, Arguments = "", WorkingDirectory = "" }).FileName); });
    foreach (var content in new[] { "[InternetShortcut]\nURL=file:///C:/Windows/x.exe", "[InternetShortcut]\nURL=javascript:alert(1)", "[InternetShortcut]\nURL=cmd:bad", "[Other]\nURL=https://example.com", "[InternetShortcut]\nURL=steam://one\nURL=https://two" })
        Check("Unsafe or malformed URL rejects: " + content, () => { File.WriteAllText(url, content); Reject(() => launcher.Validate(entry with { Kind = LaunchKind.InternetShortcut, Target = url, Arguments = "", WorkingDirectory = "" })); });
    Check("Cancelled launch does not start fixture", () => { using var cts = new CancellationTokenSource(); cts.Cancel(); try { launcher.LaunchAsync(entry, cts.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { return; } throw new TestFailure("Cancellation ignored"); });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
return failed == 0 ? 0 : 1;
sealed class TestFailure(string message) : Exception(message);

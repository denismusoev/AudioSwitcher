using System.Diagnostics;
using System.IO;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramLaunchService
{
    public void Validate(LaunchEntry entry) => BuildStartInfo(entry);

    public ProcessStartInfo BuildStartInfo(LaunchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (string.IsNullOrWhiteSpace(entry.Name)) throw new ArgumentException("Укажите название программы.");
        if (!Enum.IsDefined(entry.Kind)) throw new ArgumentException("Неизвестный тип программы.");
        if (string.IsNullOrWhiteSpace(entry.Target) || !Path.IsPathFullyQualified(entry.Target))
            throw new ArgumentException("Укажите полный путь к EXE или ярлыку.");
        var extension = entry.Kind switch { LaunchKind.Executable => ".exe", LaunchKind.Shortcut => ".lnk", _ => ".url" };
        if (!string.Equals(Path.GetExtension(entry.Target), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Для выбранного типа требуется файл {extension}.");
        if (!File.Exists(entry.Target)) throw new FileNotFoundException("Файл программы или ярлыка не найден.", entry.Target);
        if (entry.Arguments is null || entry.WorkingDirectory is null) throw new ArgumentException("Параметры программы не могут быть пустыми данными.");
        if (entry.Kind != LaunchKind.Executable && (entry.Arguments.Length != 0 || entry.WorkingDirectory.Length != 0))
            throw new ArgumentException("Аргументы и рабочая папка ярлыка задаются в самом ярлыке.");
        if (entry.WorkingDirectory.Length != 0 && (!Path.IsPathFullyQualified(entry.WorkingDirectory) || !Directory.Exists(entry.WorkingDirectory)))
            throw new DirectoryNotFoundException("Рабочая папка программы не найдена. Укажите полный путь к существующей папке.");
        var target = entry.Kind == LaunchKind.InternetShortcut ? ReadInternetShortcut(entry.Target) : entry.Target;
        return new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            Arguments = entry.Kind == LaunchKind.Executable ? entry.Arguments : "",
            WorkingDirectory = entry.Kind == LaunchKind.Executable ? entry.WorkingDirectory : ""
        };
    }

    public Task<int?> LaunchAsync(LaunchEntry entry, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = BuildStartInfo(entry);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(info);
            return process?.Id;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("Не удалось отправить команду запуска. Проверьте файл и регистрацию программы или игрового протокола: " + e.Message, e);
        }
    }, cancellationToken);

    private static string ReadInternetShortcut(string path)
    {
        bool inSection = false;
        string? target = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { inSection = line.Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase); continue; }
            var separator = line.IndexOf('=');
            if (!inSection || separator < 0 || !line[..separator].Trim().Equals("URL", StringComparison.OrdinalIgnoreCase)) continue;
            if (target is not null) throw new InvalidDataException("Ярлык содержит несколько целей URL.");
            target = line[(separator + 1)..].Trim();
        }
        if (target is null || !Uri.TryCreate(target, UriKind.Absolute, out var uri)) throw new InvalidDataException("В интернет-ярлыке отсутствует корректная цель URL.");
        if (uri.Scheme is not ("https" or "http" or "steam" or "com.epicgames.launcher"))
            throw new InvalidDataException("Схема интернет-ярлыка не поддерживается. Разрешены https, http, steam и com.epicgames.launcher.");
        if (uri.Scheme is "https" or "http" && string.IsNullOrWhiteSpace(uri.Host)) throw new InvalidDataException("В URL отсутствует адрес сайта.");
        return target;
    }
}

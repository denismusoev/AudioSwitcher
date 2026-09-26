using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace AudioSwitcher;

internal static class DiagnosticLog
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const int MaxFiles = 5;
    private const long SuppressionWindowMilliseconds = 30_000;
    private const int MaxRateKeys = 256;
    private static readonly object Sync = new();
    private static readonly object RateSync = new();
    private static readonly Dictionary<string, RateState> Rates = new(StringComparer.Ordinal);

    internal static void Error(string operation, Exception error, IReadOnlyDictionary<string, object?>? context = null) =>
        Write("ERROR", operation, error, context);

    internal static void Critical(string operation, Exception error, IReadOnlyDictionary<string, object?>? context = null) =>
        Write("CRITICAL", operation, error, context);

    private static void Write(string severity, string operation, Exception error, IReadOnlyDictionary<string, object?>? context)
    {
        try
        {
            string rateKey = string.Concat(severity, "|", operation, "|", error.GetType().FullName, "|", error.Message);
            if (!ShouldWrite(rateKey, Environment.TickCount64, out int suppressed)) return;
            string entry = FormatEntry(severity, operation, error, context, suppressed);
            lock (Sync)
            {
                if (TryAppend(Path.Combine(AppContext.BaseDirectory, "logs"), entry, MaxFileBytes, MaxFiles)) return;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _ = TryAppend(Path.Combine(local, "AudioSwitcher", "logs"), entry, MaxFileBytes, MaxFiles);
            }
        }
        catch
        {
            // A crash logger must never replace the original failure with a recursive logger failure.
        }
    }

    internal static bool ShouldWrite(string key, long timestamp, out int suppressed)
    {
        lock (RateSync)
        {
            if (Rates.TryGetValue(key, out var state))
            {
                if (timestamp - state.LastWritten < SuppressionWindowMilliseconds)
                {
                    Rates[key] = state with { Suppressed = state.Suppressed + 1 };
                    suppressed = 0;
                    return false;
                }

                suppressed = state.Suppressed;
                Rates[key] = new(timestamp, 0);
                return true;
            }

            if (Rates.Count >= MaxRateKeys)
            {
                string oldest = Rates.MinBy(pair => pair.Value.LastWritten).Key;
                Rates.Remove(oldest);
            }
            Rates.Add(key, new(timestamp, 0));
            suppressed = 0;
            return true;
        }
    }

    internal static string FormatEntry(string severity, string operation, Exception error,
        IReadOnlyDictionary<string, object?>? context, int suppressed)
    {
        var text = new StringBuilder(1024);
        text.Append(DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(" severity=").Append(Clean(severity))
            .Append(" operation=").Append(Clean(operation))
            .Append(" processId=").Append(Environment.ProcessId)
            .Append(" managedThreadId=").Append(Environment.CurrentManagedThreadId)
            .Append(" appVersion=").Append(Clean(Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"))
            .Append(" windowsVersion=").Append(Clean(Environment.OSVersion.VersionString))
            .Append(" exceptionType=").Append(Clean(error.GetType().FullName ?? error.GetType().Name))
            .Append(" hresult=0x").Append(error.HResult.ToString("X8", CultureInfo.InvariantCulture));
        if (FindWin32(error) is int nativeError) text.Append(" win32Error=").Append(nativeError);
        if (suppressed > 0) text.Append(" suppressed=").Append(suppressed);
        if (context != null)
            foreach (var pair in context.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                text.Append(' ').Append(Clean(pair.Key)).Append('=').Append(Clean(pair.Value));
        text.AppendLine()
            .Append("message=").AppendLine(Clean(error.Message))
            .Append("stackTrace=").AppendLine(Clean(error.StackTrace ?? "<unavailable>"));
        for (Exception? inner = error.InnerException; inner != null; inner = inner.InnerException)
        {
            text.Append("innerExceptionType=").AppendLine(Clean(inner.GetType().FullName ?? inner.GetType().Name))
                .Append("innerMessage=").AppendLine(Clean(inner.Message))
                .Append("innerStackTrace=").AppendLine(Clean(inner.StackTrace ?? "<unavailable>"));
        }
        return text.AppendLine().ToString();
    }

    internal static bool TryAppend(string directory, string entry, long maxFileBytes, int maxFiles)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "AudioSwitcher.log");
            long entryBytes = Encoding.UTF8.GetByteCount(entry);
            if (File.Exists(path) && new FileInfo(path).Length > 0 && new FileInfo(path).Length + entryBytes > maxFileBytes)
                Rotate(directory, path, Math.Max(1, maxFiles));
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(entry);
            writer.Flush();
            return true;
        }
        catch
        {
            // The caller tries the per-user fallback location next.
            return false;
        }
    }

    private static void Rotate(string directory, string activePath, int maxFiles)
    {
        if (maxFiles == 1)
        {
            File.Delete(activePath);
            return;
        }

        string oldest = Path.Combine(directory, $"AudioSwitcher.{maxFiles - 1}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (int index = maxFiles - 2; index >= 1; index--)
        {
            string source = Path.Combine(directory, $"AudioSwitcher.{index}.log");
            if (File.Exists(source)) File.Move(source, Path.Combine(directory, $"AudioSwitcher.{index + 1}.log"));
        }
        File.Move(activePath, Path.Combine(directory, "AudioSwitcher.1.log"));
    }

    private static int? FindWin32(Exception error)
    {
        for (Exception? current = error; current != null; current = current.InnerException)
            if (current is Win32Exception win32) return win32.NativeErrorCode;
        return null;
    }

    private static string Clean(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?
        .Replace('\r', ' ').Replace('\n', ' ') ?? "<null>";

    private readonly record struct RateState(long LastWritten, int Suppressed);
}

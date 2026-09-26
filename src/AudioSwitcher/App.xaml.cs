using System.IO;
using System.Text.Json;
using System.Windows;
using AudioSwitcher.Platform;

namespace AudioSwitcher;
public partial class App : Application
{
    private Mutex? instance;
    private bool globalHandlersRegistered;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        RegisterGlobalHandlers();
        // Diagnostics only enumerate devices; they never modify Windows settings.
        if (e.Args.Length == 2 && e.Args[0] == "--diagnostics")
        {
            try
            {
                var data = new { Audio = new AudioService().GetDevices(), Displays = new DisplayService().GetDevices(), Gamepad = Gamepad.TryRead(out _) };
                File.WriteAllText(e.Args[1], JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(e.Args[1], ex.ToString()); Shutdown(1); }
            return;
        }
        instance = new Mutex(true, "Local\\AudioSwitcher.Portable", out bool first);
        if (!first) { Shutdown(); return; }
        var window = new MainWindow();
        window.Show();
    }

    private void RegisterGlobalHandlers()
    {
        if (globalHandlersRegistered) return;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        globalHandlersRegistered = true;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        DiagnosticLog.Critical("application.dispatcher_unhandled", args.Exception);
        try { MessageBox.Show(args.Exception.Message, "AudioSwitcher"); }
        catch (Exception error) { DiagnosticLog.Error("application.crash_dialog", error); }
        Shutdown(1);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        var error = args.ExceptionObject as Exception ?? new Exception($"Unhandled object: {args.ExceptionObject?.GetType().FullName ?? "null"}");
        DiagnosticLog.Critical("application.domain_unhandled", error,
            new Dictionary<string, object?> { ["isTerminating"] = args.IsTerminating });
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        DiagnosticLog.Error("application.task_unobserved", args.Exception);
        args.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (globalHandlersRegistered)
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            globalHandlersRegistered = false;
        }
        instance?.Dispose();
        base.OnExit(e);
    }
}

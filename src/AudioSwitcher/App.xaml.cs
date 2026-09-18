using System.IO;
using System.Text.Json;
using System.Windows;
using AudioSwitcher.Platform;

namespace AudioSwitcher;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "AudioSwitcher"); args.Handled = true; Shutdown(1); };
        new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}

using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Windows.Interop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string title = args.Length > 0 ? args[0] : "AudioSwitcher fixture";
        if (args.Contains("--dpi-unaware")) SetThreadDpiAwarenessContext(-1);
        if (args.Contains("--system-aware")) SetThreadDpiAwarenessContext(-2);
        var application = new Application();
        var window = new Window { Title = title, Width = 640, Height = 400, Content = title };
        if (args.Contains("--borderless")) { window.WindowStyle = WindowStyle.None; window.ResizeMode = ResizeMode.NoResize; }
        if (args.Contains("--minimized")) window.WindowState = WindowState.Minimized;
        if (args.Contains("--maximized")) window.WindowState = WindowState.Maximized;
        if (args.Contains("--multiple"))
        {
            var second = new Window { Title = title + " second", Width = 300, Height = 200 };
            second.Loaded += (_, _) => ShowWindow(new WindowInteropHelper(second).Handle, 4);
            second.Show();
        }
        string? signal = args.FirstOrDefault(a => a.StartsWith("--signal="))?[9..];
        int.TryParse(args.FirstOrDefault(a => a.StartsWith("--grant="))?[8..], out int controller);
        if (signal != null || controller > 0)
        {
            // File commands are polled by a worker, even while the window thread is stuck.
            _ = Task.Run(async () => {
                while (true)
                {
                    // Only this owned fixture, when foreground, can authorize its test controller.
                    if (controller > 0 && GetWindowThreadProcessId(GetForegroundWindow(), out int foregroundPid) != 0 && foregroundPid == Environment.ProcessId)
                        AllowSetForegroundWindow(controller);
                    if (signal != null && File.Exists(signal))
                    {
                        string command = File.ReadAllText(signal); File.Delete(signal);
                        if (command == "hang") _ = window.Dispatcher.BeginInvoke(new Action(() => Thread.Sleep(Timeout.Infinite)), DispatcherPriority.Send);
                        if (command == "exit") Environment.Exit(0);
                    }
                    await Task.Delay(50);
                }
            });
        }
        application.Run(window);
    }
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}

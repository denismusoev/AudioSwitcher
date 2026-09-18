using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using AudioSwitcher.Platform;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool mutate = args.Contains("--switch-and-restore");
        if (args.Contains("--trace-display-test")) { TraceDisplayTest(); return 0; }
        var audio = new AudioService();
        var displays = new DisplayService();
        int passed = 0, failed = 0;
        void Check(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine($"PASS {name}"); }
            catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e}"); }
        }
        Check("Enumerate actual audio outputs", () => { if (audio.GetDevices().Count == 0) throw new Exception("No audio outputs"); });
        Check("Enumerate actual monitors", () => { if (displays.GetDevices().Count == 0) throw new Exception("No monitors"); });
        Check("Reject nonexistent audio device", () => {
            try { audio.SetDefault("missing-test-device"); } catch (InvalidOperationException) { return; }
            throw new Exception("Missing audio device accepted");
        });
        Check("Reject nonexistent display", () => {
            try { displays.SetPrimaryDisplay("missing-test-display"); } catch (InvalidOperationException) { return; }
            throw new Exception("Missing display accepted");
        });
        if (mutate)
        {
            Check("Switch audio and restore all three prior roles", () => {
                var before = CaptureAudioDefaults();
                var alternative = audio.GetDevices().First(d => d.Id != before[1]);
                try
                {
                    audio.SetDefault(alternative.Id);
                    if (CaptureAudioDefaults().Any(id => id != alternative.Id)) throw new Exception("Audio role did not change");
                }
                finally { RestoreAudioDefaults(before); }
                if (!before.SequenceEqual(CaptureAudioDefaults())) throw new Exception("Audio defaults not restored");
            });
            Check("Switch primary display and restore original layout", () => {
                var before = CaptureDisplays();
                var devices = displays.GetDevices();
                string original = devices.First(d => d.IsDefault).Id;
                string alternative = devices.First(d => !d.IsDefault).Id;
                try
                {
                    displays.SetPrimaryDisplay(alternative);
                    if (!displays.GetDevices().Any(d => d.Id == alternative && d.IsDefault)) throw new Exception("Primary monitor did not change");
                }
                finally { displays.SetPrimaryDisplay(original); }
                if (CaptureDisplays() != before) throw new Exception("Display positions, resolution or refresh changed after restore");
            });
        }
        Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: {(mutate ? 0 : 2)}");
        return failed == 0 ? 0 : 1;
    }
    // Test-only reflection keeps snapshot/restore helpers out of the shipped application API.
    private static string[] CaptureAudioDefaults()
    {
        object enumerator = typeof(AudioService).GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var type = typeof(AudioService).GetNestedType("Enumerator", BindingFlags.NonPublic)!;
        var deviceType = typeof(AudioService).GetNestedType("Device", BindingFlags.NonPublic)!;
        try
        {
            return Enumerable.Range(0, 3).Select(role => {
                object?[] values = [0, role, null];
                Marshal.ThrowExceptionForHR((int)type.GetMethod("GetDefaultAudioEndpoint")!.Invoke(enumerator, values)!);
                object device = values[2]!;
                try { object?[] ids = [null]; Marshal.ThrowExceptionForHR((int)deviceType.GetMethod("GetId")!.Invoke(device, ids)!); return (string)ids[0]!; }
                finally { Marshal.ReleaseComObject(device); }
            }).ToArray();
        }
        finally { Marshal.ReleaseComObject(enumerator); }
    }
    private static void RestoreAudioDefaults(string[] ids)
    {
        object policy = Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
        var method = typeof(AudioService).GetNestedType("Policy", BindingFlags.NonPublic)!.GetMethod("SetDefaultEndpoint")!;
        try { for (int role = 0; role < 3; role++) Marshal.ThrowExceptionForHR((int)method.Invoke(policy, [ids[role], role])!); }
        finally { Marshal.ReleaseComObject(policy); }
    }
    private static string CaptureDisplays()
    {
        var snapshots = (IEnumerable)typeof(DisplayService).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var rows = new List<string>();
        foreach (object snapshot in snapshots)
        {
            var type = snapshot.GetType();
            object mode = type.GetProperty("Mode")!.GetValue(snapshot)!;
            rows.Add(type.GetProperty("Id")!.GetValue(snapshot) + ":" + type.GetProperty("Primary")!.GetValue(snapshot) + ":" + string.Join(",", new[] { "X", "Y", "Width", "Height", "Frequency" }.Select(name => mode.GetType().GetField(name)!.GetValue(mode))));
        }
        return string.Join("|", rows.Order());
    }
    private static void TraceDisplayTest()
    {
        var snapshots = ((IEnumerable)typeof(DisplayService).GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!).Cast<object>().ToArray();
        var alternate = snapshots.First(s => !(bool)s.GetType().GetProperty("Primary")!.GetValue(s)!);
        object alternateMode = alternate.GetType().GetProperty("Mode")!.GetValue(alternate)!;
        int originX = (int)alternateMode.GetType().GetField("X")!.GetValue(alternateMode)!;
        int originY = (int)alternateMode.GetType().GetField("Y")!.GetValue(alternateMode)!;
        var method = typeof(DisplayService).GetMethod("ChangeDisplaySettingsEx", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (object snapshot in snapshots)
        {
            var type = snapshot.GetType();
            object mode = type.GetProperty("Mode")!.GetValue(snapshot)!;
            string id = (string)type.GetProperty("Id")!.GetValue(snapshot)!;
            Console.WriteLine($"{id} current mode fields: {mode.GetType().GetField("Fields")!.GetValue(mode):X}");
            object?[] values = [id, mode, IntPtr.Zero, (uint)2, IntPtr.Zero];
            Console.WriteLine($"Full current mode CDS_TEST: {method.Invoke(null, values)}");
            int x = (int)mode.GetType().GetField("X")!.GetValue(mode)!;
            int y = (int)mode.GetType().GetField("Y")!.GetValue(mode)!;
            uint fields = (uint)mode.GetType().GetField("Fields")!.GetValue(mode)!;
            mode.GetType().GetField("X")!.SetValue(mode, x - originX);
            mode.GetType().GetField("Y")!.SetValue(mode, y - originY);
            values[1] = mode;
            Console.WriteLine($"Full rebased mode {x-originX},{y-originY} CDS_TEST: {method.Invoke(null, values)}");
            mode.GetType().GetField("Fields")!.SetValue(mode, fields & ~0x20u);
            values[1] = mode;
            Console.WriteLine($"Rebased mode without DM_POSITION CDS_TEST: {method.Invoke(null, values)}");
            mode.GetType().GetField("Fields")!.SetValue(mode, fields);
            mode.GetType().GetField("X")!.SetValue(mode, 0);
            mode.GetType().GetField("Y")!.SetValue(mode, 0);
            values[1] = mode;
            Console.WriteLine($"Full origin mode CDS_TEST: {method.Invoke(null, values)}");
            values[3] = (uint)0x12;
            Console.WriteLine($"Full origin mode CDS_TEST + SET_PRIMARY: {method.Invoke(null, values)}");
            values[3] = (uint)2;
            mode.GetType().GetField("Fields")!.SetValue(mode, (uint)0x20);
            values[1] = mode;
            Console.WriteLine($"Position-only current mode CDS_TEST: {method.Invoke(null, values)}");
            mode.GetType().GetField("X")!.SetValue(mode, 0);
            mode.GetType().GetField("Y")!.SetValue(mode, 0);
            values[1] = mode;
            Console.WriteLine($"Position-only origin CDS_TEST: {method.Invoke(null, values)}");
        }
    }
}

using System.Runtime.InteropServices;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;
public sealed class DisplayService
{
    private const uint UpdateRegistry = 1, Test = 2, SetPrimary = 0x10, NoReset = 0x10000000;
    private sealed record Snapshot(string Id, string Name, bool Primary, Mode Mode);
    private static List<Snapshot> Read()
    {
        var result = new List<Snapshot>();
        for (uint i = 0; ; i++)
        {
            var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.Flags & 1) == 0 || (adapter.Flags & 8) != 0) continue;
            var mode = new Mode { Size = (ushort)Marshal.SizeOf<Mode>() };
            if (!EnumDisplaySettings(adapter.Name, -1, ref mode)) continue;
            var monitor = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
            string name = EnumDisplayDevices(adapter.Name, 0, ref monitor, 0) ? monitor.Description : adapter.Description;
            result.Add(new(adapter.Name, name, (adapter.Flags & 4) != 0, mode));
        }
        return result;
    }
    public IReadOnlyList<DeviceOption> GetDevices() => Read().Select(d => new DeviceOption(d.Id,
        $"Экран {d.Id.Replace("\\\\.\\DISPLAY", "")} · {d.Name}", $"{d.Mode.Width} × {d.Mode.Height} · {d.Mode.Frequency} Гц", d.Primary, IsDisplay: true)).ToArray();
    public void SetPrimaryDisplay(string id)
    {
        var original = Read();
        var primary = original.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("Монитор отключён. Обновите список.");
        if (primary.Primary) return;
        var positions = DisplayLayout.Rebase(original.Select(d => new DisplayPosition(d.Id, d.Mode.X, d.Mode.Y)).ToArray(), id);
        var updated = original.Select((d, i) => {
            var mode = d.Mode; mode.X = positions[i].X; mode.Y = positions[i].Y;
            mode.Fields |= 0x20; // DM_POSITION
            return d with { Mode = mode, Primary = d.Id == id };
        }).ToArray();
        foreach (var d in updated)
        {
            var mode = d.Mode;
            // CDS_TEST evaluates one monitor against the current topology. Moving the
            // current primary away from (0,0) fails until the new primary is staged.
            // Preflight the unchanged video modes; stage all positions together below.
            mode.Fields &= ~0x20u;
            Check(ChangeDisplaySettingsEx(d.Id, ref mode, IntPtr.Zero, Test, IntPtr.Zero), "Проверка конфигурации");
        }
        bool staged = false;
        try
        {
            foreach (var d in updated.OrderByDescending(d => d.Primary))
            {
                var mode = d.Mode;
                staged = true;
                Check(ChangeDisplaySettingsEx(d.Id, ref mode, IntPtr.Zero, UpdateRegistry | NoReset | (d.Primary ? SetPrimary : 0), IntPtr.Zero), "Запись конфигурации");
            }
            Check(Apply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero), "Применение конфигурации");
            if (!Read().Any(d => d.Id == id && d.Primary)) throw new InvalidOperationException("Windows не подтвердила смену главного монитора.");
        }
        catch (Exception ex)
        {
            if (!staged) throw;
            bool restored = true;
            foreach (var d in original.OrderByDescending(d => d.Primary))
            {
                var mode = d.Mode;
                if (ChangeDisplaySettingsEx(d.Id, ref mode, IntPtr.Zero, UpdateRegistry | NoReset | (d.Primary ? SetPrimary : 0), IntPtr.Zero) != 0) restored = false;
            }
            if (Apply(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) != 0) restored = false;
            throw new InvalidOperationException(restored ? $"{ex.Message} Исходные настройки восстановлены." : $"{ex.Message} Проверьте расположение экранов в параметрах Windows.", ex);
        }
    }
    private static void Check(int result, string operation) { if (result != 0) throw new InvalidOperationException($"{operation}: Windows вернула код {result}."); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    // DEVMODEW: preserve the entire native structure, including the current resolution and refresh rate.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Mode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int X, Y;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, DisplayFlags, Frequency;
        public uint IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumDisplaySettings(string device, int index, ref Mode mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ChangeDisplaySettingsEx(string device, ref Mode mode, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")] private static extern int Apply(string? device, IntPtr mode, IntPtr hwnd, uint flags, IntPtr param);
}

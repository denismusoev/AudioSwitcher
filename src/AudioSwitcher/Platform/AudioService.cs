using System.Runtime.InteropServices;

namespace AudioSwitcher.Platform;
public sealed class AudioService
{
    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static Enumerator Create() => (Enumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
    public IReadOnlyList<DeviceOption> GetDevices()
    {
        Enumerator? enumerator = null; Collection? collection = null; Device? defaultDevice = null;
        try
        {
            enumerator = Create();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out defaultDevice) >= 0) { Check(defaultDevice.GetId(out defaultId)); }
            Check(enumerator.EnumAudioEndpoints(0, 1, out collection));
            Check(collection.GetCount(out uint count));
            var result = new List<DeviceOption>();
            for (uint i = 0; i < count; i++)
            {
                Device? device = null; PropertyStore? store = null;
                try
                {
                    Check(collection.Item(i, out device)); Check(device.GetId(out string id));
                    Check(device.OpenPropertyStore(0, out store));
                    var key = new PropertyKey { Format = new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), Id = 14 };
                    Check(store.GetValue(ref key, out var value));
                    string name;
                    try { name = value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) ?? id : id; }
                    finally { PropVariantClear(ref value); }
                    result.Add(new(id, name, "Устройство вывода звука", id == defaultId));
                }
                finally { Release(store); Release(device); }
            }
            return result.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToArray();
        }
        finally { Release(defaultDevice); Release(collection); Release(enumerator); }
    }
    public void SetDefault(string id)
    {
        // IPolicyConfig is undocumented, but commonly used by Windows audio utilities.
        // Capture each role independently so a partial failure can restore the prior defaults.
        Enumerator? enumerator = null; Policy? policy = null;
        var previous = new string?[3];
        int changed = 0;
        try
        {
            if (!GetDevices().Any(d => d.Id == id)) throw new InvalidOperationException("Аудиоустройство отключено. Обновите список.");
            enumerator = Create();
            for (int role = 0; role < 3; role++)
            {
                Device? device = null;
                try { if (enumerator.GetDefaultAudioEndpoint(0, role, out device) >= 0) Check(device.GetId(out previous[role])); }
                finally { Release(device); }
            }
            policy = (Policy)Activator.CreateInstance(Type.GetTypeFromCLSID(new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"))!)!;
            for (int role = 0; role < 3; role++) { Check(policy.SetDefaultEndpoint(id, role)); changed++; }
        }
        catch (Exception ex)
        {
            bool restored = true;
            for (int role = 0; role < changed; role++)
                if (previous[role] is not string old || policy is null || policy.SetDefaultEndpoint(old, role) < 0) restored = false;
            throw new InvalidOperationException(restored ? $"Не удалось переключить звук: {ex.Message}" : "Часть аудионастроек изменена. Проверьте выход звука в параметрах Windows.", ex);
        }
        finally { Release(policy); Release(enumerator); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref Variant value);

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface Enumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint mask, out Collection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out Device device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out Device device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface Collection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out Device device);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface Device
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, out IntPtr instance);
        [PreserveSig] int OpenPropertyStore(uint access, out PropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface PropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out Variant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref Variant value);
        [PreserveSig] int Commit();
    }
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface Policy
    {
        [PreserveSig] int GetMixFormat(IntPtr id, IntPtr format);
        [PreserveSig] int GetDeviceFormat(IntPtr id, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(IntPtr id);
        [PreserveSig] int SetDeviceFormat(IntPtr id, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod(IntPtr id, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(IntPtr id, IntPtr period);
        [PreserveSig] int GetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int SetShareMode(IntPtr id, IntPtr mode);
        [PreserveSig] int GetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(IntPtr id, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr id, int visible);
    }
}

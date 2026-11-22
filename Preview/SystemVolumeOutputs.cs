using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed partial class SystemVolume
{
    internal sealed record Output(string Id, string Name, bool Selected);
    private const int Render = 0, Console = 0, Multimedia = 1;
    private const uint Active = 1;
    private static readonly PropertyKey FriendlyName = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    internal Output[]? ReadOutputs()
    {
        AudioDeviceCollection? collection = null;
        try
        {
            enumerator ??= (DeviceEnumerator)(object)new DeviceEnumeratorClass();
            string? selected = DefaultId(Multimedia);
            enumerator.EnumAudioEndpoints(Render, Active, out collection);
            collection.GetCount(out uint count);
            var outputs = new List<Output>();
            for (uint index = 0; index < count; index++)
            {
                collection.Item(index, out var device);
                try
                {
                    device.GetId(out string id);
                    outputs.Add(new(id, ReadName(device), id == selected));
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            return outputs.OrderByDescending(output => output.Selected)
                .ThenBy(output => output.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is COMException or InvalidCastException) { return null; }
        finally { if (collection is not null) Marshal.ReleaseComObject(collection); }
    }

    internal bool SwitchOutput(string id)
    {
        if (ReadOutputs()?.Any(output => output.Id == id) != true) return false;
        var priorConsole = DefaultId(Console);
        var priorMultimedia = DefaultId(Multimedia);
        if (id == priorConsole && id == priorMultimedia) return true;
        object? instance = null;
        try
        {
            instance = new AudioPolicyClass();
            var policy = (AudioPolicy)instance;
            // Communications devices have a separate default and are left to Windows.
            if (id != priorMultimedia) policy.SetDefaultEndpoint(id, Multimedia);
            if (id != priorConsole) policy.SetDefaultEndpoint(id, Console);
            if (DefaultId(Multimedia) == id && DefaultId(Console) == id) return true;
            RestoreIfChanged(policy, id, priorMultimedia, Multimedia);
            RestoreIfChanged(policy, id, priorConsole, Console);
            return false;
        }
        catch (Exception error) when (error is COMException or InvalidCastException or ArgumentException)
        {
            if (instance is AudioPolicy policy)
            {
                RestoreIfChanged(policy, id, priorMultimedia, Multimedia);
                RestoreIfChanged(policy, id, priorConsole, Console);
            }
            return false;
        }
        finally { if (instance is not null) Marshal.ReleaseComObject(instance); }
    }

    private void RestoreIfChanged(AudioPolicy policy, string target, string? previous, int role)
    {
        if (previous is null || previous == target || DefaultId(role) != target) return;
        try { policy.SetDefaultEndpoint(previous, role); }
        catch (Exception error) when (error is COMException or ArgumentException) { }
    }

    private string? DefaultId(int role)
    {
        try
        {
            enumerator!.GetDefaultAudioEndpoint(Render, role, out var device);
            try { device.GetId(out string id); return id; }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch (COMException) { return null; }
    }

    private static string ReadName(AudioDevice device)
    {
        try
        {
            device.OpenPropertyStore(0, out var properties);
            try
            {
                properties.GetValue(in FriendlyName, out var value);
                try
                {
                    if (value.Type == 31 && Marshal.PtrToStringUni(value.Pointer) is { Length: > 0 } text)
                        return text;
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(properties); }
        }
        catch (COMException) { }
        return "音频输出设备";
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out AudioDevice device);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid format, uint property)
    {
        private readonly Guid format = format;
        private readonly uint property = property;
    }

    // PROPVARIANT has a 16-byte union on the supported x64 build.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyValue
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(in PropertyKey key, out PropertyValue value);
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class AudioPolicyClass { }

    // The Windows desktop audio policy interface is optional. Unused vtable slots are declared
    // only to reach SetDefaultEndpoint, and failures are surfaced with a Windows settings link.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioPolicy
    {
        void _VtblGap1_10();
        void SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}

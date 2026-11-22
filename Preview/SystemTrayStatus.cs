using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace AITaskbar;

internal static class SystemTrayStatus
{
    internal readonly record struct Wifi(int Bars, string? Name);
    internal readonly record struct Battery(int? Percent, bool PluggedIn, bool Charging);

    internal static Wifi? TryReadWifi()
    {
        try
        {
            var preferred = NetworkInformation.GetInternetConnectionProfile();
            if (preferred is { IsWlanConnectionProfile: true }) return ReadProfile(preferred);
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (profile.IsWlanConnectionProfile &&
                    profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None)
                    return ReadProfile(profile);
            }
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { return null; }
        return new Wifi();
    }

    private static Wifi ReadProfile(ConnectionProfile profile)
    {
        int bars = profile.GetSignalBars() is byte count
            ? Math.Clamp((int)Math.Ceiling(count * 3 / 5d), 1, 3)
            : 1;
        return new(bars, profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? profile.ProfileName);
    }

    internal static Battery ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status) || status.Percent > 100 ||
            (status.BatteryFlag != 255 && (status.BatteryFlag & 128) != 0))
            return default;
        return new(status.Percent, status.AcLineStatus == 1, status.BatteryFlag != 255 && (status.BatteryFlag & 8) != 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, Percent, SystemStatus;
        public uint LifeTime, FullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}

internal sealed partial class SystemVolume : IDisposable
{
    internal readonly record struct State(int Level, bool Muted);
    private DeviceEnumerator? enumerator;
    private EndpointVolume? endpoint;
    private string? deviceId;

    internal State? Read()
    {
        try
        {
            var current = GetEndpoint();
            if (current is null) return null;
            current.GetMasterVolumeLevelScalar(out float level);
            current.GetMute(out bool muted);
            return new((int)Math.Round(level * 100), muted);
        }
        catch (COMException) { ResetEndpoint(); return null; }
    }

    internal void SetLevel(int value) => Change(current =>
    {
        current.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 100) / 100f, 0);
        if (value > 0)
        {
            current.GetMute(out bool muted);
            if (muted) current.SetMute(false, 0);
        }
    });

    internal void ToggleMute() => Change(current =>
    {
        current.GetMute(out bool muted);
        current.SetMute(!muted, 0);
    });

    private void Change(Action<EndpointVolume> change)
    {
        try
        {
            if (GetEndpoint() is { } current) change(current);
        }
        catch (COMException) { ResetEndpoint(); }
    }

    private EndpointVolume? GetEndpoint()
    {
        enumerator ??= (DeviceEnumerator)(object)new DeviceEnumeratorClass();
        enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out var device);
        try
        {
            device.GetId(out string id);
            if (id == deviceId) return endpoint;
            ResetEndpoint();
            Guid iid = typeof(EndpointVolume).GUID;
            device.Activate(ref iid, 23, 0, out var activated); // CLSCTX_ALL
            endpoint = (EndpointVolume)activated;
            deviceId = id;
            return endpoint;
        }
        finally { Marshal.ReleaseComObject(device); }
    }

    private void ResetEndpoint()
    {
        if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
        endpoint = null;
        deviceId = null;
    }

    public void Dispose()
    {
        ResetEndpoint();
        if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        enumerator = null;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class DeviceEnumeratorClass { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface DeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, uint stateMask, out AudioDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out AudioDevice device);
        void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out AudioDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioDevice
    {
        void Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        void OpenPropertyStore(uint access, out AudioPropertyStore properties);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out uint state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface EndpointVolume
    {
        void RegisterControlChangeNotify(nint callback);
        void UnregisterControlChangeNotify(nint callback);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float decibels, nint context);
        void SetMasterVolumeLevelScalar(float level, nint context);
        void GetMasterVolumeLevel(out float decibels);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float decibels, nint context);
        void SetChannelVolumeLevelScalar(uint channel, float level, nint context);
        void GetChannelVolumeLevel(uint channel, out float decibels);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, nint context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}

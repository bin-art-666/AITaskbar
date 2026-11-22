using System.Runtime.InteropServices;

namespace AITaskbar;

internal sealed partial class SystemVolume
{
    internal sealed record BluetoothOutput(Guid Container, string Id, bool Connected);
    private const uint Unplugged = 8;
    private static readonly PropertyKey AudioContainer = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly Guid BluetoothAudioProperty = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    internal static BluetoothOutput[]? ReadBluetoothOutputs(IReadOnlySet<Guid> containers)
    {
        if (containers.Count == 0) return [];
        DeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (DeviceEnumerator)(object)new DeviceEnumeratorClass();
            return ReadBluetoothOutputs(enumerator, containers);
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error) || error is InvalidCastException) { return null; }
        finally { ReleaseAudioObject(enumerator); }
    }

    private static BluetoothOutput[] ReadBluetoothOutputs(DeviceEnumerator enumerator, IReadOnlySet<Guid> containers)
    {
        enumerator.EnumAudioEndpoints(Render, Active | Unplugged, out var collection);
        try
        {
            var results = new List<BluetoothOutput>();
            collection.GetCount(out uint count);
            for (uint index = 0; index < count; index++)
            {
                collection.Item(index, out var endpoint);
                try
                {
                    var container = ReadContainer(endpoint);
                    if (container == Guid.Empty || !containers.Contains(container)) continue;
                    endpoint.GetId(out string id);
                    endpoint.GetState(out uint state);
                    results.Add(new(container, id, (state & Active) != 0));
                }
                catch (COMException) { } // Endpoints can disappear while being enumerated.
                finally { ReleaseAudioObject(endpoint); }
            }
            return results.ToArray();
        }
        finally { ReleaseAudioObject(collection); }
    }

    private static Guid ReadContainer(AudioDevice endpoint)
    {
        endpoint.OpenPropertyStore(0, out var store);
        try
        {
            store.GetValue(in AudioContainer, out var value);
            try
            {
                if (value.Pointer == 0) return Guid.Empty;
                if (value.Type == 72) return Marshal.PtrToStructure<Guid>(value.Pointer); // VT_CLSID
                return value.Type == 31 && Guid.TryParse(Marshal.PtrToStringUni(value.Pointer), out var id)
                    ? id : Guid.Empty;
            }
            finally { PropVariantClear(ref value); }
        }
        finally { ReleaseAudioObject(store); }
    }

    internal static string? RequestBluetoothAudio(Guid container, bool connect)
    {
        if (container == Guid.Empty) return "Windows 没有为此设备提供可控制的蓝牙音频输出。";
        DeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (DeviceEnumerator)(object)new DeviceEnumeratorClass();
            var outputs = ReadBluetoothOutputs(enumerator, new HashSet<Guid> { container });
            if (outputs.Length == 0) return "找不到此设备的音频输出。请确认耳机已配对、已开机且在附近。";
            if (outputs.Any(output => output.Connected) == connect) return null;
            bool accepted = false;
            int? failure = null;
            foreach (var output in outputs.Where(output => output.Connected != connect))
            {
                int? result;
                try { result = SendBluetoothAudioRequest(enumerator, output.Id, connect); }
                catch (Exception error) when (QuickControlRadios.IsUnavailable(error) || error is InvalidCastException)
                { result = error.HResult; }
                if (result is >= 0)
                {
                    if (connect) return null;
                    accepted = true;
                }
                else if (result.HasValue && (failure is null || failure == unchecked((int)0x80070490)))
                    failure = result;
            }
            return accepted ? null : BluetoothAudioFailure(failure);
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error) || error is InvalidCastException)
        {
            return BluetoothAudioFailure(error.HResult);
        }
        finally { ReleaseAudioObject(enumerator); }
    }

    private static int? SendBluetoothAudioRequest(DeviceEnumerator enumerator, string id, bool connect)
    {
        AudioDevice? endpoint = null, filter = null;
        object? topologyObject = null, controlObject = null;
        try
        {
            enumerator.GetDevice(id, out endpoint);
            Guid iid = typeof(AudioTopology).GUID;
            endpoint.Activate(ref iid, 23, 0, out topologyObject);
            if (topologyObject is not AudioTopology topology) return null;
            topology.GetConnectorCount(out uint count);
            for (uint index = 0; index < count; index++)
            {
                topology.GetConnector(index, out var connector);
                string? filterId;
                try
                {
                    if (connector.GetDeviceIdConnectedTo(out filterId) < 0 || string.IsNullOrEmpty(filterId)) continue;
                }
                finally { ReleaseAudioObject(connector); }
                enumerator.GetDevice(filterId, out filter);
                iid = typeof(AudioKsControl).GUID;
                filter.Activate(ref iid, 23, 0, out controlObject);
                if (controlObject is not AudioKsControl control) return null;
                var property = new KsProperty { Set = BluetoothAudioProperty, Id = connect ? 0u : 1u, Flags = 1 };
                return control.KsProperty(in property, (uint)Marshal.SizeOf<KsProperty>(), 0, 0, out _);
            }
            return null;
        }
        finally
        {
            ReleaseAudioObject(controlObject);
            ReleaseAudioObject(filter);
            ReleaseAudioObject(topologyObject);
            ReleaseAudioObject(endpoint);
        }
    }

    private static string BluetoothAudioFailure(int? error) => error switch
    {
        unchecked((int)0x80070005) => "Windows 拒绝了蓝牙音频操作。",
        unchecked((int)0x8007048F) => "耳机当前不可用，请确认它已开机且在附近。",
        null or unchecked((int)0x80004002) or unchecked((int)0x80070032) or unchecked((int)0x80070490) =>
            "此设备的 Windows 音频驱动不支持直接连接或断开。",
        _ => $"Windows 音频驱动未能完成请求（0x{error:X8}）。"
    };

    private static void ReleaseAudioObject(object? value)
    {
        if (value is not null) Marshal.ReleaseComObject(value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty { internal Guid Set; internal uint Id, Flags; }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioTopology
    {
        void GetConnectorCount(out uint count);
        void GetConnector(uint index, out AudioConnector connector);
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioConnector
    {
        void _VtblGap1_7();
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string? id);
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface AudioKsControl
    {
        [PreserveSig] int KsProperty(in KsProperty property, uint length, nint data, uint dataLength, out uint returned);
    }
}

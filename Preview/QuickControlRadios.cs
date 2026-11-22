using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Security.Credentials;

namespace AITaskbar;

internal sealed class QuickControlRadios
{
    internal sealed record Network(WiFiAdapter Adapter, WiFiAvailableNetwork Entry)
    {
        internal string Name => Entry.Ssid;
        internal bool Secured => Entry.SecuritySettings.NetworkAuthenticationType !=
            Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211;
    }

    internal sealed record BluetoothEntry(string Key, DeviceInformation Device, bool Connected);
    private RadioAccessStatus? access;
    private IReadOnlyList<WiFiAdapter>? wifiAdapters;

    internal static async Task<(Radio? Wifi, Radio? Bluetooth)> ReadAsync()
    {
        var radios = await Radio.GetRadiosAsync();
        return (Select(RadioKind.WiFi), Select(RadioKind.Bluetooth));

        Radio? Select(RadioKind kind) => radios.Where(radio => radio.Kind == kind)
            .OrderByDescending(radio => radio.State == RadioState.On).FirstOrDefault();
    }

    internal async Task<bool> SetAsync(Radio radio, bool enabled)
    {
        access ??= await Radio.RequestAccessAsync();
        if (access != RadioAccessStatus.Allowed) return false;
        return await radio.SetStateAsync(enabled ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
    }

    internal async Task<Network[]> NetworksAsync(bool scan)
    {
        // Enumeration can be declined by the Windows location policy.
        if (scan || wifiAdapters is null) wifiAdapters = await WiFiAdapter.FindAllAdaptersAsync();
        var adapters = wifiAdapters;
        if (scan) await Task.WhenAll(adapters.Select(async adapter => { await adapter.ScanAsync(); }));
        return adapters.SelectMany(adapter => adapter.NetworkReport.AvailableNetworks.Select(entry => new Network(adapter, entry)))
            .Where(network => !string.IsNullOrWhiteSpace(network.Name))
            .GroupBy(network => (network.Name, network.Secured))
            .Select(group => group.MaxBy(network => network.Entry.SignalBars)!)
            .OrderByDescending(network => network.Entry.SignalBars).ThenBy(network => network.Name).ToArray();
    }

    internal static async Task<WiFiConnectionStatus> ConnectAsync(Network network, string? password)
    {
        var result = password is null
            ? await network.Adapter.ConnectAsync(network.Entry, WiFiReconnectionKind.Automatic)
            : await network.Adapter.ConnectAsync(network.Entry, WiFiReconnectionKind.Automatic, new PasswordCredential
                { Password = password });
        return result.ConnectionStatus;
    }

    internal static async Task<bool> DisconnectAsync(string name)
    {
        foreach (var adapter in await WiFiAdapter.FindAllAdaptersAsync())
        {
            var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
            if (profile?.WlanConnectionProfileDetails?.GetConnectedSsid() != name) continue;
            adapter.Disconnect();
            return true;
        }
        return false;
    }

    internal static async Task<BluetoothEntry[]> BluetoothDevicesAsync(bool paired = true)
    {
        const string Connected = "System.Devices.Aep.IsConnected";
        const string Address = "System.Devices.Aep.DeviceAddress";
        string[] properties = [Connected, Address];
        string[] selectors = [BluetoothDevice.GetDeviceSelectorFromPairingState(paired),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(paired)];
        var all = await Task.WhenAll(selectors.Select(async selector =>
            await DeviceInformation.FindAllAsync(selector, properties, DeviceInformationKind.AssociationEndpoint)));
        return all.SelectMany(group => group).Where(device => !string.IsNullOrWhiteSpace(device.Name) &&
            (paired || device.Pairing.CanPair))
            .GroupBy(device => device.Properties.TryGetValue(Address, out var address) && address is string key ? key : device.Id,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new BluetoothEntry(group.Key, group.First(), group.Any(device =>
                device.Properties.TryGetValue(Connected, out var value) && value is true)))
            .OrderByDescending(device => device.Connected).ThenBy(device => device.Device.Name).ToArray();
    }

    internal static bool IsUnavailable(Exception error) => error is COMException or UnauthorizedAccessException
        or InvalidOperationException or ArgumentException;
}

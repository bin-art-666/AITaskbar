using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Radios;

namespace AITaskbar;

internal sealed partial class QuickControlPanel
{
    private readonly record struct WirelessKey(string Name, bool Secured = false);
    private sealed record WirelessItem(WirelessKey Key, string Title, string Status, Action? Select);
    private sealed record WirelessRow(Button Button, TextBlock Title, TextBlock Status)
    {
        internal Action? Select;
    }

    private readonly Dictionary<WirelessKey, WirelessRow> rowsByKey = new();
    private StackPanel? wirelessRows;
    private Button? disconnectButton, discoverButton;
    private bool wirelessEditor, wirelessReading, scanRequested;
    private long nextWifiScan;
    private void SetWirelessListStatus(string message)
    {
        listStatus.Text = message;
        listStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowWirelessNotice(string? message)
    {
        wirelessStatusStrip.Text = message ?? "";
        wirelessStatusStrip.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async Task ReturnToWirelessListAsync(bool scan = false)
    {
        if (detailArea is not (Area.Wifi or Area.Bluetooth)) return;
        if (wirelessRows is null || wirelessEditor)
        {
            detailRevision++;
            wirelessEditor = false;
            ShowWirelessNotice(null);
            SetWirelessListStatus("正在读取…");
            rowsByKey.Clear();
            detailList.Children.Clear();
            wirelessRows = new StackPanel { Spacing = 3 };
            detailList.Children.Add(wirelessRows);
            disconnectButton = discoverButton = null;
            if (detailArea == Area.Wifi)
            {
                disconnectButton = DetailAction("断开连接", "");
                disconnectButton.Visibility = Visibility.Collapsed;
                disconnectButton.Click += async (_, _) =>
                {
                    if (wifiStatus is { Bars: > 0, Name: { } name })
                        await ChangeNetworkAsync(name, network: null);
                };
                detailList.Children.Add(disconnectButton);
            }
            else
            {
                discoverButton = DetailAction("查找新设备", "");
                discoverButton.Click += async (_, _) => await DiscoverBluetoothAsync();
                detailList.Children.Add(discoverButton);
            }
            var area = detailArea.Value;
            var settings = DetailAction(area == Area.Wifi ? "Windows 网络设置" : "Windows 蓝牙设备设置", "打开");
            settings.Click += async (_, _) => await OpenSettingsAsync(area);
            detailList.Children.Add(settings);
        }
        scanRequested |= scan;
        await RefreshWirelessListAsync();
    }

    private async Task RefreshWirelessListAsync()
    {
        if (wirelessRows is null || wirelessEditor || wirelessReading || radioBusy || networkBusy ||
            detailArea is not (Area.Wifi or Area.Bluetooth)) return;
        var area = detailArea.Value;
        int revision = detailRevision;
        bool Current() => !disposed && !panel.IsClosing && !radioBusy && !networkBusy && detailArea == area && revision == detailRevision;
        wirelessReading = true;
        try
        {
            await PollRadiosAsync();
            if (!Current()) return;
            if (SelectedRadio?.State is RadioState.Off or RadioState.Disabled)
            {
                UpdateWirelessRows([], area == Area.Wifi ? "Wi‑Fi 已关闭" : "蓝牙已关闭");
                return;
            }
            if (area == Area.Wifi)
            {
                bool scan = scanRequested || Environment.TickCount64 >= nextWifiScan;
                scanRequested = false;
                if (scan) nextWifiScan = Environment.TickCount64 + 20000;
                var networks = await radios.NetworksAsync(scan);
                if (!Current()) return;
                var connected = wifiStatus.Bars > 0 ? wifiStatus.Name : null;
                var activeNetwork = connected is null ? null : networks.FirstOrDefault(network => network.Name == connected);
                var items = networks.OrderByDescending(network => network == activeNetwork).Select(network =>
                {
                    bool active = network == activeNetwork;
                    return new WirelessItem(new(network.Name, network.Secured), network.Name, active ? "已连接" :
                        $"{network.Entry.SignalBars}/5{(network.Secured ? " · 已加密" : "")}",
                        active ? null : () => ChooseNetwork(network));
                }).ToList();
                if (connected is not null && activeNetwork is null)
                    items.Insert(0, new(new(connected), connected, "已连接", null));
                UpdateWirelessRows(items, items.Count == 0 ? "未找到可用网络" : "");
            }
            else
            {
                var devices = await QuickControlRadios.BluetoothDevicesAsync();
                if (!Current()) return;
                var containers = devices.Select(device => device.Container).Where(id => id != Guid.Empty).ToHashSet();
                var outputs = await Task.Run(() => SystemVolume.ReadBluetoothOutputs(containers));
                if (!Current()) return;
                var audio = outputs?.ToLookup(output => output.Container);
                UpdateWirelessRows(devices.Select(device =>
                {
                    var endpoints = audio?[device.Container];
                    bool hasAudio = endpoints?.Any() == true;
                    bool connected = hasAudio ? endpoints!.Any(output => output.Connected) : device.Connected;
                    string status = hasAudio ? connected ? "已连接 · 断开" : "未连接 · 连接"
                        : connected ? "已连接" : "已配对 · 未连接";
                    return new WirelessItem(new(device.Key), device.Device.Name, status,
                        async () => await ChangeBluetoothAudioAsync(device, !connected));
                }).ToArray(),
                    devices.Length == 0 ? "没有已配对的设备" : "");
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (Current()) SetWirelessRefreshError(area == Area.Wifi ? "Windows 未授权读取 Wi‑Fi 网络。" : "Windows 未授权读取蓝牙设备。");
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error))
        {
            if (Current()) SetWirelessRefreshError(area == Area.Wifi ? "暂时无法更新 Wi‑Fi 网络。" : "暂时无法更新蓝牙设备。");
        }
        finally { wirelessReading = false; }
    }

    private void UpdateWirelessRows(IReadOnlyList<WirelessItem> items, string status)
    {
        KeepWirelessScroll(() =>
        {
            SetWirelessListStatus(status);
            var desired = items.Select(item => item.Key).ToHashSet();
            foreach (var key in rowsByKey.Keys.Where(key => !desired.Contains(key)).ToArray())
            {
                wirelessRows!.Children.Remove(rowsByKey[key].Button);
                rowsByKey.Remove(key);
            }
            foreach (var item in items)
            {
                if (!rowsByKey.TryGetValue(item.Key, out var row))
                {
                    var button = DetailAction(item.Title, item.Status, out var title, out var subtitle);
                    row = new(button, title, subtitle);
                    button.Click += (_, _) => row.Select?.Invoke();
                    rowsByKey.Add(item.Key, row);
                    wirelessRows!.Children.Add(button);
                }
                if (row.Title.Text != item.Title) row.Title.Text = item.Title;
                if (row.Status.Text != item.Status) row.Status.Text = item.Status;
                row.Select = item.Select;
                row.Button.IsEnabled = item.Select is not null;
                string label = $"{item.Title} · {item.Status}";
                if (AutomationProperties.GetName(row.Button) != label) AutomationProperties.SetName(row.Button, label);
            }
            if (disconnectButton is not null)
                disconnectButton.Visibility = wifiStatus.Bars > 0 && SelectedRadio?.State is not (RadioState.Off or RadioState.Disabled)
                    ? Visibility.Visible : Visibility.Collapsed;
            if (discoverButton is not null) discoverButton.IsEnabled = SelectedRadio?.State is not (RadioState.Off or RadioState.Disabled);
        });
    }

    private void SetWirelessRefreshError(string message) => KeepWirelessScroll(() => SetWirelessListStatus(message));

    private void KeepWirelessScroll(Action update)
    {
        if (detailScroll.VerticalOffset <= 0)
        {
            update();
            return;
        }
        var visible = wirelessRows!.Children.OfType<FrameworkElement>()
            .Select(row => (Row: row, Top: row.TransformToVisual(detailScroll).TransformPoint(new()).Y))
            .Where(row => row.Top + row.Row.ActualHeight > 0 && row.Top < detailScroll.ViewportHeight).ToArray();
        update();
        foreach (var row in visible)
        {
            if (!wirelessRows.Children.Contains(row.Row)) continue;
            detailScroll.UpdateLayout();
            var top = row.Row.TransformToVisual(detailScroll).TransformPoint(new()).Y;
            if (Math.Abs(top - row.Top) > .5)
                detailScroll.ChangeView(null, Math.Clamp(detailScroll.VerticalOffset + top - row.Top, 0, detailScroll.ScrollableHeight), null, true);
            break;
        }
    }

    private void ResetWirelessScroll()
    {
        detailScroll.UpdateLayout();
        detailScroll.ChangeView(null, 0, null, true);
    }
}

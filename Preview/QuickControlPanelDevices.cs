using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Devices.Enumeration;
using Windows.System;

namespace AITaskbar;

internal sealed partial class QuickControlPanel
{
    private readonly QuickControlRadios radios = new();
    private Radio? wifiRadio, bluetoothRadio;
    private SystemTrayStatus.Wifi wifiStatus;
    private bool pollingRadios, applyingRadio, radioBusy, networkBusy;
    private int detailRevision;
    private long nextPoll;
    private QuickControlBrightness.Display? display;
    private bool applyingBrightness, brightnessBusy;
    private int? pendingBrightness;
    private int brightnessReadFailures;

    private static bool IsOn(Radio? radio) => radio?.State == RadioState.On;

    internal void UpdateWifi(SystemTrayStatus.Wifi value)
    {
        wifiStatus = value;
        detailWifi.SetLevel(value.Bars);
        wifiText.Text = wifiRadio?.State == RadioState.Off ? "已关闭" :
            value.Bars > 0 ? value.Name ?? "已连接" : "未连接";
        ApplyAccents();
    }

    internal void Pulse()
    {
        if (!opened || closing || disposed || Environment.TickCount64 < nextPoll) return;
        nextPoll = Environment.TickCount64 + 2000;
        if (detailArea == Area.Audio) RefreshOutputs();
        _ = PollRadiosAsync();
        if (!brightnessBusy) _ = RefreshBrightnessAsync();
    }

    private async Task PollRadiosAsync()
    {
        if (pollingRadios) return;
        pollingRadios = true;
        try
        {
            var state = await QuickControlRadios.ReadAsync();
            if (disposed) return;
            wifiRadio = state.Wifi;
            bluetoothRadio = state.Bluetooth;
            UpdateWifi(wifiStatus);
            bluetoothText.Text = bluetoothRadio is null ? "不可用" : IsOn(bluetoothRadio) ? "已开启" : "已关闭";
            UpdateRadioSwitch();
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
        finally { pollingRadios = false; }
    }

    private Radio? SelectedRadio => detailArea == Area.Wifi ? wifiRadio : bluetoothRadio;

    private void UpdateRadioSwitch()
    {
        if (detailArea is not (Area.Wifi or Area.Bluetooth)) return;
        var radio = SelectedRadio;
        applyingRadio = true;
        radioSwitch.IsOn = IsOn(radio) || (detailArea == Area.Wifi && radio is null && wifiStatus.Bars > 0);
        radioSwitch.IsEnabled = !radioBusy && radio?.State is RadioState.On or RadioState.Off;
        applyingRadio = false;
    }

    private async void RadioToggled(object sender, RoutedEventArgs args)
    {
        if (applyingRadio || radioBusy || detailArea is not { } area || SelectedRadio is not { } radio) return;
        radioBusy = true;
        radioSwitch.IsEnabled = false;
        bool enabled = radioSwitch.IsOn;
        bool success = false;
        try { success = await radios.SetAsync(radio, enabled); }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
        finally { radioBusy = false; }
        if (disposed || detailArea != area) return;
        if (!success)
        {
            UpdateRadioSwitch();
            listStatus.Text = "Windows 未允许更改开关，可在系统设置中操作。";
            return;
        }
        // Some drivers publish the new state after the operation completes.
        await Task.Delay(350);
        if (disposed || detailArea != area) return;
        await PollRadiosAsync();
        await RefreshDetailsAsync(scan: enabled);
    }

    private async Task RefreshDetailsAsync(bool scan)
    {
        if (detailArea is not (Area.Wifi or Area.Bluetooth)) return;
        var area = detailArea.Value;
        int revision = ++detailRevision;
        bool Current() => !disposed && detailArea == area && detailRevision == revision;
        detailList.Children.Clear();
        listStatus.Text = "正在读取…";
        try
        {
            await PollRadiosAsync();
            if (!Current()) return;
            if (SelectedRadio?.State is RadioState.Off or RadioState.Disabled)
            {
                listStatus.Text = area == Area.Wifi ? "Wi‑Fi 已关闭" : "蓝牙已关闭";
                return;
            }
            if (area == Area.Wifi)
            {
                var networks = await QuickControlRadios.NetworksAsync(scan);
                if (!Current()) return;
                listStatus.Text = networks.Length == 0 ? "未找到可用网络" : "";
                foreach (var network in networks.OrderByDescending(network => network.Name == wifiStatus.Name))
                {
                    bool connected = network.Name == wifiStatus.Name && wifiStatus.Bars > 0;
                    var button = DetailAction(network.Name, connected ? "已连接" :
                        $"{network.Entry.SignalBars}/5{(network.Secured ? " · 已加密" : "")}");
                    if (connected)
                    {
                        button.IsEnabled = false;
                        detailList.Children.Add(button);
                        var disconnect = DetailAction("断开连接", "");
                        disconnect.Click += async (_, _) => await ChangeNetworkAsync(network, true, null);
                        detailList.Children.Add(disconnect);
                        continue;
                    }
                    button.Click += (_, _) => ChooseNetwork(network);
                    detailList.Children.Add(button);
                }
            }
            else
            {
                var devices = await QuickControlRadios.BluetoothDevicesAsync();
                if (!Current()) return;
                listStatus.Text = devices.Length == 0 ? "没有已配对的设备" : "";
                foreach (var device in devices)
                {
                    var button = DetailAction(device.Device.Name, device.Connected ? "已连接" : "已配对");
                    if (device.Connected) button.IsEnabled = false;
                    else button.Click += async (_, _) => await OpenSettingsAsync(Area.Bluetooth);
                    detailList.Children.Add(button);
                }
                var discover = DetailAction("查找新设备", "");
                discover.Click += async (_, _) => await DiscoverBluetoothAsync();
                detailList.Children.Add(discover);
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (Current()) listStatus.Text = area == Area.Wifi ? "Windows 未授权读取 Wi‑Fi 网络。" : "Windows 未授权读取蓝牙设备。";
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error))
        {
            if (Current()) listStatus.Text = area == Area.Wifi ? "暂时无法读取 Wi‑Fi 网络。" : "暂时无法读取蓝牙设备。";
        }
        finally
        {
            if (Current())
            {
                var settings = DetailAction(area == Area.Wifi ? "Windows 网络设置" : "Windows 蓝牙设备设置", "打开");
                settings.Click += async (_, _) => await OpenSettingsAsync(area);
                detailList.Children.Add(settings);
            }
        }
    }

    private Button DetailAction(string title, string subtitle) => DetailAction(title, subtitle, out _);

    private Button DetailAction(string title, string subtitle, out TextBlock status)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var label = Text(title, 13, ink);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        status = Text(subtitle, 12, subtle);
        row.Children.Add(label);
        row.Children.Add(status);
        Grid.SetColumn(status, 1);
        var button = FlatButton(row, subtitle.Length == 0 ? title : title + " · " + subtitle);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(8, 5, 8, 5);
        button.Background = tile;
        return button;
    }

    private async void ChooseNetwork(QuickControlRadios.Network network)
    {
        if (networkBusy || detailArea != Area.Wifi) return;
        if (network.Secured)
        {
            detailList.Children.Clear();
            listStatus.Text = network.Name;
            var password = new PasswordBox { PlaceholderText = "网络密码" };
            AutomationProperties.SetName(password, "网络密码");
            var connect = DetailAction("连接", "");
            connect.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(password.Password))
                {
                    listStatus.Text = "请输入网络密码，或在 Windows 网络设置中连接。";
                    return;
                }
                string value = password.Password;
                password.Password = "";
                await ChangeNetworkAsync(network, false, value);
            };
            var back = DetailAction("返回网络列表", "");
            back.Click += async (_, _) => { if (!networkBusy) await RefreshDetailsAsync(scan: false); };
            var settings = DetailAction("Windows 网络设置", "打开");
            settings.Click += async (_, _) => await OpenSettingsAsync(Area.Wifi);
            detailList.Children.Add(password);
            detailList.Children.Add(connect);
            detailList.Children.Add(back);
            detailList.Children.Add(settings);
            password.Focus(FocusState.Programmatic);
            return;
        }
        await ChangeNetworkAsync(network, false, null);
    }

    private async Task ChangeNetworkAsync(QuickControlRadios.Network network, bool disconnect, string? password)
    {
        if (networkBusy) return;
        networkBusy = true;
        var revision = detailRevision;
        listStatus.Text = disconnect ? "正在断开…" : "正在连接…";
        bool requested = false, confirmed = false;
        try
        {
            requested = disconnect ? await QuickControlRadios.DisconnectAsync(network.Name)
                : await QuickControlRadios.ConnectAsync(network, password) == WiFiConnectionStatus.Success;
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
        try
        {
            for (int attempt = 0; !disposed && attempt < (requested ? 7 : 1); attempt++)
            {
                if (attempt > 0) await Task.Delay(350);
                var state = await Task.Run(SystemTrayStatus.TryReadWifi);
                if (disposed) return;
                if (state is not { } observed) continue;
                UpdateWifi(observed);
                bool connected = observed.Bars > 0 && observed.Name == network.Name;
                confirmed = requested && connected != disconnect;
                if (confirmed) break;
            }
        }
        finally { networkBusy = false; }
        if (disposed || detailArea != Area.Wifi || revision != detailRevision) return;
        if (confirmed) await RefreshDetailsAsync(scan: false);
        else if (requested)
        {
            await RefreshDetailsAsync(scan: false);
            if (detailArea == Area.Wifi && detailRevision == revision + 1)
                listStatus.Text = "Windows 尚未确认连接状态，请查看当前网络。";
        }
        else listStatus.Text = disconnect ? "未能断开，请查看 Windows 网络设置。"
            : "连接失败，请核对密码或在 Windows 网络设置中连接。";
    }

    private async Task OpenSettingsAsync(Area area)
    {
        string address = area == Area.Wifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth";
        try { if (await Launcher.LaunchUriAsync(new Uri(address))) Hide(); }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
    }

    private async Task DiscoverBluetoothAsync()
    {
        int revision = ++detailRevision;
        detailList.Children.Clear();
        listStatus.Text = "正在查找可配对设备…";
        bool Current() => !disposed && detailArea == Area.Bluetooth && detailRevision == revision;
        try
        {
            var devices = await QuickControlRadios.BluetoothDevicesAsync(paired: false);
            if (!Current()) return;
            listStatus.Text = devices.Length == 0 ? "未找到可配对设备。" : "";
            foreach (var entry in devices)
            {
                var button = DetailAction(entry.Device.Name, "配对");
                button.Click += async (_, _) =>
                {
                    if (!Current() || networkBusy) return;
                    networkBusy = true;
                    listStatus.Text = "正在配对…";
                    DevicePairingResultStatus? status = null;
                    try
                    {
                        var result = await entry.Device.Pairing.PairAsync();
                        status = result.Status;
                    }
                    catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
                    finally { networkBusy = false; }
                    if (!Current()) return;
                    if (status is DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired)
                        await RefreshDetailsAsync(scan: false);
                    else listStatus.Text = status switch
                    {
                        DevicePairingResultStatus.PairingCanceled => "已取消配对。",
                        DevicePairingResultStatus.AuthenticationTimeout => "配对超时，请让设备保持配对模式后重试。",
                        DevicePairingResultStatus.AccessDenied => "Windows 未允许配对，可在蓝牙设置中重试。",
                        _ => "配对未完成，可在 Windows 蓝牙设备设置中重试。"
                    };
                };
                detailList.Children.Add(button);
            }
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error))
        {
            if (Current()) listStatus.Text = "暂时无法查找蓝牙设备。";
        }
        finally
        {
            if (Current())
            {
                var back = DetailAction("返回已配对设备", "");
                back.Click += async (_, _) => await RefreshDetailsAsync(scan: false);
                var settings = DetailAction("Windows 蓝牙设备设置", "打开");
                settings.Click += async (_, _) => await OpenSettingsAsync(Area.Bluetooth);
                detailList.Children.Add(back);
                detailList.Children.Add(settings);
            }
        }
    }

    private async Task RefreshBrightnessAsync()
    {
        brightnessBusy = true;
        try
        {
            nint monitor = QuickControlBrightness.MonitorFromWindow(owner, 2);
            var previous = display;
            var result = await Task.Run(() => QuickControlBrightness.Read(monitor, previous));
            if (disposed) return;
            if (result is not null) brightnessReadFailures = 0;
            if (result is not null || previous?.Monitor != monitor || ++brightnessReadFailures >= 3)
                ApplyBrightness(result);
        }
        finally { brightnessBusy = false; }
        if (pendingBrightness.HasValue && !disposed) _ = WriteBrightnessAsync();
    }

    private void ApplyBrightness(QuickControlBrightness.Display? value)
    {
        display = value;
        applyingBrightness = true;
        brightnessSlider.IsEnabled = value is not null;
        if (value is not null && !pendingBrightness.HasValue) brightnessSlider.Value = value.Level;
        brightnessText.Text = value is null ? "显示器不支持" : $"{(int)brightnessSlider.Value}%";
        applyingBrightness = false;
    }

    private void BrightnessChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (applyingBrightness || display is null) return;
        int value = (int)Math.Round(args.NewValue);
        brightnessText.Text = $"{value}%";
        var displayedAngle = sun.Angle;
        double sunAngle = displayedAngle + (args.NewValue - args.OldValue) * 3.6;
        sunAnimation?.Stop();
        sun.Angle = sunAngle;
        if (Motion)
        {
            sunAnimation = new Storyboard();
            AddAnimation(sunAnimation, sun, "Angle", displayedAngle, sunAngle, 170);
            sunAnimation.Begin();
        }
        pendingBrightness = value;
        if (!brightnessBusy) _ = WriteBrightnessAsync();
    }

    private async Task WriteBrightnessAsync()
    {
        brightnessBusy = true;
        try
        {
            // Coalesce a drag into the latest hardware value rather than queueing every step.
            while (!disposed && pendingBrightness.HasValue && display is { } target)
            {
                await Task.Delay(75);
                if (disposed || pendingBrightness is not { } value) return;
                pendingBrightness = null;
                var applied = await Task.Run(() => QuickControlBrightness.Set(target, value));
                if (disposed) return;
                if (applied is { } level)
                    ApplyBrightness(target with { Level = level });
                else
                {
                    var current = await Task.Run(() => QuickControlBrightness.Read(target.Monitor, target));
                    if (disposed) return;
                    ApplyBrightness(current ?? target);
                    if (!pendingBrightness.HasValue)
                        brightnessText.Text = current is null ? "无法设置" : $"无法设置 · 当前 {current.Level}%";
                }
            }
        }
        finally { brightnessBusy = false; }
    }
}

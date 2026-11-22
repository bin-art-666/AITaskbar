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
        if (!panel.IsOpen || panel.IsClosing || disposed || Environment.TickCount64 < nextPoll) return;
        nextPoll = Environment.TickCount64 + 2000;
        if (detailArea == Area.Audio) RefreshOutputs();
        if (detailArea is Area.Wifi or Area.Bluetooth && !wirelessEditor) _ = RefreshWirelessListAsync();
        else _ = PollRadiosAsync();
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
        radioSwitch.IsEnabled = !radioBusy && !networkBusy && radio?.State is RadioState.On or RadioState.Off;
        applyingRadio = false;
    }

    private async void RadioToggled(object sender, RoutedEventArgs args)
    {
        if (applyingRadio || radioBusy || networkBusy || detailArea is not { } area || SelectedRadio is not { } radio) return;
        int revision = detailRevision;
        ShowWirelessNotice(null);
        radioBusy = true;
        radioSwitch.IsEnabled = false;
        bool enabled = radioSwitch.IsOn;
        bool success = false;
        try { success = await radios.SetAsync(radio, enabled); }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
        finally { radioBusy = false; }
        if (disposed || detailArea != area || revision != detailRevision) return;
        if (!success)
        {
            UpdateRadioSwitch();
            ShowWirelessNotice("Windows 未允许更改开关，可在系统设置中操作。");
            return;
        }
        // Some drivers publish the new state after the operation completes.
        await Task.Delay(350);
        if (disposed || detailArea != area || revision != detailRevision) return;
        await PollRadiosAsync();
        if (disposed || detailArea != area || revision != detailRevision) return;
        await ReturnToWirelessListAsync(scan: enabled);
    }

    private Button DetailAction(string title, string subtitle) => DetailAction(title, subtitle, out _);

    private Button DetailAction(string title, string subtitle, out TextBlock status) =>
        DetailAction(title, subtitle, out _, out status);

    private Button DetailAction(string title, string subtitle, out TextBlock label, out TextBlock status)
    {
        bool wireless = detailArea is Area.Wifi or Area.Bluetooth;
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        label = Text(title, wireless ? WirelessBodySize : 13, ink);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        status = Text(subtitle, wireless ? WirelessBodySize : 12, subtle);
        status.TextAlignment = TextAlignment.Right;
        row.Children.Add(label);
        row.Children.Add(status);
        Grid.SetColumn(status, 1);
        var button = FlatButton(row, subtitle.Length == 0 ? title : title + " · " + subtitle);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = wireless ? new Thickness(8, 4, 8, 4) : new Thickness(8, 5, 8, 5);
        if (wireless) button.MinHeight = WirelessRowHeight;
        button.Background = tile;
        return button;
    }

    private async void ChooseNetwork(QuickControlRadios.Network network)
    {
        if (networkBusy || radioBusy || detailArea != Area.Wifi) return;
        if (network.Secured)
        {
            wirelessEditor = true;
            detailRevision++;
            ShowWirelessNotice(null);
            detailList.Children.Clear();
            SetWirelessListStatus(network.Name);
            var password = new PasswordBox { PlaceholderText = "网络密码", FontSize = WirelessBodySize };
            AutomationProperties.SetName(password, "网络密码");
            var connect = DetailAction("连接", "");
            connect.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(password.Password))
                {
                    ShowWirelessNotice("请输入网络密码，或在 Windows 网络设置中连接。");
                    return;
                }
                string value = password.Password;
                password.Password = "";
                await ChangeNetworkAsync(network.Name, network, value);
            };
            var back = DetailAction("返回网络列表", "");
            back.Click += async (_, _) => { if (!networkBusy) await ReturnToWirelessListAsync(); };
            var settings = DetailAction("Windows 网络设置", "打开");
            settings.Click += async (_, _) => await OpenSettingsAsync(Area.Wifi);
            detailList.Children.Add(password);
            detailList.Children.Add(connect);
            detailList.Children.Add(back);
            detailList.Children.Add(settings);
            ResetWirelessScroll();
            password.Focus(FocusState.Programmatic);
            return;
        }
        await ChangeNetworkAsync(network.Name, network);
    }

    private async Task ChangeNetworkAsync(string name, QuickControlRadios.Network? network, string? password = null)
    {
        if (networkBusy || radioBusy) return;
        bool disconnect = network is null;
        networkBusy = true;
        var revision = detailRevision;
        ShowWirelessNotice(disconnect ? "正在断开…" : "正在连接…");
        bool requested = false, confirmed = false;
        try
        {
            requested = network is null ? await QuickControlRadios.DisconnectAsync(name)
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
                bool connected = observed.Bars > 0 && observed.Name == name;
                confirmed = requested && connected != disconnect;
                if (confirmed) break;
            }
        }
        finally { networkBusy = false; }
        if (disposed || detailArea != Area.Wifi || revision != detailRevision) return;
        if (confirmed)
        {
            ShowWirelessNotice(null);
            await ReturnToWirelessListAsync();
        }
        else if (requested)
        {
            ShowWirelessNotice("Windows 尚未确认连接状态，请查看当前网络。");
            await RefreshWirelessListAsync();
        }
        else
        {
            ShowWirelessNotice(disconnect ? "未能断开，请查看 Windows 网络设置。"
                : "连接失败，请核对密码或在 Windows 网络设置中连接。");
            await RefreshWirelessListAsync();
        }
    }

    private async Task OpenSettingsAsync(Area area)
    {
        string address = area == Area.Wifi ? "ms-settings:network-wifi" : "ms-settings:bluetooth";
        int revision = detailRevision;
        try { if (await Launcher.LaunchUriAsync(new Uri(address))) { Hide(); return; } }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error)) { }
        if (!disposed && detailArea == area && detailRevision == revision)
            ShowWirelessNotice("暂时无法打开 Windows 设置。");
    }

    private async Task ChangeBluetoothAudioAsync(QuickControlRadios.BluetoothEntry device, bool connect)
    {
        if (disposed || panel.IsClosing || detailArea != Area.Bluetooth || wirelessEditor || networkBusy || radioBusy) return;
        string action = connect ? "连接" : "断开";
        if (bluetoothRadio?.State is RadioState.Off or RadioState.Disabled)
        {
            ShowWirelessNotice($"无法{action}「{device.Device.Name}」：蓝牙已关闭。");
            return;
        }
        int revision = detailRevision;
        bool Current() => !disposed && !panel.IsClosing && detailArea == Area.Bluetooth && detailRevision == revision;
        networkBusy = true;
        UpdateRadioSwitch();
        ShowWirelessNotice($"正在{action}「{device.Device.Name}」…");
        string? failure;
        try
        {
            failure = await Task.Run(() => SystemVolume.RequestBluetoothAudio(device.Container, connect));
            if (failure is null)
            {
                var containers = new HashSet<Guid> { device.Container };
                bool confirmed = false;
                for (int attempt = 0; Current() && attempt < 18; attempt++)
                {
                    if (attempt > 0) await Task.Delay(500);
                    if (!Current()) break;
                    var endpoints = await Task.Run(() => SystemVolume.ReadBluetoothOutputs(containers));
                    if (endpoints is not null && endpoints.Any(endpoint => endpoint.Connected) == connect)
                    {
                        confirmed = true;
                        break;
                    }
                }
                if (!confirmed) failure = connect ? "等待音频连接超时。请确认耳机已开机、在附近，并可接受连接。"
                    : "等待音频断开超时，Windows 仍未确认已断开。";
            }
        }
        finally { networkBusy = false; }
        if (!Current()) return;
        UpdateRadioSwitch();
        ShowWirelessNotice(failure is null ? null : $"无法{action}「{device.Device.Name}」：{failure}");
        await RefreshWirelessListAsync();
    }

    private async Task DiscoverBluetoothAsync()
    {
        if (networkBusy || radioBusy) return;
        wirelessEditor = true;
        ShowWirelessNotice(null);
        int revision = ++detailRevision;
        detailList.Children.Clear();
        ResetWirelessScroll();
        SetWirelessListStatus("正在查找可配对设备…");
        bool Current() => !disposed && detailArea == Area.Bluetooth && detailRevision == revision;
        try
        {
            var devices = await QuickControlRadios.BluetoothDevicesAsync(paired: false);
            if (!Current()) return;
            SetWirelessListStatus(devices.Length == 0 ? "未找到可配对设备。" : "");
            foreach (var entry in devices)
            {
                var button = DetailAction(entry.Device.Name, "配对");
                button.Click += async (_, _) =>
                {
                    if (!Current() || networkBusy) return;
                    networkBusy = true;
                    ShowWirelessNotice("正在配对…");
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
                        await ReturnToWirelessListAsync();
                    else ShowWirelessNotice(status switch
                    {
                        DevicePairingResultStatus.PairingCanceled => "已取消配对。",
                        DevicePairingResultStatus.AuthenticationTimeout => "配对超时，请让设备保持配对模式后重试。",
                        DevicePairingResultStatus.AccessDenied => "Windows 未允许配对，可在蓝牙设置中重试。",
                        _ => "配对未完成，可在 Windows 蓝牙设备设置中重试。"
                    });
                };
                detailList.Children.Add(button);
            }
        }
        catch (Exception error) when (QuickControlRadios.IsUnavailable(error))
        {
            if (Current()) SetWirelessListStatus("暂时无法查找蓝牙设备。");
        }
        finally
        {
            if (Current())
            {
                var back = DetailAction("返回已配对设备", "");
                back.Click += async (_, _) => await ReturnToWirelessListAsync();
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
            TaskbarPanelFlyout.AddAnimation(sunAnimation, sun, "Angle", displayedAngle, sunAngle, 170);
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

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using static AITaskbar.TaskbarPanelFlyout;

namespace AITaskbar;

internal sealed partial class QuickControlPanel : IDisposable
{
    private enum Area { Wifi, Bluetooth, Audio, Appearance }

    private const double WirelessBodySize = 14, WirelessHeadingSize = 16, WirelessRowHeight = 29;
    private const string SunGlyph = "\uE706";
    private readonly nint owner;
    private readonly TaskbarPanelFlyout panel;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Grid scene = new() { Width = 358, Height = 254 };
    private readonly StackPanel main = new() { Spacing = 8, Margin = new Thickness(13) };
    private readonly Border backplate = new() { CornerRadius = new CornerRadius(Radius) };
    private readonly Border shade = new() { CornerRadius = new CornerRadius(Radius),
        Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)) };
    private readonly Border detail = new() { Margin = new Thickness(8, 35, 8, 8), CornerRadius = new CornerRadius(Radius),
        BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
    private readonly CompositeTransform detailMotion = new();
    private readonly SolidColorBrush ink = new(), subtle = new(), idle = new(), tile = new(), edge = new();
    private readonly SolidColorBrush wifiFill = new(), bluetoothFill = new(), volumeFill = new();
    private readonly SolidColorBrush wifiGlyph = new(), bluetoothGlyph = new(), volumeGlyph = new();
    private readonly Button wifiCapsule, bluetoothCapsule, volumeButton, outputButton, appearanceButton, detailClose;
    private readonly Button detailedAppearance;
    private readonly Func<QuickControlAppearance, bool> saveAppearance;
    private readonly Dictionary<QuickBackdrop, (Button Button, TextBlock Check)> modeRows = new();
    private readonly Border wifiDisc, bluetoothDisc, volumeDisc;
    private readonly TaskbarStatusIcon detailWifi, detailVolume;
    private readonly TextBlock wifiText, bluetoothText, volumeText, brightnessText, detailHeading, listStatus;
    private readonly TextBlock wirelessStatusStrip;
    private readonly Slider volumeSlider, brightnessSlider;
    private readonly StackPanel detailList = new() { Spacing = 3 };
    private readonly ScrollViewer detailScroll;
    private readonly ToggleSwitch radioSwitch = new() { HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 0,
        FontSize = WirelessBodySize };
    private readonly RotateTransform sun = new();
    private QuickControlAppearance style;
    private LumaBar.BarAppearance? taskbar;
    private Storyboard? detailAnimation, sunAnimation;
    private Area? selected, detailArea;
    private bool disposed, applyingAudio, light;
    private SystemVolume.State? audioState;
    private Action? afterClose;

    internal QuickControlPanel(nint owner, FrameworkElement anchor, QuickControlAppearance appearance, Action<int> setVolume,
        Action mute, Func<SystemVolume.Output[]?> readOutputs, Func<string, bool> switchOutput,
        Func<QuickControlAppearance, bool> saveAppearance, Action showAppearance, Action opening)
    {
        this.owner = owner;
        this.readOutputs = readOutputs;
        this.switchOutput = switchOutput;
        this.saveAppearance = saveAppearance;
        style = appearance;
        var titleRow = new Grid();
        titleRow.Children.Add(Text("快捷控制", 16, ink, strong: true));
        appearanceButton = FlatButton(Glyph("\uE713", ink, 16), "快捷控制外观");
        appearanceButton.Width = appearanceButton.Height = 30;
        appearanceButton.HorizontalAlignment = HorizontalAlignment.Right;
        ToolTipService.SetToolTip(appearanceButton, "快捷控制外观");
        appearanceButton.Click += (_, _) => OpenDetails(Area.Appearance);
        titleRow.Children.Add(appearanceButton);
        main.Children.Add(titleRow);
        var shortcuts = new Grid { ColumnSpacing = 8 };
        shortcuts.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        shortcuts.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        detailWifi = new(true, wifiGlyph) { Width = 21, Height = 21 };
        (wifiCapsule, wifiDisc, wifiText) = Connection("Wi‑Fi", detailWifi, () => OpenDetails(Area.Wifi));
        (bluetoothCapsule, bluetoothDisc, bluetoothText) = Connection("蓝牙", Glyph("\uE702", bluetoothGlyph),
            () => OpenDetails(Area.Bluetooth));
        shortcuts.Children.Add(wifiCapsule);
        shortcuts.Children.Add(bluetoothCapsule);
        Grid.SetColumn(bluetoothCapsule, 1);
        main.Children.Add(shortcuts);

        detailVolume = new(false, volumeGlyph);
        volumeDisc = Disc(detailVolume);
        volumeButton = FlatButton(volumeDisc, "静音");
        volumeButton.Click += (_, _) => mute();
        var outputLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        outputLabel.Children.Add(Text("音量", 14, ink, strong: true));
        outputLabel.Children.Add(Glyph("\uE76C", subtle, 10));
        outputButton = FlatButton(outputLabel, "音量 · 选择输出设备");
        outputButton.HorizontalAlignment = HorizontalAlignment.Left;
        outputButton.Click += (_, _) => OpenDetails(Area.Audio);
        ToolTipService.SetToolTip(outputButton, "选择音频输出设备");
        (var volumeRow, volumeSlider, volumeText) = LevelRow("音量", volumeButton, outputButton);
        volumeSlider.ValueChanged += (_, args) => { if (!applyingAudio) setVolume((int)Math.Round(args.NewValue)); };
        main.Children.Add(volumeRow);

        var sunIcon = Glyph(SunGlyph, ink);
        sunIcon.RenderTransform = sun;
        sunIcon.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5);
        (var brightnessRow, brightnessSlider, brightnessText) = LevelRow("亮度", Disc(sunIcon));
        brightnessSlider.ValueChanged += BrightnessChanged;
        main.Children.Add(brightnessRow);

        var details = new Grid { Padding = new Thickness(11), RowSpacing = 5 };
        details.RowDefinitions.Add(new() { Height = GridLength.Auto });
        details.RowDefinitions.Add(new() { Height = GridLength.Auto });
        details.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        details.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        detailHeading = Text("", WirelessHeadingSize, ink, strong: true);
        header.Children.Add(detailHeading);
        header.Children.Add(radioSwitch);
        Grid.SetColumn(radioSwitch, 1);
        radioSwitch.OnContent = Text("已开启", WirelessBodySize, ink);
        radioSwitch.OffContent = Text("已关闭", WirelessBodySize, ink);
        radioSwitch.Toggled += RadioToggled;
        detailClose = FlatButton(Glyph("\uE711", ink, 16), "关闭详细面板");
        detailClose.Width = detailClose.Height = 32;
        detailClose.Click += (_, _) => CloseDetails();
        header.Children.Add(detailClose);
        Grid.SetColumn(detailClose, 2);
        details.Children.Add(header);
        wirelessStatusStrip = Text("", WirelessBodySize, ink);
        wirelessStatusStrip.TextWrapping = TextWrapping.Wrap;
        wirelessStatusStrip.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(wirelessStatusStrip, AutomationLiveSetting.Polite);
        details.Children.Add(wirelessStatusStrip);
        Grid.SetRow(wirelessStatusStrip, 1);
        listStatus = Text("", 12, subtle);
        listStatus.TextWrapping = TextWrapping.Wrap;
        AutomationProperties.SetLiveSetting(listStatus, AutomationLiveSetting.Polite);
        var detailContent = new StackPanel { Spacing = 4 };
        detailContent.Children.Add(listStatus);
        detailContent.Children.Add(detailList);
        detailScroll = new ScrollViewer { Content = detailContent, HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        details.Children.Add(detailScroll);
        Grid.SetRow(detailScroll, 2);
        detailedAppearance = DetailAction("详细外观设置", "›");
        AutomationProperties.SetName(detailedAppearance, "详细外观设置");
        detailedAppearance.Visibility = Visibility.Collapsed;
        detailedAppearance.Click += (_, _) => { afterClose = showAppearance; Hide(); };
        details.Children.Add(detailedAppearance);
        Grid.SetRow(detailedAppearance, 3);
        detail.Child = details;
        detail.RenderTransform = detailMotion;
        shade.PointerPressed += (_, args) => { args.Handled = true; CloseDetails(); };
        scene.Children.Add(backplate);
        scene.Children.Add(main);
        scene.Children.Add(shade);
        scene.Children.Add(detail);
        scene.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            if (detailArea.HasValue) CloseDetails();
            else Hide();
        }), true);

        panel = new(anchor, scene, backplate);
        panel.Opening += () => { opening(); ApplyPalette(); };
        panel.Opened += () => { FocusSelected(); Pulse(); };
        panel.Closed += () =>
        {
            selected = null;
            ResetDetails();
            var action = afterClose;
            afterClose = null;
            if (!disposed) action?.Invoke();
        };
        ApplyPalette();
        UpdateAudio(null);
        ApplyBrightness(null);
    }

    internal void Toggle(bool wifi)
    {
        var area = wifi ? Area.Wifi : Area.Audio;
        if (panel.IsOpen)
        {
            if (area == selected) { Hide(); return; }
            panel.CancelClosing();
            ResetDetails();
            selected = area;
            FocusSelected();
            return;
        }
        selected = area;
        nextPoll = 0;
        panel.Show();
    }

    internal void Hide() => panel.Hide();

    private bool Motion => panel.CanAnimate;

    private void FocusSelected()
    {
        if (selected == Area.Wifi) wifiCapsule.Focus(FocusState.Programmatic);
        else (volumeSlider.IsEnabled ? (Control)volumeSlider : wifiCapsule).Focus(FocusState.Programmatic);
    }

    private void OpenDetails(Area area)
    {
        ResetDetails();
        detailArea = area;
        detailHeading.Text = area switch
        {
            Area.Wifi => "Wi‑Fi",
            Area.Bluetooth => "蓝牙",
            Area.Audio => "音频输出设备",
            _ => "背板模式"
        };
        AutomationProperties.SetName(radioSwitch, detailHeading.Text + " 开关");
        radioSwitch.Visibility = area is Area.Wifi or Area.Bluetooth ? Visibility.Visible : Visibility.Collapsed;
        detailedAppearance.Visibility = area == Area.Appearance ? Visibility.Visible : Visibility.Collapsed;
        detail.Visibility = shade.Visibility = Visibility.Visible;
        SetMainTabStops(false);
        detailClose.Focus(FocusState.Programmatic);
        detailList.Children.Clear();
        listStatus.Visibility = area == Area.Appearance ? Visibility.Collapsed : Visibility.Visible;
        listStatus.FontSize = area is Area.Wifi or Area.Bluetooth ? WirelessBodySize : 12;
        listStatus.Text = "正在读取…";
        UpdateRadioSwitch();
        scene.UpdateLayout();
        if (Motion) AnimateDetail(area, show: true);
        if (area == Area.Audio) RefreshOutputs(force: true);
        else if (area == Area.Appearance) ShowBackdropModes();
        else _ = ReturnToWirelessListAsync(scan: true);
    }

    private void CloseDetails()
    {
        if (detailArea is not { } area) return;
        detailArea = null;
        detail.IsHitTestVisible = false;
        if (Motion) AnimateDetail(area, show: false);
        else ResetDetails();
        SetMainTabStops(true);
        DetailSource(area).Focus(FocusState.Programmatic);
    }

    private void SetMainTabStops(bool enabled)
    {
        main.IsHitTestVisible = enabled;
        foreach (var row in main.Children.Skip(1)) row.Opacity = enabled ? 1 : 0;
        foreach (var control in new Control[] { wifiCapsule, bluetoothCapsule, volumeButton, volumeSlider, brightnessSlider, outputButton, appearanceButton })
            control.IsTabStop = enabled;
    }

    private void ResetDetails()
    {
        detailArea = null;
        detailRevision++;
        wirelessEditor = false;
        ShowWirelessNotice(null);
        wirelessRows = null;
        detailAnimation?.Stop();
        shade.Visibility = detail.Visibility = Visibility.Collapsed;
        detail.IsHitTestVisible = true;
        detail.Opacity = detailMotion.ScaleX = detailMotion.ScaleY = 1;
        detailMotion.TranslateX = detailMotion.TranslateY = 0;
        SetMainTabStops(true);
    }

    private void AnimateDetail(Area area, bool show)
    {
        detailAnimation?.Stop();
        var source = DetailSource(area);
        var point = source.TransformToVisual(scene).TransformPoint(new());
        double x = point.X - detail.Margin.Left, y = point.Y - detail.Margin.Top;
        double sx = source.ActualWidth / Math.Max(1, detail.ActualWidth);
        double sy = source.ActualHeight / Math.Max(1, detail.ActualHeight);
        int duration = show ? 220 : 150;
        detailAnimation = new();
        AddAnimation(detailAnimation, detailMotion, "TranslateX", show ? x : 0, show ? 0 : x, duration);
        AddAnimation(detailAnimation, detailMotion, "TranslateY", show ? y : 0, show ? 0 : y, duration);
        AddAnimation(detailAnimation, detailMotion, "ScaleX", show ? sx : 1, show ? 1 : sx, duration);
        AddAnimation(detailAnimation, detailMotion, "ScaleY", show ? sy : 1, show ? 1 : sy, duration);
        AddAnimation(detailAnimation, detail, "Opacity", show ? .35 : 1, show ? 1 : .35, duration);
        if (!show) detailAnimation.Completed += (_, _) => ResetDetails();
        detailAnimation.Begin();
    }

    private Button DetailSource(Area area) => area switch
    {
        Area.Wifi => wifiCapsule,
        Area.Bluetooth => bluetoothCapsule,
        Area.Audio => outputButton,
        _ => appearanceButton
    };

    private (Button Button, Border Disc, TextBlock Status) Connection(string label, FrameworkElement icon, Action open)
    {
        var grid = new Grid { ColumnSpacing = 7 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var disc = Disc(icon);
        var status = Text("未连接", 12, subtle);
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(Text(label, 14, ink, strong: true));
        labels.Children.Add(status);
        var arrow = Glyph("\uE76C", subtle, 11);
        grid.Children.Add(disc);
        grid.Children.Add(labels);
        grid.Children.Add(arrow);
        Grid.SetColumn(labels, 1);
        Grid.SetColumn(arrow, 2);
        var button = FlatButton(grid, label + " 详细设置");
        button.Background = tile;
        button.Padding = new Thickness(7, 5, 9, 5);
        button.CornerRadius = new CornerRadius(28);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Click += (_, _) => open();
        return (button, disc, status);
    }

    private (Border Row, Slider Slider, TextBlock Value) LevelRow(string name, FrameworkElement icon, FrameworkElement? title = null)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var label = new Grid();
        label.Children.Add(title ?? Text(name, 14, ink, strong: true));
        var value = Text("", 12, subtle);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        label.Children.Add(value);
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 1, MinHeight = 22 };
        AutomationProperties.SetName(slider, name);
        var settings = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        settings.Children.Add(label);
        settings.Children.Add(slider);
        row.Children.Add(icon);
        row.Children.Add(settings);
        Grid.SetColumn(settings, 1);
        var surface = new Border { Background = tile, Padding = new Thickness(7, 4, 12, 4), CornerRadius = new CornerRadius(28),
            Child = row, MinHeight = 53 };
        QuickControlWheel.Attach(surface,
            change => slider.Value = Math.Clamp(slider.Value + change, slider.Minimum, slider.Maximum),
            () => slider.IsEnabled && !detailArea.HasValue);
        return (surface, slider, value);
    }

    private Border Disc(FrameworkElement child) => new() { Child = child, Width = 42, Height = 42,
        Background = idle, CornerRadius = new CornerRadius(21), VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Text(string text, double size, Brush color, bool strong = false) => new()
    {
        Text = text, FontSize = size, Foreground = color, VerticalAlignment = VerticalAlignment.Center,
        FontWeight = strong ? FontWeights.SemiBold : FontWeights.Normal
    };

    private static FontIcon Glyph(string glyph, Brush color, double size = 19) => new()
    {
        Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), Foreground = color, FontSize = size
    };

    private static Button FlatButton(object content, string name)
    {
        var button = new Button { Content = content, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetName(button, name);
        return button;
    }

    internal void UpdateAudio(SystemVolume.State? value)
    {
        audioState = value;
        int level = value?.Level ?? 0;
        bool muted = value?.Muted ?? true;
        detailVolume.SetVolume(level, muted);
        volumeText.Text = value is null ? "无输出设备" : muted ? "静音" : $"{level}%";
        volumeButton.IsEnabled = volumeSlider.IsEnabled = value.HasValue;
        AutomationProperties.SetName(volumeButton, muted ? "取消静音" : "静音");
        applyingAudio = true;
        volumeSlider.Value = level;
        applyingAudio = false;
        ApplyAccents();
    }

    internal void SetAppearance(QuickControlAppearance appearance)
    {
        style = appearance;
        ApplyPalette();
        if (detailArea == Area.Appearance) UpdateBackdropChecks();
    }

    private void ShowBackdropModes()
    {
        modeRows.Clear();
        foreach (var (mode, label) in QuickControlAppearance.Modes)
        {
            var button = DetailAction(label, "", out var check);
            button.Click += (_, _) =>
            {
                bool saved = saveAppearance(style with { Backdrop = mode });
                listStatus.Text = saved ? "" : "无法保存背板模式。";
                listStatus.Visibility = saved ? Visibility.Collapsed : Visibility.Visible;
            };
            modeRows.Add(mode, (button, check));
            detailList.Children.Add(button);
        }
        UpdateBackdropChecks();
    }

    private void UpdateBackdropChecks()
    {
        foreach (var (mode, row) in modeRows)
        {
            bool selected = style.Backdrop == mode;
            row.Check.Text = selected ? "✓" : "";
            AutomationProperties.SetItemStatus(row.Button, selected ? "当前模式" : "");
        }
    }

    internal void SetTaskbarAppearance(bool light, LumaBar.BarAppearance? appearance)
    {
        this.light = light;
        taskbar = appearance;
        ApplyPalette();
    }

    private void ApplyPalette()
    {
        bool highContrast = accessibility.HighContrast;
        var (text, raised) = panel.ApplyPalette(style, light, taskbar);
        ink.Color = text;
        subtle.Color = text;
        idle.Color = Alpha(text, 28);
        tile.Color = Alpha(text, 17);
        edge.Color = Alpha(text, 54);
        detail.Background = new SolidColorBrush(raised);
        detail.BorderBrush = edge;
        wifiFill.Color = highContrast ? system.GetColorValue(UIColorType.Accent) : FromRgb(style.Wifi);
        bluetoothFill.Color = highContrast ? system.GetColorValue(UIColorType.Accent) : FromRgb(style.Bluetooth);
        volumeFill.Color = highContrast ? system.GetColorValue(UIColorType.Accent) : FromRgb(style.Volume);
        ApplyAccents();
    }

    private void ApplyAccents()
    {
        bool hasWifi = wifiRadio is not null ? IsOn(wifiRadio) : wifiStatus.Bars > 0;
        bool hasBluetooth = IsOn(bluetoothRadio);
        bool hasAudio = audioState is { Muted: false, Level: > 0 };
        wifiDisc.Background = hasWifi ? wifiFill : idle;
        bluetoothDisc.Background = hasBluetooth ? bluetoothFill : idle;
        volumeDisc.Background = hasAudio ? volumeFill : idle;
        wifiGlyph.Color = hasWifi ? AccentInk(wifiFill.Color) : ink.Color;
        bluetoothGlyph.Color = hasBluetooth ? AccentInk(bluetoothFill.Color) : ink.Color;
        volumeGlyph.Color = hasAudio ? AccentInk(volumeFill.Color) : ink.Color;
    }

    private static Color AccentInk(Color color) => IsLight(color) ? Color.FromArgb(255, 15, 22, 31) : Microsoft.UI.Colors.White;
    public void Dispose()
    {
        disposed = true;
        panel.Dispose();
        detailAnimation?.Stop();
        sunAnimation?.Stop();
    }
}

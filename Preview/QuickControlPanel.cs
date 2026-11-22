using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed partial class QuickControlPanel : IDisposable
{
    private enum Area { Wifi, Bluetooth, Audio, Appearance }

    private const double Radius = 23;
    private const string SunGlyph = "\uE706";
    private readonly nint owner;
    private readonly FrameworkElement anchor;
    private readonly Flyout flyout;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Grid scene = new() { Width = 358, Height = 254 };
    private readonly StackPanel main = new() { Spacing = 8, Margin = new Thickness(13) };
    private readonly Border backplate = new() { CornerRadius = new CornerRadius(Radius) };
    private readonly Border shade = new() { CornerRadius = new CornerRadius(Radius),
        Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)) };
    private readonly Border detail = new() { Margin = new Thickness(8, 35, 8, 8), CornerRadius = new CornerRadius(Radius),
        BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
    private readonly CompositeTransform panelMotion = new(), detailMotion = new();
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
        OnContent = "已开启", OffContent = "已关闭" };
    private readonly RotateTransform sun = new();
    private QuickControlAppearance style;
    private LumaBar.BarAppearance? taskbar;
    private Halo.GlassBackdrop? desktopBackdrop;
    private string? backdropKind;
    private Storyboard? panelAnimation, detailAnimation, sunAnimation;
    private Area? selected, detailArea;
    private bool opened, closing, finishClose, disposed, applyingAudio, light;
    private SystemVolume.State? audioState;
    private Action? afterClose;

    internal QuickControlPanel(nint owner, FrameworkElement anchor, QuickControlAppearance appearance, Action<int> setVolume,
        Action mute, Func<SystemVolume.Output[]?> readOutputs, Func<string, bool> switchOutput,
        Func<QuickControlAppearance, bool> saveAppearance, Action showAppearance, Action opening)
    {
        this.owner = owner;
        this.anchor = anchor;
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
        detailHeading = Text("", 16, ink, strong: true);
        header.Children.Add(detailHeading);
        header.Children.Add(radioSwitch);
        Grid.SetColumn(radioSwitch, 1);
        radioSwitch.Toggled += RadioToggled;
        detailClose = FlatButton(Glyph("\uE711", ink), "关闭详细面板");
        detailClose.Width = detailClose.Height = 32;
        detailClose.Click += (_, _) => CloseDetails();
        header.Children.Add(detailClose);
        Grid.SetColumn(detailClose, 2);
        details.Children.Add(header);
        wirelessStatusStrip = Text("", 12, ink);
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
        scene.RenderTransform = panelMotion;
        scene.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            if (detailArea.HasValue) CloseDetails();
            else Hide();
        }), true);

        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(Radius)));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        presenter.Setters.Add(new Setter(FlyoutPresenter.IsDefaultShadowEnabledProperty, false));
        flyout = new() { Content = scene, Placement = FlyoutPlacementMode.Top, ShouldConstrainToRootBounds = false,
            AreOpenCloseAnimationsEnabled = false, FlyoutPresenterStyle = presenter,
            OverlayInputPassThroughElement = anchor };
        flyout.Opening += (_, _) => { opened = true; opening(); ApplyPalette(); };
        flyout.Opened += (_, _) =>
        {
            FocusSelected();
            AnimatePanel(show: true);
            Pulse();
        };
        flyout.Closing += (_, args) =>
        {
            if (finishClose || disposed || !Motion) return;
            args.Cancel = true;
            if (!closing) AnimatePanel(show: false);
        };
        flyout.Closed += (_, _) =>
        {
            opened = closing = finishClose = false;
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
        if (opened)
        {
            if (area == selected) { Hide(); return; }
            if (closing)
            {
                panelAnimation?.Stop();
                closing = false;
                scene.Opacity = 1;
                panelMotion.TranslateY = 0;
            }
            ResetDetails();
            selected = area;
            FocusSelected();
            return;
        }
        selected = area;
        nextPoll = 0;
        flyout.ShowAt(anchor);
    }

    internal void Hide() { if (opened) flyout.Hide(); }

    private bool Motion => system.AnimationsEnabled && !accessibility.HighContrast;

    private void FocusSelected()
    {
        if (selected == Area.Wifi) wifiCapsule.Focus(FocusState.Programmatic);
        else (volumeSlider.IsEnabled ? (Control)volumeSlider : wifiCapsule).Focus(FocusState.Programmatic);
    }

    private void AnimatePanel(bool show)
    {
        double opacity = show ? 0 : scene.Opacity;
        double offset = show ? 12 : panelMotion.TranslateY;
        panelAnimation?.Stop();
        closing = !show;
        if (!Motion)
        {
            scene.Opacity = 1;
            panelMotion.TranslateY = 0;
            return;
        }
        scene.Opacity = show ? 1 : 0;
        panelMotion.TranslateY = show ? 0 : 12;
        panelAnimation = new();
        AddAnimation(panelAnimation, scene, "Opacity", opacity, scene.Opacity, show ? 180 : 140);
        AddAnimation(panelAnimation, panelMotion, "TranslateY", offset, panelMotion.TranslateY, show ? 180 : 140);
        panelAnimation.Completed += (_, _) =>
        {
            if (show) return;
            finishClose = true;
            flyout.Hide();
        };
        panelAnimation.Begin();
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

    private static void AddAnimation(Storyboard storyboard, DependencyObject target, string property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation { From = from, To = to, EnableDependentAnimation = true,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

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
        bool followsBar = style.Backdrop == QuickBackdrop.FollowTaskbar;
        bool isLight = style.Backdrop switch { QuickBackdrop.Light => true, QuickBackdrop.Dark => false,
            QuickBackdrop.Custom => IsLight(FromRgb(style.Tint)), _ => light };
        var neutral = isLight ? Color.FromArgb(255, 243, 246, 250) : Color.FromArgb(255, 30, 34, 42);
        string material = followsBar ? taskbar?.Material ?? "normal" : "blur";
        Color tint = style.Backdrop == QuickBackdrop.Custom ? FromRgb(style.Tint)
            : followsBar && material != "normal" && taskbar is { } bar ? PreviewBarAppearance.FromAbgr(bar.Abgr) : neutral;
        double opacity = style.Backdrop == QuickBackdrop.Custom ? 1 - style.Transparency / 100d
            : followsBar ? tint.A / 255d : .86;
        var solid = Blend(neutral, tint, opacity);
        var text = highContrast ? system.GetColorValue(UIColorType.Foreground)
            : isLight ? Color.FromArgb(255, 24, 39, 57) : Color.FromArgb(255, 242, 247, 255);
        ink.Color = text;
        subtle.Color = text;
        idle.Color = Alpha(text, 28);
        tile.Color = Alpha(text, 17);
        edge.Color = Alpha(text, 54);
        scene.RequestedTheme = highContrast ? ElementTheme.Default : isLight ? ElementTheme.Light : ElementTheme.Dark;
        bool glass = !highContrast && system.AdvancedEffectsEnabled && material is not ("normal" or "opaque");
        bool acrylic = glass && material == "acrylic" && DesktopAcrylicController.IsSupported();
        int blur = followsBar ? material == "acrylic" ? 30 : taskbar?.Blur ?? 0 : 34;
        SetBackdrop(!glass ? null : acrylic ? "acrylic" : material is "blur" or "acrylic" ? "blur" : "desktop");
        if (acrylic)
            desktopBackdrop!.Update(new Halo.MaterialOptions(Thin: true, TintOpacity: (float)opacity, LuminosityOpacity: .65f,
                FollowTheme: false, TintHex: $"#{tint.R:X2}{tint.G:X2}{tint.B:X2}"));
        else desktopBackdrop?.UpdateBlur(blur);
        backplate.Background = new SolidColorBrush(highContrast ? system.GetColorValue(UIColorType.Background)
            : !glass ? solid : acrylic ? Microsoft.UI.Colors.Transparent : Alpha(tint, (byte)Math.Round(opacity * 255)));
        var raised = Blend(solid, text, .045);
        detail.Background = new SolidColorBrush(highContrast ? system.GetColorValue(UIColorType.Background)
            : glass ? Alpha(raised, 72) : raised);
        detail.BorderBrush = edge;
        wifiFill.Color = highContrast ? system.GetColorValue(UIColorType.Accent) : FromRgb(style.Wifi);
        bluetoothFill.Color = highContrast ? system.GetColorValue(UIColorType.Accent) : FromRgb(style.Bluetooth);
        volumeFill.Color = highContrast ? system.GetColorValue(UIColorType.Accent) : FromRgb(style.Volume);
        ApplyAccents();
    }

    private void SetBackdrop(string? kind)
    {
        if (kind == backdropKind) return;
        backdropKind = kind;
        desktopBackdrop = kind switch
        {
            "blur" or "desktop" => new Halo.GlassBackdrop(new Halo.MaterialOptions(TintOpacity: 0, LuminosityOpacity: 0, NoBase: true))
            {
                TransparentCanvas = true, ConfigureWindowFrame = false, BlurScale = 1f / 3,
                KeepBlurBackdropAtZero = kind == "blur", MaximumBlurStrength = LumaBar.TaskbarAppearance.MaximumBlurRadius
            },
            "acrylic" => new Halo.GlassBackdrop(Halo.GlassStyle.Acrylic),
            _ => null
        };
        flyout.SystemBackdrop = desktopBackdrop;
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
    private static bool IsLight(Color color) => (color.R * 299 + color.G * 587 + color.B * 114) >= 155000;
    private static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    private static Color FromRgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static Color Blend(Color first, Color second, double fraction) => Color.FromArgb(255,
        (byte)Math.Round(first.R + (second.R - first.R) * fraction), (byte)Math.Round(first.G + (second.G - first.G) * fraction),
        (byte)Math.Round(first.B + (second.B - first.B) * fraction));

    public void Dispose()
    {
        disposed = true;
        flyout.Hide();
        panelAnimation?.Stop();
        detailAnimation?.Stop();
        sunAnimation?.Stop();
    }
}

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed partial class TaskbarSystemTray : StackPanel, IDisposable
{
    private readonly DispatcherQueueTimer timer;
    private readonly DispatcherQueueTimer volumeFeedbackTimer;
    private const double VolumeFeedbackWidth = 62, VolumeFeedbackHeight = 32;
    private readonly TextBlock volumeFeedbackText = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border volumeFeedbackSurface = new()
    {
        Width = VolumeFeedbackWidth, Height = VolumeFeedbackHeight,
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), IsHitTestVisible = false
    };
    private readonly Popup volumeFeedback = new() { ShouldConstrainToRootBounds = false, IsHitTestVisible = false };
    private readonly SystemVolume audio = new();
    private readonly UISettings ui = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly SolidColorBrush foreground = new(Microsoft.UI.Colors.White);
    private readonly TaskbarBatteryBorder batteryBorder = new();
    private readonly Grid capsule = new() { Width = TaskbarBatteryBorder.CapsuleWidth,
        Height = TaskbarBatteryBorder.CapsuleHeight, VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly TaskbarStatusIcon wifiIcon, audioIcon;
    private readonly Button wifi, volume;
    private readonly QuickControlPanel quickControls;
    private readonly FontIcon bolt = new() { FontFamily = new("Segoe Fluent Icons"), Glyph = "\uE945", FontSize = 12,
        Width = 12, Visibility = Visibility.Collapsed };
    private readonly TextBlock time = new() { FontSize = 15, TextAlignment = TextAlignment.Right };
    private readonly TextBlock date = new() { FontSize = 11, TextAlignment = TextAlignment.Right };
    private readonly RotateTransform direction = new() { CenterX = 6, CenterY = 4 };
    private readonly Flyout trayFlyout;
    private readonly WindowsNotificationArea notificationArea;
    private bool notificationReceiverEnabled;
    private readonly nint window;
    private const int AppColumns = 5, AppTileSize = 38, AppTileSpacing = 6;
    private readonly Grid appIcons = new() { ColumnSpacing = AppTileSpacing, RowSpacing = AppTileSpacing };
    private readonly ScrollViewer appScroll;
    private readonly TrayIconOrder appOrder;
    private readonly Action<string> report;
    private readonly TextBlock emptyApps = new() { Opacity = .75, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<WindowsNotificationArea.TrayIcon, TrayTile> tiles = new();
    private Storyboard? arrowTransition;
    private bool disposed, wifiRefreshing, trayOpen;
    private long wifiReadAt, trayRefreshedAt;
    private bool volumeHovered;
    private string? batteryDescription;
    private SystemVolume.State? lastAudio;
    private (SystemTrayStatus.Battery Battery, bool Motion)? lastBattery;

    internal TaskbarSystemTray(DispatcherQueue dispatcher, nint window, string root, TaskbarClockStyle clockStyle,
        QuickControlAppearance quickAppearance, Action showSettings, Action showAppearance, Action showQuickAppearance,
        Func<QuickControlAppearance, bool> saveQuickAppearance, Action dismissThumbnails, Action<string> report)
    {
        this.window = window;
        this.report = report;
        appOrder = new(Path.Combine(root, "tray-icon-order.json"));
        notificationArea = new(dispatcher);
        notificationArea.Changed += RefreshAppIcons;
        Orientation = Orientation.Horizontal;
        Spacing = 4;
        VerticalAlignment = VerticalAlignment.Center;

        var arrow = TaskbarStatusIcon.CreatePath("M1 6 L5 2 Q6 1 7 2 L11 6 Q12 7 10.5 7 H1.5 Q0 7 1 6 Z", foreground);
        var arrowIcon = new Grid { Width = 12, Height = 8, RenderTransform = direction };
        arrowIcon.Children.Add(arrow);
        var trayGlyph = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        trayGlyph.Children.Add(arrowIcon);
        trayGlyph.Children.Add(new Border { Width = 13, Height = 1.25, CornerRadius = new CornerRadius(1),
            Background = foreground });
        var trayButton = TrayButton("后台应用", trayGlyph, 30);
        var trayItems = new StackPanel { Spacing = 8, MinWidth = 184 };
        trayItems.Children.Add(new TextBlock { Text = "后台应用", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3) });
        for (int index = 0; index < AppColumns; index++) appIcons.ColumnDefinitions.Add(new() { Width = new GridLength(AppTileSize) });
        trayItems.Children.Add(emptyApps);
        appScroll = new ScrollViewer { Content = appIcons, MaxHeight = 210,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        appScroll.ViewChanged += (_, _) => PreviewAppDrop();
        trayItems.Children.Add(appScroll);
        trayItems.Children.Add(ActionButton("AITaskbar 设置", Symbol.Setting, () => { trayFlyout!.Hide(); showSettings(); }));
        trayItems.Children.Add(ActionButton("LumaBar 外观", Symbol.View, () => { trayFlyout!.Hide(); showAppearance(); }));
        trayFlyout = CreateAboveTaskbarFlyout(trayItems);
        trayFlyout.Opening += (_, _) =>
        {
            HideVolumeFeedback();
            quickControls?.Hide();
            trayOpen = true;
            RefreshAppIcons();
            SetArrow(180);
            AutomationProperties.SetItemStatus(trayButton, "已展开");
            dismissThumbnails();
        };
        trayFlyout.Closing += (_, _) =>
        {
            trayOpen = false;
            CancelAppReorder();
            SetArrow(0);
            AutomationProperties.SetItemStatus(trayButton, "已收起");
        };
        AutomationProperties.SetItemStatus(trayButton, "已收起");
        trayButton.Flyout = trayFlyout;
        Children.Add(trayButton);

        wifiIcon = new(true, foreground);
        audioIcon = new(false, foreground);
        wifi = TrayButton("Wi-Fi 未连接", wifiIcon, 32);
        volume = TrayButton("音量", audioIcon, 32);
        ToolTipService.SetToolTip(volume, null);
        volume.PointerEntered += (_, _) => SetVolumeHover(true);
        volume.PointerExited += (_, _) => SetVolumeHover(false);
        volumeFeedbackSurface.Child = volumeFeedbackText;
        volumeFeedback.Child = volumeFeedbackSurface;
        volumeFeedbackTimer = dispatcher.CreateTimer();
        volumeFeedbackTimer.IsRepeating = false;
        volumeFeedbackTimer.Interval = TimeSpan.FromMilliseconds(950);
        volumeFeedbackTimer.Tick += (_, _) => HideVolumeFeedback();
        quickControls = new(window, capsule, quickAppearance,
            SetVolume,
            () => { audio.ToggleMute(); RefreshAudio(); },
            audio.ReadOutputs, id => { bool switched = audio.SwitchOutput(id); RefreshAudio(); return switched; },
            saveQuickAppearance, showQuickAppearance,
            () => { HideVolumeFeedback(); trayFlyout.Hide(); dismissThumbnails(); RefreshAudio(); });
        wifi.Click += (_, _) => quickControls.Toggle(wifi: true);
        volume.Click += (_, _) => quickControls.Toggle(wifi: false);
        QuickControlWheel.Attach(volume, change =>
        {
            if (audio.Read() is not { } state) return;
            SetVolumeHover(true);
            int next = Math.Clamp(state.Level + change, 0, 100);
            if (next != state.Level) SetVolume(next);
            else RefreshAudio();
            if (lastAudio is { } confirmed) ShowVolumeFeedback(confirmed.Level);
        });

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(wifi);
        controls.Children.Add(volume);
        capsule.Children.Add(batteryBorder);
        capsule.Children.Add(controls);
        Children.Add(capsule);
        bolt.Foreground = foreground;
        Children.Add(bolt);

        var clock = new StackPanel { MinWidth = 66, Margin = new Thickness(2, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center, Spacing = 0 };
        time.Foreground = date.Foreground = foreground;
        clock.Children.Add(time);
        clock.Children.Add(date);
        Children.Add(clock);
        SetClockStyle(clockStyle);

        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => { Refresh(); ScrollAppReorder(); };
        Loaded += (_, _) => { if (!disposed) { Refresh(); timer.Start(); } };
        Unloaded += (_, _) => { timer.Stop(); HideVolumeFeedback(); };
        ApplyTone(light: false);
    }

    private static Flyout CreateAboveTaskbarFlyout(FrameworkElement content) => new()
    {
        Placement = FlyoutPlacementMode.Top,
        // The AppBar HWND is only as tall as the strip. Use a desktop popup above it.
        ShouldConstrainToRootBounds = false,
        Content = content,
    };

    private static Button TrayButton(string name, object icon, double width)
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var button = new Button { Content = icon, Background = clear, BorderThickness = new Thickness(0),
            Width = width, Height = 30, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        foreach (var state in new[] { "PointerOver", "Pressed", "Disabled" })
        {
            button.Resources["ButtonBackground" + state] = clear;
            button.Resources["ButtonBorderBrush" + state] = clear;
        }
        Describe(button, name);
        return button;
    }

    private static void Describe(Button button, string text)
    {
        if (AutomationProperties.GetName(button) == text) return;
        AutomationProperties.SetName(button, text);
        ToolTipService.SetToolTip(button, text);
    }

    private void RefreshAppIcons()
    {
        if (!trayOpen || disposed || appDrag is not null) return;
        var entries = displayedApps = appOrder.Sort(notificationArea.Icons);
        foreach (var icon in tiles.Keys.Except(entries).ToArray())
        {
            appIcons.Children.Remove(tiles[icon].Button);
            tiles.Remove(icon);
        }
        int rowCount = (entries.Length + AppColumns - 1) / AppColumns;
        while (appIcons.RowDefinitions.Count < rowCount) appIcons.RowDefinitions.Add(new() { Height = GridLength.Auto });
        while (appIcons.RowDefinitions.Count > rowCount) appIcons.RowDefinitions.RemoveAt(appIcons.RowDefinitions.Count - 1);
        appIcons.Visibility = entries.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        emptyApps.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        emptyApps.Text = notificationArea.Available ? "暂无已接入的后台应用图标。" : "后台应用图标暂不可用。";
        for (int index = 0; index < entries.Length; index++)
        {
            var icon = entries[index];
            string name = string.IsNullOrWhiteSpace(icon.Title) ? "后台应用" : icon.Title;
            if (!tiles.TryGetValue(icon, out var tile))
            {
                tile = CreateTrayTile(icon, name);
                tiles.Add(icon, tile);
                appIcons.Children.Add(tile.Button);
            }
            Describe(tile.Button, name);
            if (tile.Source != icon.Image)
            {
                using var stream = new MemoryStream(icon.Image!, writable: false);
                using var bytes = stream.AsRandomAccessStream();
                var bitmap = new BitmapImage();
                bitmap.SetSource(bytes);
                tile.Image.Source = bitmap;
                tile.Source = icon.Image;
            }
            SetAppSlot(tile.Button, index);
        }
    }

    private TrayTile CreateTrayTile(WindowsNotificationArea.TrayIcon icon, string name)
    {
        var image = new Image { Width = 21, Height = 21, IsHitTestVisible = false };
        var button = TrayButton(name, image, AppTileSize);
        button.Height = AppTileSize;
        Windows.Foundation.Point? pointer = null;
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var press = args.GetCurrentPoint(button);
            pointer = press.Properties.IsLeftButtonPressed ? press.Position : null;
        }), handledEventsToo: true);
        button.PointerCanceled += (_, _) => pointer = null;
        button.Click += (_, _) =>
        {
            if (suppressedAppClick != button) ActivateIcon(icon, button, context: false, pointer);
            pointer = null;
        };
        button.ContextRequested += (_, args) =>
        {
            args.Handled = true;
            if (appDrag is null)
                ActivateIcon(icon, button, context: true, args.TryGetPosition(button, out var position) ? position : null);
            pointer = null;
        };
        EnableAppReorder(icon, button);
        return new(button, image);
    }

    private sealed record TrayTile(Button Button, Image Image)
    {
        internal byte[]? Source;
    }

    private void ActivateIcon(WindowsNotificationArea.TrayIcon icon, Button button, bool context, Windows.Foundation.Point? pointer)
    {
        if (!icon.CanActivate) return;
        var origin = new ScreenPoint();
        ClientToScreen(window, ref origin);
        var offset = button.TransformToVisual(null).TransformPoint(new(0, 0));
        double scale = button.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Round(button.ActualWidth * scale), height = (int)Math.Round(button.ActualHeight * scale);
        int left = origin.X + (int)Math.Round(offset.X * scale), top = origin.Y + (int)Math.Round(offset.Y * scale);
        int x = left + width / 2, y = top + height / 2;
        if (pointer is { } local && GetCursorPos(out var cursor))
        {
            x = cursor.X; y = cursor.Y;
            left = x - (int)Math.Round(local.X * scale);
            top = y - (int)Math.Round(local.Y * scale);
        }
        bool keyboard = pointer is null;
        if (context || keyboard) trayFlyout.Hide();
        notificationArea.Activate(icon, context, keyboard, left, top, left + width, top + height, x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref ScreenPoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out ScreenPoint point);

    private static Button ActionButton(string text, Symbol symbol, Action open)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new SymbolIcon(symbol) { Width = 18 });
        label.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => open();
        return button;
    }

    internal void SetClockStyle(TaskbarClockStyle style)
    {
        time.FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)style.TimeWeight };
        date.FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)style.DateWeight };
    }

    internal void SetQuickAppearance(QuickControlAppearance appearance) => quickControls.SetAppearance(appearance);

    internal void ApplyTone(bool light, LumaBar.BarAppearance? appearance = null)
    {
        quickControls.SetTaskbarAppearance(light, appearance);
        bool contrast = accessibility.HighContrast;
        var text = contrast ? ui.GetColorValue(UIColorType.Foreground)
            : light ? Color.FromArgb(255, 24, 39, 57) : Color.FromArgb(255, 239, 247, 255);
        foreground.Color = text;
        volumeFeedbackText.Foreground = new SolidColorBrush(text);
        volumeFeedbackSurface.Background = new SolidColorBrush(contrast ? ui.GetColorValue(UIColorType.Background)
            : light ? Color.FromArgb(250, 243, 246, 250) : Color.FromArgb(250, 30, 34, 42));
        volumeFeedbackSurface.BorderBrush = new SolidColorBrush(Color.FromArgb(contrast ? (byte)255 : (byte)45,
            text.R, text.G, text.B));
        batteryBorder.ApplyColors(contrast ? text : Color.FromArgb(100, text.R, text.G, text.B),
            contrast ? ui.GetColorValue(UIColorType.Accent) : light ? Color.FromArgb(255, 0, 123, 83) : Color.FromArgb(255, 66, 237, 169),
            contrast ? text : light ? Color.FromArgb(255, 12, 154, 107) : Color.FromArgb(255, 208, 255, 233));
    }

    // Let Explorer finish the initial AppBar placement before adding another Shell_TrayWnd.
    internal void EnableNotificationReceiver() => notificationReceiverEnabled = true;

    private void Refresh()
    {
        long tick = Environment.TickCount64;
        if (notificationReceiverEnabled && (trayRefreshedAt == 0 || tick - trayRefreshedAt >= 1000))
        {
            trayRefreshedAt = tick;
            notificationArea.Refresh();
        }
        var now = DateTime.Now;
        string clock = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        string today = now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
        if (time.Text != clock) time.Text = clock;
        if (date.Text != today) date.Text = today;
        RefreshAudio();
        quickControls.Pulse();
        var battery = SystemTrayStatus.ReadBattery();
        bool motion = ui.AnimationsEnabled && !accessibility.HighContrast;
        if (lastBattery != (battery, motion))
        {
            lastBattery = (battery, motion);
            batteryBorder.Update(battery.Percent, battery.Charging, motion);
            bolt.Visibility = battery.PluggedIn || battery.Charging ? Visibility.Visible : Visibility.Collapsed;
            batteryDescription = battery.Percent is int percent
                ? $"电量 {percent}%{(battery.Charging ? " · 充电中" : battery.PluggedIn ? " · 已接通电源" : "")}" : null;
            if (!volumeHovered) ToolTipService.SetToolTip(capsule, batteryDescription);
            ToolTipService.SetToolTip(bolt, batteryDescription);
            AutomationProperties.SetName(bolt, batteryDescription ?? "电源");
        }
        if (!wifiRefreshing && (wifiReadAt == 0 || Environment.TickCount64 - wifiReadAt >= 1500))
            _ = RefreshWifiAsync();
    }

    private async Task RefreshWifiAsync()
    {
        wifiRefreshing = true;
        try
        {
            if (await Task.Run(SystemTrayStatus.TryReadWifi) is not { } status || disposed) return;
            wifiIcon.SetLevel(status.Bars);
            quickControls.UpdateWifi(status);
            Describe(wifi, status.Bars == 0 ? "Wi-Fi 未连接" : $"Wi-Fi · {status.Name} · 信号 {status.Bars}/3");
        }
        finally { wifiReadAt = Environment.TickCount64; wifiRefreshing = false; }
    }

    private void SetVolume(int level)
    {
        audio.SetLevel(level);
        RefreshAudio();
    }

    private void RefreshAudio()
    {
        var state = audio.Read();
        if (state == lastAudio) return;
        lastAudio = state;
        int level = state?.Level ?? 0;
        bool silenced = state?.Muted ?? true;
        audioIcon.SetVolume(level, silenced);
        AutomationProperties.SetName(volume, state is null ? "音量 · 无输出设备" :
            silenced ? $"音量 {level}% · 已静音" : $"音量 {level}%");
        quickControls.UpdateAudio(state);
    }

    private void ShowVolumeFeedback(int level)
    {
        if (volume.XamlRoot is not { } root || disposed) return;
        var anchor = volume.TransformToVisual(null).TransformPoint(new(volume.ActualWidth / 2, 0));
        volumeFeedback.XamlRoot = root;
        volumeFeedback.HorizontalOffset = anchor.X - VolumeFeedbackWidth / 2;
        volumeFeedback.VerticalOffset = anchor.Y - VolumeFeedbackHeight - 6;
        volumeFeedbackText.Text = $"{level}%";
        volumeFeedback.IsOpen = true;
        volumeFeedbackTimer.Stop();
        volumeFeedbackTimer.Start();
    }

    private void SetVolumeHover(bool hovered)
    {
        if (volumeHovered == hovered) return;
        volumeHovered = hovered;
        ToolTipService.SetToolTip(capsule, hovered ? null : batteryDescription);
    }

    private void HideVolumeFeedback()
    {
        volumeFeedbackTimer?.Stop();
        volumeFeedback.IsOpen = false;
    }

    private void SetArrow(double angle)
    {
        double from = direction.Angle;
        arrowTransition?.Stop();
        direction.Angle = angle;
        if (!ui.AnimationsEnabled) return;
        var motion = new DoubleAnimation { From = from, To = angle, Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
        Storyboard.SetTarget(motion, direction);
        Storyboard.SetTargetProperty(motion, "Angle");
        arrowTransition = new Storyboard();
        arrowTransition.Children.Add(motion);
        arrowTransition.Begin();
    }

    public void Dispose()
    {
        disposed = true;
        timer.Stop();
        HideVolumeFeedback();
        notificationArea.Dispose();
        trayFlyout.Hide();
        quickControls.Dispose();
        arrowTransition?.Stop();
        batteryBorder.Dispose();
        audio.Dispose();
    }
}

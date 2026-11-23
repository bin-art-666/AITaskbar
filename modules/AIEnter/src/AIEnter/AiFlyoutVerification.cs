using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.UI;

namespace AIEnter;

public partial class App
{
    async Task VerifyAiFlyout()
    {
        AiWindow? panel = null;
        Window? owner = null, background = null;
        string resultPath = Path.Combine(DataDirectory, "flyout-result.json");
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var monitor = Native.Monitors().First(m => m.Primary);
            var work = monitor.Info.Work;
            int scale = Math.Max(1, (int)Math.Round(monitor.Dpi / 96d));
            bool customColors = Environment.GetCommandLineArgs().Contains("--verify-ai-custom-backplate");
            var options = customColors ? Ai.Options : new AiOptionsStore(Path.Combine(DataDirectory, "flyout-ai.json"));
            options.Save(options.Data with { Pinned = true, FloatingPanel = true, DockWidth = 380, DockHeight = 420 });
            var backdrop = new Border { Background = new SolidColorBrush(Color.FromArgb(255, 67, 153, 87)) };
            background = FixtureWindow(backdrop,
                new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), topmost: false);
            var anchor = new Border { Width = 48, Height = 40, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var ownerBounds = new RectInt32(work.Right - 280 * scale, work.Bottom - 54 * scale, 230 * scale, 44 * scale);
            var root = new Grid { Background = backdrop.Background };
            root.Children.Add(anchor);
            owner = FixtureWindow(root, ownerBounds);
            if (Environment.GetCommandLineArgs().Contains("--verify-ai-expanded-layout"))
            {
                // Click the fixture before sending Esc. A background-launched process
                // can show its window even when Activate cannot claim the foreground.
                var ownerPoint = new Native.Point { X = ownerBounds.X + ownerBounds.Width / 2, Y = ownerBounds.Y + ownerBounds.Height / 2 };
                await Task.Run(() => AiWindow.DesktopInputFixture.ClickAt(ownerPoint));
            }
            Action? verifyColors = customColors ? () =>
            {
                var settings = new SettingsPage(options, value => options.Save(value), null, owner,
                    new AITaskbar.SettingsPaneWidths(DataDirectory, _ => { }));
                settings.VerifyBackplateColorSettings(DataDirectory);
            } : null;
            await Task.Delay(250);
            var backgroundFrame = ScreenshotCapture.Desktop();
            panel = new AiWindow(options, DataDirectory, monitor, () => { }, verification: true, taskbarAnchor: anchor);
            options.Changed += panel.ApplyOptions;
            await panel.Open(monitor, work.Top);
            var result = await panel.VerifyTaskbarFlyout(backgroundFrame,
                new Native.Point { X = ownerBounds.X + ownerBounds.Width - 80 * scale, Y = ownerBounds.Y - 100 * scale }, DataDirectory, backdrop, verifyColors);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(result));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
        }
        finally
        {
            panel?.Destroy();
            owner?.Close();
            background?.Close();
            Exit();
        }
    }

    internal static Window FixtureWindow(UIElement content, RectInt32 bounds, bool topmost = true)
    {
        var window = new Window { Content = content };
        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = topmost;
        window.AppWindow.MoveAndResize(bounds);
        window.Activate();
        return window;
    }
}

internal sealed partial class SettingsPage
{
    internal void VerifyBackplateColorSettings(string directory)
    {
        static void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("Backplate color preview, save or cancel did not preserve the expected settings.");
        }

        Check(backplateColorMode.SelectedIndex == 0 && backplateColorFields.Visibility == Visibility.Collapsed);
        backplateColorMode.SelectedIndex = 1;
        SetBackplateColor(false, "#20486C");
        Check(App.Appearance.BackplateColorFor(false) == "#20486C" && App.Appearance.BackplateColorFor(true) == "");
        DiscardDraft();
        Check(backplateColorMode.SelectedIndex == 0 && !ReadAppearance().CustomBackplate && !IsDirty);
        backplateColorMode.SelectedIndex = 1;
        SetBackplateColor(true, "#FFE0B0");
        Check(App.Appearance.BackplateColorFor(false) == "" && App.Appearance.BackplateColorFor(true) == "#FFE0B0");
        SetBackplateColor(false, "#20486C");
        Save();
        var saved = new AiOptionsStore(Path.Combine(directory, "ai.json")).Data.Appearance;
        Check(saved is { CustomBackplate: true, LightBackplateColor: "#20486C", DarkBackplateColor: "#FFE0B0" } && !IsDirty);
        SetBackplateColor(false, "");
        Check(App.Appearance.BackplateColorFor(false) == "");
        DiscardDraft();
        Check(App.Appearance.BackplateColorFor(false) == "#20486C" && !IsDirty);
        backplateColorMode.SelectedIndex = 0;
        Check(App.Appearance.BackplateColorFor(false) == "" && ReadAppearance().LightBackplateColor == "#20486C");
        ResetPreview();
        Check(ReadAppearance() == saved && App.Appearance == saved);
    }
}

internal sealed partial class AiWindow
{
    internal async Task<object> VerifyTaskbarFlyout(ScreenshotFrame background, Native.Point sample, string output, Border backdrop, Action? verifyColors)
    {
        if (current?.View.CoreWebView2 is not { } core || !IsOpen || TaskbarFlyout is null)
            throw new InvalidOperationException("The taskbar flyout or WebView2 did not open.");
        var view = current.View;
        for (int i = 0; i < 50 && await core.ExecuteScriptAsync("!!document.getElementById('draft')") != "true"; i++)
            await Task.Delay(40);
        await core.ExecuteScriptAsync("document.getElementById('draft').value='保留这条草稿';document.body.insertAdjacentHTML('beforeend','<div style=height:3000px></div>');window.scrollTo(0,180);");
        var host = PanelContent!;
        await Task.Delay(250);
        var popup = FlyoutAt(sample);
        if (popup == IntPtr.Zero || AppWindow.IsVisible) throw new InvalidOperationException("An independent AI window was shown.");
        double compactWidth = host.ActualWidth;
        int totalCornerPixels = 0;
        foreach (bool expanded in new[] { false, true, false, true, false })
        {
            if (Detached != expanded) ToggleDetached();
            await Task.Delay(220);
            if (Detached != expanded || (expanded ? host.ActualWidth <= compactWidth + 100 : Math.Abs(host.ActualWidth - compactWidth) > 1))
                throw new InvalidOperationException("The same flyout did not resize between compact and expanded bounds.");
            if (!ReferenceEquals(current?.View, view) || !ReferenceEquals(view.CoreWebView2, core) || FlyoutAt(sample) != popup || AppWindow.IsVisible)
                throw new InvalidOperationException("Resizing changed the flyout, WebView2, or native window visibility.");
            if (JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.getElementById('draft').value")) != "保留这条草稿"
                || await core.ExecuteScriptAsync("window.scrollY") != "180")
                throw new InvalidOperationException("Resizing changed the draft or scroll position.");
            totalCornerPixels += await CheckFlyoutCorners(background, popup, Path.Combine(output, expanded ? "expanded" : "compact"));
        }
        verifyColors?.Invoke();
        bool customColors = verifyColors is not null;
        bool liveTransparency = Environment.GetCommandLineArgs().Contains("--verify-ai-toolbar-high-transparency");
        if (liveTransparency) App.PreviewAppearance(App.Appearance with { BackplateTransparency = 41, BackplateBlur = 47 });
        var toolbar = Environment.GetCommandLineArgs().Contains("--verify-ai-toolbar-contrast")
            ? await VerifyToolbarContrast(backdrop, popup, sample, core, output, liveTransparency, customColors) : null;
        if (liveTransparency) App.PreviewAppearance(null);
        bool verifyLayout = Environment.GetCommandLineArgs().Contains("--verify-ai-expanded-layout");
        var expandedLayout = verifyLayout ? await VerifyExpandedLayout(background, popup, core, output) : null;
        bool verifyPinLayer = Environment.GetCommandLineArgs().Contains("--verify-ai-pin-layer");
        if (verifyPinLayer) await VerifyExpandedPinLayer(popup, core);
        bool verifyOutsideClick = Environment.GetCommandLineArgs().Contains("--verify-outside-click");
        if (verifyOutsideClick)
        {
            await VerifyCompactOutsideClick(sample, popup);
            await Open(display, dockTop);
            await Task.Delay(250);
            popup = FlyoutAt(sample);
        }
        bool verifyFileDrop = Environment.GetCommandLineArgs().Contains("--verify-native-file-drop");
        if (verifyFileDrop)
        {
            await VerifyExpandedFileDrop(output, popup, "first");
            await CloseVerifiedFlyout();
            await Open(display, dockTop);
            await Task.Delay(250);
            var probe = TaskbarFlyout.CanMove ? Center(ExpandedFlyoutBounds()) : sample;
            await VerifyExpandedFileDrop(output, FlyoutAt(probe), "after-reopen");
        }
        await CloseVerifiedFlyout();
        return new { passed = true, sameFlyout = true, sameBrowser = true, draftPreserved = true, scrollPreserved = true, noIndependentWindow = true, cornerPixels = totalCornerPixels, toolbar, customColors, expandedLayout, expandedPinLayer = verifyPinLayer, compactOutsideClick = verifyOutsideClick, nativeFileDrop = verifyFileDrop, nativeFileDropAfterReopen = verifyFileDrop };
    }

    async Task VerifyExpandedPinLayer(nint popup, CoreWebView2 core)
    {
        if (!Detached) ToggleDetached();
        await Task.Delay(220);
        var owner = GetLayerOwner(popup, OwnerWindow);
        var ownerTopmost = GetLayerStyle(owner, ExtendedStyleIndex).ToInt64() & TopmostStyle;
        var bounds = ExpandedFlyoutBounds();
        var point = new Native.Point { X = bounds.X + 35, Y = bounds.Y + bounds.Height / 2 };
        var exposed = new Native.Point { X = bounds.X - 35, Y = point.Y };
        var host = PanelContent!;
        var pinCenter = pin.TransformToVisual(host).TransformPoint(new(pin.ActualWidth / 2, pin.ActualHeight / 2));
        var pinPoint = new Native.Point { X = bounds.X + (int)Math.Round(pinCenter.X * FlyoutScale), Y = bounds.Y + (int)Math.Round(pinCenter.Y * FlyoutScale) };
        var overlap = App.FixtureWindow(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Orange) },
            new(bounds.X - 60, point.Y - 40, 120, 80), topmost: false);
        try
        {
            var overlapHandle = WinRT.Interop.WindowNative.GetWindowHandle(overlap);
            foreach (bool pinned in new[] { true, false, true, false })
            {
                if (options.Data.Pinned != pinned)
                {
                    await Task.Run(() => DesktopInputFixture.ClickAt(pinPoint));
                    await Task.Delay(160);
                }
                if (options.Data.Pinned != pinned || FlyoutAt(exposed) != overlapHandle)
                    throw new InvalidOperationException("The pin click or the overlap fixture was unavailable.");
                await Task.Run(() => DesktopInputFixture.ClickAt(exposed));
                await Task.Delay(250);
                var hit = FlyoutAt(point);
                var topmost = (GetLayerStyle(popup, ExtendedStyleIndex).ToInt64() & TopmostStyle) != 0;
                if (topmost != pinned || hit != (pinned ? popup : overlapHandle))
                    throw new InvalidOperationException($"Expanded flyout layering: pinned={pinned}, topmost={topmost}, hit={hit:X}, popup={popup:X}, overlap={overlapHandle:X}.");
                if (!IsOpen || AppWindow.IsVisible || GetLayerOwner(popup, OwnerWindow) == 0
                    || !ReferenceEquals(core, current?.View.CoreWebView2)
                    || (GetLayerStyle(owner, ExtendedStyleIndex).ToInt64() & TopmostStyle) != ownerTopmost)
                    throw new InvalidOperationException("Changing the expanded pin affected the taskbar or the flyout lifecycle.");
            }
        }
        finally
        {
            overlap.Close();
            options.Save(options.Data with { Pinned = true, FloatingPanel = true });
        }
    }

    async Task<object> VerifyToolbarContrast(Border backdrop, IntPtr popup, Native.Point sample, CoreWebView2 core, string output,
        bool liveTransparency, bool customColors)
    {
        var white = Microsoft.UI.Colors.White;
        var black = Microsoft.UI.Colors.Black;
        var original = backdrop.Background;
        double minimum = double.PositiveInfinity;
        int states = 0;
        foreach (bool expanded in new[] { false, true })
        {
            if (Detached != expanded) ToggleDetached();
            await Task.Delay(220);
            GetBackplateWindowRect(popup, out var position);
            var work = display.Info.Work;
            double split = ((position.Left + position.Right) / 2d - work.Left) / (work.Right - work.Left);
            var mixed = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 0) };
            mixed.GradientStops.Add(new GradientStop { Color = black, Offset = split });
            mixed.GradientStops.Add(new GradientStop { Color = white, Offset = split });
            foreach (var (name, brush) in new (string, Brush)[]
                { ("white", new SolidColorBrush(white)), ("black", new SolidColorBrush(black)), ("mixed", mixed) })
            {
                backdrop.Background = brush;
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    SetTaskbarPalette(theme, theme == ElementTheme.Light
                        ? Color.FromArgb(255, 229, 241, 249) : Color.FromArgb(255, 22, 33, 51));
                    await Task.Delay(180);
                    if (customColors)
                    {
                        var configured = PanelTheme.PaletteColor(UiAppearance.ColorValue(
                            App.Appearance.BackplateColorFor(theme == ElementTheme.Dark), 0));
                        var rendered = backplateTint.Color;
                        if ((configured.R, configured.G, configured.B) != (rendered.R, rendered.G, rendered.B))
                            throw new InvalidOperationException("The taskbar theme did not select the exact saved backplate color.");
                    }
                    if (aiHeader.ActualTheme != theme || FlyoutAt(sample) != popup || AppWindow.IsVisible
                        || !ReferenceEquals(core, current?.View.CoreWebView2)
                        || await core.ExecuteScriptAsync("window.scrollY") != "180"
                        || JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.getElementById('draft').value")) != "保留这条草稿")
                        throw new InvalidOperationException("Changing the taskbar theme changed the flyout, draft, or scroll position.");
                    foreach (var button in ToolbarButtons())
                    {
                        bool toggle = button is ToggleButton;
                        var prefix = toggle ? "ToggleButton" : "Button";
                        foreach (var state in PanelTheme.ButtonStates.Where(state => toggle || !state.StartsWith("Checked")))
                        {
                            var fill = ((SolidColorBrush)button.Resources[prefix + "Background" + state]).Color;
                            var text = ((SolidColorBrush)button.Resources[prefix + "Foreground" + state]).Color;
                            if (liveTransparency && !customColors && theme == ElementTheme.Dark && state == "" && text != white)
                                throw new InvalidOperationException("Dark toolbar buttons lost their white foreground at high transparency.");
                            foreach (var desktop in new[] { black, white })
                            {
                                var color = PanelTheme.Composite(fill, PanelTheme.Composite(backplateTint.Color, desktop));
                                double contrast = PanelTheme.Contrast(text, color);
                                if (contrast < 4.5) throw new InvalidOperationException($"Toolbar contrast: {theme} {prefix}{state} {contrast:F2}:1.");
                                minimum = Math.Min(minimum, contrast);
                                states++;
                            }
                        }
                    }
                    foreach (var icon in ToolbarButtons().Where(button => button.Visibility == Visibility.Visible))
                    {
                        if (icon.Content is not Viewbox { Child: Canvas { Children: var children } }
                            || children[0] is not Microsoft.UI.Xaml.Shapes.Path path) continue;
                        for (var parent = VisualTreeHelper.GetParent(path); parent is not null && parent != icon;
                            parent = VisualTreeHelper.GetParent(parent))
                        {
                            if (parent is not ContentPresenter presenter) continue;
                            if (path.Stroke is not SolidColorBrush stroke || presenter.Foreground is not SolidColorBrush foreground
                                || stroke.Color != foreground.Color)
                                throw new InvalidOperationException($"Toolbar glyph did not use its rendered state foreground: {theme} {ToolTipService.GetToolTip(icon)}.");
                            break;
                        }
                    }
                    GetBackplateWindowRect(popup, out var bounds);
                    var frame = ScreenshotCapture.Desktop();
                    int headerHeight = (int)Math.Ceiling((aiHeader.ActualHeight + root.Padding.Top + 12) * aiHeader.XamlRoot.RasterizationScale);
                    await frame.Crop(bounds.Left - frame.Left, bounds.Top - frame.Top,
                        bounds.Right - bounds.Left, headerHeight).Save(Path.Combine(output, $"toolbar-{expanded}-{name}-{theme}"));
                }
            }
        }
        backdrop.Background = original;
        if (Detached) ToggleDetached();
        await Task.Delay(250);
        return new { states, minimumContrast = Math.Round(minimum, 2), captures = 12 };
    }

    async Task VerifyCompactOutsideClick(Native.Point inside, IntPtr popup)
    {
        var work = display.Info.Work;
        using var target = new DesktopInputFixture(work.Left + 20, work.Top + 200);
        target.Show();
        GetBackplateWindowRect(target.Handle, out var bounds);
        var outside = new Native.Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
        async Task Click(Native.Point point, IntPtr expectedWindow)
        {
            if (FlyoutAt(point) != expectedWindow) throw new InvalidOperationException("Outside-click fixture is not visible.");
            await Task.Run(() => DesktopInputFixture.ClickAt(point));
            await Task.Delay(250);
        }

        await Click(outside, target.Handle);
        if (!IsOpen || FlyoutAt(inside) != popup) throw new InvalidOperationException("An outside click hid the pinned compact flyout.");
        options.Save(options.Data with { Pinned = false });
        await Click(inside, popup);
        if (!IsOpen) throw new InvalidOperationException("An inside click closed the compact flyout.");
        await Click(outside, target.Handle);
        if (IsOpen || TaskbarFlyout!.IsOpen) throw new InvalidOperationException("An outside click did not close the unpinned compact flyout.");
    }

    async Task CloseVerifiedFlyout()
    {
        for (int attempt = 0; attempt < 20 && (DragActive || ModalActive); attempt++) await Task.Delay(40);
        Hide();
        await Task.Delay(220);
        if (IsOpen || TaskbarFlyout!.IsOpen || AppWindow.IsVisible) throw new InvalidOperationException("The taskbar flyout did not close.");
    }

    async Task VerifyExpandedFileDrop(string directory, IntPtr popup, string suffix)
    {
        options.Save(options.Data with { FloatingPanel = false, Pinned = false });
        await Task.Delay(250);
        var core = current!.View.CoreWebView2;
        string fileName = $"flyout-drop-{suffix}.txt";
        string path = Path.Combine(directory, fileName);
        await File.WriteAllTextAsync(path, "AIEnter local file drop verification");
        await core.ExecuteScriptAsync("window.submitted=0; document.addEventListener('submit',()=>window.submitted++);");
        int dragEnters = 0;
        PanelContent!.AddHandler(UIElement.DragEnterEvent, new Microsoft.UI.Xaml.DragEventHandler((_, _) => dragEnters++), true);
        GetBackplateWindowRect(popup, out var bounds);
        var work = display.Info.Work;
        using var source = new DesktopInputFixture(work.Left + 20, work.Top + 200, path);
        source.Show();
        GetBackplateWindowRect(source.Handle, out var sourceBounds);
        var from = new Native.Point { X = (sourceBounds.Left + sourceBounds.Right) / 2, Y = (sourceBounds.Top + sourceBounds.Bottom) / 2 };
        var to = new Native.Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
        if (FlyoutAt(from) != source.Handle || FlyoutAt(to) != popup) throw new InvalidOperationException("Drag fixture is not fully visible.");
        await Task.Run(() => DesktopInputFixture.Drag(from, to));
        for (int i = 0; i < 30 && !Status.Contains("文件已加入网页附件"); i++) await Task.Delay(100);
        if (!IsOpen || !Detached || source.Effect != System.Windows.Forms.DragDropEffects.Copy
            || JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.getElementById('attachments').files[0]?.name")) != fileName
            || await core.ExecuteScriptAsync("window.submitted") != "0")
            throw new InvalidOperationException($"Expanded flyout file drop failed. Open={IsOpen}, test popup={popup:X}, location={flyoutDropLocation}, register={flyoutFileDropResult:X8}, started={source.Started}, enter={dragEnters}, effect={source.Effect}, status={Status}");
    }

    internal sealed class DesktopInputFixture : System.Windows.Forms.Form
    {
        internal System.Windows.Forms.DragDropEffects Effect { get; private set; }
        internal bool Started { get; private set; }
        protected override bool ShowWithoutActivation => true;

        internal DesktopInputFixture(int x, int y, string? file = null)
        {
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            Bounds = new System.Drawing.Rectangle(x, y, 180, 100);
            if (file is null) return;
            MouseDown += (_, _) =>
            {
                Started = true;
                var data = new System.Windows.Forms.DataObject(System.Windows.Forms.DataFormats.FileDrop, new[] { file });
                Effect = DoDragDrop(data, System.Windows.Forms.DragDropEffects.Copy);
            };
        }

        internal static void Drag(Native.Point from, Native.Point to, bool cancel = false)
        {
            var previous = System.Windows.Forms.Cursor.Position;
            SetDragCursor(from.X, from.Y);
            MouseInput(0x0002, 0, 0, 0, UIntPtr.Zero);
            try
            {
                Thread.Sleep(180);
                for (int step = 1; step <= 10; step++)
                {
                    SetDragCursor(from.X + (to.X - from.X) * step / 10, from.Y + (to.Y - from.Y) * step / 10);
                    Thread.Sleep(25);
                }
                Thread.Sleep(160);
                if (cancel)
                {
                    KeyInput(0x1B, 0, 0, UIntPtr.Zero);
                    KeyInput(0x1B, 0, 0x0002, UIntPtr.Zero);
                    Thread.Sleep(110);
                }
            }
            finally
            {
                MouseInput(0x0004, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(150);
                SetDragCursor(previous.X, previous.Y);
            }
        }

        internal static void ClickAt(Native.Point point)
        {
            var previous = System.Windows.Forms.Cursor.Position;
            SetDragCursor(point.X, point.Y);
            try
            {
                MouseInput(0x0002, 0, 0, 0, UIntPtr.Zero);
                MouseInput(0x0004, 0, 0, 0, UIntPtr.Zero);
            }
            finally { SetDragCursor(previous.X, previous.Y); }
        }

        [DllImport("user32.dll", EntryPoint = "SetCursorPos")] static extern bool SetDragCursor(int x, int y);
        [DllImport("user32.dll", EntryPoint = "mouse_event")] static extern void MouseInput(uint flags, uint x, uint y, uint data, UIntPtr extra);
        [DllImport("user32.dll", EntryPoint = "keybd_event")] static extern void KeyInput(byte key, byte scan, uint flags, UIntPtr extra);
    }

    async Task<int> CheckFlyoutCorners(ScreenshotFrame background, IntPtr popup, string output)
    {
        GetBackplateWindowRect(popup, out var bounds);
        var host = (Border)PanelContent!;
        double scale = host.XamlRoot.RasterizationScale;
        int width = (int)Math.Round(host.ActualWidth * scale);
        int height = (int)Math.Round(host.ActualHeight * scale);
        var frame = ScreenshotCapture.Desktop();
        await frame.Crop(bounds.Left - frame.Left, bounds.Top - frame.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top).Save(output);
        var visible = LocateVisibleFlyout(background, frame, bounds, width, height);
        int left = visible.X, top = visible.Y, right = visible.X + width, bottom = visible.Y + height;
        int checkedPixels = 0, changedPixels = 0;
        const int margin = 12;
        foreach (bool lower in new[] { false, true })
        foreach (bool trailing in new[] { false, true })
        {
            int radius = (int)Math.Round((lower ? host.CornerRadius.BottomLeft : host.CornerRadius.TopLeft) * scale);
            int x = trailing ? right - radius : left - margin;
            int y = lower ? bottom - radius : top - margin;
            for (int row = 0; row < radius + margin; row++)
            for (int column = 0; column < radius + margin; column++)
            {
                int u = trailing ? radius - column - 1 : column - margin;
                int v = lower ? radius - row - 1 : row - margin;
                if (u >= -3 && v >= -3 && Math.Sqrt(Math.Pow(radius - u, 2) + Math.Pow(radius - v, 2)) <= radius + 4) continue;
                int px = x + column - frame.Left, py = y + row - frame.Top;
                if (px < 0 || px >= frame.Width || py < 0 || py >= frame.Height) continue;
                checkedPixels++;
                if (FlyoutPixelDiff(frame, background, x + column, y + row) > 3) changedPixels++;
            }
            string corner = (lower ? "bottom" : "top") + (trailing ? "-right" : "-left");
            await frame.Crop(x - frame.Left, y - frame.Top, radius + margin, radius + margin).Save(Path.Combine(output, corner));
        }
        if (checkedPixels < 400 || changedPixels > 0)
            throw new InvalidOperationException($"Unexpected pixels beyond the flyout corners: {changedPixels}/{checkedPixels}; bounds {bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}; content {host.ActualWidth}×{host.ActualHeight} at {scale}.");
        return checkedPixels;
    }

    static AiBounds LocateVisibleFlyout(ScreenshotFrame background, ScreenshotFrame frame, Native.Rect bounds, int width, int height)
    {
        // The popup HWND includes invisible presenter margins. Use the opaque
        // page edges to locate the arranged panel; the glass can blend into the
        // desktop and a light window can overlap the left edge.
        int centerY = (bounds.Top + bounds.Bottom) / 2;
        bool IsPanel(int x, int y) => FlyoutPixelDiff(frame, background, x, y) > 80;
        int right = Enumerable.Range(bounds.Left, bounds.Right - bounds.Left).Last(x => IsPanel(x, centerY)) + 1;
        int bottom = Enumerable.Range(bounds.Top, bounds.Bottom - bounds.Top).Last(y => IsPanel(right - width / 4, y)) + 1;
        int left = right - width, top = bottom - height;
        if (left < bounds.Left || top < bounds.Top)
            throw new InvalidOperationException($"Visible bounds {left},{top}–{right},{bottom}; arranged size {width}×{height}; native bounds {bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}.");
        return new(left, top, width, height);
    }

    static int FlyoutPixelDiff(ScreenshotFrame frame, ScreenshotFrame background, int x, int y)
    {
        int px = x - frame.Left, py = y - frame.Top;
        if (px < 0 || px >= frame.Width || py < 0 || py >= frame.Height) return 0;
        int index = (py * frame.Width + px) * 4;
        return Math.Max(Math.Abs(frame.Pixels[index] - background.Pixels[index]),
            Math.Max(Math.Abs(frame.Pixels[index + 1] - background.Pixels[index + 1]),
                Math.Abs(frame.Pixels[index + 2] - background.Pixels[index + 2])));
    }

    static IntPtr FlyoutAt(Native.Point sample) => Native.GetAncestor(PopupWindowAtPoint(sample), 2);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] static extern IntPtr PopupWindowAtPoint(Native.Point point);
}

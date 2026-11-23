using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

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
            var options = new AiOptionsStore(Path.Combine(DataDirectory, "flyout-ai.json"));
            options.Save(options.Data with { Pinned = true, FloatingPanel = true, DockWidth = 380, DockHeight = 420 });
            background = FixtureWindow(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 67, 153, 87)) },
                new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));
            var anchor = new Border { Width = 48, Height = 40, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var ownerBounds = new RectInt32(work.Right - 280 * scale, work.Bottom - 54 * scale, 230 * scale, 44 * scale);
            var root = new Grid { Background = background.Content is Border backdrop ? backdrop.Background : null };
            root.Children.Add(anchor);
            owner = FixtureWindow(root, ownerBounds);
            await Task.Delay(250);
            var backgroundFrame = ScreenshotCapture.Desktop();
            panel = new AiWindow(options, DataDirectory, monitor, () => { }, verification: true, taskbarAnchor: anchor);
            options.Changed += panel.ApplyOptions;
            await panel.Open(monitor, work.Top);
            var result = await panel.VerifyTaskbarFlyout(backgroundFrame,
                new Native.Point { X = ownerBounds.X + ownerBounds.Width - 80 * scale, Y = ownerBounds.Y - 100 * scale }, DataDirectory);
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

    static Window FixtureWindow(UIElement content, RectInt32 bounds)
    {
        var window = new Window { Content = content };
        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.MoveAndResize(bounds);
        window.Activate();
        return window;
    }
}

internal sealed partial class AiWindow
{
    internal async Task<object> VerifyTaskbarFlyout(ScreenshotFrame background, Native.Point sample, string output)
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
        Hide();
        await Task.Delay(220);
        if (IsOpen || TaskbarFlyout.IsOpen || AppWindow.IsVisible) throw new InvalidOperationException("The taskbar flyout did not close.");
        return new { passed = true, sameFlyout = true, sameBrowser = true, draftPreserved = true, scrollPreserved = true, noIndependentWindow = true, cornerPixels = totalCornerPixels };
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
        // The popup HWND includes invisible presenter margins. Locate the solid edges along the center lines.
        int centerX = (bounds.Left + bounds.Right) / 2, centerY = (bounds.Top + bounds.Bottom) / 2;
        bool IsPanel(int x, int y) => PixelDiff(x, y) > 80;
        int left = Enumerable.Range(bounds.Left, bounds.Right - bounds.Left).First(x => IsPanel(x, centerY));
        int top = Enumerable.Range(bounds.Top, bounds.Bottom - bounds.Top).First(y => IsPanel(centerX, y));
        int right = left + width, bottom = top + height;
        int visibleRight = Enumerable.Range(bounds.Left, bounds.Right - bounds.Left).Last(x => IsPanel(x, centerY)) + 1;
        int visibleBottom = Enumerable.Range(bounds.Top, bounds.Bottom - bounds.Top).Last(y => IsPanel(centerX, y)) + 1;
        if (Math.Abs(visibleRight - right) > 4 || Math.Abs(visibleBottom - bottom) > 4)
            throw new InvalidOperationException($"Visible bounds {left},{top}–{visibleRight},{visibleBottom}; arranged size {width}×{height}; native bounds {bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}.");
        int PixelDiff(int x, int y)
        {
            int px = x - frame.Left, py = y - frame.Top;
            if (px < 0 || px >= frame.Width || py < 0 || py >= frame.Height) return 0;
            int index = (py * frame.Width + px) * 4;
            return Math.Max(Math.Abs(frame.Pixels[index] - background.Pixels[index]),
                Math.Max(Math.Abs(frame.Pixels[index + 1] - background.Pixels[index + 1]),
                    Math.Abs(frame.Pixels[index + 2] - background.Pixels[index + 2])));
        }
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
                if (PixelDiff(x + column, y + row) > 3) changedPixels++;
            }
            string corner = (lower ? "bottom" : "top") + (trailing ? "-right" : "-left");
            await frame.Crop(x - frame.Left, y - frame.Top, radius + margin, radius + margin).Save(Path.Combine(output, corner));
        }
        if (checkedPixels < 400 || changedPixels > 0)
            throw new InvalidOperationException($"Unexpected pixels beyond the flyout corners: {changedPixels}/{checkedPixels}; bounds {bounds.Left},{bounds.Top}–{bounds.Right},{bounds.Bottom}; content {host.ActualWidth}×{host.ActualHeight} at {scale}.");
        return checkedPixels;
    }

    static IntPtr FlyoutAt(Native.Point sample) => Native.GetAncestor(PopupWindowAtPoint(sample), 2);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] static extern IntPtr PopupWindowAtPoint(Native.Point point);
}

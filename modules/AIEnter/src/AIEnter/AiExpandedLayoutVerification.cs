using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AIEnter;

internal sealed partial class AiWindow
{
    static Native.Point Center(AiBounds bounds) => new() { X = bounds.X + bounds.Width / 2, Y = bounds.Y + bounds.Height / 2 };

    async Task<object> VerifyExpandedLayout(ScreenshotFrame background, IntPtr popup, CoreWebView2 core, string output)
    {
        var host = PanelContent!;
        double initialCompactWidth = host.ActualWidth, initialCompactHeight = host.ActualHeight;
        if (!Detached) ToggleDetached();
        await Task.Delay(240);
        double scale = host.XamlRoot.RasterizationScale;
        int Pixels(int dip) => (int)Math.Round(dip * scale);

        AiBounds Bounds()
        {
            GetBackplateWindowRect(popup, out var window);
            return LocateVisibleFlyout(background, ScreenshotCapture.Desktop(), window,
                (int)Math.Round(host.ActualWidth * scale), (int)Math.Round(host.ActualHeight * scale));
        }
        async Task Drag(Native.Point from, int dx, int dy, bool cancel = false)
        {
            if (FlyoutAt(from) != popup) throw new InvalidOperationException($"The layout grip at {from.X},{from.Y} is outside the flyout.");
            await Task.Run(() => DesktopInputFixture.Drag(from, new Native.Point { X = from.X + dx, Y = from.Y + dy }, cancel));
            await Task.Delay(175);
        }
        async Task CheckSession(AiBounds bounds)
        {
            const long ToolWindow = 0x80, AppWindowStyle = 0x40000;
            long style = VerificationWindowStyle(popup, -20).ToInt64();
            if (!IsOpen || AppWindow.IsVisible || FlyoutAt(Center(bounds)) != popup || (style & AppWindowStyle) != 0
                || ((style & ToolWindow) == 0 && VerificationWindowOwner(popup, 4) == IntPtr.Zero))
                throw new InvalidOperationException($"Resizing or moving created a different or switchable window. Style={style:X}.");
            if (!ReferenceEquals(core, current?.View.CoreWebView2) || await core.ExecuteScriptAsync("window.scrollY") != "180"
                || JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.getElementById('draft').value")) != "保留这条草稿")
                throw new InvalidOperationException("Resizing or moving lost the draft or scroll position.");
        }

        var resized = Bounds();
        var edges = new[]
        {
            AiPanelEdge.Right, AiPanelEdge.Bottom, AiPanelEdge.Left, AiPanelEdge.Top,
            AiPanelEdge.TopLeft, AiPanelEdge.TopRight, AiPanelEdge.BottomLeft, AiPanelEdge.BottomRight
        };
        foreach (var edge in edges)
        {
            bool left = edge.HasFlag(AiPanelEdge.Left), right = edge.HasFlag(AiPanelEdge.Right);
            bool top = edge.HasFlag(AiPanelEdge.Top), bottom = edge.HasFlag(AiPanelEdge.Bottom);
            bool corner = (left || right) && (top || bottom);
            int insetX = Pixels(corner ? 8 : 4), insetY = Pixels(corner ? CornerSpan - 9 : 4);
            var point = new Native.Point
            {
                X = left ? resized.X + insetX : right ? resized.X + resized.Width - insetX : Center(resized).X,
                Y = top ? resized.Y + insetY : bottom ? resized.Y + resized.Height - insetY : Center(resized).Y
            };
            int step = Pixels(26);
            int dx = left ? step : right ? -step : 0;
            int dy = top ? step : bottom ? -step : 0;
            var expected = new AiBounds(resized.X + (left ? step : 0), resized.Y + (top ? step : 0),
                resized.Width - Math.Abs(dx), resized.Height - Math.Abs(dy));
            await Drag(point, dx, dy);
            var actual = Bounds();
            if (Math.Abs(actual.X - expected.X) > 4 || Math.Abs(actual.Y - expected.Y) > 4
                || Math.Abs(actual.Width - expected.Width) > 4 || Math.Abs(actual.Height - expected.Height) > 4
                || options.Data.ExpandedLayout?.Display != display.Device)
                throw new InvalidOperationException($"The {edge} grip did not preserve the opposite side or corner. Before={resized}; expected={expected}; actual={actual}.");
            resized = actual;
        }

        var saved = options.Data.ExpandedLayout;
        await Drag(new() { X = resized.X + Pixels(4), Y = resized.Y + resized.Height / 2 }, -Pixels(35), 0, cancel: true);
        if (Bounds() != resized || options.Data.ExpandedLayout != saved)
            throw new InvalidOperationException("Canceling resize did not restore the saved geometry.");
        await CheckSession(resized);
        int cornerPixels = await CheckFlyoutCorners(background, popup, Path.Combine(output, "resized"));

        bool movable = TaskbarFlyout!.CanMove;
        var final = resized;
        if (movable)
        {
            var actions = headerActions.TransformToVisual(host).TransformPoint(new());
            var navigationEnd = aiNavigation.TransformToVisual(host).TransformPoint(new(aiNavigation.ActualWidth, 0));
            var headerCenter = aiHeader.TransformToVisual(host).TransformPoint(new(0, aiHeader.ActualHeight / 2));
            Native.Point HeaderBlank(AiBounds bounds) => new()
            {
                X = bounds.X + (int)Math.Round((actions.X + navigationEnd.X) * scale / 2),
                Y = bounds.Y + (int)Math.Round(headerCenter.Y * scale)
            };

            await Drag(HeaderBlank(resized), -Pixels(94), -Pixels(76));
            final = Bounds();
            if (resized.X - final.X < Pixels(70) || resized.Y - final.Y < Pixels(56)
                || final.Width != resized.Width || final.Height != resized.Height || options.Data.ExpandedLayout?.Display != display.Device)
                throw new InvalidOperationException($"The empty header did not move and save the flyout. Before={resized}; after={final}.");
            var savedMove = options.Data.ExpandedLayout;
            await Drag(HeaderBlank(final), Pixels(55), Pixels(42), cancel: true);
            if (Bounds() != final || options.Data.ExpandedLayout != savedMove)
                throw new InvalidOperationException("Canceling a header drag did not restore the saved position.");

            var pinCenter = pin.TransformToVisual(host).TransformPoint(new(pin.ActualWidth / 2, pin.ActualHeight / 2));
            var pinPoint = new Native.Point { X = final.X + (int)Math.Round(pinCenter.X * scale), Y = final.Y + (int)Math.Round(pinCenter.Y * scale) };
            bool wasPinned = options.Data.Pinned;
            await Task.Run(() => DesktopInputFixture.ClickAt(pinPoint));
            await Task.Delay(110);
            if (options.Data.Pinned == wasPinned || Bounds() != final)
                throw new InvalidOperationException("A toolbar button click was consumed by header dragging.");
            await Task.Run(() => DesktopInputFixture.ClickAt(pinPoint));
            await Task.Delay(110);
            if (options.Data.Pinned != wasPinned)
                throw new InvalidOperationException("The toolbar pin did not restore its initial state.");
            await CheckSession(final);
            cornerPixels += await CheckFlyoutCorners(background, popup, Path.Combine(output, "moved"));
        }
        saved = options.Data.ExpandedLayout;
        ToggleDetached();
        await Task.Delay(175);
        if (Detached || Math.Abs(host.ActualWidth - initialCompactWidth) > 1 || Math.Abs(host.ActualHeight - initialCompactHeight) > 1)
            throw new InvalidOperationException("Expanded geometry affected the compact panel.");
        ToggleDetached();
        await Task.Delay(200);
        if (!Detached || Bounds() != final || options.Data.ExpandedLayout != saved)
            throw new InvalidOperationException("Expanding again did not restore the saved geometry.");
        await CheckSession(final);
        ToggleDetached();
        await Task.Delay(175);
        return new { resize = true, resizeDirections = edges.Select(edge => edge.ToString()), canceledResize = true,
            movedFromHeader = movable, canceledHeaderMove = movable, headerButtonsClickable = movable, savedGeometry = true, cornerPixels };
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr VerificationWindowStyle(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    static extern IntPtr VerificationWindowOwner(IntPtr window, uint command);
}

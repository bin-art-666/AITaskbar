using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AIEnter;

internal sealed class ScreenshotSelectionHost : UserControl
{
    internal ScreenshotSelectionHost() => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Cross);
}

internal sealed class ScreenshotSelection : Window
{
    readonly ScreenshotFrame frame;
    readonly Canvas canvas = new();
    readonly Grid surface = new();
    readonly Border selection = new() { BorderThickness = new Thickness(2), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Cyan), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(1,255,255,255)) };
    readonly Border[] shades = Enumerable.Range(0, 4).Select(_ => new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(100,0,0,0)), IsHitTestVisible = false }).ToArray();
    readonly TextBlock hint = new() { Text = "拖动框选截图 · Esc 取消", Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), FontSize = 18, IsHitTestVisible = false };
    readonly TaskCompletionSource<ScreenshotFrame?> completion = new();
    Point? start;
    Rect rectangle;
    bool finished;
    ScreenshotSelection(ScreenshotFrame frame)
    {
        this.frame = frame; Title = "AIEnter · 框选截图";
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        AppWindow.IsShownInSwitchers = false;
        surface.Children.Add(new Image { Source = null, Stretch = Stretch.Fill });
        surface.Children.Add(canvas);
        foreach (var shade in shades) canvas.Children.Add(shade);
        canvas.Children.Add(selection); canvas.Children.Add(hint);
        selection.Visibility = Visibility.Collapsed;
        var host = new ScreenshotSelectionHost { Content = surface, IsTabStop = true };
        Content = host;
        canvas.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        surface.SizeChanged += (_, _) => Draw();
        host.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; Finish(null); } };
        canvas.PointerPressed += (_, e) =>
        {
            var point = e.GetCurrentPoint(canvas);
            if (point.Properties.IsRightButtonPressed) { Finish(null); return; }
            if (!point.Properties.IsLeftButtonPressed || finished) return;
            start = Clamp(point.Position); rectangle = new Rect(start.Value, start.Value);
            canvas.CapturePointer(e.Pointer); Draw(); e.Handled = true;
        };
        canvas.PointerMoved += (_, e) => { if (start is { } origin) { rectangle = new Rect(origin, Clamp(e.GetCurrentPoint(canvas).Position)); Draw(); } };
        canvas.PointerReleased += (_, e) =>
        {
            if (start == null || finished) return;
            rectangle = new Rect(start.Value, Clamp(e.GetCurrentPoint(canvas).Position));
            start = null; canvas.ReleasePointerCaptures();
            if (rectangle.Width < 3 || rectangle.Height < 3) { selection.Visibility = Visibility.Collapsed; Draw(); return; }
            double sx = frame.Width / surface.ActualWidth, sy = frame.Height / surface.ActualHeight;
            int x = (int)Math.Floor(rectangle.X * sx), y = (int)Math.Floor(rectangle.Y * sy);
            Finish(frame.Crop(x, y, (int)Math.Ceiling(rectangle.Width * sx), (int)Math.Ceiling(rectangle.Height * sy)));
        };
        canvas.PointerCaptureLost += (_, _) => { if (!finished) start = null; };
        AppWindow.Closing += (_, e) => { if (!finished) { e.Cancel = true; Finish(null); } };
        Closed += (_, _) => completion.TrySetResult(null);
        host.Loaded += (_, _) => host.Focus(FocusState.Programmatic);
    }
    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, surface.ActualWidth), Math.Clamp(p.Y, 0, surface.ActualHeight));
    void Draw()
    {
        double w = surface.ActualWidth, h = surface.ActualHeight;
        var r = start != null || rectangle.Width > 0 ? rectangle : new Rect(0,0,0,0);
        void Place(FrameworkElement item, double x, double y, double width, double height)
        { Canvas.SetLeft(item,x); Canvas.SetTop(item,y); item.Width=Math.Max(0,width); item.Height=Math.Max(0,height); }
        Place(shades[0],0,0,w,r.Top); Place(shades[1],0,r.Bottom,w,h-r.Bottom);
        Place(shades[2],0,r.Top,r.Left,r.Height); Place(shades[3],r.Right,r.Top,w-r.Right,r.Height);
        Place(selection,r.X,r.Y,r.Width,r.Height); selection.Visibility = r.Width > 0 ? Visibility.Visible : Visibility.Collapsed;
        hint.Text = r.Width > 0 ? $"{(int)(r.Width * frame.Width / Math.Max(1,w))} × {(int)(r.Height * frame.Height / Math.Max(1,h))} · 松开保存 · Esc 取消" : "拖动框选截图 · Esc 取消";
        Canvas.SetLeft(hint, Math.Clamp(r.Width > 0 ? r.X : w/2-130, 8, Math.Max(8,w-360)));
        Canvas.SetTop(hint, Math.Clamp(r.Width > 0 ? r.Y-32 : h/2, 8, Math.Max(8,h-40)));
    }
    void Finish(ScreenshotFrame? result)
    {
        if (finished) return; finished = true; completion.TrySetResult(result); Close();
    }
    internal void Cancel() => Finish(null);
    internal static async Task<ScreenshotFrame?> Select(ScreenshotFrame frame, Action<ScreenshotSelection?> track)
    {
        var image = await frame.Image();
        var window = new ScreenshotSelection(frame); track(window);
        ((Image)window.surface.Children[0]).Source = image;
        window.Activate();
        Native.RemoveWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(window));
        window.AppWindow.MoveAndResize(new(frame.Left,frame.Top,frame.Width,frame.Height));
        try { return await window.completion.Task; }
        finally { track(null); }
    }
}

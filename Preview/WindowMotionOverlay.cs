using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Microsoft.UI.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using CreateParams = System.Windows.Forms.CreateParams;
using NativeWindow = System.Windows.Forms.NativeWindow;

namespace AITaskbar;

internal sealed class WindowMotionOverlay : NativeWindow, IDisposable
{
    private readonly DesktopChildSiteBridge bridge;
    private readonly ContentIsland island;
    private readonly SpriteVisual visual;
    private readonly CompositionSurfaceBrush brush;
    private readonly CompositionGraphicsDevice graphics;
    private readonly CompositionDrawingSurface frame;
    private readonly CanvasBitmap image;
    private readonly WindowMotionMesh? mesh;
    private readonly Rect window, icon, source, content;
    private readonly nint insertAfter;
    private readonly double? taskbarTop;
    private int left, top, width, height;
    private bool visible;

    internal WindowMotionOverlay(FrameworkElement owner, WindowMotionNative.Snapshot snapshot, Rect window, Rect icon,
        WindowAnimationStyle style, nint insertAfter = -1)
    {
        if (!owner.IsLoaded) throw new InvalidOperationException("The taskbar is no longer open.");
        this.insertAfter = insertAfter;
        left = (int)Math.Floor(Math.Min(window.Left, icon.Left)) - 1;
        int bendMargin = style == WindowAnimationStyle.Curved ? WindowMotionMesh.BendMargin(window) : 1;
        top = (int)Math.Floor(Math.Min(window.Top, icon.Top)) - bendMargin;
        width = (int)Math.Ceiling(Math.Max(window.Right, icon.Right)) - left + 1;
        height = (int)Math.Ceiling(Math.Max(window.Bottom, icon.Bottom)) - top + bendMargin;
        if ((long)width * height > 12_000_000) throw new ArgumentOutOfRangeException(nameof(window));
        this.window = new Rect(window.X - left, window.Y - top, window.Width, window.Height);
        this.icon = new Rect(icon.X - left, icon.Y - top, icon.Width, icon.Height);
        if (style == WindowAnimationStyle.WindowsStyle && owner.XamlRoot?.Content is FrameworkElement taskbar &&
            WindowMotionNative.ScreenBounds(insertAfter, taskbar) is { } taskbarBounds &&
            taskbarBounds.Top > window.Top && taskbarBounds.Top <= icon.Top)
            taskbarTop = taskbarBounds.Top - top;
        source = new Rect(0, 0, snapshot.Width, snapshot.Height);
        content = snapshot.Content;

        var compositor = ElementCompositionPreview.GetElementVisual(owner).Compositor;
        var device = CanvasDevice.GetSharedDevice();
        image = CanvasBitmap.CreateFromBytes(device, snapshot.Pixels, snapshot.Width, snapshot.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
        try
        {
            if (style == WindowAnimationStyle.Curved)
                mesh = new WindowMotionMesh(device, image, this.window, this.icon, content);
            graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, device);
            frame = graphics.CreateDrawingSurface(new Size(width, height), Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            brush = compositor.CreateSurfaceBrush(frame);
            brush.Stretch = CompositionStretch.None;
            visual = compositor.CreateSpriteVisual();
            visual.Size = new Vector2(width, height);
            visual.Brush = brush;
            visual.IsHitTestVisible = false;
            island = ContentIsland.Create(visual);
            island.IsHitTestVisibleWhenTransparent = false;
            const int popup = unchecked((int)0x80000000), disabled = 0x08000000;
            const int transparent = 0x20, toolWindow = 0x80, noRedirection = 0x00200000, noActivate = 0x08000000;
            CreateHandle(new CreateParams
            {
                Caption = "AITaskbar 窗口过渡",
                X = left, Y = top, Width = width, Height = height, Style = popup | disabled,
                ExStyle = transparent | toolWindow | noRedirection | noActivate
            });
            bridge = DesktopChildSiteBridge.Create(compositor, Microsoft.UI.Win32Interop.GetWindowIdFromWindow(Handle));
            bridge.OverrideScale = 1;
            bridge.Connect(island);
            bridge.MoveAndResize(new RectInt32(0, 0, width, height));
            bridge.Show();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void DrawFlatFrame() => Draw(1, opening: true);

    internal void DrawTransition(double progress, bool opening) => Draw(opening ? progress : 1 - progress, opening);

    private void Draw(double extent, bool opening)
    {
        var bounds = mesh is null ? RectangularBounds(extent) : window;
        double opacity = WindowMotionMesh.SmoothStep(0, .12, extent);
        if (taskbarTop is { } edge)
        {
            // Fade the part still visible above the bar before it becomes a narrow horizontal strip.
            double visibleHeight = Math.Min(bounds.Height, edge - bounds.Top);
            opacity *= WindowMotionMesh.SmoothStep(icon.Height * .08, icon.Height * .28, visibleHeight);
        }
        using (var drawing = CanvasComposition.CreateDrawingSession(frame))
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            if (opacity > 0 && extent == 1)
                DrawFlat(drawing, window);
            else if (opacity > 0)
            {
                if (mesh is not null) mesh.Draw(drawing, extent, opening);
                else DrawFlat(drawing, bounds);
            }
        }
        visual.Opacity = (float)opacity;
        if (visible) return;

        const uint NoActivateShow = 0x0010 | 0x0040;
        if (!SetWindowPos(Handle, insertAfter, left, top, width, height, NoActivateShow))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        visible = true;
    }

    private Rect RectangularBounds(double extent)
    {
        double progress = WindowMotionMesh.SmoothStep(0, 1, extent);
        return new Rect(Mix(icon.X, window.X, progress), Mix(icon.Y, window.Y, progress),
            Mix(icon.Width, window.Width, progress), Mix(icon.Height, window.Height, progress));
    }

    private static double Mix(double from, double to, double progress) => from + (to - from) * progress;

    internal void AlignFinalFrame(Rect bounds)
    {
        var target = new Rect(bounds.X - left, bounds.Y - top, bounds.Width, bounds.Height);
        if (target == window) return;
        if (target.Left < 0 || target.Top < 0 || target.Right > width || target.Bottom > height)
        {
            left = (int)Math.Floor(bounds.Left);
            top = (int)Math.Floor(bounds.Top);
            width = (int)Math.Ceiling(bounds.Right) - left;
            height = (int)Math.Ceiling(bounds.Bottom) - top;
            if ((long)width * height > 12_000_000) throw new ArgumentOutOfRangeException(nameof(bounds));
            if (!SetWindowPos(Handle, 0, left, top, width, height, 0x0010 | 0x0004))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            bridge.MoveAndResize(new RectInt32(0, 0, width, height));
            CanvasComposition.Resize(frame, new Size(width, height));
            visual.Size = new Vector2(width, height);
            target = new Rect(bounds.X - left, bounds.Y - top, bounds.Width, bounds.Height);
        }
        using var drawing = CanvasComposition.CreateDrawingSession(frame);
        drawing.Clear(Microsoft.UI.Colors.Transparent);
        DrawFlat(drawing, target);
    }

    internal async Task FadeOutAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var compositor = visual.Compositor;
        using var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = TimeSpan.FromMilliseconds(50);
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(1, 0, compositor.CreateLinearEasingFunction());
        using var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.Completed += (_, _) => completed.TrySetResult();
        visual.StartAnimation(nameof(visual.Opacity), fade);
        batch.End();
        try { await completed.Task.WaitAsync(TimeSpan.FromMilliseconds(180), token); }
        catch (TimeoutException) { }
    }

    private void DrawFlat(CanvasDrawingSession drawing, Rect bounds) =>
        drawing.DrawImage(image, new Rect(bounds.X + content.X * bounds.Width, bounds.Y + content.Y * bounds.Height,
            content.Width * bounds.Width, content.Height * bounds.Height), source, 1, CanvasImageInterpolation.Linear);

    public void Dispose()
    {
        if (Handle != 0) ShowWindow(Handle, 0);
        bridge?.Dispose();
        if (Handle != 0) DestroyHandle();
        island?.Dispose();
        visual?.Dispose();
        brush?.Dispose();
        frame?.Dispose();
        graphics?.Dispose();
        mesh?.Dispose();
        image.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
}

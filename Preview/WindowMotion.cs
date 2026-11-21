using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace AITaskbar;

internal sealed class WindowMotion(FrameworkElement owner)
{
    private readonly Dictionary<(nint Handle, uint Process), WindowMotionNative.Snapshot> recent = [];
    private int capturing;

    internal async Task<bool> MinimizeAsync(RunningWindow window, Rect icon, int duration, CancellationToken token)
    {
        var image = await CaptureAsync(window, token);
        if (image is null) return RunningWindows.Minimize(window);
        try
        {
            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon);
            overlay.DrawFlatFrame();
            await PresentWindowAsync(token);
            using var transitions = new WindowMotionNative.NativeTransitionScope(window);
            using var restoreTransitions = token.Register(transitions.Dispose);
            bool minimized = transitions.Suppressed
                ? RunningWindows.Minimize(window)
                : await TransitionFromHiddenAsync(window, minimize: true, token);
            if (!minimized) return false;
            Remember(window, image);
            if (!await WaitForWindowStateAsync(window, minimized: true, token)) return false;
            await AnimateAsync(overlay, opening: false, duration, token);
            return true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            Debug.WriteLine(error);
            return RunningWindows.IsMinimized(window) || RunningWindows.Minimize(window);
        }
    }

    internal async Task<bool> RestoreAsync(RunningWindow window, Rect icon, int duration, CancellationToken token)
    {
        var key = (window.Handle, window.ProcessId);
        recent.Remove(key, out var saved);
        var image = saved ?? await CaptureAsync(window, token);
        if (image is null) return RunningWindows.Activate(window);
        try
        {
            using var transitions = new WindowMotionNative.NativeTransitionScope(window);
            using var restoreTransitions = token.Register(transitions.Dispose);

            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon);
            await AnimateAsync(overlay, opening: true, duration, token);
            WindowMotionNative.DwmFlush();
            await Task.Delay(16, token);
            bool restored = transitions.Suppressed
                ? RunningWindows.RequestRestore(window)
                : await TransitionFromHiddenAsync(window, minimize: false, token);
            if (!restored || !await WaitForWindowStateAsync(window, minimized: false, token))
                return RunningWindows.Activate(window);

            bool activated = RunningWindows.Activate(window);
            await HandOffAsync(overlay, window, token);
            return activated;
        }
        catch (Exception error) when (Recoverable(error))
        {
            Debug.WriteLine(error);
            return RunningWindows.Activate(window);
        }
    }

    internal async Task ExpandLaunchedAsync(RunningWindow window, Rect icon, int duration, CancellationToken token)
    {
        if (RunningWindows.IsMinimized(window)) return;
        var image = await CaptureAsync(window, token);
        if (image is null && RunningWindows.Foreground() == window.Handle)
        {
            await Task.Delay(32, token);
            image = await CaptureAsync(window, token);
        }
        if (image is null || RunningWindows.Foreground() != window.Handle) return;
        try
        {
            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon);
            overlay.DrawTransition(0, opening: true);
            using var cloak = new WindowMotionNative.WindowCover(window);
            if (!cloak.Applied) return;
            using var cancellation = token.Register(cloak.Dispose);
            await AnimateAsync(overlay, opening: true, duration, token);
            cloak.Dispose();
            await HandOffAsync(overlay, window, token);
        }
        catch (Exception error) when (Recoverable(error)) { Debug.WriteLine(error); }
    }

    internal static void Pulse(FrameworkElement plate)
    {
        if (!plate.IsLoaded) return;
        var visual = ElementCompositionPreview.GetElementVisual(plate);
        visual.CenterPoint = new Vector3((float)(plate.ActualWidth / 2), (float)(plate.ActualHeight / 2), 0);
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(180);
        animation.InsertKeyFrame(0, Vector3.One);
        animation.InsertKeyFrame(.35f, new Vector3(.92f, .92f, 1));
        animation.InsertKeyFrame(1, Vector3.One);
        visual.StartAnimation("Scale", animation);
    }

    private async Task<WindowMotionNative.Snapshot?> CaptureAsync(RunningWindow window, CancellationToken token)
    {
        if (Interlocked.Exchange(ref capturing, 1) != 0) return null;
        var capture = Task.Run(() =>
        {
            try { return WindowMotionNative.Capture(window); }
            catch (Exception error) when (Recoverable(error)) { Debug.WriteLine(error); return null; }
            finally { Volatile.Write(ref capturing, 0); }
        });
        try { return await capture.WaitAsync(TimeSpan.FromMilliseconds(100), token); }
        catch (TimeoutException) { return null; }
    }

    private void Remember(RunningWindow window, WindowMotionNative.Snapshot snapshot)
    {
        const int byteBudget = 48 * 1024 * 1024;
        if (snapshot.Pixels.Length > byteBudget) return;
        var key = (window.Handle, window.ProcessId);
        recent.Remove(key);
        foreach (var old in recent.Keys.ToArray())
        {
            if (recent.Count < 4 && recent.Values.Sum(value => value.Pixels.Length) + snapshot.Pixels.Length <= byteBudget) break;
            recent.Remove(old);
        }
        recent.Add(key, snapshot);
    }

    internal void Clear() => recent.Clear();

    private static async Task AnimateAsync(WindowMotionOverlay overlay, bool opening, int duration, CancellationToken token)
    {
        var start = Stopwatch.GetTimestamp();
        double progress;
        do
        {
            token.ThrowIfCancellationRequested();
            progress = Math.Min(1, Stopwatch.GetElapsedTime(start).TotalMilliseconds / duration);
            overlay.DrawTransition(progress, opening);
            if (progress < 1) await Task.Delay(12, token);
        } while (progress < 1);
    }

    private static async Task<bool> TransitionFromHiddenAsync(RunningWindow window, bool minimize, CancellationToken token)
    {
        if (!RunningWindows.HideForTransition(window, wasMinimized: !minimize))
            return minimize ? RunningWindows.Minimize(window) : RunningWindows.RequestRestore(window);
        bool changeQueued = false;
        try
        {
            // Start the system operation from a hidden window with its original region intact.
            for (int frame = 0; frame < 6 && RunningWindows.IsCurrent(window); frame++)
                await Task.Delay(12, token);
            token.ThrowIfCancellationRequested();
            changeQueued = RunningWindows.ShowAfterHide(window, minimized: minimize);
            return changeQueued;
        }
        finally
        {
            if (!changeQueued) RunningWindows.ReshowAfterInterruptedTransition(window);
        }
    }

    private static async Task<bool> WaitForWindowStateAsync(RunningWindow window, bool minimized, CancellationToken token)
    {
        for (int frame = 0; frame < 10; frame++)
        {
            if (!RunningWindows.OwnsHandle(window)) return false;
            if (RunningWindows.IsCurrent(window) && RunningWindows.IsMinimized(window) == minimized) return true;
            await Task.Delay(16, token);
        }
        return RunningWindows.IsCurrent(window) && RunningWindows.IsMinimized(window) == minimized;
    }

    private static async Task PresentWindowAsync(CancellationToken token)
    {
        WindowMotionNative.DwmFlush();
        await Task.Delay(32, token);
        WindowMotionNative.DwmFlush();
    }

    private static async Task HandOffAsync(WindowMotionOverlay overlay, RunningWindow window, CancellationToken token)
    {
        await PresentWindowAsync(token);
        if (!RunningWindows.IsCurrent(window) || RunningWindows.IsMinimized(window)) return;
        if (WindowMotionNative.WindowBounds(window) is { } bounds)
            overlay.AlignFinalFrame(bounds);
        await overlay.FadeOutAsync(token);
    }

    private static bool Recoverable(Exception error) => error is ArgumentException or
        System.Runtime.InteropServices.ExternalException or InvalidOperationException or NotSupportedException;

}

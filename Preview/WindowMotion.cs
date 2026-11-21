using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;

namespace AITaskbar;

internal sealed class WindowMotion(FrameworkElement owner, nint taskbarHandle)
{
    private readonly Dictionary<(nint Handle, uint Process), WindowMotionNative.Snapshot> recent = [];
    private RectangularRequest? rectangularRequest;
    private int capturing;

    internal bool HasRectangularRequest => rectangularRequest is { } request && !request.Token.IsCancellationRequested;

    private sealed class RectangularRequest(RunningWindow window, bool opening, CancellationToken token)
    {
        internal RunningWindow Window { get; } = window;
        internal CancellationToken Token { get; } = token;
        internal bool Opening { get; set; } = opening;
    }

    internal bool RetargetRectangular(RunningWindow window, bool? opening)
    {
        if (rectangularRequest is not { } request || request.Token.IsCancellationRequested ||
            request.Window.Handle != window.Handle || request.Window.ProcessId != window.ProcessId) return false;
        request.Opening = opening ?? !request.Opening;
        return true;
    }

    internal async Task<bool> RunRectangularAsync(RunningWindow window, Rect icon, bool opening, int duration,
        CancellationToken token)
    {
        var request = new RectangularRequest(window, opening, token);
        rectangularRequest = request;
        try
        {
            // A click during the real-window handoff is handled as soon as that handoff finishes.
            do
            {
                if (!await PlayRectangularAsync(request, icon, duration) || !RunningWindows.IsCurrent(window))
                    return false;
            } while (request.Opening == RunningWindows.IsMinimized(window));
            return true;
        }
        finally
        {
            if (ReferenceEquals(rectangularRequest, request)) rectangularRequest = null;
        }
    }

    private async Task<bool> PlayRectangularAsync(RectangularRequest request, Rect icon, int duration)
    {
        var window = request.Window;
        var token = request.Token;
        token.ThrowIfCancellationRequested();
        bool startsMinimized = RunningWindows.IsMinimized(window);
        if (request.Opening != startsMinimized) return startsMinimized || RunningWindows.Activate(window);

        var key = (window.Handle, window.ProcessId);
        var image = startsMinimized && recent.TryGetValue(key, out var saved) ? saved : await CaptureAsync(window, token);
        token.ThrowIfCancellationRequested();
        if (!RunningWindows.IsCurrent(window)) return false;
        if (request.Opening != startsMinimized) return startsMinimized || RunningWindows.Activate(window);
        if (image is null) return await FinishRectangularNativelyAsync(window, request.Opening, token);

        try
        {
            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon, WindowAnimationStyle.WindowsStyle,
                insertAfter: taskbarHandle);
            using var transitions = new WindowMotionNative.NativeTransitionScope(window);
            using var restoreTransitions = token.Register(transitions.Dispose);
            if (!startsMinimized)
            {
                overlay.DrawFlatFrame();
                await PresentWindowAsync(token);
                if (request.Opening) return RunningWindows.Activate(window);
                if (!await ChangeWindowStateAsync(window, minimized: true, transitions.Suppressed, token))
                    return await FinishRectangularNativelyAsync(window, request.Opening, token);
                Remember(window, image);
            }

            double extent = startsMinimized ? 0 : 1;
            while (await AnimateRectangularAsync(overlay, request, extent, duration))
            {
                WindowMotionNative.DwmFlush();
                await Task.Delay(16, token);
                if (request.Opening)
                {
                    if (!await ChangeWindowStateAsync(window, minimized: false, transitions.Suppressed, token))
                        return await FinishRectangularNativelyAsync(window, request.Opening, token);
                    bool activated = RunningWindows.Activate(window);
                    recent.Remove(key);
                    await HandOffAsync(overlay, window, token);
                    return activated;
                }
                extent = 1;
            }
            return true;
        }
        catch (Exception error) when (Recoverable(error))
        {
            Debug.WriteLine(error);
            return await FinishRectangularNativelyAsync(window, request.Opening, token);
        }
    }

    private static async Task<bool> AnimateRectangularAsync(WindowMotionOverlay overlay, RectangularRequest request,
        double extent, int duration)
    {
        double from = extent;
        bool opening = request.Opening;
        bool reversing = false;
        long start = Stopwatch.GetTimestamp();
        while (true)
        {
            request.Token.ThrowIfCancellationRequested();
            if (opening != request.Opening)
            {
                from = extent;
                opening = request.Opening;
                reversing = true;
                start = Stopwatch.GetTimestamp();
            }
            double target = opening ? 1 : 0;
            double legDuration = Math.Abs(target - from) * duration;
            double progress = legDuration == 0 ? 1 : Math.Min(1, Stopwatch.GetElapsedTime(start).TotalMilliseconds / legDuration);
            double interpolation = reversing ? WindowMotionMesh.SmoothStep(0, 1, progress) : progress;
            extent = from + (target - from) * interpolation;
            overlay.DrawTransition(extent, opening: true);
            if (progress == 1) return opening;
            await Task.Delay(12, request.Token);
        }
    }

    private static async Task<bool> ChangeWindowStateAsync(RunningWindow window, bool minimized, bool transitionsSuppressed,
        CancellationToken token)
    {
        bool queued = transitionsSuppressed
            ? minimized ? RunningWindows.Minimize(window) : RunningWindows.RequestRestore(window)
            : await TransitionFromHiddenAsync(window, minimized, token);
        return queued && await WaitForWindowStateAsync(window, minimized, token);
    }

    private static async Task<bool> FinishRectangularNativelyAsync(RunningWindow window, bool opening, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!RunningWindows.IsCurrent(window)) return false;
        if (RunningWindows.IsMinimized(window) != opening) return !opening || RunningWindows.Activate(window);
        // A window that can't supply a frame should still change state without flying to the system taskbar.
        using var transitions = new WindowMotionNative.NativeTransitionScope(window);
        using var restoreTransitions = token.Register(transitions.Dispose);
        return await ChangeWindowStateAsync(window, minimized: !opening, transitions.Suppressed, token) &&
            (!opening || RunningWindows.Activate(window));
    }

    internal async Task<bool> MinimizeAsync(RunningWindow window, Rect icon, WindowAnimationStyle style, int duration,
        CancellationToken token)
    {
        var image = await CaptureAsync(window, token);
        if (image is null) return RunningWindows.Minimize(window);
        try
        {
            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon, style);
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

    internal async Task<bool> RestoreAsync(RunningWindow window, Rect icon, WindowAnimationStyle style, int duration,
        CancellationToken token)
    {
        var key = (window.Handle, window.ProcessId);
        recent.Remove(key, out var saved);
        var image = saved ?? await CaptureAsync(window, token);
        if (image is null) return RunningWindows.Activate(window);
        try
        {
            using var transitions = new WindowMotionNative.NativeTransitionScope(window);
            using var restoreTransitions = token.Register(transitions.Dispose);

            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon, style);
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

    internal async Task ExpandLaunchedAsync(RunningWindow window, Rect icon, WindowAnimationStyle style, int duration,
        CancellationToken token)
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
            using var overlay = new WindowMotionOverlay(owner, image, image.Bounds, icon, style);
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

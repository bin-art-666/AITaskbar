using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace AIEnter;

internal static class PanelMotion
{
    sealed record Motion(float FromOpacity, float FromY, float ToOpacity, float ToY, bool Entering, int Milliseconds, long Started)
    {
        internal EventHandler<object>? Rendering;
        internal (float Opacity, float Y)? LastFrame;
        internal bool Complete => Stopwatch.GetElapsedTime(Started).TotalMilliseconds >= Milliseconds;

        internal (float Opacity, float Y) Sample()
        {
            float t = (float)Math.Clamp(Stopwatch.GetElapsedTime(Started).TotalMilliseconds / Milliseconds, 0, 1);
            float progress = Entering ? 1 - MathF.Pow(1 - t, 3) : t * t * t;
            return (FromOpacity + (ToOpacity - FromOpacity) * progress, FromY + (ToY - FromY) * progress);
        }
    }
    static readonly ConditionalWeakTable<FrameworkElement, Motion> motions = new();
    internal static bool Enabled => PanelTheme.Current.Motion != UiMotion.Off && new UISettings().AnimationsEnabled
        && !new AccessibilitySettings().HighContrast;
    internal static void Stop(FrameworkElement content)
    {
        if (motions.TryGetValue(content, out var motion)) StopRendering(motion);
        motions.Remove(content);
    }

    static void StopRendering(Motion motion)
    {
        if (motion.Rendering is null) return;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= motion.Rendering;
        motion.Rendering = null;
    }

    internal static void Reset(FrameworkElement content, Action<float, float>? backdrop = null)
    {
        Stop(content);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(content, true);
            var visual = ElementCompositionPreview.GetElementVisual(content);
            visual.StopAnimation("Opacity"); visual.Opacity = 1;
            visual.StopAnimation("Translation"); visual.Properties.InsertVector3("Translation", Vector3.Zero);
            backdrop?.Invoke(1, 0);
        }
        catch (COMException) { /* Disconnected content must never block dismissal. */ }
    }
    internal static void Enter(FrameworkElement content, bool floatFade = false, bool resume = false, Action<float, float>? backdrop = null)
    {
        if (!Enabled) { Reset(content, backdrop); return; }
        try
        {
            int duration = floatFade ? UiDefaults.PanelEnterMilliseconds : (int)PanelTheme.Value<double>("EnterMilliseconds");
            float offset = floatFade ? UiDefaults.PanelMotionOffset : (float)PanelTheme.Value<double>("EnterOffset");
            if (!resume) Reset(content, backdrop);
            Animate(content, true, duration, offset, resume, !floatFade, backdrop);
        }
        catch (COMException) { Reset(content, backdrop); }
    }
    internal static int Exit(FrameworkElement content, Action<float, float>? backdrop = null)
    {
        if (!Enabled) return 0;
        try { Animate(content, false, UiDefaults.PanelExitMilliseconds, UiDefaults.PanelMotionOffset, true, backdrop: backdrop); return UiDefaults.PanelExitMilliseconds; }
        catch (COMException) { Reset(content, backdrop); return 0; }
    }
    static void Animate(FrameworkElement content, bool entering, int milliseconds, float offset, bool resume, bool legacy = false,
        Action<float, float>? backdrop = null)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        var from = resume && motions.TryGetValue(content, out var previous) ? previous.LastFrame ?? previous.Sample()
            : entering ? (0f, offset) : (1f, 0f);
        var motion = new Motion(from.Item1, from.Item2, entering ? 1 : 0, entering ? 0 : offset, entering, milliseconds, Stopwatch.GetTimestamp());
        Stop(content); motions.Add(content, motion);
        var visual = ElementCompositionPreview.GetElementVisual(content);
        if (backdrop is not null)
        {
            void SetFrame(float opacity, float y)
            {
                visual.Opacity = opacity;
                visual.Properties.InsertVector3("Translation", new Vector3(0, y, 0));
                backdrop(opacity, y);
                motion.LastFrame = (opacity, y);
            }
            SetFrame(motion.FromOpacity, motion.FromY);
            motion.Rendering = (_, _) =>
            {
                try
                {
                    var frame = motion.Sample();
                    SetFrame(frame.Opacity, frame.Y);
                    if (motion.Complete) StopRendering(motion);
                }
                catch (COMException) { Reset(content, backdrop); }
            };
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += motion.Rendering;
            return;
        }
        var compositor = visual.Compositor;
        // Linear Bezier X with cubic-out/in Y, matching Sample when a transition is interrupted.
        using var easing = legacy ? compositor.CreateCubicBezierEasingFunction(new(.2f, .8f), new(.2f, 1))
            : compositor.CreateCubicBezierEasingFunction(new(1f / 3, entering ? 1 : 0), new(2f / 3, entering ? 1 : 0));
        using var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, motion.FromOpacity); opacity.InsertKeyFrame(1, motion.ToOpacity, easing);
        opacity.Duration = TimeSpan.FromMilliseconds(milliseconds);
        using var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(0, motion.FromY, 0));
        translation.InsertKeyFrame(1, new Vector3(0, motion.ToY, 0), easing); translation.Duration = opacity.Duration;
        visual.StartAnimation("Opacity", opacity); visual.StartAnimation("Translation", translation);
    }
}

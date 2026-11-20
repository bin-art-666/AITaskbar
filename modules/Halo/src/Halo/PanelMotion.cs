using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace Halo;

internal static class PanelMotion
{
    sealed record Motion(float FromOpacity, float FromY, float ToOpacity, float ToY, bool Entering, int Milliseconds, long Started)
    {
        internal (float Opacity, float Y) Sample()
        {
            float t = (float)Math.Clamp(Stopwatch.GetElapsedTime(Started).TotalMilliseconds / Milliseconds, 0, 1);
            float progress = Entering ? 1 - MathF.Pow(1 - t, 3) : t * t * t;
            return (FromOpacity + (ToOpacity - FromOpacity) * progress, FromY + (ToY - FromY) * progress);
        }
    }
    static readonly ConditionalWeakTable<FrameworkElement, Motion> motions = new();
    internal static bool Enabled => PanelTheme.Current.Motion != UiMotion.Off && new UISettings().AnimationsEnabled;
    internal static void Reset(FrameworkElement content)
    {
        motions.Remove(content);
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(content, true);
            var visual = ElementCompositionPreview.GetElementVisual(content);
            visual.StopAnimation("Opacity"); visual.Opacity = 1;
            visual.StopAnimation("Translation"); visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch (COMException) { /* Disconnected content must never block dismissal. */ }
    }
    internal static void Enter(FrameworkElement content, bool floatFade = false, bool resume = false)
    {
        if (!Enabled) { Reset(content); return; }
        try
        {
            int duration = floatFade ? UiDefaults.PanelEnterMilliseconds : (int)PanelTheme.Value<double>("EnterMilliseconds");
            float offset = floatFade ? UiDefaults.PanelMotionOffset : (float)PanelTheme.Value<double>("EnterOffset");
            if (!resume) Reset(content);
            Animate(content, true, duration, offset, resume, !floatFade);
        }
        catch (COMException) { Reset(content); }
    }
    internal static int Exit(FrameworkElement content)
    {
        if (!Enabled) return 0;
        try { Animate(content, false, UiDefaults.PanelExitMilliseconds, UiDefaults.PanelMotionOffset, true); return UiDefaults.PanelExitMilliseconds; }
        catch (COMException) { Reset(content); return 0; }
    }
    static void Animate(FrameworkElement content, bool entering, int milliseconds, float offset, bool resume, bool legacy = false)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(content, true);
        var from = resume && motions.TryGetValue(content, out var previous) ? previous.Sample() : entering ? (0f, offset) : (1f, 0f);
        var motion = new Motion(from.Item1, from.Item2, entering ? 1 : 0, entering ? 0 : offset, entering, milliseconds, Stopwatch.GetTimestamp());
        motions.Remove(content); motions.Add(content, motion);
        var visual = ElementCompositionPreview.GetElementVisual(content);
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

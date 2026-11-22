using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class TaskbarPanelFlyout : IDisposable
{
    internal const double Radius = 23;
    internal const float Travel = 8;

    private readonly FrameworkElement anchor, content;
    private readonly Border backplate;
    private readonly Flyout flyout;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly CompositeTransform motion = new();
    private TaskbarGlassBackdrop? desktopBackdrop;
    private long transitionStart;
    private float progress = 1, transitionFrom, transitionTo;
    private bool animating, finishClose, disposed;

    internal bool IsOpen { get; private set; }
    internal bool IsClosing { get; private set; }
    internal bool CanAnimate => system.AnimationsEnabled && !accessibility.HighContrast;
    internal event Action? Opening, Opened, Closed;

    internal TaskbarPanelFlyout(FrameworkElement anchor, FrameworkElement content, Border backplate)
    {
        this.anchor = anchor;
        this.content = content;
        this.backplate = backplate;
        content.RenderTransform = motion;
        content.SizeChanged += (_, _) => SetFrame(progress);
        var popup = new Border { Child = content, Padding = new Thickness(0, 0, 0, Travel) };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(0)));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        presenter.Setters.Add(new Setter(FlyoutPresenter.IsDefaultShadowEnabledProperty, false));
        flyout = new() { Content = popup, Placement = FlyoutPlacementMode.Top, ShouldConstrainToRootBounds = false,
            AreOpenCloseAnimationsEnabled = false, FlyoutPresenterStyle = presenter,
            OverlayInputPassThroughElement = anchor };
        flyout.Opening += (_, _) =>
        {
            IsOpen = true;
            SetFrame(CanAnimate ? 0 : 1);
            Opening?.Invoke();
        };
        flyout.Opened += (_, _) =>
        {
            Animate(show: true);
            Opened?.Invoke();
        };
        flyout.Closing += (_, args) =>
        {
            if (finishClose || disposed || !CanAnimate) return;
            args.Cancel = true;
            if (!IsClosing) Animate(show: false);
        };
        flyout.Closed += (_, _) =>
        {
            StopAnimation();
            SetFrame(0);
            IsOpen = IsClosing = finishClose = false;
            Closed?.Invoke();
        };
    }

    internal void Show() => flyout.ShowAt(anchor);
    internal void Hide() { if (IsOpen) flyout.Hide(); }

    internal void CancelClosing()
    {
        if (!IsClosing) return;
        Animate(show: true);
    }

    private void Animate(bool show)
    {
        IsClosing = !show;
        transitionFrom = progress;
        transitionTo = show ? 1 : 0;
        if (!CanAnimate || transitionFrom == transitionTo) { CompleteAnimation(); return; }
        transitionStart = Stopwatch.GetTimestamp();
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += RenderFrame;
    }

    private void RenderFrame(object? sender, object args)
    {
        double time = Stopwatch.GetElapsedTime(transitionStart).TotalMilliseconds / (IsClosing ? 140 : 180);
        if (time >= 1) { CompleteAnimation(); return; }
        float eased = (float)(1 - Math.Pow(1 - time, 3));
        SetFrame(transitionFrom + (transitionTo - transitionFrom) * eased);
    }

    private void SetFrame(float amount)
    {
        progress = amount;
        content.Opacity = amount;
        motion.TranslateY = (1 - amount) * Travel;
        desktopBackdrop?.SetFrame(new Vector2((float)content.ActualWidth, (float)content.ActualHeight), amount, (float)motion.TranslateY);
    }

    private void CompleteAnimation()
    {
        StopAnimation();
        SetFrame(transitionTo);
        if (!IsClosing) return;
        finishClose = true;
        flyout.Hide();
    }

    private void StopAnimation()
    {
        if (!animating) return;
        CompositionTarget.Rendering -= RenderFrame;
        animating = false;
    }

    internal static void AddAnimation(Storyboard storyboard, DependencyObject target, string property,
        double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation { From = from, To = to, EnableDependentAnimation = true,
            Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    internal (Color Text, Color Raised) ApplyPalette(QuickControlAppearance style, bool light, LumaBar.BarAppearance? taskbar)
    {
        var palette = ResolvePalette(style, light, taskbar, system, accessibility.HighContrast);
        content.RequestedTheme = palette.Theme;
        SetBackdrop(palette.Blur > 0);
        desktopBackdrop?.SetBlur(palette.Blur);
        backplate.Background = new SolidColorBrush(palette.Background);
        return (palette.Text, palette.Raised);
    }

    internal sealed record Palette(ElementTheme Theme, Color Background, Color Solid, Color Text, Color Raised,
        bool Glass, float Blur);

    internal static Palette ResolvePalette(QuickControlAppearance style, bool light, LumaBar.BarAppearance? taskbar,
        UISettings system, bool highContrast)
    {
        bool followsBar = style.Backdrop == QuickBackdrop.FollowTaskbar;
        bool isLight = style.Backdrop switch { QuickBackdrop.Light => true, QuickBackdrop.Dark => false,
            QuickBackdrop.Custom => IsLight(FromRgb(style.Tint)), _ => light };
        var neutral = isLight ? Color.FromArgb(255, 243, 246, 250) : Color.FromArgb(255, 30, 34, 42);
        string material = followsBar ? taskbar?.Material ?? "normal" : "blur";
        Color tint = style.Backdrop == QuickBackdrop.Custom ? FromRgb(style.Tint)
            : followsBar && material != "normal" && taskbar is { } bar ? PreviewBarAppearance.FromAbgr(bar.Abgr) : neutral;
        double opacity = style.Backdrop == QuickBackdrop.Custom ? 1 - style.Transparency / 100d
            : followsBar ? tint.A / 255d : .86;
        var solid = Blend(neutral, tint, opacity);
        var text = highContrast ? system.GetColorValue(UIColorType.Foreground)
            : isLight ? Color.FromArgb(255, 24, 39, 57) : Color.FromArgb(255, 242, 247, 255);
        var theme = highContrast ? ElementTheme.Default : isLight ? ElementTheme.Light : ElementTheme.Dark;
        bool glass = !highContrast && system.AdvancedEffectsEnabled && material is not ("normal" or "opaque");
        int blur = material switch { "acrylic" => 30, "blur" => followsBar ? taskbar?.Blur ?? 0 : 34, _ => 0 };
        var background = highContrast ? system.GetColorValue(UIColorType.Background)
            : !glass ? solid : Alpha(tint, (byte)Math.Round(opacity * 255));
        var raised = Blend(solid, text, .045);
        return new(theme, background, solid, text,
            highContrast ? system.GetColorValue(UIColorType.Background) : glass ? Alpha(raised, 72) : raised,
            glass, glass ? blur / 3f : 0f);
    }

    private void SetBackdrop(bool enabled)
    {
        if (enabled == (desktopBackdrop is not null)) return;
        desktopBackdrop = enabled ? new TaskbarGlassBackdrop() : null;
        SetFrame(progress);
        flyout.SystemBackdrop = desktopBackdrop;
    }

    internal static bool IsLight(Color color) => (color.R * 299 + color.G * 587 + color.B * 114) >= 155000;
    internal static Color Alpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
    internal static Color FromRgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    private static Color Blend(Color first, Color second, double fraction) => Color.FromArgb(255,
        (byte)Math.Round(first.R + (second.R - first.R) * fraction), (byte)Math.Round(first.G + (second.G - first.G) * fraction),
        (byte)Math.Round(first.B + (second.B - first.B) * fraction));

    public void Dispose()
    {
        disposed = true;
        Hide();
        StopAnimation();
        flyout.SystemBackdrop = null;
    }
}

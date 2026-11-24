using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class PreviewBarAppearance : IDisposable
{
    private const int TintStopCount = 5;
    private const double TintSweepFraction = .45;
    private readonly Window window;
    private readonly DispatcherQueue dispatcher;
    private readonly AppearanceHost host;
    private readonly Border surface;
    private readonly Canvas lightLayer = new() { IsHitTestVisible = false };
    private readonly SolidColorBrush tintBrush = new(Microsoft.UI.Colors.Transparent);
    private readonly LinearGradientBrush tintGradient = new() { StartPoint = new(0, 0), EndPoint = new(0, 1) };
    private readonly Border topLine = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed };
    private readonly Image cursorLight = new() { Visibility = Visibility.Collapsed };
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Action<bool, LumaBar.BarAppearance?> updateIconTone;
    private Halo.GlassBackdrop? backdrop;
    private Halo.GlassBackdrop? desktopBackdrop;
    private string? backdropKind;
    private LumaBar.BarAppearance? last;
    private bool? lastContrast, lastEffects, lastLight, lastAnimations;
    private double lastScale;
    private (int Diameter, bool Light, int Strength) sprite;
    private Color? targetTint;
    private Storyboard? tintTransition;
    private bool detachBackdropAfterTint;
    private bool disposed;
    public bool? IsLight => lastLight;

    public PreviewBarAppearance(Window window, Border surface, FrameworkElement content, AppearanceHost host,
        Action<bool, LumaBar.BarAppearance?> updateIconTone)
    {
        this.window = window;
        dispatcher = window.DispatcherQueue;
        this.surface = surface;
        this.host = host;
        this.updateIconTone = updateIconTone;
        lightLayer.Children.Add(cursorLight);
        var layers = new Grid();
        layers.Children.Add(lightLayer);
        layers.Children.Add(content);
        layers.Children.Add(topLine);
        for (int i = 0; i < TintStopCount; i++)
            tintGradient.GradientStops.Add(new GradientStop { Offset = i / (TintStopCount - 1d) });
        surface.Background = tintBrush;
        surface.Child = layers;
        surface.SizeChanged += (_, _) => lightLayer.Clip = new RectangleGeometry { Rect = new(0, 0, surface.ActualWidth, surface.ActualHeight) };
        surface.ActualThemeChanged += (_, _) => Refresh();
        surface.Loaded += (_, _) => Refresh();
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(PointerMoved), true);
        surface.PointerExited += (_, _) => cursorLight.Visibility = Visibility.Collapsed;
        host.BarAppearanceChanged += OnAppearanceChanged;
    }

    private void OnAppearanceChanged() => dispatcher.TryEnqueue(Refresh);

    public void Refresh()
    {
        if (disposed) return;
        var appearance = host.CurrentBarAppearance;
        bool contrast = accessibility.HighContrast;
        bool effects = system.AdvancedEffectsEnabled;
        bool animations = !contrast && system.AnimationsEnabled;
        bool light = contrast || appearance is null
            ? surface.ActualTheme != ElementTheme.Dark : appearance.Tone == LumaBar.Tone.Light;
        double scale = surface.XamlRoot?.RasterizationScale ?? 1;
        if (last == appearance && lastContrast == contrast && lastEffects == effects && lastLight == light &&
            lastScale == scale && lastAnimations == animations) return;
        bool animateTint = animations && lastContrast == false && lastEffects == effects && backdropKind != "acrylic";
        last = appearance;
        lastContrast = contrast;
        lastEffects = effects;
        lastLight = light;
        lastScale = scale;
        lastAnimations = animations;
        surface.RequestedTheme = contrast || appearance is null
            ? ElementTheme.Default : light ? ElementTheme.Light : ElementTheme.Dark;
        updateIconTone(light, appearance);

        var normal = light ? Color.FromArgb(255, 243, 246, 250) : Color.FromArgb(255, 30, 34, 42);
        string kind = appearance?.Material ?? "normal";
        var tint = appearance is null ? normal : FromAbgr(appearance.Abgr);
        var displayTint = kind == "normal" ? normal : kind == "opaque" || !effects ? SolidTint(tint, normal) : tint;
        topLine.Visibility = appearance?.ShowLine == true ? Visibility.Visible : Visibility.Collapsed;
        if (topLine.Visibility == Visibility.Visible)
        {
            topLine.Height = light && !contrast ? 1 : 1 / scale;
            topLine.Background = new SolidColorBrush(contrast ? system.GetColorValue(UIColorType.Foreground) :
                light ? Color.FromArgb(90, 93, 110, 138) : DarkLine(displayTint, normal, kind is "normal" or "opaque" || !effects));
        }
        Color background = displayTint;
        int tintDuration = animateTint ? appearance?.TransitionMilliseconds ?? 0 : 0;
        detachBackdropAfterTint = false;
        if (contrast || kind is "normal" or "opaque" || !effects)
        {
            // Keep the glass behind the gradient until the new tint covers it completely.
            detachBackdropAfterTint = tintDuration > 0 && backdropKind == "desktop";
            if (!detachBackdropAfterTint) SetBackdrop(null);
            if (contrast) background = system.GetColorValue(UIColorType.Background);
        }
        else if (kind == "acrylic" && DesktopAcrylicController.IsSupported())
        {
            SetBackdrop("acrylic");
            backdrop!.Update(new Halo.MaterialOptions(Thin: true, TintOpacity: tint.A / 255f,
                LuminosityOpacity: .65f, FollowTheme: false, TintHex: $"#{tint.R:X2}{tint.G:X2}{tint.B:X2}"));
            // The acrylic controller already supplies this material's tint.
            background = Microsoft.UI.Colors.Transparent;
            tintDuration = 0;
        }
        else
        {
            desktopBackdrop ??= new Halo.GlassBackdrop(new Halo.MaterialOptions(TintOpacity: 0, LuminosityOpacity: 0, NoBase: true))
            {
                TransparentCanvas = true, BlurScale = 1f / 3,
                MaximumBlurStrength = LumaBar.TaskbarAppearance.MaximumBlurRadius,
                RetainTransparentResourcesOnDisconnect = true
            };
            // At full transparency and zero blur, let DWM composite the live windows directly.
            desktopBackdrop.UpdateBlur(kind == "blur" ? appearance!.Blur : kind == "acrylic" ? 30 : 0,
                keepBackdropAtZero: kind == "blur" && tint.A != 0);
            SetBackdrop("desktop");
        }
        if (contrast || appearance?.MouseGlow != true || appearance.GlowStrength == 0)
            cursorLight.Visibility = Visibility.Collapsed;
        UpdateTint(background, tintDuration);
    }

    private void UpdateTint(Color tint, int milliseconds)
    {
        if (milliseconds <= 0 || targetTint is null)
        {
            FinishTint(tint);
            return;
        }
        if (tint == targetTint)
        {
            if (tintTransition is null) FinishTint(tint);
            return;
        }

        // Capture every stop before replacing the storyboard so reversals keep the visible gradient.
        Span<Color> startingColors = stackalloc Color[TintStopCount];
        for (int i = 0; i < startingColors.Length; i++)
            startingColors[i] = tintTransition is null ? tintBrush.Color : tintGradient.GradientStops[i].Color;
        tintTransition?.Stop();

        var transition = new Storyboard();
        var blendDuration = TimeSpan.FromMilliseconds(milliseconds * (1 - TintSweepFraction));
        for (int i = 0; i < startingColors.Length; i++)
        {
            var stop = tintGradient.GradientStops[i];
            stop.Color = startingColors[i];
            var blend = new ColorAnimation
            {
                From = stop.Color, To = tint, Duration = blendDuration,
                BeginTime = TimeSpan.FromMilliseconds(milliseconds * TintSweepFraction * stop.Offset),
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(blend, stop);
            Storyboard.SetTargetProperty(blend, nameof(GradientStop.Color));
            transition.Children.Add(blend);
        }
        surface.Background = tintGradient;
        targetTint = tint;
        tintTransition = transition;
        transition.Completed += (_, _) =>
        {
            if (ReferenceEquals(tintTransition, transition)) FinishTint(tint);
        };
        transition.Begin();
    }

    private void FinishTint(Color tint)
    {
        var transition = tintTransition;
        tintTransition = null;
        targetTint = tint;
        tintBrush.Color = tint;
        surface.Background = tintBrush;
        transition?.Stop();
        if (detachBackdropAfterTint)
        {
            detachBackdropAfterTint = false;
            SetBackdrop(null);
        }
    }

    private void SetBackdrop(string? kind)
    {
        if (backdropKind == kind) return;
        backdropKind = kind;
        backdrop = kind switch
        {
            "desktop" => desktopBackdrop,
            "acrylic" => new Halo.GlassBackdrop(Halo.GlassStyle.Acrylic),
            _ => null
        };
        window.SystemBackdrop = backdrop;
    }

    private void PointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (lastContrast == true || last is not { MouseGlow: true, GlowStrength: > 0 } appearance) return;
        var point = args.GetCurrentPoint(lightLayer);
        if (point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
        int diameter = LumaBar.TaskbarGlow.Diameter(new System.Drawing.Rectangle(0, 0,
            (int)surface.ActualWidth, (int)surface.ActualHeight), appearance.GlowRadius);
        var newSprite = (diameter, appearance.Tone == LumaBar.Tone.Light, appearance.GlowStrength);
        if (sprite != newSprite)
        {
            sprite = newSprite;
            using var bitmap = LumaBar.TaskbarGlowWindow.CreateSprite(diameter, newSprite.Item2, appearance.GlowStrength);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            using var bytes = stream.AsRandomAccessStream();
            var image = new BitmapImage();
            image.SetSource(bytes);
            cursorLight.Source = image;
            cursorLight.Width = cursorLight.Height = diameter;
        }
        Canvas.SetLeft(cursorLight, point.Position.X - diameter / 2.0);
        Canvas.SetTop(cursorLight, point.Position.Y - diameter / 2.0);
        cursorLight.Visibility = Visibility.Visible;
    }

    internal static Color FromAbgr(uint value) => Color.FromArgb((byte)(value >> 24), (byte)value, (byte)(value >> 8), (byte)(value >> 16));

    private static Color DarkLine(Color tint, Color normal, bool solid)
    {
        var background = SolidTint(tint, normal);
        byte Lift(byte channel) => (byte)(channel + (255 - channel) * .08);
        byte alpha = solid ? (byte)255 : (byte)Math.Clamp(tint.A / 2, 24, 140);
        return Color.FromArgb(alpha, Lift(background.R), Lift(background.G), Lift(background.B));
    }

    private static Color SolidTint(Color tint, Color normal)
    {
        byte Mix(byte top, byte bottom) => (byte)((top * tint.A + bottom * (255 - tint.A)) / 255);
        return Color.FromArgb(255, Mix(tint.R, normal.R), Mix(tint.G, normal.G), Mix(tint.B, normal.B));
    }

    public void Dispose()
    {
        disposed = true;
        var transition = tintTransition;
        tintTransition = null;
        transition?.Stop();
        host.BarAppearanceChanged -= OnAppearanceChanged;
        desktopBackdrop?.ReleaseRetainedTransparentResources();
    }
}

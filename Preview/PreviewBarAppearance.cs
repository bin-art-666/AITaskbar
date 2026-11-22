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
    private readonly Window window;
    private readonly DispatcherQueue dispatcher;
    private readonly AppearanceHost host;
    private readonly Border surface;
    private readonly Canvas lightLayer = new() { IsHitTestVisible = false };
    private readonly Border fadingTint = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Border topLine = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed };
    private readonly Image cursorLight = new() { Visibility = Visibility.Collapsed };
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Action<bool> updateIconTone;
    private Halo.GlassBackdrop? backdrop;
    private string? backdropKind;
    private LumaBar.BarAppearance? last;
    private bool? lastContrast, lastEffects, lastLight;
    private double lastScale;
    private (int Diameter, bool Light, int Strength) sprite;
    private Color? previousTint;
    private Storyboard? tintTransition;
    private bool disposed;
    public bool? IsLight => lastLight;

    public PreviewBarAppearance(Window window, Border surface, FrameworkElement content, AppearanceHost host, Action<bool> updateIconTone)
    {
        this.window = window;
        dispatcher = window.DispatcherQueue;
        this.surface = surface;
        this.host = host;
        this.updateIconTone = updateIconTone;
        lightLayer.Children.Add(cursorLight);
        var layers = new Grid();
        layers.Children.Add(fadingTint);
        layers.Children.Add(lightLayer);
        layers.Children.Add(content);
        layers.Children.Add(topLine);
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
        bool light = contrast || appearance is null
            ? surface.ActualTheme != ElementTheme.Dark : appearance.Tone == LumaBar.Tone.Light;
        double scale = surface.XamlRoot?.RasterizationScale ?? 1;
        if (last == appearance && lastContrast == contrast && lastEffects == effects && lastLight == light && lastScale == scale) return;
        last = appearance;
        lastContrast = contrast;
        lastEffects = effects;
        lastLight = light;
        lastScale = scale;
        surface.RequestedTheme = contrast || appearance is null
            ? ElementTheme.Default : light ? ElementTheme.Light : ElementTheme.Dark;
        updateIconTone(light);

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
        if (contrast || kind is "normal" or "opaque" || !effects)
        {
            SetBackdrop(null);
            surface.Background = new SolidColorBrush(contrast ? system.GetColorValue(UIColorType.Background)
                : kind == "normal" ? normal : SolidTint(tint, normal));
        }
        else if (kind == "acrylic" && DesktopAcrylicController.IsSupported())
        {
            SetBackdrop("acrylic");
            backdrop!.Update(new Halo.MaterialOptions(Thin: true, TintOpacity: tint.A / 255f,
                LuminosityOpacity: .65f, FollowTheme: false, TintHex: $"#{tint.R:X2}{tint.G:X2}{tint.B:X2}"));
            surface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else
        {
            // At full transparency and zero blur, let DWM composite the live windows directly.
            bool useBlurBackdrop = kind == "blur" && (appearance!.Blur > 0 || tint.A != 0);
            SetBackdrop(useBlurBackdrop ? "blur" : "desktop");
            backdrop!.UpdateBlur(kind == "blur" ? appearance!.Blur : kind == "acrylic" ? 30 : 0);
            surface.Background = new SolidColorBrush(tint);
        }
        if (contrast || appearance?.MouseGlow != true || appearance.GlowStrength == 0)
            cursorLight.Visibility = Visibility.Collapsed;
        UpdateTransition(contrast ? null : displayTint, contrast || !system.AnimationsEnabled
            ? 0 : appearance?.TransitionMilliseconds ?? 0);
    }

    private void UpdateTransition(Color? tint, int milliseconds)
    {
        if (milliseconds > 0 && tint == previousTint) return;
        tintTransition?.Stop();
        tintTransition = null;
        if (milliseconds > 0 && tint is not null && previousTint is { } previous)
        {
            fadingTint.Background = new SolidColorBrush(previous);
            var fade = new DoubleAnimation
            {
                From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(milliseconds),
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(fade, fadingTint);
            Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
            tintTransition = new Storyboard();
            tintTransition.Children.Add(fade);
            tintTransition.Begin();
        }
        previousTint = tint;
    }

    private void SetBackdrop(string? kind)
    {
        if (backdropKind == kind) return;
        backdropKind = kind;
        backdrop = kind switch
        {
            "desktop" or "blur" => new Halo.GlassBackdrop(new Halo.MaterialOptions(TintOpacity: 0, LuminosityOpacity: 0, NoBase: true))
                { TransparentCanvas = true, BlurScale = 1f / 3, KeepBlurBackdropAtZero = kind == "blur",
                    MaximumBlurStrength = LumaBar.TaskbarAppearance.MaximumBlurRadius },
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
        tintTransition?.Stop();
        host.BarAppearanceChanged -= OnAppearanceChanged;
    }
}

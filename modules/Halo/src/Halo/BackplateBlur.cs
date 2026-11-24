using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace Halo;

internal sealed class BackplateBlur : IDisposable
{
    readonly FrameworkElement host;
    SpriteVisual? visual;
    CompositionEffectFactory? factory;
    CompositionEffectBrush? effect;
    CompositionBackdropBrush? backdrop;
    int strength;
    CornerRadius corners;
    bool disposed;
    internal int Strength => strength;

    internal BackplateBlur(FrameworkElement host)
    {
        this.host = host;
        host.Loaded += OnLoaded;
        host.SizeChanged += OnSizeChanged;
    }

    internal void Update(int value, CornerRadius radius)
    {
        strength = Math.Clamp(value, 0, 100);
        corners = radius;
        if (disposed || strength == 0 || !host.IsLoaded)
        {
            if (visual is not null) visual.Opacity = 0;
            return;
        }
        EnsureVisual();
        effect!.Properties.InsertScalar("Blur.BlurAmount", strength * .18f);
        visual!.Opacity = 1;
        UpdateBounds();
    }

    void OnLoaded(object sender, RoutedEventArgs args) => Update(strength, corners);
    void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateBounds();

    void EnsureVisual()
    {
        if (visual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var description = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = .1f,
            BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("backdrop")
        };
        factory = compositor.CreateEffectFactory(description, ["Blur.BlurAmount"]);
        effect = factory.CreateBrush();
        backdrop = compositor.CreateBackdropBrush();
        effect.SetSourceParameter("backdrop", backdrop);
        visual = compositor.CreateSpriteVisual();
        visual.Brush = effect;
        ElementCompositionPreview.SetElementChildVisual(host, visual);
    }

    void UpdateBounds()
    {
        if (visual is null || disposed || host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
        float width = (float)host.ActualWidth, height = (float)host.ActualHeight;
        visual.Size = new(width, height);
        var clip = visual.Compositor.CreateRectangleClip();
        clip.Left = clip.Top = 0; clip.Right = width; clip.Bottom = height;
        float bottomLeft = (float)Math.Clamp(corners.BottomLeft, 0, Math.Min(width, height) / 2);
        float bottomRight = (float)Math.Clamp(corners.BottomRight, 0, Math.Min(width, height) / 2);
        clip.TopLeftRadius = new Vector2((float)Math.Clamp(corners.TopLeft, 0, Math.Min(width, height) / 2));
        clip.TopRightRadius = new Vector2((float)Math.Clamp(corners.TopRight, 0, Math.Min(width, height) / 2));
        clip.BottomLeftRadius = new Vector2(bottomLeft);
        clip.BottomRightRadius = new Vector2(bottomRight);
        var previous = visual.Clip;
        visual.Clip = clip;
        previous?.Dispose();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        host.Loaded -= OnLoaded;
        host.SizeChanged -= OnSizeChanged;
        ElementCompositionPreview.SetElementChildVisual(host, null);
        visual?.Dispose(); effect?.Dispose(); factory?.Dispose(); backdrop?.Dispose();
        visual = null; effect = null; factory = null; backdrop = null;
    }
}

internal sealed class BackplateSurface : ContentControl, IDisposable
{
    readonly Border blurHost = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly Border tint = new();
    readonly ContentPresenter content = new();
    readonly BackplateBlur blur;
    Windows.UI.Color? tintOverride;

    internal bool HasTintSurface => tint.Background is SolidColorBrush;
    internal int BlurStrength => blur.Strength;

    internal BackplateSurface()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var layers = new Grid();
        layers.Children.Add(blurHost);
        layers.Children.Add(tint);
        layers.Children.Add(content);
        Content = layers;
        blur = new BackplateBlur(blurHost);
        ActualThemeChanged += (_, _) => RefreshTint();
    }

    internal void SetSurfaceContent(UIElement child) => content.Content = child;

    internal void UpdateAppearance(UiAppearance appearance, bool dark, Windows.UI.Color? color = null)
    {
        tintOverride = color;
        tint.CornerRadius = CornerRadius;
        tint.Background = new SolidColorBrush(color ?? PanelTheme.BackplateColor(appearance, dark));
        blur.Update(appearance.BackplateBlur, CornerRadius);
    }

    // The tint colour follows the effective system theme, not Application.RequestedTheme.
    internal void RefreshTint()
    {
        if (ActualTheme == ElementTheme.Default) return; // Not laid out yet; UpdateAppearance will run on open.
        tint.Background = new SolidColorBrush(tintOverride ?? PanelTheme.BackplateColor(App.Appearance, ActualTheme == ElementTheme.Dark));
    }

    public void Dispose() => blur.Dispose();
}

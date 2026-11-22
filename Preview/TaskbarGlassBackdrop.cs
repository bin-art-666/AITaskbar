using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Composition;
using BackdropTarget = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;

namespace AITaskbar;

internal sealed class TaskbarGlassBackdrop(float radius = (float)TaskbarPanelFlyout.Radius,
    float travel = TaskbarPanelFlyout.Travel) : SystemBackdrop
{
    private Compositor? compositor;
    private CompositionBackdropBrush? desktop;
    private CompositionEffectBrush? blur;
    private CompositionColorBrush? white;
    private ContainerVisual? maskRoot;
    private SpriteVisual? silhouette;
    private CompositionRoundedRectangleGeometry? geometry;
    private CompositionGeometricClip? clip;
    private CompositionVisualSurface? surface;
    private CompositionSurfaceBrush? mask;
    private CompositionMaskBrush? glass;
    private Vector2 size;
    private float opacity, offset, blurAmount;

    internal void SetBlur(float amount)
    {
        blurAmount = amount;
        blur?.Properties.InsertScalar("Blur.BlurAmount", amount);
    }

    internal void SetFrame(Vector2 size, float opacity, float offset)
    {
        if (this.size != size && geometry is not null)
        {
            geometry.Size = Vector2.Max(size, Vector2.One);
            maskRoot!.Size = silhouette!.Size = surface!.SourceSize = SurfaceSize(size);
        }
        this.size = size;
        this.opacity = opacity;
        this.offset = offset;
        if (silhouette is null) return;
        silhouette.Opacity = opacity;
        geometry!.Offset = new Vector2(0, offset);
    }

    protected override void OnTargetConnected(BackdropTarget target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        Halo.WindowsCompositionQueue.EnsureCurrentThread();
        compositor = new();
        desktop = compositor.CreateHostBackdropBrush();
        using var factory = compositor.CreateEffectFactory(new GaussianBlurEffect
        {
            Name = "Blur", BlurAmount = blurAmount, BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("desktop"),
        }, ["Blur.BlurAmount"]);
        blur = factory.CreateBrush();
        blur.SetSourceParameter("desktop", desktop);

        geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(radius);
        geometry.Size = Vector2.Max(size, Vector2.One);
        geometry.Offset = new Vector2(0, offset);
        clip = compositor.CreateGeometricClip(geometry);
        white = compositor.CreateColorBrush(Microsoft.UI.Colors.White);
        silhouette = compositor.CreateSpriteVisual();
        silhouette.Clip = clip;
        silhouette.Brush = white;
        silhouette.Opacity = opacity;
        silhouette.Size = SurfaceSize(size);
        maskRoot = compositor.CreateContainerVisual();
        maskRoot.Size = silhouette.Size;
        maskRoot.Children.InsertAtTop(silhouette);
        surface = compositor.CreateVisualSurface();
        surface.SourceVisual = maskRoot;
        surface.SourceSize = silhouette.Size;
        mask = compositor.CreateSurfaceBrush(surface);
        mask.Stretch = CompositionStretch.Fill;
        glass = compositor.CreateMaskBrush();
        glass.Mask = mask;
        glass.Source = blur;
        target.SystemBackdrop = glass;
    }

    private Vector2 SurfaceSize(Vector2 size) => Vector2.Max(size + new Vector2(0, travel), Vector2.One);

    protected override void OnTargetDisconnected(BackdropTarget target)
    {
        target.SystemBackdrop = null;
        glass?.Dispose();
        mask?.Dispose();
        surface?.Dispose();
        maskRoot?.Dispose();
        silhouette?.Dispose();
        clip?.Dispose();
        geometry?.Dispose();
        white?.Dispose();
        blur?.Dispose();
        desktop?.Dispose();
        compositor?.Dispose();
        glass = null;
        mask = null;
        surface = null;
        maskRoot = null;
        silhouette = null;
        clip = null;
        geometry = null;
        white = null;
        blur = null;
        desktop = null;
        compositor = null;
        base.OnTargetDisconnected(target);
    }
}

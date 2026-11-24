using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Composition;
using BackdropTarget = Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop;

namespace AIEnter;

internal sealed class DesktopGlassBackdrop(bool transparentWindow = false, bool allowGlassFallback = false) : SystemBackdrop
{
    private Compositor? compositor;
    private BackdropTarget? target;
    private TransparentWindowFrame? windowFrame;
    private CompositionColorBrush? clear;
    private CompositionBackdropBrush? desktop;
    private CompositionEffectBrush? blur;
    private CompositionColorBrush? white;
    private ContainerVisual? maskRoot;
    private ContainerVisual? bandVisual;
    private InsetClip? band;
    private SpriteVisual? silhouette;
    private CompositionRoundedRectangleGeometry? geometry;
    private CompositionGeometricClip? clip;
    private CompositionVisualSurface? surface;
    private CompositionSurfaceBrush? mask;
    private CompositionMaskBrush? glass;
    private Vector2 canvas, size;
    private float radius, opacity, offset, blurAmount;
    private bool squareBottom, failed;

    internal bool IsReady => target is not null && (!transparentWindow || clear is not null);
    internal bool HasGlass => !failed && blurAmount > 0 && glass is not null;
    internal bool CanUseGlass => !failed;
    internal Vector2 MaskSize => size;
    internal (float Opacity, float Offset) Motion => (opacity, offset);
    internal event Action? GlassUnavailable;
    internal event Action? WindowSettingsChanged;
    internal void ReapplyWindowFrame() => windowFrame?.Reapply();

    internal void SetBlur(float amount)
    {
        if (blurAmount == amount) return;
        blurAmount = amount;
        blur?.Properties.InsertScalar("Blur.BlurAmount", amount);
        UpdateBrush();
    }

    internal void SetShape(Vector2 canvas, Vector2 size, float radius, bool squareBottom = false)
    {
        if (this.canvas == canvas && this.size == size && this.radius == radius && this.squareBottom == squareBottom) return;
        this.canvas = canvas;
        this.size = size;
        this.radius = radius;
        this.squareBottom = squareBottom;
        UpdateShape();
    }

    internal void SetMotion(float opacity, float offset)
    {
        this.opacity = opacity;
        this.offset = offset;
        UpdateMotion();
    }

    private void UpdateShape()
    {
        if (geometry is null) return;
        float corner = Math.Clamp(radius, 0, Math.Min(size.X / 2, squareBottom ? size.Y : size.Y / 2));
        geometry.CornerRadius = new Vector2(corner);
        // Extend the round rectangle below the clip when only the top corners are visible.
        geometry.Size = Vector2.Max(size + new Vector2(0, squareBottom ? corner : 0), Vector2.One);
        maskRoot!.Size = bandVisual!.Size = silhouette!.Size = surface!.SourceSize = Vector2.Max(canvas, Vector2.One);
        UpdateMotion();
    }

    private void UpdateMotion()
    {
        if (silhouette is null) return;
        silhouette.Opacity = canvas.X > 0 && size.Y > 0 ? opacity : 0;
        geometry!.Offset = new Vector2(0, offset);
        band!.BottomInset = Math.Max(0, canvas.Y - size.Y - offset);
    }

    protected override void OnTargetConnected(BackdropTarget target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        WindowsCompositionQueue.EnsureCurrentThread();
        this.target = target;
        compositor = new();
        if (transparentWindow)
        {
            var handle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
            windowFrame = new TransparentWindowFrame(handle, () => WindowSettingsChanged?.Invoke());
            clear = compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        }
        UpdateBrush();
    }

    private void UpdateBrush()
    {
        if (target is null) return;
        if (blurAmount > 0 && !failed && glass is null)
        {
            try { CreateGlass(); }
            catch (COMException) when (transparentWindow || allowGlassFallback)
            {
                failed = true;
                ReleaseGlass();
                GlassUnavailable?.Invoke();
            }
        }
        target.SystemBackdrop = HasGlass ? glass : clear;
    }

    private void CreateGlass()
    {
        var compositor = this.compositor!;
        desktop = compositor.CreateHostBackdropBrush();
        using var factory = compositor.CreateEffectFactory(new GaussianBlurEffect
        {
            Name = "Blur", BlurAmount = blurAmount, BorderMode = EffectBorderMode.Hard,
            Source = new CompositionEffectSourceParameter("desktop"),
        }, ["Blur.BlurAmount"]);
        blur = factory.CreateBrush();
        blur.SetSourceParameter("desktop", desktop);

        geometry = compositor.CreateRoundedRectangleGeometry();
        clip = compositor.CreateGeometricClip(geometry);
        white = compositor.CreateColorBrush(Microsoft.UI.Colors.White);
        silhouette = compositor.CreateSpriteVisual();
        silhouette.Clip = clip;
        silhouette.Brush = white;
        maskRoot = compositor.CreateContainerVisual();
        bandVisual = compositor.CreateContainerVisual();
        band = compositor.CreateInsetClip();
        bandVisual.Clip = band;
        bandVisual.Children.InsertAtTop(silhouette);
        maskRoot.Children.InsertAtTop(bandVisual);
        surface = compositor.CreateVisualSurface();
        surface.SourceVisual = maskRoot;
        mask = compositor.CreateSurfaceBrush(surface);
        mask.Stretch = CompositionStretch.Fill;
        glass = compositor.CreateMaskBrush();
        glass.Mask = mask;
        glass.Source = blur;
        UpdateShape();
    }

    protected override void OnTargetDisconnected(BackdropTarget target)
    {
        target.SystemBackdrop = null;
        this.target = null;
        ReleaseGlass();
        clear?.Dispose();
        clear = null;
        compositor?.Dispose();
        compositor = null;
        windowFrame?.Dispose();
        windowFrame = null;
        base.OnTargetDisconnected(target);
    }

    private void ReleaseGlass()
    {
        glass?.Dispose();
        mask?.Dispose();
        surface?.Dispose();
        maskRoot?.Dispose();
        bandVisual?.Dispose();
        band?.Dispose();
        silhouette?.Dispose();
        clip?.Dispose();
        geometry?.Dispose();
        white?.Dispose();
        blur?.Dispose();
        desktop?.Dispose();
        glass = null;
        mask = null;
        surface = null;
        maskRoot = null;
        bandVisual = null;
        band = null;
        silhouette = null;
        clip = null;
        geometry = null;
        white = null;
        blur = null;
        desktop = null;
    }
}

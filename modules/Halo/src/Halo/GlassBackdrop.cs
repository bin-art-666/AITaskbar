using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Halo;

// Each window owns its backdrop. System policy still controls transparency fallback.
public sealed class GlassBackdrop : SystemBackdrop
{
    MaterialOptions material;
    internal bool TransparentCanvas { get; init; }
    internal float BlurScale { get; init; } = .18f;
    public GlassBackdrop(GlassStyle style) : this(MaterialOptions.ForPreset(style)) { }
    public GlassBackdrop(MaterialOptions material) => this.material = material;

    public void Update(MaterialOptions material)
    {
        if (TransparentCanvas) material = new(TintOpacity: 0, LuminosityOpacity: 0, NoBase: true);
        if (this.material == material) return;
        this.material = material;
        UpdatePalette();
    }
    DesktopAcrylicController? controller;
    ICompositionSupportsSystemBackdrop? connectedTarget;
    IntPtr handle;
    Windows.UI.Composition.Compositor? transparentCompositor;
    Windows.UI.Composition.CompositionColorBrush? transparentBrush;
    Windows.UI.Composition.CompositionEffectFactory? blurFactory;
    Windows.UI.Composition.CompositionEffectBrush? blurBrush;
    Windows.UI.Composition.CompositionBackdropBrush? desktopSource;
    int blurStrength;
    internal int BlurStrength => blurStrength;
    internal void UpdateBlur(int value)
    {
        value = Math.Clamp(value, 0, 100);
        if (blurStrength == value) return;
        blurStrength = value;
        UpdatePalette();
    }
    TransparentWindowFrame? transparentFrame;
    internal bool IsClearCanvas => TransparentCanvas && transparentBrush?.Color.A == 0 && controller == null;
    internal bool IsNoBaseActive => material.NoBase && transparentBrush != null;
    internal bool IsReady => material.NoBase ? transparentBrush != null : controller != null || !DesktopAcrylicController.IsSupported();
    internal int ConnectionCount { get; private set; }
    internal void ReapplyTransparentFrame() => transparentFrame?.Reapply();
    SystemBackdropConfiguration? configuration;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        ConnectionCount++;
        connectedTarget = target;
        handle = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
        configuration = GetDefaultSystemBackdropConfiguration(target, root);
        UpdatePalette();
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        if (connectedTarget is null) return;
        try { base.OnDefaultSystemBackdropConfigurationChanged(target, root); }
        catch (ArgumentException error) when (error.HResult == unchecked((int)0x80070057))
        {
            // WinUI can deliver a pending notification after replacing the backdrop target.
            return;
        }
        UpdatePalette();
    }

    void UpdatePalette()
    {
        if (connectedTarget == null || configuration == null) return;
        bool dark = configuration.Theme == SystemBackdropTheme.Dark;
        if (material.NoBase)
        {
            WindowsCompositionQueue.EnsureCurrentThread();
            if (controller != null)
            {
                controller.RemoveSystemBackdropTarget(connectedTarget); controller.Dispose(); controller = null;
            }
            transparentFrame ??= new TransparentWindowFrame(handle);
            transparentCompositor ??= new Windows.UI.Composition.Compositor();
            var color = material.FlatColor(dark);
            transparentBrush ??= transparentCompositor.CreateColorBrush();
            transparentBrush.Color = Color.FromArgb(color.Alpha, (byte)(color.Rgb >> 16), (byte)(color.Rgb >> 8), (byte)color.Rgb);
            if (TransparentCanvas && blurStrength > 0)
            {
                if (blurBrush == null)
                {
                    var description = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                    {
                        Name = "DesktopBlur", BlurAmount = .1f,
                        BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Hard,
                        Source = new Windows.UI.Composition.CompositionEffectSourceParameter("desktop")
                    };
                    blurFactory = transparentCompositor.CreateEffectFactory(description, new[] { "DesktopBlur.BlurAmount" });
                    blurBrush = blurFactory.CreateBrush();
                    desktopSource = transparentCompositor.CreateHostBackdropBrush();
                    blurBrush.SetSourceParameter("desktop", desktopSource);
                }
                blurBrush.Properties.InsertScalar("DesktopBlur.BlurAmount", blurStrength * BlurScale);
                connectedTarget.SystemBackdrop = blurBrush;
            }
            else connectedTarget.SystemBackdrop = transparentBrush;
            return;
        }
        ClearTransparent();
        if (!DesktopAcrylicController.IsSupported()) return;
        bool attach = controller == null;
        controller ??= new DesktopAcrylicController();
        controller.Kind = material.Thin ? DesktopAcrylicKind.Thin : DesktopAcrylicKind.Base;
        uint rgb = Convert.ToUInt32(material.TintHex[1..], 16);
        controller.TintColor = material.FollowTheme
            ? (dark ? Color.FromArgb(255, 20, 29, 28) : Color.FromArgb(255, 246, 251, 249))
            : Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        controller.FallbackColor = dark ? Color.FromArgb(255, 31, 39, 38) : Color.FromArgb(255, 239, 245, 242);
        controller.TintOpacity = material.TintOpacity;
        controller.LuminosityOpacity = material.LuminosityOpacity;
        // A desktop bar stays visually consistent when focus moves to another app.
        configuration.IsInputActive = true;
        if (attach)
        {
            controller.AddSystemBackdropTarget(connectedTarget);
            controller.SetSystemBackdropConfiguration(configuration);
        }
    }

    void ClearTransparent()
    {
        if (transparentBrush == null && transparentFrame == null) return;
        if (connectedTarget != null) connectedTarget.SystemBackdrop = null;
        blurBrush?.Dispose(); blurBrush = null;
        blurFactory?.Dispose(); blurFactory = null;
        desktopSource?.Dispose(); desktopSource = null;
        transparentBrush?.Dispose(); transparentBrush = null;
        transparentCompositor?.Dispose(); transparentCompositor = null;
        transparentFrame?.Dispose(); transparentFrame = null;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        controller?.RemoveSystemBackdropTarget(target);
        controller?.Dispose();
        controller = null;
        ClearTransparent();
        connectedTarget = null;
        configuration = null;
        base.OnTargetDisconnected(target);
    }
}

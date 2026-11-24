using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed record AppCenterBackplate(ElementTheme Theme, Color Tint, Color Solid, Color Text, Color Taskbar, float Blur)
{
    private const byte MinimumGlassOpacity = 192;

    internal static AppCenterBackplate Resolve(QuickBackdrop mode, LumaBar.BarAppearance? bar, UISettings system, bool highContrast)
    {
        if (highContrast)
        {
            var background = system.GetColorValue(UIColorType.Background);
            return new(ElementTheme.Default, background, background, system.GetColorValue(UIColorType.Foreground), background, 0);
        }

        bool barDark = bar is null ? !TaskbarPanelFlyout.IsLight(system.GetColorValue(UIColorType.Background))
            : bar.Tone == LumaBar.Tone.Dark;
        var barNeutral = barDark ? Color.FromArgb(255, 30, 34, 42) : Color.FromArgb(255, 243, 246, 250);
        var barTint = bar is null || bar.Material == "normal" ? barNeutral : PreviewBarAppearance.FromAbgr(bar.Abgr);
        var barSolid = AIEnter.PanelTheme.Composite(barTint, barNeutral);
        bool barGlass = system.AdvancedEffectsEnabled && bar is { Material: "blur" or "acrylic" or "clear" };

        bool dark = mode == QuickBackdrop.Dark || mode == QuickBackdrop.FollowTaskbar && barDark;
        Color tint, solid;
        float blur;
        if (mode == QuickBackdrop.FollowTaskbar)
        {
            solid = AIEnter.PanelTheme.ReadableToolbarTint(barSolid, dark);
            var readableTint = barTint;
            readableTint.A = Math.Max(readableTint.A, MinimumGlassOpacity);
            tint = barGlass ? AIEnter.PanelTheme.ReadableToolbarTint(readableTint, dark) : solid;
            blur = barGlass ? (bar!.Material == "acrylic" ? 30 : bar.Material == "blur" ? bar.Blur : 0) / 3f : 0;
        }
        else
        {
            var material = new AIEnter.MaterialOptions();
            var (alpha, rgb) = material.FlatColor(dark);
            solid = AIEnter.PanelTheme.PaletteColor(0xFF000000u | rgb);
            tint = solid;
            if (system.AdvancedEffectsEnabled)
                tint.A = (byte)Math.Max(MinimumGlassOpacity, Math.Round(alpha * (100d - AIEnter.UiDefaults.BackplateTransparency)
                    / (100d - AIEnter.UiDefaults.BackplateTransparencyReference)));
            blur = tint.A < 255 ? AIEnter.UiDefaults.BackplateBlur * .18f : 0;
        }

        return new(dark ? ElementTheme.Dark : ElementTheme.Light, tint, solid,
            dark ? Color.FromArgb(255, 241, 247, 244) : Color.FromArgb(255, 25, 37, 35), barGlass ? barTint : barSolid, blur);
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIEnter;

internal static partial class PanelTheme
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ButtonBase, Dictionary<string, SolidColorBrush>> buttonBrushes = new();
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ButtonBase, ToolbarPalette> toolbarPalettes = new();
    internal static readonly IReadOnlyList<string> ButtonStates = Array.AsReadOnly(new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" });
    internal sealed record ToolbarPalette(ElementTheme Theme, Color Tint);
    const double MinimumToolbarContrast = 4.6; // Leave room for rounding in the compositor.

    internal static void SetToolbarPalette(ButtonBase button, ToolbarPalette palette)
    {
        toolbarPalettes.Remove(button);
        toolbarPalettes.Add(button, palette);
        ApplyButtonColors(button);
    }

    internal static bool HasToolbarPalette(ButtonBase button) => toolbarPalettes.TryGetValue(button, out _);

    internal static Color ReadableToolbarTint(Color tint, bool dark)
    {
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        var neutral = dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
        tint = ReadableTint(tint, neutral, candidate => Contrast(foreground, WithAlpha(candidate, 255)));
        return RaiseOpacity(tint, foreground, Microsoft.UI.Colors.Black, Microsoft.UI.Colors.White);
    }

    static (Color Background, Color Foreground) ReadableButton(Color background, ToolbarPalette toolbar, bool selected)
    {
        var overBlack = Composite(toolbar.Tint, Microsoft.UI.Colors.Black);
        var overWhite = Composite(toolbar.Tint, Microsoft.UI.Colors.White);
        var black = Microsoft.UI.Colors.Black;
        var white = Microsoft.UI.Colors.White;
        var preferred = toolbar.Theme == ElementTheme.Dark ? white : black;
        var alternative = preferred == black ? white : black;
        if (WorstContrast(preferred, background, overBlack, overWhite) >= MinimumToolbarContrast) return (background, preferred);
        if (WorstContrast(alternative, background, overBlack, overWhite) >= MinimumToolbarContrast) return (background, alternative);
        if (!selected)
        {
            // A translucent ordinary button can keep the toolbar ink by shading
            // just its fill. Making a bright tint opaque would switch dark mode to black text.
            var shade = ReadableTint(background, alternative,
                candidate => WorstContrast(preferred, candidate, overBlack, overWhite));
            return (shade, preferred);
        }
        var opaque = WithAlpha(background, 255);
        // At full opacity, pure black or white always reaches 4.5:1.
        var foreground = Contrast(black, opaque) >= Contrast(white, opaque) ? black : white;
        return (RaiseOpacity(background, foreground, overBlack, overWhite), foreground);
    }

    static Color ReadableTint(Color tint, Color neutral, Func<Color, double> contrast)
    {
        if (contrast(tint) >= MinimumToolbarContrast) return tint;
        Color Mix(byte amount) => WithAlpha(Composite(WithAlpha(tint, (byte)(255 - amount)), neutral), tint.A);
        return Mix(FirstPassing(0, amount => contrast(Mix(amount)) >= MinimumToolbarContrast));
    }

    static Color RaiseOpacity(Color background, Color foreground, Color overBlack, Color overWhite)
    {
        if (WorstContrast(foreground, background, overBlack, overWhite) >= MinimumToolbarContrast) return background;
        return WithAlpha(background, FirstPassing(background.A, alpha =>
            WorstContrast(foreground, WithAlpha(background, alpha), overBlack, overWhite) >= MinimumToolbarContrast));
    }

    static byte FirstPassing(byte start, Func<byte, bool> passes)
    {
        int low = start, high = 255;
        while (low < high)
        {
            int midpoint = (low + high) / 2;
            if (passes((byte)midpoint)) high = midpoint;
            else low = midpoint + 1;
        }
        return (byte)low;
    }

    static double WorstContrast(Color foreground, Color layer, Color overBlack, Color overWhite) =>
        Math.Min(Contrast(foreground, Composite(layer, overBlack)), Contrast(foreground, Composite(layer, overWhite)));

    internal static Color Composite(Color top, Color bottom)
    {
        byte Channel(byte front, byte back) => (byte)((front * top.A + back * (255 - top.A) + 127) / 255);
        return Color.FromArgb(255, Channel(top.R, bottom.R), Channel(top.G, bottom.G), Channel(top.B, bottom.B));
    }

    static Color WithAlpha(Color value, byte alpha) => Color.FromArgb(alpha, value.R, value.G, value.B);

    internal static double Contrast(Color first, Color second)
    {
        static double Channel(byte channel)
        {
            double value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        static double Luminance(Color value) => .2126 * Channel(value.R) + .7152 * Channel(value.G) + .0722 * Channel(value.B);
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
    static Color PaletteColor(uint value) => Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    static Color ReadableText(Color background, bool dark)
    {
        double a = background.A / 255d;
        double Channel(byte c) { double value = (c * a + (dark ? 32 : 245) * (1 - a)) / 255d; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
        double luminance = .2126 * Channel(background.R) + .7152 * Channel(background.G) + .0722 * Channel(background.B);
        return luminance > .179 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }
    static void ApplyButtonColors(ButtonBase button)
    {
        string prefix = button is ToggleButton ? "ToggleButton" : "Button";
        toolbarPalettes.TryGetValue(button, out var toolbar);
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        if (highContrast)
        {
            foreach (string state in ButtonStates)
                foreach (string part in new[] { "Background", "Foreground", "BorderBrush" }) button.Resources.Remove(prefix + part + state);
            button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.ForegroundProperty); button.ClearValue(Control.BorderBrushProperty);
            AiControls.RefreshToolbarIcon(button);
            return;
        }
        bool dark = (toolbar?.Theme ?? button.ActualTheme) == ElementTheme.Dark;
        var normal = PaletteColor(UiAppearance.ColorValue(App.Appearance.ButtonColor, dark ? 0x0FFFFFFFu : 0x1CFFFFFFu));
        uint accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var accentValue) && accentValue is Color color
            ? (0xFF000000u | (uint)color.R << 16 | (uint)color.G << 8 | color.B) : 0xFF0067C0u;
        var selected = PaletteColor(UiAppearance.ColorValue(App.Appearance.SelectedButtonColor, accent));
        SolidColorBrush Brush(string key, Color value)
        {
            var owned = buttonBrushes.GetOrCreateValue(button);
            if (owned.TryGetValue(key, out var brush)) { brush.Color = value; button.Resources[key] = brush; return brush; }
            var created = new SolidColorBrush(value); owned[key] = created; button.Resources[key] = created; return created;
        }
        foreach (string state in ButtonStates)
        {
            if (button is not ToggleButton && state.StartsWith("Checked")) continue;
            bool check = state.StartsWith("Checked");
            var background = check ? selected : normal;
            var foreground = toolbar is null ? ReadableText(background, dark) : ReadableButton(background, toolbar, check).Foreground;
            if (state.Contains("PointerOver") || state.Contains("Pressed"))
            {
                double blend = state.Contains("Pressed") ? .18 : .09;
                byte Mix(byte channel, byte target) => (byte)Math.Round(channel * (1 - blend) + target * blend);
                background = Color.FromArgb(Math.Max(background.A, (byte)48), Mix(background.R, foreground.R), Mix(background.G, foreground.G), Mix(background.B, foreground.B));
            }
            if (toolbar is not null) (background, foreground) = ReadableButton(background, toolbar, check);
            var border = check ? background : toolbar is not null && !dark
                ? Color.FromArgb(48, 24, 39, 57) : Color.FromArgb(dark ? (byte)88 : (byte)190, 255, 255, 255);
            Brush(prefix + "Background" + state, background);
            Brush(prefix + "Foreground" + state, foreground);
            Brush(prefix + "BorderBrush" + state, border);
        }
        button.Background = (SolidColorBrush)button.Resources[prefix + "Background"];
        button.Foreground = (SolidColorBrush)button.Resources[prefix + "Foreground"];
        button.BorderBrush = (SolidColorBrush)button.Resources[prefix + "BorderBrush"];
        if (toolbar is not null) AiControls.RefreshToolbarIcon(button);
    }
}

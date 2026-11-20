using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIEnter;

internal static partial class PanelTheme
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ButtonBase, Dictionary<string, SolidColorBrush>> buttonBrushes = new();
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
        bool highContrast = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
        if (highContrast)
        {
            foreach (string state in new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" })
                foreach (string part in new[] { "Background", "Foreground", "BorderBrush" }) button.Resources.Remove(prefix + part + state);
            button.ClearValue(Control.BackgroundProperty); button.ClearValue(Control.ForegroundProperty); button.ClearValue(Control.BorderBrushProperty);
            return;
        }
        bool dark = button.ActualTheme == ElementTheme.Dark;
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
        foreach (string state in new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" })
        {
            if (button is not ToggleButton && state.StartsWith("Checked")) continue;
            bool check = state.StartsWith("Checked");
            var background = check ? selected : normal;
            var foreground = ReadableText(background, dark);
            if (state.Contains("PointerOver") || state.Contains("Pressed"))
            {
                double blend = state.Contains("Pressed") ? .18 : .09;
                byte Mix(byte channel, byte target) => (byte)Math.Round(channel * (1 - blend) + target * blend);
                background = Color.FromArgb(Math.Max(background.A, (byte)48), Mix(background.R, foreground.R), Mix(background.G, foreground.G), Mix(background.B, foreground.B));
            }
            var border = check ? background : Color.FromArgb(dark ? (byte)88 : (byte)190, 255, 255, 255);
            Brush(prefix + "Background" + state, background);
            Brush(prefix + "Foreground" + state, foreground);
            Brush(prefix + "BorderBrush" + state, border);
        }
        button.Background = (SolidColorBrush)button.Resources[prefix + "Background"];
        button.Foreground = (SolidColorBrush)button.Resources[prefix + "Foreground"];
        button.BorderBrush = (SolidColorBrush)button.Resources[prefix + "BorderBrush"];
    }
}

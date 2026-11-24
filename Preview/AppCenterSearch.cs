using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AITaskbar;

internal static class AppCenterSearch
{
    private const string PointerOverBackground = "TextControlBackgroundPointerOver";
    private const string FocusedBackground = "TextControlBackgroundFocused";
    private const string PointerOverBorder = "TextControlBorderBrushPointerOver";
    private const string FocusedBorder = "TextControlBorderBrushFocused";
    private const string FocusedBorderThickness = "TextControlBorderThemeThicknessFocused";

    internal static TextBox Create(bool compact = false) => new()
    {
        PlaceholderText = compact ? "搜索应用" : "搜索应用 · Esc 收起",
        MaxWidth = compact ? 164 : 550,
        MinHeight = compact ? 32 : 44,
        CornerRadius = new CornerRadius(compact ? 16 : 22),
        Padding = compact ? new Thickness(12, 4, 12, 4) : new Thickness(16, 9, 16, 9),
        FontSize = compact ? 13 : 15
    };

    internal static void ApplyTheme(TextBox input, bool dark, bool highContrast)
    {
        if (highContrast)
        {
            input.ClearValue(Control.BackgroundProperty);
            input.ClearValue(Control.BorderBrushProperty);
            input.Resources.Remove(PointerOverBackground);
            input.Resources.Remove(FocusedBackground);
            input.Resources.Remove(PointerOverBorder);
            input.Resources.Remove(FocusedBorder);
            input.Resources.Remove(FocusedBorderThickness);
            return;
        }

        var edge = Brush(dark ? 0x535862u : 0xD5DBE2u);
        input.Background = Brush(dark ? 0x22252Bu : 0xF8FAFCu);
        input.BorderBrush = edge;
        input.Resources[PointerOverBackground] = Brush(dark ? 0x292D34u : 0xFFFFFFu);
        input.Resources[FocusedBackground] = Brush(dark ? 0x262A31u : 0xFFFFFFu);
        input.Resources[PointerOverBorder] = edge;
        input.Resources[FocusedBorder] = Brush(dark ? 0x777C86u : 0x9AA4AEu);
        input.Resources[FocusedBorderThickness] = new Thickness(1);
    }

    private static SolidColorBrush Brush(uint rgb) =>
        new(Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}

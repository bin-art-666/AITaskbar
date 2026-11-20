using System.Text.Json;

namespace AIEnter;

public enum GlassStyle { Light, Frosted, Acrylic, Custom }


public sealed record MaterialOptions(bool Thin = false, float TintOpacity = .22f,
    float LuminosityOpacity = .78f, bool FollowTheme = true, string TintHex = "#D5E9E1", bool ShowLine = false, bool NoBase = false)
{
    internal (byte Alpha, uint Rgb) FlatColor(bool dark)
    {
        uint tint = FollowTheme ? (dark ? 0x141D1Cu : 0xF6FBF9u) : Convert.ToUInt32(TintHex[1..], 16);
        double tintAlpha = TintOpacity, maskAlpha = LuminosityOpacity * (1 - tintAlpha);
        double alpha = tintAlpha + maskAlpha;
        if (alpha == 0) return (0, 0);
        uint Channel(int shift) => (uint)Math.Clamp(Math.Round((((tint >> shift) & 255) * tintAlpha
            + (dark ? 0 : 255) * maskAlpha) / alpha), 0, 255);
        return ((byte)Math.Round(alpha * 255), (Channel(16) << 16) | (Channel(8) << 8) | Channel(0));
    }
    public static MaterialOptions ForPreset(GlassStyle style) => style switch
    {
        GlassStyle.Light => new(true, .08f, .55f),
        GlassStyle.Acrylic => new(false, .45f, .94f),
        _ => new()
    };

    public static bool IsColor(string? value) => value is { Length: 7 } && value[0] == '#'
        && uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    internal bool IsValid => float.IsFinite(TintOpacity) && TintOpacity is >= 0 and <= 1
        && float.IsFinite(LuminosityOpacity) && LuminosityOpacity is >= 0 and <= 1 && IsColor(TintHex);
}

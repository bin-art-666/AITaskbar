using System.Text.Json;
using System.Text.Json.Serialization;

namespace AITaskbar;

internal enum IconScrollAnimation { Off, Fast, Standard, Relaxed }
internal enum WindowAnimationStyle { Curved, WindowsDefault, WindowsStyle }

internal sealed record PreviewIconLayout
{
    internal const int MinGap = 2, MaxGap = 12, DefaultGap = 7;
    internal const int MinWindowDurationMs = 200, MaxWindowDurationMs = 1000, DefaultWindowDurationMs = 240;
    private const int WindowsStyleDurationMs = 200; // Match the visible resize in the native animation comparison.
    internal static PreviewIconLayout Default { get; } = new();

    public int Gap { get; init; } = DefaultGap;
    [JsonConverter(typeof(JsonStringEnumConverter<IconScrollAnimation>))]
    public IconScrollAnimation Animation { get; init; } = IconScrollAnimation.Standard;
    [JsonConverter(typeof(JsonStringEnumConverter<WindowAnimationStyle>))]
    public WindowAnimationStyle WindowAnimation { get; init; } = WindowAnimationStyle.WindowsDefault;
    public int WindowDurationMs { get; init; } = DefaultWindowDurationMs;
    public bool ShowRecentFiles { get; init; } = true;
    public bool ShowEdgeRecentFiles { get; init; } = true;
    public bool ShowEdgeRecentlyClosed { get; init; } = true;
    public bool ShowChromeRecentlyClosed { get; init; } = true;
    internal bool UsesPreviewWindowAnimation => WindowAnimation is WindowAnimationStyle.Curved or WindowAnimationStyle.WindowsStyle;
    internal int EffectiveWindowDurationMs => WindowAnimation == WindowAnimationStyle.WindowsStyle
        ? WindowsStyleDurationMs : WindowDurationMs;

    internal int DurationMs => Animation switch
    {
        IconScrollAnimation.Off => 0,
        IconScrollAnimation.Fast => 110,
        IconScrollAnimation.Relaxed => 260,
        _ => 170
    };

    internal static PreviewIconLayout Read(string root)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(FilePath(root)));
            if (json.RootElement.Deserialize<PreviewIconLayout>() is { } value)
            {
                int duration = value.WindowDurationMs;
                if (!json.RootElement.TryGetProperty(nameof(WindowDurationMs), out _) &&
                    json.RootElement.TryGetProperty("WindowSpeed", out var speed) && IsLegacySlow(speed))
                    duration = MaxWindowDurationMs;
                return value with
                {
                    Gap = Math.Clamp(value.Gap, MinGap, MaxGap),
                    Animation = Enum.IsDefined(value.Animation) ? value.Animation : Default.Animation,
                    WindowAnimation = Enum.IsDefined(value.WindowAnimation) ? value.WindowAnimation : Default.WindowAnimation,
                    WindowDurationMs = Math.Clamp(duration, MinWindowDurationMs, MaxWindowDurationMs)
                };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return Default;
    }

    private static bool IsLegacySlow(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()?.Trim() is { } name &&
            (name.Equals("Slow", StringComparison.OrdinalIgnoreCase) || name == "1"),
        JsonValueKind.Number => value.TryGetInt32(out int speed) && speed == 1,
        _ => false
    };

    internal void Save(string root)
    {
        Directory.CreateDirectory(root);
        string file = FilePath(root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this));
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string FilePath(string root) => Path.Combine(root, "preview-icon-layout.json");

    internal static double ScrollOffset(double from, double to, double progress)
    {
        double remaining = 1 - progress;
        return to + (from - to) * remaining * remaining * remaining;
    }

    internal static double EdgeOpacity(double left, double iconSize, double viewportWidth)
    {
        double visible = Math.Clamp(Math.Min(left + iconSize, viewportWidth) - Math.Max(0, left), 0, iconSize);
        return .6 + .4 * visible / iconSize;
    }
}

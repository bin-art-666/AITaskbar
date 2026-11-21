using System.Text.Json;
using System.Text.Json.Serialization;

namespace AITaskbar;

internal enum IconScrollAnimation { Off, Fast, Standard, Relaxed }

internal sealed record PreviewIconLayout
{
    internal const int MinGap = 2, MaxGap = 12, DefaultGap = 7;
    internal static PreviewIconLayout Default { get; } = new();

    public int Gap { get; init; } = DefaultGap;
    [JsonConverter(typeof(JsonStringEnumConverter<IconScrollAnimation>))]
    public IconScrollAnimation Animation { get; init; } = IconScrollAnimation.Standard;

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
            if (JsonSerializer.Deserialize<PreviewIconLayout>(File.ReadAllBytes(FilePath(root))) is { } value)
                return value with
                {
                    Gap = Math.Clamp(value.Gap, MinGap, MaxGap),
                    Animation = Enum.IsDefined(value.Animation) ? value.Animation : Default.Animation
                };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return Default;
    }

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

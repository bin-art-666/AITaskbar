using System.Globalization;
using System.Text.Json;
using Windows.UI;

namespace AITaskbar;

internal sealed record TaskbarAppFeedbackStyle
{
    internal const int MinDiameter = 3, MaxDiameter = 6, MinExpansion = 1, MaxExpansion = 4;
    internal static TaskbarAppFeedbackStyle Default { get; } = new();

    public string Active { get; init; } = "#0078D4";
    public string Background { get; init; } = "#000000";
    public int Diameter { get; init; } = 4;
    public bool ShowHover { get; init; } = true;
    public int HoverExpansion { get; init; } = 2;

    internal static TaskbarAppFeedbackStyle Read(string root)
    {
        try
        {
            var style = JsonSerializer.Deserialize<TaskbarAppFeedbackStyle>(File.ReadAllBytes(FilePath(root)));
            if (style is not null) return style with
            {
                Active = Parse(style.Active) is { } active ? Format(active) : Default.Active,
                Background = Parse(style.Background) is { } background ? Format(background) : Default.Background,
                Diameter = Math.Clamp(style.Diameter, MinDiameter, MaxDiameter),
                HoverExpansion = Math.Clamp(style.HoverExpansion, MinExpansion, MaxExpansion)
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }

        try
        {
            var legacy = File.ReadAllText(Path.Combine(root, "preview-indicator-color.txt")).Trim();
            if (Parse(legacy) is { } active) return Default with { Active = Format(active) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return Default;
    }

    internal static Color? Parse(string? text) => text is { Length: 7 } && text[0] == '#' &&
        uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb)
        ? Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : null;

    internal static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

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

    private static string FilePath(string root) => Path.Combine(root, "preview-app-feedback.json");
}

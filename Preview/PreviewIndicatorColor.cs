using System.Globalization;
using Windows.UI;

namespace AITaskbar;

internal static class PreviewIndicatorColor
{
    private static string FilePath(string root) => Path.Combine(root, "preview-indicator-color.txt");

    public static Color? Read(string root)
    {
        try
        {
            string text = File.ReadAllText(FilePath(root)).Trim();
            if (text.Length == 7 && text[0] == '#' &&
                uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
                return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    public static void Save(string root, Color? color)
    {
        Directory.CreateDirectory(root);
        string file = FilePath(root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, color is { } value ? Format(value) : "");
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

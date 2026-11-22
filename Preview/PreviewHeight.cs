using System.Globalization;

namespace AITaskbar;

internal static class PreviewHeight
{
    internal const int Minimum = 10, Default = 50, Maximum = 60;

    internal static int Clamp(int height) => Math.Clamp(height, Minimum, Maximum);

    private static string FilePath(string root) => Path.Combine(root, "preview-height.txt");

    internal static int? Read(string root)
    {
        try
        {
            if (int.TryParse(File.ReadAllText(FilePath(root)), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height)
                && height > 0)
                return Clamp(height);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return null;
    }

    internal static void Save(string root, int? height)
    {
        Directory.CreateDirectory(root);
        string file = FilePath(root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, height?.ToString(CultureInfo.InvariantCulture) ?? "auto");
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

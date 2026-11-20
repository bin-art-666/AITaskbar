using System.Globalization;

namespace AITaskbar;

internal static class PreviewIconSize
{
    public const int Minimum = 24, Maximum = 36, Default = 32;

    private static string FilePath(string root) => Path.Combine(root, "preview-icon-size.txt");

    public static int Read(string root)
    {
        try
        {
            if (int.TryParse(File.ReadAllText(FilePath(root)), NumberStyles.Integer, CultureInfo.InvariantCulture, out int size)
                && size is >= Minimum and <= Maximum)
                return size;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return Default;
    }

    public static void Save(string root, int size)
    {
        Directory.CreateDirectory(root);
        string file = FilePath(root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, size.ToString(CultureInfo.InvariantCulture));
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

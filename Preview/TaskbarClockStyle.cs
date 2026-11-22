namespace AITaskbar;

internal readonly record struct TaskbarClockStyle(int TimeWeight, int DateWeight)
{
    internal static TaskbarClockStyle Default => new(400, 400);
    private static string FilePath(string root) => Path.Combine(root, "taskbar-clock-weight.txt");

    internal static TaskbarClockStyle Read(string root)
    {
        try
        {
            if (File.ReadAllText(FilePath(root)).Split(',') is [var time, var date])
                return new(Parse(time), Parse(date));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return Default;
    }

    private static int Parse(string value) => value switch { "500" => 500, "600" => 600, _ => 400 };

    internal void Save(string root)
    {
        Directory.CreateDirectory(root);
        string file = FilePath(root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, FormattableString.Invariant($"{TimeWeight},{DateWeight}"));
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

using System.Text.Json;

namespace AITaskbar;

internal sealed class PreviewIconPreferences(string file)
{
    private HashSet<string> original = Read(file);

    public IReadOnlyCollection<string> OriginalExecutables => original;
    public bool UsesOriginal(string executable) => original.Contains(executable);

    public void SetOriginal(string executable, bool useOriginal)
    {
        var next = new HashSet<string>(original, StringComparer.OrdinalIgnoreCase);
        if (!(useOriginal ? next.Add(executable) : next.Remove(executable))) return;

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next));
            File.Move(temporary, file, overwrite: true);
            original = next;
        }
        finally { File.Delete(temporary); }
    }

    private static HashSet<string> Read(string file)
    {
        try
        {
            return new(JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file)) ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }
}

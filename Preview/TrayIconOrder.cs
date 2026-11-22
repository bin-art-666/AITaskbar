using System.Text.Json;

namespace AITaskbar;

internal sealed class TrayIconOrder(string file)
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private string[] order = Read(file);

    internal WindowsNotificationArea.TrayIcon[] Sort(IEnumerable<WindowsNotificationArea.TrayIcon> icons)
    {
        var ranks = new Dictionary<string, int>(order.Length, Comparer);
        for (int i = 0; i < order.Length; i++) ranks.Add(order[i], i);
        return icons.OrderBy(icon => ranks.GetValueOrDefault(icon.OrderKey, int.MaxValue)).ToArray();
    }

    internal void Move(IEnumerable<string> displayed, string source, string target, bool after)
    {
        if (Comparer.Equals(source, target)) return;
        // Retain the positions of applications that are currently closed or hidden.
        var next = order.Concat(displayed).Distinct(Comparer).ToList();
        if (next.RemoveAll(key => Comparer.Equals(key, source)) == 0) return;
        int destination = next.FindIndex(key => Comparer.Equals(key, target));
        if (destination < 0) return;
        next.Insert(destination + (after ? 1 : 0), source);

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next));
            File.Move(temporary, file, overwrite: true);
            order = next.ToArray();
        }
        finally { File.Delete(temporary); }
    }

    private static string[] Read(string file)
    {
        try
        {
            return (JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file)) ?? [])
                .Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(Comparer).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }
}

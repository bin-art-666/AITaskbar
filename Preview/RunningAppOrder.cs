namespace AITaskbar;

internal sealed class RunningAppOrder
{
    private readonly List<string> keys = [];

    internal AppGroup[] Apply(AppGroup[] groups)
    {
        var running = groups.Where(group => group.Pin is null).ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);
        keys.RemoveAll(key => !running.ContainsKey(key));
        var existing = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            if (group.Pin is null && existing.Add(group.Key)) keys.Add(group.Key);
        return [.. groups.TakeWhile(group => group.Pin is not null), .. keys.Select(key => running[key])];
    }

    internal bool Move(string source, string target)
    {
        int from = keys.FindIndex(key => StringComparer.OrdinalIgnoreCase.Equals(key, source));
        int to = keys.FindIndex(key => StringComparer.OrdinalIgnoreCase.Equals(key, target));
        if (from < 0 || to < 0 || from == to) return false;
        var key = keys[from];
        keys.RemoveAt(from);
        keys.Insert(to, key);
        return true;
    }
}

namespace AITaskbar;

internal sealed record AppGroup(string Key, string Name, string? Executable, PinnedApp? Pin, IReadOnlyList<RunningWindow> Windows);

internal static class AppGroups
{
    public static AppGroup[] Build(IReadOnlyList<PinnedApp> pins, IReadOnlyList<RunningWindow> windows)
    {
        var running = windows.GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<AppGroup>();
        foreach (var pin in pins)
        {
            var key = "exe:" + pin.Executable;
            running.Remove(key, out var members);
            result.Add(new(key, pin.Name, pin.Executable, pin, members ?? []));
        }
        foreach (var (key, members) in running)
        {
            var first = members[0];
            var name = key.StartsWith("exe:", StringComparison.Ordinal)
                ? Path.GetFileNameWithoutExtension(first.Executable) : first.Title;
            result.Add(new(key, name ?? first.Title, first.Executable, null, members));
        }
        return result.ToArray();
    }

    private static string Key(RunningWindow window)
    {
        var path = window.Executable;
        if (string.Equals(Path.GetFileName(path), "ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return "window:" + window.Handle;
        return path is null ? "process:" + window.ProcessId : "exe:" + path;
    }
}

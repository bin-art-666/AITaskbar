namespace AITaskbar;

internal sealed record AppGroup(string Key, string Name, string? Executable, PinnedApp? Pin, IReadOnlyList<RunningWindow> Windows)
{
    internal string? PinAppId => Pin?.AppId ?? (Windows.Count > 0 ? Windows[0].PinAppId : null);
    internal string? IconExecutable => Windows.Count > 0
        ? Windows[0].IconExecutable ?? (Pin?.AppId is not null ? Windows[0].Executable : Executable)
        : Executable;
    internal string? IconAppId => (Windows.Count > 0 ? Windows[0].IconAppId : null) ??
        (StringComparer.OrdinalIgnoreCase.Equals(PinAppId, PackagedAppPin.SettingsId) ? null : PinAppId);
}

internal static class AppGroups
{
    public static AppGroup[] Build(IReadOnlyList<PinnedApp> pins, IReadOnlyList<RunningWindow> windows)
    {
        var legacyPaths = pins.Where(pin => pin.AppId is null).Select(pin => pin.Executable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = windows.GroupBy(window => Key(window, legacyPaths), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<AppGroup>();
        foreach (var pin in pins)
        {
            var key = pin.Key;
            running.Remove(key, out var members);
            result.Add(new(key, pin.Name, pin.Executable, pin, members ?? []));
        }
        foreach (var (key, members) in running)
        {
            var first = members[0];
            var name = PackagedAppPin.Name(first.PinAppId) ?? (key.StartsWith("exe:", StringComparison.Ordinal)
                ? Path.GetFileNameWithoutExtension(first.Executable) : first.Title);
            result.Add(new(key, name ?? first.Title, first.Executable, null, members));
        }
        return result.ToArray();
    }

    private static string Key(RunningWindow window, HashSet<string> legacyPaths)
    {
        var path = window.Executable;
        if (window.PinAppId is { } appId && (path is null || !legacyPaths.Contains(path))) return "app:" + appId;
        if (RunningWindows.IsFrameHost(path))
            return "window:" + window.Handle;
        return path is null ? "process:" + window.ProcessId : "exe:" + path;
    }
}

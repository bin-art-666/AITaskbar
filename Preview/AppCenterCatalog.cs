using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AITaskbar;

internal sealed record AppCenterEntry(string Key, string Name, string? Executable, string? IconAppId,
    string? ShellId = null, string? ActivationId = null, bool SeparateIcon = false, string? LaunchTarget = null)
{
    internal void Launch()
    {
        if (ShellId is not null) AppCenterCatalog.Launch(ShellId);
        else if (ActivationId is not null) PackagedAppPin.Launch(ActivationId);
        else if ((LaunchTarget ?? Executable) is { } target)
        {
            if (AppCenterCatalog.IsMissingLocalTarget(target)) throw new FileNotFoundException(null, target);
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }
}

internal static class AppCenterCatalog
{
    private const string AppsFolder = "shell:AppsFolder";
    private static Task<AppCenterEntry[]>? catalog;
    private static long catalogRequestedAt;

    internal static Task<AppCenterEntry[]> ReadAsync()
    {
        long now = Environment.TickCount64;
        if (catalog is { } current && (!current.IsCompleted ||
            current.IsCompletedSuccessfully && now - catalogRequestedAt < 20_000))
            return current;
        catalogRequestedAt = now;
        return catalog = RunOnShellThread(Read, "AITaskbar application catalog");
    }

    internal static void Invalidate() => catalog = null;

    internal static Task<T> RunOnShellThread<T>(Func<T> read, string name)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(read()); }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    private static AppCenterEntry[] Read()
    {
        using var shell = new ShellFolder();
        dynamic items = shell.Folder.Items();
        try
        {
            var apps = new List<AppCenterEntry>();
            int count = items.Count;
            for (int index = 0; index < count; index++)
            {
                dynamic? item = items.Item(index);
                if (item is null) continue;
                try
                {
                    string? id = item.Path;
                    string? name = item.Name;
                    if (string.IsNullOrWhiteSpace(id) || id.StartsWith("::{", StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(name)) continue;
                    string? target = ReadLaunchTarget(item, id);
                    if (IsMissingLocalTarget(target)) continue;
                    var exe = ExecutablePath(target) ?? ExecutablePath(id);
                    if (StringComparer.OrdinalIgnoreCase.Equals(id, PackagedAppPin.SettingsId))
                        exe = RunningWindows.SystemSettingsExecutable;
                    string? iconId = id.Contains('!') || exe is null ? id : null;
                    if (StringComparer.OrdinalIgnoreCase.Equals(id, PackagedAppPin.SettingsId)) iconId = null;
                    apps.Add(new("shell:" + id, name, exe, iconId, id, LaunchTarget: target));
                }
                catch (COMException) { }
                finally { Marshal.ReleaseComObject(item); }
            }
            var unique = apps.DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase).ToArray();
            var sharedTargets = unique.Where(app => app.Executable is not null && app.IconAppId is null)
                .GroupBy(app => app.Executable!, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Shortcuts for different apps can point to the same host, for example WSLg.
            return unique.Select(app => app.IconAppId is null && app.Executable is { } exe && sharedTargets.Contains(exe)
                ? app with { IconAppId = app.ShellId, SeparateIcon = true }
                : app).ToArray();
        }
        finally { Marshal.ReleaseComObject(items); }
    }

    internal static void Launch(string shellId)
    {
        using var shell = new ShellFolder();
        dynamic? item = shell.Folder.ParseName(shellId);
        if (item is null) throw new FileNotFoundException();
        try
        {
            string? target = ReadLaunchTarget(item, shellId);
            if (IsMissingLocalTarget(target)) throw new FileNotFoundException(null, target);
            item.InvokeVerb();
        }
        finally { Marshal.ReleaseComObject(item); }
    }

    private static string? ReadLaunchTarget(dynamic item, string id) =>
        FullPath(item.ExtendedProperty("System.Link.TargetParsingPath") as string) ?? FullPath(id);

    private static string? FullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        return Path.IsPathFullyQualified(path) ? path : null;
    }

    private static string? ExecutablePath(string? path) =>
        path is not null && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ? path : null;

    internal static bool IsMissingLocalTarget(string? target)
    {
        // Windows shell items, network paths and offline drives are not evidence of a broken shortcut.
        if (target is not { Length: >= 3 } || !char.IsAsciiLetter(target[0]) || target[1] != ':' ||
            target[2] is not ('\\' or '/')) return false;
        try
        {
            var drive = new DriveInfo(target[..3]);
            if (drive.DriveType != DriveType.Fixed) return false;
            File.GetAttributes(target);
            return false;
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            try { return new DriveInfo(target[..3]).IsReady; }
            catch (IOException) { return false; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class ShellFolder : IDisposable
    {
        private readonly dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        internal dynamic Folder { get; }

        internal ShellFolder()
        {
            try { Folder = shell.NameSpace(AppsFolder) ?? throw new InvalidOperationException("无法读取 Windows 应用列表。"); }
            catch { Marshal.ReleaseComObject(shell); throw; }
        }

        public void Dispose()
        {
            Marshal.ReleaseComObject(Folder);
            Marshal.ReleaseComObject(shell);
        }
    }
}

internal sealed class AppCenterOrder
{
    private readonly string file;
    private string[] keys;

    internal AppCenterOrder(string root)
    {
        file = Path.Combine(root, "app-center-order.json");
        try { keys = JsonSerializer.Deserialize<string[]>(File.ReadAllBytes(file)) ?? []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { keys = []; }
    }

    internal AppCenterEntry[] Arrange(IEnumerable<AppCenterEntry> alphabetical)
    {
        var ranks = keys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((key, index) => (key, index)).ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
        return alphabetical.OrderBy(app => ranks.GetValueOrDefault(app.Key, int.MaxValue)).ToArray();
    }

    internal void Save(IEnumerable<AppCenterEntry> apps)
    {
        var next = apps.Select(app => app.Key).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next));
            File.Move(temporary, file, overwrite: true);
            keys = next;
        }
        finally { File.Delete(temporary); }
    }
}

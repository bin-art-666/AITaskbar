using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AITaskbar;

internal sealed record AppCenterEntry(string Key, string Name, string? Executable, string? IconAppId,
    string? ShellId = null, string? ActivationId = null)
{
    internal void Launch()
    {
        if (ShellId is not null) AppCenterCatalog.Launch(ShellId);
        else if (ActivationId is not null) PackagedAppPin.Launch(ActivationId);
        else if (Executable is not null)
        {
            using var process = Process.Start(new ProcessStartInfo(Executable) { UseShellExecute = true });
        }
    }
}

internal static class AppCenterCatalog
{
    private const string AppsFolder = "shell:AppsFolder";

    internal static Task<AppCenterEntry[]> ReadAsync()
    {
        var result = new TaskCompletionSource<AppCenterEntry[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { result.SetResult(Read()); }
            catch (Exception error) { result.SetException(error); }
        }) { IsBackground = true, Name = "AITaskbar application catalog" };
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
                    string? target = item.ExtendedProperty("System.Link.TargetParsingPath") as string;
                    var exe = ExecutablePath(target) ?? ExecutablePath(id);
                    if (StringComparer.OrdinalIgnoreCase.Equals(id, PackagedAppPin.SettingsId))
                        exe = RunningWindows.SystemSettingsExecutable;
                    string? iconId = id.Contains('!') || exe is null ? id : null;
                    if (StringComparer.OrdinalIgnoreCase.Equals(id, PackagedAppPin.SettingsId)) iconId = null;
                    apps.Add(new("shell:" + id, name, exe, iconId, id));
                }
                catch (COMException) { }
                finally { Marshal.ReleaseComObject(item); }
            }
            return apps.DistinctBy(app => app.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { Marshal.ReleaseComObject(items); }
    }

    internal static void Launch(string shellId)
    {
        using var shell = new ShellFolder();
        dynamic? item = shell.Folder.ParseName(shellId);
        if (item is null) throw new InvalidOperationException("此应用已移动或卸载。");
        try { item.InvokeVerb(); }
        finally { Marshal.ReleaseComObject(item); }
    }

    private static string? ExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim('"');
        return Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
            ? path : null;
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

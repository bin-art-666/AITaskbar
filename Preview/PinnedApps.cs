using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AITaskbar;

internal sealed record PinnedApp(string Name, string Executable,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AppId = null)
{
    internal string Key => AppId is null ? "exe:" + Executable : "app:" + AppId;
}

internal sealed class PinnedApps(string file)
{
    internal event Action? Changed;
    internal IReadOnlyList<PinnedApp> Items { get; private set; } = File.Exists(file)
        ? (JsonSerializer.Deserialize<PinnedApp[]>(File.ReadAllBytes(file)) ?? [])
            .Where(pin => pin is not null && !string.IsNullOrWhiteSpace(pin.Name) && (pin.AppId is null
                ? Path.IsPathFullyQualified(pin.Executable ?? "") : PackagedAppPin.Name(pin.AppId) is not null))
            .DistinctBy(pin => pin.Key, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    public void Add(string executable)
    {
        if (!RunningWindows.CanPin(executable))
            throw new InvalidOperationException("请选择可启动的桌面应用程序（.exe）。");
        if (Items.Any(pin => SameProgram(pin.Key, "exe:" + executable))) return;
        var name = FileVersionInfo.GetVersionInfo(executable).FileDescription;
        Save([.. Items, new(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(executable) : name.Trim(), executable)]);
    }

    public void Add(string appId, string? executable)
    {
        if (PackagedAppPin.Name(appId) is not { } name) throw new InvalidOperationException("应用无法固定。");
        var pin = new PinnedApp(name, executable ?? "", appId);
        if (!Items.Any(item => SameProgram(item.Key, pin.Key))) Save([.. Items, pin]);
    }

    public void Remove(PinnedApp pin) => Save(Items.Where(item => !SameProgram(item.Key, pin.Key)).ToArray());

    public bool Move(PinnedApp source, PinnedApp target, bool after)
    {
        if (SameProgram(source.Key, target.Key)) return false;
        var next = Items.ToList();
        int sourceIndex = next.FindIndex(pin => SameProgram(pin.Key, source.Key));
        if (sourceIndex < 0) return false;
        var moved = next[sourceIndex];
        next.RemoveAt(sourceIndex);
        int targetIndex = next.FindIndex(pin => SameProgram(pin.Key, target.Key));
        if (targetIndex < 0) return false;
        int insertion = targetIndex + (after ? 1 : 0);
        if (insertion == sourceIndex) return false;
        next.Insert(insertion, moved);
        Save(next.ToArray());
        return true;
    }

    private void Save(PinnedApp[] next)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(next));
            File.Move(temporary, file, overwrite: true);
            Items = next;
        }
        finally { File.Delete(temporary); }
        Changed?.Invoke();
    }

    public static bool SameProgram(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

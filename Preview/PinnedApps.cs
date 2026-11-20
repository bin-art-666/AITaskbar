using System.Diagnostics;
using System.Text.Json;

namespace AITaskbar;

internal sealed record PinnedApp(string Name, string Executable);

internal sealed class PinnedApps(string file)
{
    internal IReadOnlyList<PinnedApp> Items { get; private set; } = File.Exists(file)
        ? (JsonSerializer.Deserialize<PinnedApp[]>(File.ReadAllBytes(file)) ?? [])
            .Where(pin => pin is not null && Path.IsPathFullyQualified(pin.Executable ?? "") && !string.IsNullOrWhiteSpace(pin.Name))
            .DistinctBy(pin => pin.Executable, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    public bool Contains(string executable) => Items.Any(pin => SameProgram(pin.Executable, executable));

    public void Add(string executable)
    {
        if (!RunningWindows.CanPin(executable))
            throw new InvalidOperationException("请选择可启动的桌面应用程序（.exe）。");
        if (Contains(executable)) return;
        var name = FileVersionInfo.GetVersionInfo(executable).FileDescription;
        Save([.. Items, new(string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(executable) : name.Trim(), executable)]);
    }

    public void Remove(string executable) => Save(Items.Where(pin => !SameProgram(pin.Executable, executable)).ToArray());

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
    }

    public static bool SameProgram(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

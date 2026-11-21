using System.Text.Json;

namespace AITaskbar;

internal sealed class SettingsPaneWidths
{
    private readonly string file;
    private readonly Dictionary<SettingsSection, double> widths = [];
    private readonly Action<string> report;

    internal SettingsPaneWidths(string root, Action<string> report)
    {
        file = Path.Combine(root, "settings-pane-widths.json");
        this.report = report;
        try
        {
            foreach (var (section, width) in JsonSerializer.Deserialize<Dictionary<SettingsSection, double>>(File.ReadAllText(file)) ?? [])
                if (Enum.IsDefined(section) && double.IsFinite(width)) widths[section] = width;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    internal double Get(SettingsSection section, double fallback) => widths.GetValueOrDefault(section, fallback);

    internal void Set(SettingsSection section, double width)
    {
        if (widths.TryGetValue(section, out var previous) && width == previous) return;
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var next = new Dictionary<SettingsSection, double>(widths) { [section] = width };
            File.WriteAllText(temporary, JsonSerializer.Serialize(next));
            File.Move(temporary, file, overwrite: true);
            widths[section] = width;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            report("无法记住分栏宽度。");
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

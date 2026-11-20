namespace Halo;

internal sealed partial class StationStore
{
    internal string? FolderColorOverride(StationItem item) => item.Kind == StationKind.Folder
        ? data.FolderColors?.GetValueOrDefault(item.Value.ToUpperInvariant()) : null;

    internal async Task SetFolderColor(StationItem item, StationItem? root, string? color)
    {
        if (item.Kind != StationKind.Folder || color != null && !UiAppearance.ValidRingColor(color))
            throw new IOException("请选择文件夹并输入有效的 #RRGGBB 颜色。");
        await desktopGate.WaitAsync();
        try {
            await Change(document => {
                var anchor = root ?? item;
                if (!document.Items.Any(entry => entry.Id == anchor.Id && entry.Kind == StationKind.Folder
                        && SamePath(entry.Value, anchor.Value)) || !Directory.Exists(ValidateFolderPath(anchor, item.Value)))
                    throw new IOException("文件夹已移动或移除。");
                string key = item.Value.ToUpperInvariant();
                string? value = color?.ToUpperInvariant();
                if (document.FolderColors?.GetValueOrDefault(key) == value) return document;
                var colors = new Dictionary<string, string>(document.FolderColors ?? []);
                if (value == null) colors.Remove(key);
                else colors[key] = value;
                return document with { FolderColors = colors.Count == 0 ? null : colors };
            });
        } finally { desktopGate.Release(); }
    }

    static void ValidateFolderColors(Dictionary<string, string>? colors)
    {
        if (colors == null) return;
        if (colors.Count > 100000 || colors.Any(entry => entry.Key.Length > 32767
            || !Path.IsPathFullyQualified(entry.Key) || entry.Key.Any(char.IsControl)
            || Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Key)) != entry.Key
            || entry.Key != entry.Key.ToUpperInvariant() || !UiAppearance.ValidRingColor(entry.Value)))
            throw new InvalidDataException("文件夹颜色记录无效。");
    }

    static Dictionary<string, string>? PruneFolderColors(StationDocument document)
    {
        if (document.FolderColors is not { } colors) return null;
        var roots = document.Items.Concat(document.Trash ?? []).Where(item => item.Kind == StationKind.Folder)
            .Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Tracked(string value) {
            for (string? current = value; current != null; current = Path.GetDirectoryName(current))
                if (roots.Contains(current)) return true;
            return false;
        }
        var retained = colors.Where(entry => Tracked(entry.Key)).ToDictionary();
        return retained.Count == 0 ? null : retained;
    }

    // Follow the same sequence as the file transaction, including temporary paths used by case-only renames.
    static Dictionary<string, string>? MoveFolderColors(Dictionary<string, string>? colors, IEnumerable<FlatMove> moves)
    {
        if (colors == null) return null;
        var result = new Dictionary<string, string>(colors);
        foreach (var move in moves.Where(move => move.Kind == StationKind.Folder)) {
            string from = Path.TrimEndingDirectorySeparator(move.From).ToUpperInvariant();
            var affected = result.Where(entry => entry.Key == from || Below(entry.Key, from)).ToArray();
            foreach (var entry in affected) result.Remove(entry.Key);
            foreach (var entry in affected)
                result[(move.To + entry.Key[from.Length..]).ToUpperInvariant()] = entry.Value;
        }
        return result;
    }
}

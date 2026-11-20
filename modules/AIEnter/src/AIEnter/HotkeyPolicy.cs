namespace AIEnter;

internal static class HotkeyPolicy
{
    static readonly string[] ModifierOrder = ["Ctrl", "Alt", "Shift", "Win"];
    internal static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value)) return true;
        var parts = value.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace)) return false;
        var mods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts[..^1])
        {
            string? mod = part.ToLowerInvariant() switch
            { "ctrl" or "control" => "Ctrl", "alt" => "Alt", "shift" => "Shift", "win" or "windows" => "Win", _ => null };
            if (mod is null || !mods.Add(mod)) return false;
        }
        if (!mods.Contains("Ctrl") && !mods.Contains("Alt") && !mods.Contains("Win")) return false;
        string key = parts[^1].ToUpperInvariant();
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') { }
        else if (key.Length >= 2 && key[0] == 'F' && int.TryParse(key[1..], out int fn) && fn is >= 1 and <= 24) key = "F" + fn;
        else if (key is "SPACE" or "SPACEBAR" or "TAB" or "ESC" or "ESCAPE" or "ENTER" or "INSERT" or "DELETE" or "HOME" or "END" or "PAGEUP" or "PAGEDOWN" or "LEFT" or "RIGHT" or "UP" or "DOWN")
            key = key switch { "ESCAPE" => "ESC", "SPACEBAR" => "SPACE", _ => key };
        else return false;
        normalized = string.Join('+', ModifierOrder.Where(mods.Contains).Append(key));
        return true;
    }
}

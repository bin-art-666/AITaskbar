using Microsoft.Win32;
using System.Text.Json;

namespace AITaskbar;

internal static class BrowserRecentHostRegistration
{
    internal static void EnsureInstalled(string root, RecentBrowser browser)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "BrowserCompanions", "Host", "AITaskbar.EdgeRecentHost.exe");
        if (!File.Exists(executable)) return;

        var (browserKey, name, origin) = browser == RecentBrowser.Chrome
            ? (@"Google\Chrome", "com.aitaskbar.chrome_recent", BrowserRecentProtocol.ChromeOrigin)
            : (@"Microsoft\Edge", "com.aitaskbar.edge_recent", BrowserRecentProtocol.EdgeOrigin);
        var registryPath = $@"Software\{browserKey}\NativeMessagingHosts\{name}";
        var directory = Path.Combine(root, "BrowserCompanions");
        var manifest = Path.Combine(directory, name + ".json");
        using (var existing = Registry.CurrentUser.OpenSubKey(registryPath))
        {
            if (existing?.GetValue(null) is string installed &&
                !string.Equals(installed, manifest, StringComparison.OrdinalIgnoreCase) && File.Exists(installed)) return;
        }

        var contents = JsonSerializer.Serialize(new
        {
            name,
            description = $"AITaskbar {browser} recent sessions",
            path = executable,
            type = "stdio",
            allowed_origins = new[] { origin }
        });
        Directory.CreateDirectory(directory);
        if (!File.Exists(manifest) || File.ReadAllText(manifest) != contents)
        {
            var temporary = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, contents);
                File.Move(temporary, manifest, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }

        using var entry = Registry.CurrentUser.CreateSubKey(registryPath);
        if (!string.Equals(entry.GetValue(null) as string, manifest, StringComparison.OrdinalIgnoreCase))
            entry.SetValue(null, manifest);
    }
}

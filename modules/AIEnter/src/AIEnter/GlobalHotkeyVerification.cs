using System.Text.Json;

namespace AIEnter;

public partial class App
{
    async Task VerifyGlobalHotkey()
    {
        string path = Path.Combine(DataDirectory, "hotkey-result.json");
        try
        {
            if (!Environment.GetCommandLineArgs().Contains("--data-dir")
                || DataDirectory.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIEnter"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shortcut verification requires an isolated data directory.");
            string reservedKey = "Ctrl+Alt+Shift+F24";
            SaveOptions(Ai.Options.Data with { Hotkey = reservedKey });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(aiLifetimeWindow!);
            bool conflictDetected = false;
            try { SaveOptions(Ai.Options.Data with { Hotkey = "Win+L" }); }
            catch (InvalidOperationException) { conflictDetected = Ai.Options.Data.Hotkey == reservedKey; }
            if (!conflictDetected) throw new Exception("Reserved system shortcut did not report a conflict.");
            bool previousStillHeld = false;
            try { using var probe = GlobalHotkey.Prepare(hwnd, reservedKey, static () => { }); }
            catch (InvalidOperationException) { previousStillHeld = true; }
            if (!previousStillHeld) throw new Exception("A failed shortcut replacement released the previous registration.");
            SaveOptions(Ai.Options.Data with { Hotkey = "" });
            using var reusable = GlobalHotkey.Prepare(hwnd, reservedKey, static () => { });
            if (reusable is null) throw new Exception("Cleared shortcut did not release its system registration.");
            reusable.Dispose();
            File.WriteAllText(path, JsonSerializer.Serialize(new { passed = true, registered = true, reservedConflictDetected = true,
                previousShortcutPreservedOnFailure = true, clearedShortcutReleased = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1; Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
        }
        finally { QuitStandaloneAi(); }
        await Task.CompletedTask;
    }
}

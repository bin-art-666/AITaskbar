using System.Text.Json;
using Microsoft.UI.Windowing;

namespace AIEnter;

internal sealed partial class AiWindow
{
    async Task VerifyToggleModes()
    {
        var saved = options.Data;
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        try
        {
            foreach (bool floating in new[] { true, false })
            {
                options.Save(options.Data with { FloatingPanel = floating, Pinned = true });
                await Open(display, dockTop);
                var original = current!; var size = AppWindow.Size;
                await ToggleFromTaskbar(display);
                Check(!IsOpen, "Taskbar did not hide an open/pinned window.");
                await ToggleFromTaskbar(display);
                Check(IsOpen && Detached == !floating && ReferenceEquals(original, current) && AppWindow.Size == size, "Toggle changed mode, size or browser.");
                var presenter = (OverlappedPresenter)AppWindow.Presenter;
                presenter.Minimize(); await Task.Delay(120);
                Check(presenter.State == OverlappedPresenterState.Minimized, "Minimize fixture failed.");
                await ToggleFromTaskbar(display); await Task.Delay(120);
                Check(IsOpen && presenter.State != OverlappedPresenterState.Minimized && ReferenceEquals(original, current), "Minimized toggle failed to restore the same browser.");
            }
            var point = new Native.Point { X = 100, Y = 200 };
            ObserveOutsidePress(1000, point); Hide();
            await ToggleFromTaskbar(display, 1000, point);
            Check(!IsOpen, "Same-gesture outside dismissal reopened the window.");
            await ToggleFromTaskbar(display, 1500, point);
            Check(IsOpen, "A later click did not open the hidden window.");
        }
        finally { options.Save(saved); await Open(display, dockTop); }
    }
}

public partial class App
{
    async Task VerifyToggleEntry()
    {
        try
        {
            if (!Environment.GetCommandLineArgs().Contains("--data-dir") || DataDirectory.Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIEnter"), StringComparison.OrdinalIgnoreCase)) throw new Exception("Toggle verification requires isolated data.");
            Ai.Options.Save(Ai.Options.Data with { Provider = "chatgpt", DefaultProvider = "deepseek", Pinned = true });
            var opening = OpenAiWindow();
            await ToggleAiWindow(); await opening;
            if (Ai.IsOpen) throw new Exception("Toggle could not hide a window while the browser was initializing.");
            async Task Signal(bool open)
            {
                await Task.Delay(350);
                var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--ai"); start.ArgumentList.Add("--toggle-ai");
                start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Path.GetDirectoryName(DataDirectory)!);
                using var process = System.Diagnostics.Process.Start(start); if (process != null) await process.WaitForExitAsync();
                for (int attempt = 0; attempt < 100 && Ai.IsOpen != open; attempt++) await Task.Delay(30);
                if (Ai.IsOpen != open) throw new Exception("Cross-process toggle did not reach the requested state.");
            }
            await Signal(true); await Signal(false); await Signal(true);
            if (Ai.Options.Data.Provider != "chatgpt") throw new Exception("Toggle reset the current AI to the startup default.");
            await Task.Delay(350); await ToggleAiWindow(); await ToggleAiWindow();
            if (Ai.IsOpen) throw new Exception("Debounce accepted a duplicate toggle.");
            File.WriteAllText(Path.Combine(DataDirectory, "toggle-result.json"), JsonSerializer.Serialize(new { passed = true, localPageOnly = true, crossProcessToggle = true, loadingToggle = true, currentAiPreserved = true, debounce = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1; Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(Path.Combine(DataDirectory, "toggle-result.json"), JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
        }
        finally { QuitStandaloneAi(); }
    }
}

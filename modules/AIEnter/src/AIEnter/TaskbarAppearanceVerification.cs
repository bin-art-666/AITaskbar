using System.Text.Json;

namespace AIEnter;

public partial class App
{
    async Task VerifyTaskbarAppearance(bool abruptExit)
    {
        string resultPath = Path.Combine(DataDirectory, abruptExit ? "taskbar-crash-result.json" : "taskbar-result.json");
        try
        {
            string live = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIEnter"));
            if (!Environment.GetCommandLineArgs().Contains("--data-dir") || DataDirectory.Equals(live, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Taskbar checks require an isolated --data-dir.");
            Directory.CreateDirectory(DataDirectory);
            // Keep focus transitions from hiding the panel during appearance-only checks.
            Ai.Options.Save(Ai.Options.Data with { Pinned = true });
            string native = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".tools", "windhawk", "AppData", "Engine", "ModsWritable", "aienter-taskbar-ai.ini"));
            async Task WaitFor(int glow, int size = UiDefaults.TaskbarIconSize)
            {
                var expectedAppearance = Appearance;
                for (int attempt = 0; attempt < 80; attempt++)
                {
                    try
                    {
                        var fields = File.ReadAllLines(native).Where(line => line.Contains('=')).Select(line => line.Split('=', 2))
                            .GroupBy(pair => pair[0]).ToDictionary(group => group.Key, group => group.Last()[1]);
                        if (fields.GetValueOrDefault("GlowActive") == glow.ToString() && fields.GetValueOrDefault("ArtworkSize") == size.ToString()
                            && fields.GetValueOrDefault("ScreenshotDoubleClickEnabled") == (expectedAppearance.ScreenshotDoubleClick ? "1" : "0")
                            && int.TryParse(fields.GetValueOrDefault("DoubleClickMilliseconds"), out int doubleClickMilliseconds) && doubleClickMilliseconds > 0
                            && fields.GetValueOrDefault("ColorFlowSpeed") == expectedAppearance.FlowSpeed.ToString()
                            && fields.GetValueOrDefault("GlowRange") == expectedAppearance.GlowRange.ToString()
                            && fields.GetValueOrDefault("GlowIntensity") == expectedAppearance.GlowIntensity.ToString()
                            && fields.GetValueOrDefault("ButtonWidth") == (size + 4).ToString()
                            && fields.GetValueOrDefault("ButtonHeight") == (size + 4).ToString()
                            && fields.GetValueOrDefault("ButtonRoundClipped") == "1"
                            && fields.GetValueOrDefault("GlowOverlayNonInteractive") == "1") return;
                    }
                    catch (IOException) { }
                    await Task.Delay(100);
                }
                throw new InvalidOperationException($"Native appearance did not reach glow={glow}, icon={size}. Open={Ai.IsOpen}; appearance={JsonSerializer.Serialize(Appearance)}; native={File.ReadAllText(native).Split("[SymbolCache]")[0]}");
            }
            await WaitFor(0);
            await OpenAiWindow(); await WaitFor(1);
            if (abruptExit)
            {
                File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = true, localPageOnly = true, abruptExitWithGlowActive = true }));
                Environment.Exit(0); // Test-only: exercise the native process-handle cleanup without normal shutdown.
            }
            Ai.Hide(); await WaitFor(0);
            OpenSettings(); await WaitFor(0);
            PreviewAppearance(new UiAppearance(TaskbarIconSize: 36, GlowRange: 12, GlowIntensity: 100));
            await WaitFor(0, 36);
            await OpenAiWindow(); await WaitFor(1, 36);
            ((AITaskbar.App)Microsoft.UI.Xaml.Application.Current).HideSettings(); await WaitFor(1);
            PreviewAppearance(new UiAppearance(GlowEnabled: false)); await WaitFor(0);
            PreviewAppearance(new UiAppearance(GlowIntensity: 0)); await WaitFor(0);
            PreviewAppearance(new UiAppearance(GlowRange: 0)); await WaitFor(0);
            PreviewAppearance(new UiAppearance(ScreenshotDoubleClick: false)); await WaitFor(1);
            PreviewAppearance(new UiAppearance(FlowSpeed: 0)); await WaitFor(1);
            PreviewAppearance(new UiAppearance(FlowSpeed: 1800)); await WaitFor(1);
            PreviewAppearance(null); await WaitFor(1);
            Ai.Hide(); await WaitFor(0);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = true, localPageOnly = true,
                showLights = true, hideClears = true, settingsOnlyStaysOff = true, iconPreview = true,
                screenshotGesturePreviewAndRollback = true, flowSpeedPreviewAndRollback = true, previewRollback = true, disabledAndZeroClear = true, compactCircle = true, glowDoesNotReserveWidth = true }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
        }
        finally { PreviewAppearance(null); QuitStandaloneAi(); }
    }
}

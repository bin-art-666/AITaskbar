using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Security.Principal;

namespace AIEnter;

public partial class App
{
    EventWaitHandle? aiOpenEvent, aiQuitEvent, settingsEvent;
    RegisteredWaitHandle? aiOpenWait, aiQuitWait, settingsWait;
    Window? aiLifetimeWindow;
    SettingsWindow? settingsWindow;
    bool aiOpening, aiQuitting;
    GlobalHotkey? globalHotkey;
    string? hotkeyWarning;

    void LaunchAiOnly(string[] arguments)
    {
        bool background = arguments.Contains("--background");
        string prefix = AITaskbar.App.EventPrefix("AIEnter", DataDirectory);
        instance = new Mutex(true, prefix, out bool first);
        aiOpenEvent = new(false, EventResetMode.AutoReset, prefix + ".Open");
        aiQuitEvent = new(false, EventResetMode.AutoReset, prefix + ".Quit");
        settingsEvent = new(false, EventResetMode.AutoReset, prefix + ".Settings");
        InitializeToggleEntry(prefix); InitializeScreenshotEntry(prefix);
        if (!first || arguments.Contains("--quit"))
        {
            if (!first && !background)
            {
                if (arguments.Contains("--screenshot")) screenshotEvent!.Set();
                else if (arguments.Contains("--toggle-ai")) SignalToggle(arguments);
                else if (arguments.Contains("--taskbar-drop") || arguments.Contains("--drag-hover"))
                    SignalDropEntry(prefix, arguments.Contains("--taskbar-drop"));
                else (arguments.Contains("--quit") ? aiQuitEvent : arguments.Contains("--settings") ? settingsEvent : aiOpenEvent).Set();
            }
            DisposeScreenshotEntry(); DisposeToggleEntry(); aiOpenEvent.Dispose(); aiQuitEvent.Dispose(); settingsEvent.Dispose(); instance.Dispose();
            Exit(); return;
        }
        PanelTheme.Apply(UiStyle.Data);
        if (!background && !arguments.Any(a => a.StartsWith("--verify-") || a is "--taskbar-drop" or "--drag-hover" or "--toggle-ai" or "--screenshot") && !Ai.Options.ReadOnly)
        {
            var saved = Ai.Options.Data;
            string preferred = saved.DefaultProvider ?? UiDefaults.Provider;
            if (saved.Provider != preferred) Ai.Options.Save(saved with { Provider = preferred });
        }
        StartAppearanceBridge();
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        // A hidden window keeps the WinUI lifetime independent of the toolbar.
        aiLifetimeWindow = new Window { Title = "AIEnter 后台" };
        aiLifetimeWindow.AppWindow.IsShownInSwitchers = false;
        aiLifetimeWindow.AppWindow.Hide();
        nint lifetimeHandle = WinRT.Interop.WindowNative.GetWindowHandle(aiLifetimeWindow);
        try { globalHotkey = GlobalHotkey.Replace(lifetimeHandle, Ai.Options.Data.Hotkey, () => dispatcher.TryEnqueue(() => _ = ToggleAiWindow(false))); }
        catch (Exception error) { hotkeyWarning = error.Message; }
        aiOpenWait = ThreadPool.RegisterWaitForSingleObject(aiOpenEvent,
            (_, _) => dispatcher.TryEnqueue(() => _ = OpenAiWindow()), null, Timeout.Infinite, false);
        aiQuitWait = ThreadPool.RegisterWaitForSingleObject(aiQuitEvent,
            (_, _) => dispatcher.TryEnqueue(QuitStandaloneAi), null, Timeout.Infinite, true);
        settingsWait = ThreadPool.RegisterWaitForSingleObject(settingsEvent,
            (state, timedOut) => dispatcher.TryEnqueue(OpenSettings), null, Timeout.Infinite, false);
        InitializeDropEntry(prefix, dispatcher); RegisterScreenshotEntry(dispatcher);
        toggleWait = ThreadPool.RegisterWaitForSingleObject(toggleEvent!, (_, _) => dispatcher.TryEnqueue(() => _ = ToggleAiWindow()), null, Timeout.Infinite, false);
        if (background) return;
        if (arguments.Contains("--screenshot")) _ = TakeScreenshot();
        else if (arguments.Contains("--taskbar-drop") || Directory.Exists(Path.Combine(DataDirectory, "incoming-drops")) && Directory.EnumerateFiles(Path.Combine(DataDirectory, "incoming-drops"), "*.json").Any()) _ = DrainTaskbarDrops();
        else if (arguments.Contains("--drag-hover")) _ = OpenDropEntry(false);
        else if (arguments.Contains("--verify-toggle")) _ = VerifyToggleEntry();
        else if (arguments.Contains("--verify-hotkey")) _ = VerifyGlobalHotkey();
        else if (arguments.Contains("--verify-ai-sites")) _ = VerifyAiSiteCapabilities();
        else if (arguments.Any(a => a is "--verify-taskbar" or "--verify-taskbar-crash"))
            _ = VerifyTaskbarAppearance(arguments.Contains("--verify-taskbar-crash"));
        else if (arguments.Contains("--toggle-ai")) { SignalToggle(arguments); }
        else if (arguments.Contains("--settings")) OpenSettings();
        else _ = OpenAiWindow();
    }

    internal static bool Relay(string[] arguments, string dataDirectory)
    {
        var prefix = AITaskbar.App.EventPrefix("AIEnter", dataDirectory);
        var action = arguments.Contains("--screenshot") ? "Screenshot"
            : arguments.Contains("--toggle-ai") ? "Toggle"
            : arguments.Contains("--taskbar-drop") ? "Drop"
            : arguments.Contains("--drag-hover") ? "Hover"
            : arguments.Contains("--settings") ? "Settings" : "Open";
        using var signal = AITaskbar.App.WaitForAction(prefix + "." + action);
        if (signal is null) return false;
        if (action == "Toggle")
        {
            using var input = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(prefix + ".ToggleInput");
            using var view = input.CreateViewAccessor(0, 16);
            WriteToggleGesture(arguments, view);
        }
        return signal.Set();
    }

    internal void OpenSettings()
    {
        if (aiQuitting) return;
        settingsWindow ??= new SettingsWindow(Ai.Options, QuitStandaloneAi, SaveOptions, hotkeyWarning);
        settingsWindow.Open();
    }

    void SaveOptions(AiOptions value)
    {
        if (!HotkeyPolicy.TryNormalize(value.Hotkey, out string normalized)) throw new InvalidDataException("快捷键格式无效。");
        value = value with { Hotkey = normalized };
        HotkeyPolicy.TryNormalize(Ai.Options.Data.Hotkey, out string previous);
        GlobalHotkey? candidate = null;
        if (!string.Equals(previous, value.Hotkey, StringComparison.OrdinalIgnoreCase)
            || globalHotkey is null && !string.IsNullOrWhiteSpace(value.Hotkey))
            candidate = GlobalHotkey.Prepare(WinRT.Interop.WindowNative.GetWindowHandle(aiLifetimeWindow!), value.Hotkey,
                () => DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => _ = ToggleAiWindow(false)));
        try
        {
            Ai.Options.Save(value);
            if (candidate is not null) { candidate.Commit(); globalHotkey = candidate; }
            else if (string.IsNullOrWhiteSpace(value.Hotkey)) { globalHotkey?.Dispose(); globalHotkey = null; }
            hotkeyWarning = null;
        }
        catch { candidate?.Dispose(); throw; }
    }

    async Task OpenAiWindow()
    {
        if (aiOpening || aiQuitting) return;
        aiOpening = true;
        try
        {
            var monitors = Native.Monitors();
            await Ai.OpenPreferred(monitors.First(m => m.Primary));
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "ai-launch.log"), $"{DateTimeOffset.Now:O} {error}\n");
        }
        finally { aiOpening = false; }
    }

    void QuitStandaloneAi() => ((AITaskbar.App)Application.Current).RequestExit();

    internal void Stop()
    {
        if (aiQuitting) return;
        aiQuitting = true;
        aiOpenWait?.Unregister(null); aiQuitWait?.Unregister(null); settingsWait?.Unregister(null);
        DisposeScreenshotEntry(); DisposeToggleEntry(); globalHotkey?.Dispose(); globalHotkey = null; DisposeDropEntry(); settingsWindow?.Destroy(); StopAppearanceBridge(); StopAi(); aiLifetimeWindow?.Close();
        aiOpenEvent?.Dispose(); aiQuitEvent?.Dispose(); settingsEvent?.Dispose(); instance?.Dispose();
    }
}

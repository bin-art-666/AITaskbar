using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Security.Cryptography;
using System.Text;

namespace Halo;

public partial class App
{
    internal static App Current => ((AITaskbar.App)Application.Current).HaloModule;
    internal static string DataDirectory { get; private set; } = "";
    internal static SettingsStore Settings { get; private set; } = null!;
    internal static StationStore Station { get; private set; } = null!;
    internal static UiAppearance Appearance => appearancePreview ?? Settings.Data.Appearance;
    static UiAppearance? appearancePreview;
    internal static StationWindow? Panel;
    internal static RingAppearanceBridge? RingBridge;
    readonly List<EventWaitHandle> events = [];
    readonly List<RegisteredWaitHandle> waits = [];
    Mutex? instance;
    Window? lifetime;
    FrameworkElement? taskbarAnchor;
    (ElementTheme Theme, Windows.UI.Color Color)? taskbarPalette;
    bool quitting;
    internal static bool Verification;
    internal void Start(string[] arguments, string dataDirectory)
    {
        DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        bool verifyFlyout = arguments.Contains("--verify-halo-flyout");
        Verification = verifyFlyout || arguments.Contains("--verify-station");
        string prefix = AITaskbar.App.EventPrefix("Halo", DataDirectory);
        instance = new Mutex(true, prefix, out bool first);
        string action = ActionFor(arguments);
        if (!first || action == "Quit")
        {
            if (!first) { using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "." + action); signal.Set(); }
            instance.Dispose(); Application.Current.Exit(); return;
        }
        Settings = new(Path.Combine(DataDirectory, "settings.json")); Station = new(Path.Combine(DataDirectory, "station.json"));
        RingBridge = new(); RingBridge.Publish(Appearance); RingBridge.PublishImportFeedback(null);
        PanelTheme.Apply(new()); lifetime = new Window { Title = "Halo 后台" };
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings.SaveFailed += error => { Log(error); dispatcher.TryEnqueue(() => Panel?.Notify("设置保存失败：" + error.Message, true)); };
        foreach (string command in new[] { "Open", "Toggle", "Settings", "Hover", "Leave", "Drop", "Quit", "Pin", "Detach" })
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, prefix + "." + command); events.Add(signal);
            waits.Add(ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => dispatcher.TryEnqueue(() => Handle(command)), null, Timeout.Infinite, false));
        }
        if (verifyFlyout) { _ = VerifyStationFlyout(arguments.Contains("--verify-halo-flyout-files")); return; }
        if (Verification) { _ = VerifyStation(); return; }
        StartStorageMaintenance(dispatcher);
        Handle(arguments.Contains("--background") ? "Background" : action);
        if (Directory.Exists(Path.Combine(DataDirectory, "incoming-drops"))) _ = DrainDrops();
    }
    private static string ActionFor(string[] arguments) =>
        arguments.Contains("--quit") ? "Quit" : arguments.Contains("--settings") ? "Settings"
        : arguments.Contains("--drag-hover") ? "Hover" : arguments.Contains("--drag-leave") ? "Leave"
        : arguments.Contains("--taskbar-drop") ? "Drop" : arguments.Contains("--pin") ? "Pin" : arguments.Contains("--detach") ? "Detach" : arguments.Contains("--toggle") ? "Toggle" : "Open";

    internal static bool Relay(string[] arguments, string dataDirectory)
    {
        using var signal = AITaskbar.App.WaitForAction(AITaskbar.App.EventPrefix("Halo", dataDirectory) + "." + ActionFor(arguments));
        return signal?.Set() == true;
    }
    internal void Handle(string command)
    {
        if (quitting) return;
        try
        {
            if (command == "Quit") { _ = Quit(); return; }
            if (command == "Settings") { OpenSettings(); return; }
            if (command == "Leave") { Panel?.ScheduleDragDismiss(); return; }
            if (command == "Background") return;
            Panel ??= CreatePanel();
            if (command is "Pin" or "Detach") {
                if (Panel.ModalActive || Panel.DragActive) return;
                if (command == "Pin") Panel.TogglePin(); else Panel.SetDetached(!Panel.Detached);
                Panel.Open(); return;
            }
            if (command == "Toggle") { if (!Panel.ShowPendingDropFailures()) Panel.Toggle(); }
            else if (command == "Drop") _ = DrainDrops();
            else Panel.Open(command != "Hover", command == "Hover");
        }
        catch (Exception e) { Log(e); }
    }
    internal static void Preview(UiAppearance? appearance)
    { appearancePreview = appearance; PanelTheme.ApplyAppearance(Appearance); Panel?.ApplyAppearance(); RingBridge?.Publish(Appearance); }
    internal static void OpenSettings()
        => ((AITaskbar.App)Application.Current).ShowSettings(AITaskbar.SettingsSection.Halo);

    internal SettingsPage CreateSettingsPage(Window host, AITaskbar.SettingsPaneWidths widths)
    {
        Panel ??= CreatePanel();
        var page = new SettingsPage(Panel, host, widths) { Visibility = Visibility.Visible };
        page.Reset();
        return page;
    }
    StationWindow CreatePanel()
    {
        var panel = new StationWindow(taskbarAnchor);
        if (taskbarPalette is { } palette) panel.SetTaskbarPalette(palette.Theme, palette.Color);
        return panel;
    }
    internal void AttachTaskbar(FrameworkElement anchor)
    {
        if (ReferenceEquals(taskbarAnchor, anchor)) return;
        taskbarAnchor = anchor;
        if (Panel is { IsOpen: true }) return;
        Panel?.Destroy();
        Panel = null;
        Settings.UseIntegratedWindowDefaults();
    }
    internal void DetachTaskbar(FrameworkElement anchor)
    {
        if (!ReferenceEquals(taskbarAnchor, anchor)) return;
        taskbarAnchor = null;
        taskbarPalette = null;
        if (Panel?.UsesTaskbarFlyout != true) return;
        Panel.Destroy();
        Panel = null;
    }
    internal void SetTaskbarPalette(ElementTheme theme, Windows.UI.Color color)
    {
        taskbarPalette = (theme, color);
        if (Panel?.UsesTaskbarFlyout == true) Panel.SetTaskbarPalette(theme, color);
    }
    internal static void Log(Exception e)
    { try { Directory.CreateDirectory(DataDirectory); File.AppendAllText(Path.Combine(DataDirectory, "error.log"), $"{DateTimeOffset.Now:o} {e}\n"); } catch { } }
    internal Task Quit() => ((AITaskbar.App)Application.Current).ShutdownAsync();

    internal async Task StopAsync()
    {
        if (quitting || Station is null) return; quitting = true;
        await StopStorageMaintenance();
        if (Panel is { } panel) await panel.PendingSettingsSave;
        await Station.Flush(); Panel?.Destroy();
        try { await Settings.Flush(); } catch (Exception error) { Log(error); }
        RingBridge = null;
        foreach (var wait in waits) wait.Unregister(null); foreach (var signal in events) signal.Dispose();
        lifetime?.Close(); instance?.Dispose();
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Security.Cryptography;
using System.Text;

namespace AITaskbar;

public partial class App : Application
{
    internal AIEnter.App AiModule { get; } = new();
    internal Halo.App HaloModule { get; } = new();
    private readonly AppearanceHost appearance = new();
    private Mutex? instance;
    private EventWaitHandle? open, quit, compare, startup;
    private RegisteredWaitHandle? openWait, quitWait, compareWait, startupWait;
    private TaskbarSettingsWindow? settingsWindow;
    private PreviewTaskbar? preview;
    private bool shuttingDown, taskbarStarting, comparisonRequested;
    private bool useDefaultRoot;
    private string root = "";
    private int iconSize;
    private int? previewHeight;
    internal bool TaskbarCoversWindows { get; private set; }
    private string TaskbarCoverFile => Path.Combine(root, "preview-cover-windows");
    private string ShowSystemTaskbarFile => Path.Combine(root, "show-system-taskbar");
    internal TaskbarAppFeedbackStyle AppFeedback { get; private set; } = TaskbarAppFeedbackStyle.Default;
    internal PreviewIconLayout IconLayout { get; private set; } = PreviewIconLayout.Default;
    internal TaskbarAppArtwork AppArtwork { get; } = new();
    internal TaskbarClockStyle ClockStyle { get; private set; } = TaskbarClockStyle.Default;
    internal QuickControlAppearance QuickAppearance { get; private set; } = new();
    internal PreviewIconPreferences IconPreferences { get; private set; } = null!;
    internal AppAttentionDiagnosticLog AttentionDiagnostics { get; } = new();
    internal AppCenterAppearance SavedAppCenterAppearance { get; private set; } = new();
    internal AppCenterAppearance CurrentAppCenterAppearance { get; private set; } = new();

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, error) => Log(error.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length > 1 && arguments[1] == "--watch-system-taskbar")
        {
            _ = WatchSystemTaskbarAsync(arguments);
            return;
        }
        var customRoot = Array.IndexOf(arguments, "--data-dir");
        var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AITaskbar");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(customRoot >= 0 && customRoot + 1 < arguments.Length
            ? arguments[customRoot + 1]
            : defaultRoot));
        iconSize = PreviewIconSize.Read(root);
        previewHeight = PreviewHeight.Read(root);
        TaskbarCoversWindows = File.Exists(TaskbarCoverFile);
        AppFeedback = TaskbarAppFeedbackStyle.Read(root);
        IconLayout = PreviewIconLayout.Read(root);
        AttentionDiagnostics.SetEnabled(IconLayout.AppAttentionDiagnostics);
        ClockStyle = TaskbarClockStyle.Read(root);
        QuickAppearance = QuickControlAppearance.Read(root);
        IconPreferences = new(Path.Combine(root, "preview-original-icons.json"));
        CurrentAppCenterAppearance = SavedAppCenterAppearance = AppCenterAppearance.Read(root);
        useDefaultRoot = string.Equals(root, defaultRoot, StringComparison.OrdinalIgnoreCase);
        var verifying = arguments.Any(arg => arg.StartsWith("--verify-", StringComparison.Ordinal));
        if (verifying && useDefaultRoot)
        {
            Environment.ExitCode = 2;
            Exit();
            return;
        }
        var useExistingProfiles = useDefaultRoot
            && File.Exists(Path.Combine(root, "use-existing-profiles"));
        var profileRoot = useExistingProfiles ? Path.GetDirectoryName(defaultRoot)! : root;
        var aiDirectory = Path.Combine(profileRoot, "AIEnter");
        var haloDirectory = Path.Combine(profileRoot, "Halo");
        var prefix = "Local\\AITaskbar." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..16];
        instance = new Mutex(true, prefix, out var first);
        open = new(false, EventResetMode.AutoReset, prefix + ".Open");
        quit = new(false, EventResetMode.AutoReset, prefix + ".Quit");
        compare = new(false, EventResetMode.AutoReset, prefix + ".Compare");
        startup = new(false, EventResetMode.AutoReset, prefix + ".Startup");

        if (!first)
        {
            if (arguments.Contains("--quit")) quit.Set();
            else if (arguments.Contains("--ai")) Environment.ExitCode = AIEnter.App.Relay(arguments, aiDirectory) ? 0 : 1;
            else if (arguments.Contains("--halo")) Environment.ExitCode = Halo.App.Relay(arguments, haloDirectory) ? 0 : 1;
            else if (arguments.Contains("--compare")) compare.Set();
            else if (arguments.Contains("--startup")) startup.Set();
            else open.Set();
            Exit();
            return;
        }

        if (arguments.Contains("--quit"))
        {
            Exit();
            return;
        }

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        openWait = ThreadPool.RegisterWaitForSingleObject(open, (_, _) => dispatcher.TryEnqueue(ShowSettings), null, Timeout.Infinite, false);
        quitWait = ThreadPool.RegisterWaitForSingleObject(quit, (_, _) => dispatcher.TryEnqueue(RequestExit), null, Timeout.Infinite, false);
        compareWait = ThreadPool.RegisterWaitForSingleObject(compare, (_, _) => dispatcher.TryEnqueue(() => StartTaskbar(comparison: true)), null, Timeout.Infinite, false);
        startupWait = ThreadPool.RegisterWaitForSingleObject(startup, (_, _) => dispatcher.TryEnqueue(() => StartTaskbar()), null, Timeout.Infinite, false);

        if (arguments.Contains("--verify-station") || arguments.Contains("--verify-halo-flyout"))
        {
            HaloModule.Start(arguments, haloDirectory);
            return;
        }

        if (arguments.Contains("--verify-appearance"))
        {
            _ = VerifyAppearanceAsync();
            return;
        }

        if (verifying)
        {
            AiModule.Start(arguments, aiDirectory);
            return;
        }

        WindowOperationLog.Start(root);
        var ai = arguments.Contains("--ai");
        var halo = arguments.Contains("--halo");
        EnsureBrowserRecentHosts();
        AiModule.Start(ai ? arguments : ["--background"], aiDirectory);
        HaloModule.Start(halo ? arguments : ["--background"], haloDirectory);
        if (arguments.Contains("--compare")) StartTaskbar(comparison: true);
        else if (arguments.Contains("--startup")) StartTaskbar();
        else if (!ai && !halo && !arguments.Contains("--background")) ShowSettings();
    }

    private async Task WatchSystemTaskbarAsync(string[] arguments)
    {
        await SystemTaskbarVisibility.WatchAsync(arguments);
        Exit();
    }

    private async Task VerifyAppearanceAsync()
    {
        var directory = Path.Combine(root, "LumaBar");
        try
        {
            Directory.CreateDirectory(directory);
            var fixtures = new LumaBar.Settings { MouseGlow = true, MouseGlowRadius = 65, Transition = "soft" };
            fixtures.Glass.Enabled = true;
            fixtures.Glass.Desktop.Material = fixtures.Glass.Window.Material = fixtures.Glass.Maximized.Material = "blur";
            File.WriteAllText(Path.Combine(directory, "settings.json"), LumaBar.Files.Json.Serialize(fixtures));
            var restored = LumaBar.Files.Json.Deserialize<LumaBar.Settings>(File.ReadAllText(Path.Combine(directory, "settings.json")));
            if (restored.MouseGlowRadius != 65 || restored.TransitionMilliseconds != 280)
                throw new InvalidDataException("LumaBar settings round trip failed.");

            for (var cycle = 0; cycle < 2; cycle++)
            {
                await appearance.ShowAsync(directory, hidden: true, verification: true).WaitAsync(TimeSpan.FromSeconds(15));
                for (int attempt = 0; appearance.CurrentBarAppearance is null && attempt < 60; attempt++)
                    await Task.Delay(250);
                if (appearance.CurrentBarAppearance is not { Material: "blur", MouseGlow: true, GlowRadius: 65, TransitionMilliseconds: 280 })
                    throw new InvalidDataException("Preview-only glass, glow and transition parameters were not published.");
                await appearance.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (appearance.CurrentBarAppearance is not null)
                    throw new InvalidDataException("Preview-only appearance remained after stopping.");
            }
            File.WriteAllText(Path.Combine(directory, "appearance-result.json"), "{\"passed\":true,\"startStopCycles\":2,\"previewGlassAndGlow\":true}");
            await ShutdownAsync();
        }
        catch (Exception error)
        {
            Log(error);
            Environment.ExitCode = 1;
            Exit();
        }
    }

    internal static string EventPrefix(string scope, string path) =>
        "Local\\AITaskbar." + scope + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..16];

    internal static EventWaitHandle? WaitForAction(string name)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (EventWaitHandle.TryOpenExisting(name, out var signal)) return signal;
            Thread.Sleep(50);
        }
        return null;
    }

    internal void ShowSettings() => ShowSettings(SettingsSection.Taskbar);

    internal void ShowSettings(SettingsSection section)
    {
        if (shuttingDown) return;
        if (settingsWindow is null)
        {
            settingsWindow = new TaskbarSettingsWindow(this, root);
            _ = settingsWindow.ResetGlassAsync();
        }
        settingsWindow.Select(section);
        settingsWindow.UpdatePreviewState(preview is not null);
        settingsWindow.Activate();
    }

    internal void HideSettings() => settingsWindow?.HideSettings();
    internal void ShowQuickControlSettings()
    {
        ShowSettings(SettingsSection.Taskbar);
        if (!shuttingDown) settingsWindow?.ShowQuickControlAppearance();
    }

    internal void ShowAppCenterInterfaceSettings()
    {
        ShowSettings(SettingsSection.AppCenter);
        settingsWindow?.ShowAppCenterInterfaceSettings();
    }

    internal void PreviewAppCenterAppearance(AppCenterAppearance next)
    {
        CurrentAppCenterAppearance = next;
        preview?.SetAppCenterIcon(next.Icon);
    }

    internal bool TrySaveAppCenterAppearance(AppCenterAppearance next)
    {
        try { next.Save(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            return false;
        }
        SavedAppCenterAppearance = next;
        PreviewAppCenterAppearance(next);
        return true;
    }

    internal void ShowHaloStorageSettings()
    {
        ShowSettings(SettingsSection.Halo);
        settingsWindow?.ShowHaloStorage();
    }

    internal int IconSize => iconSize;
    internal int TaskbarHeight => previewHeight ?? PreviewHeight.Default;

    internal void SaveTaskbarCoverWindows(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(root);
            using var marker = File.Create(TaskbarCoverFile);
        }
        else File.Delete(TaskbarCoverFile);
        TaskbarCoversWindows = enabled;
    }

    internal void SaveSystemTaskbarVisibility(bool visible)
    {
        if (!visible)
        {
            File.Delete(ShowSystemTaskbarFile);
            return;
        }
        Directory.CreateDirectory(root);
        using var marker = File.Create(ShowSystemTaskbarFile);
    }
    internal bool PreviewVisible => preview is not null;
    internal IReadOnlyList<PinnedApp> PreviewPins => preview?.PinnedItems ?? new PinnedApps(Path.Combine(root, "preview-pins.json")).Items;
    internal PreviewModuleIcons? ModuleIcons => preview?.ModuleIcons;
    internal void FocusPreview() => preview?.Activate();
    internal LumaBar.BarAppearance? BarAppearance => appearance.CurrentBarAppearance;
    internal event Action? BarAppearanceChanged
    {
        add => appearance.BarAppearanceChanged += value;
        remove => appearance.BarAppearanceChanged -= value;
    }
    internal async Task<T> EditAppearance<T>(Func<LumaBar.TrayApp, T> edit)
    {
        await appearance.ShowAsync(Path.Combine(root, "LumaBar"), hidden: true);
        return await appearance.EditAsync(edit);
    }

    internal bool TrySetIconSize(int size)
    {
        if (size == iconSize) return true;
        try { PreviewIconSize.Save(root, size); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存应用图标大小。");
            return false;
        }
        iconSize = size;
        preview?.SetIconSize(size);
        settingsWindow?.SetStatus("");
        return true;
    }

    internal bool TrySetClockStyle(TaskbarClockStyle style)
    {
        if (ClockStyle == style) return true;
        try { style.Save(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存任务栏时钟字重。");
            return false;
        }
        ClockStyle = style;
        preview?.SetClockStyle(style);
        settingsWindow?.SetStatus("");
        return true;
    }

    internal bool TrySetQuickAppearance(QuickControlAppearance appearance)
    {
        if (QuickAppearance == appearance) return true;
        try { appearance.Save(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存快捷控制外观。");
            return false;
        }
        QuickAppearance = appearance;
        preview?.SetQuickAppearance(appearance);
        settingsWindow?.UpdateQuickControlAppearance();
        settingsWindow?.SetStatus("");
        return true;
    }

    internal void SetTaskbarHeight(int? height)
    {
        if (height is not null) height = PreviewHeight.Clamp(height.Value);
        if (height == previewHeight) return;
        try { PreviewHeight.Save(root, height); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存任务栏高度。");
            settingsWindow?.UpdateHeightControl();
            return;
        }
        previewHeight = height;
        preview?.SetHeight(TaskbarHeight);
        settingsWindow?.UpdateHeightControl();
        settingsWindow?.SetStatus("");
    }

    internal bool TrySetAppFeedback(TaskbarAppFeedbackStyle style)
    {
        if (AppFeedback == style) return true;
        try { style.Save(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存窗口圆点和悬停底框设置。");
            return false;
        }
        AppFeedback = style;
        PreviewAppFeedback(style);
        settingsWindow?.SetStatus("");
        return true;
    }

    internal void PreviewAppFeedback(TaskbarAppFeedbackStyle style) => preview?.SetAppFeedback(style);

    internal void PreviewThumbnailWidth(int width) => preview?.SetThumbnailWidth(width);
    internal void PreviewThumbnailAnimation(int durationMs) => preview?.SetThumbnailAnimation(durationMs);

    internal bool TrySetIconLayout(PreviewIconLayout layout)
    {
        if (IconLayout == layout) return true;
        try { layout.Save(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存图标布局和动画设置。");
            return false;
        }
        bool enableRecentHost = (layout.ShowChromeRecentlyClosed && !IconLayout.ShowChromeRecentlyClosed) ||
            (layout.ShowEdgeRecentlyClosed && !IconLayout.ShowEdgeRecentlyClosed);
        IconLayout = layout;
        AttentionDiagnostics.SetEnabled(layout.AppAttentionDiagnostics);
        if (enableRecentHost) EnsureBrowserRecentHosts();
        preview?.SetIconLayout(layout);
        settingsWindow?.SetStatus("");
        return true;
    }

    private void EnsureBrowserRecentHosts()
    {
        if (!useDefaultRoot) return;
        EnsureBrowserRecentHost(RecentBrowser.Chrome, IconLayout.ShowChromeRecentlyClosed);
        EnsureBrowserRecentHost(RecentBrowser.Edge, IconLayout.ShowEdgeRecentlyClosed);
    }

    private void EnsureBrowserRecentHost(RecentBrowser browser, bool enabled)
    {
        if (!enabled) return;
        try { BrowserRecentHostRegistration.EnsureInstalled(root, browser); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log(error);
        }
    }

    internal bool TrySetOriginalIcon(string executable, bool useOriginal)
    {
        try { IconPreferences.SetOriginal(executable, useOriginal); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log(error);
            settingsWindow?.SetStatus("无法保存软件图标设置。");
            return false;
        }
        preview?.RefreshIconStyle(executable);
        settingsWindow?.SetStatus("");
        return true;
    }

    internal async void TogglePreview()
    {
        if (shuttingDown) return;
        if (preview is not null) { preview.Close(); return; }
        await OpenPreviewAsync();
    }

    private async void StartTaskbar(bool comparison = false)
    {
        if (shuttingDown) return;
        comparisonRequested |= comparison;
        if (taskbarStarting) return;
        taskbarStarting = true;
        try
        {
            ShowAppearance(hidden: true);
            // At sign-in, Explorer may still be creating the native taskbar.
            for (var attempt = 0; attempt < 40 && !shuttingDown && preview is null; attempt++)
            {
                if (WindowsNotificationArea.FindExplorerTray() != 0)
                {
                    await OpenPreviewAsync(activate: false, comparison: comparisonRequested);
                    break;
                }
                await Task.Delay(250);
            }
            if (!shuttingDown && preview is null)
                ReportPreviewFailure(new InvalidOperationException("Windows 原生任务栏尚未就绪，可从控制台重新开启预览。"));
            if (comparisonRequested && preview is { } bar)
                await bar.SetSystemTaskbarVisibilityAsync(visible: true);
        }
        finally
        {
            comparisonRequested = false;
            taskbarStarting = false;
        }
    }

    private async Task OpenPreviewAsync(bool activate = true, bool comparison = false)
    {
        try
        {
            ShowAppearance(hidden: true);
            var bar = new PreviewTaskbar(this, root, appearance, iconSize, ReportPreviewFailure);
            preview = bar;
            bar.Closed += (_, _) =>
            {
                preview = null;
                settingsWindow?.UpdatePreviewState(false);
            };
            bar.ShowDocked(activate);
            settingsWindow?.UpdatePreviewState(true);
            await bar.SetSystemTaskbarVisibilityAsync(comparison || File.Exists(ShowSystemTaskbarFile));
        }
        catch (Exception error)
        {
            preview?.Close();
            ReportPreviewFailure(error);
        }
    }

    private void ReportPreviewFailure(Exception error)
    {
        Log(error);
        settingsWindow?.SetStatus("任务栏预览未能继续。" + error.Message);
    }

    internal void ShowAppearanceSettings() => ShowAppearance(hidden: false);

    private async void ShowAppearance(bool hidden)
    {
        try
        {
            await appearance.ShowAsync(Path.Combine(root, "LumaBar"), hidden);
            if (!hidden) settingsWindow?.SetStatus("");
        }
        catch (Exception error)
        {
            Log(error);
            settingsWindow?.SetStatus(error.Message);
        }
    }

    internal async void RequestExit() => await ShutdownAsync();

    internal async Task ShutdownAsync()
    {
        if (shuttingDown) return;
        shuttingDown = true;
        if (settingsWindow is not null) await settingsWindow.PendingSettings;
        preview?.Close();
        try { await appearance.StopAsync(); }
        catch (Exception error)
        {
            Log(error);
            shuttingDown = false;
            settingsWindow?.SetStatus("任务栏外观恢复失败。请在 LumaBar 中重试恢复，然后退出。");
            return;
        }
        try { await HaloModule.StopAsync(); }
        catch (Exception error) { Log(error); }
        try { AiModule.Stop(); }
        catch (Exception error) { Log(error); }
        openWait?.Unregister(null);
        quitWait?.Unregister(null);
        compareWait?.Unregister(null);
        startupWait?.Unregister(null);
        open?.Dispose();
        quit?.Dispose();
        compare?.Dispose();
        startup?.Dispose();
        instance?.Dispose();
        Exit();
    }

    private void Log(Exception error)
    {
        try
        {
            Directory.CreateDirectory(root);
            File.AppendAllText(Path.Combine(root, "error.log"), $"{DateTimeOffset.Now:O} {error}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

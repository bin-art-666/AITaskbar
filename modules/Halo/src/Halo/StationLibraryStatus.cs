using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    readonly ScrollViewer offlineView = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock offlinePath = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly Button retryLibrary = new() { Content = "重试" };
    DispatcherQueueTimer libraryMonitor = null!;
    bool probingLibrary;

    void InitializeLibraryAvailability()
    {
        var message = new TextBlock { Text = "文件库暂不可用", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        var instructions = new TextBlock { Text = "重新连接磁盘或确认目录可访问后重试。原来的文件库位置和布局会保留。", TextWrapping = TextWrapping.Wrap };
        var openSettings = new Button { Content = "打开存储设置" };
        retryLibrary.Click += async (_, _) => await Run(() => RefreshLibraryStatus(userInitiated: true));
        openSettings.Click += (_, _) => {
            OpenSettings();
            if (ShowingSettings) settingsPage!.ShowStorageSettings();
        };
        PanelTheme.StyleTextAction(retryLibrary);
        PanelTheme.StyleTextAction(openSettings);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(retryLibrary);
        actions.Children.Add(openSettings);
        var body = new StackPanel { Spacing = 10, Padding = new(16), MaxWidth = 440, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(message);
        body.Children.Add(instructions);
        body.Children.Add(offlinePath);
        body.Children.Add(actions);
        offlineView.Content = body;
        Grid.SetRow(offlineView, 1);
        root.Children.Insert(root.Children.IndexOf(statusHost), offlineView);
        libraryMonitor = DispatcherQueue.CreateTimer();
        libraryMonitor.Interval = TimeSpan.FromSeconds(3);
        libraryMonitor.Tick += async (_, _) => await RefreshLibraryStatus();
        Closed += (_, _) => libraryMonitor.Stop();
        UpdateOfflineView();
    }

    void StartLibraryMonitoring()
    {
        if (!libraryMonitor.IsRunning) libraryMonitor.Start();
        _ = RefreshLibraryStatus();
    }

    void UpdateOfflineView()
    {
        offlineView.Visibility = !ShowingSettings && App.Station.LibraryOffline ? Visibility.Visible : Visibility.Collapsed;
        offlinePath.Text = App.Station.DesktopDirectory;
        UpdateStationContent();
    }

    async Task RefreshLibraryStatus(bool userInitiated = false)
    {
        if (probingLibrary || destroyed || DragActive || !userInitiated && ModalActive || activeContentDialog != null) return;
        probingLibrary = true;
        if (userInitiated) retryLibrary.IsEnabled = false;
        try {
            bool changed = await App.Station.RefreshLibraryAvailability();
            if (destroyed) return;
            if (changed) {
                if (App.Station.LibraryOffline) {
                    CloseFolderBrowser();
                    HideSearch();
                    thumbnails.Cancel();
                } else {
                    Rebuild();
                    App.Current.ResumeQueuedDrops();
                    if (App.Station.LoadWarning is { } warning) Notify(warning, true);
                }
                settingsPage?.RefreshStorageAvailability();
                UpdateOfflineView();
                if (!ShowingSettings && !App.Station.LibraryOffline && IsOpen) FocusGridSelection(files);
            }
            if (userInitiated && App.Station.LibraryOffline)
                Notify(StationStore.LibraryOfflineMessage, true);
        } finally {
            probingLibrary = false;
            if (userInitiated) retryLibrary.IsEnabled = true;
        }
    }
}

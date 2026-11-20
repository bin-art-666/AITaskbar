using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    SettingsPage? settingsPage;
    bool returnDetached;
    Visibility searchVisibility;
    internal bool ShowingSettings => settingsPage?.Visibility == Visibility.Visible;
    internal Task PendingSettingsSave => settingsPage?.PendingOperation ?? Task.CompletedTask;
    internal SettingsPage SettingsForVerification => settingsPage!;

    internal void OpenSettings()
    {
        if (destroyed || DragActive || resizing || activeContentDialog != null) return;
        if (ShowingSettings) { ShowPanel(); return; }
        if (changingMode) { DispatcherQueue.TryEnqueue(OpenSettings); return; }
        CloseFolderBrowser();
        if (settingsPage == null)
        {
            settingsPage = new(this);
            Grid.SetRow(settingsPage, 1);
            root.Children.Insert(root.Children.IndexOf(statusHost), settingsPage);
        }
        ChangeWindowMode(() => {
            if (IsOpen && !detached) SaveSize();
            returnDetached = detached;
            searchVisibility = desktopSearch.Visibility;
            desktopSearch.Visibility = files.Visibility = empty.Visibility = Visibility.Collapsed;
            settingsPage.Visibility = Visibility.Visible;
            UpdateOfflineView();
            searchRequest?.Cancel();
            UpdateSelectionDetails();
            bool needsPosition = !detached || !IsOpen;
            detached = true;
            if (needsPosition) Position();
            Title = "Halo · 设置";
            settingsPage.Reset();
        }, show: true);
        expand.Focus(FocusState.Programmatic);
    }

    internal void ReturnFromSettings()
    {
        if (changingMode || !ShowingSettings || !PendingSettingsSave.IsCompleted || resizing) return;
        ChangeWindowMode(() => {
            SaveSize();
            settingsPage!.ClearPreviews();
            RestoreStationView();
            if (!detached) Position();
        });
        expand.Focus(FocusState.Programmatic);
    }

    void RestoreStationView()
    {
        settingsPage!.Visibility = Visibility.Collapsed;
        detached = returnDetached;
        desktopSearch.Visibility = IsOpen && !App.Station.LibraryOffline ? searchVisibility : Visibility.Collapsed;
        UpdateOfflineView();
        if (Searching) _ = RefreshSearch();
        UpdateSelectionDetails();
        Title = "Halo · 中转站";
    }

    internal void ClearSettingsNotification() => ClearNotification();
}

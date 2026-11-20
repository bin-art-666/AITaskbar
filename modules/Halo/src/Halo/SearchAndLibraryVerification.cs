using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifySearchAndLibraryAvailability(string catalog, Action<string, bool> check)
    {
        using var retain = Retain();
        Open();
        var store = App.Station;
        await store.NewFolder("定位验收");
        var folder = store.Items.Single(item => item.Kind == StationKind.Folder && DesktopName(item) == "定位验收");
        string left = Directory.CreateDirectory(Path.Combine(folder.Value, "甲")).FullName;
        string right = Directory.CreateDirectory(Path.Combine(folder.Value, "乙")).FullName;
        const string name = "定位同名样本.txt";
        await File.WriteAllTextAsync(Path.Combine(left, name), "甲");
        await File.WriteAllTextAsync(Path.Combine(right, name), "乙");
        await Task.Delay(90);
        ShowSearch();
        desktopSearch.Text = name;
        await RefreshSearch(debounce: false);
        var hits = searchResults.Items.Cast<ListViewItem>().Select(row => (SearchHit)row.Tag).ToArray();
        check("SearchDistinguishesNestedNamesByRelativePath", hits.Length == 2
            && hits.Any(hit => hit.Location == "定位验收 › 甲") && hits.Any(hit => hit.Location == "定位验收 › 乙"));
        await LocateSearchResult(hits.Single(hit => hit.Directory == right));
        check("SearchLocatesExactNestedItem", folderBrowser?.CurrentDirectory == right
            && folderBrowser.Files.SelectedItem is GridViewItem { Tag: StationItem selected } && selected.Value == Path.Combine(right, name)
            && desktopSearch.Visibility == Visibility.Collapsed);
        CloseFolderBrowser();
        ShowSearch();
        desktopSearch.Text = DesktopName(folder);
        await LocateSearchResult(new(folder));
        check("SearchLocatesRootItemInManualGrid", Selected().SingleOrDefault()?.Id == folder.Id && files.Visibility == Visibility.Visible);

        await store.Flush();
        var before = await File.ReadAllBytesAsync(catalog);
        string library = store.DesktopDirectory, displaced = library + "-verification-offline";
        thumbnails.Cancel();
        Directory.Move(library, displaced);
        try {
            var restarted = new StationStore(catalog);
            check("OfflineStartupRetainsCatalogAndTrash", restarted.LibraryOffline && restarted.ReadOnly
                && restarted.Items.SequenceEqual(store.Items) && restarted.Trash.SequenceEqual(store.Trash)
                && restarted.DesktopDirectory == library);
            await RefreshLibraryStatus(userInitiated: true);
            check("NativeOfflineLibraryReplacesEmptyState", store.LibraryOffline && offlineView.Visibility == Visibility.Visible
                && files.Visibility == Visibility.Collapsed && offlinePath.Text == library);
            bool blocked = false;
            try { await store.NewFolder("离线时禁止写入"); }
            catch (IOException) { blocked = true; }
            check("OfflineWriteCannotRecreateLibrary", blocked && !Directory.Exists(library)
                && (await File.ReadAllBytesAsync(catalog)).SequenceEqual(before));
            OpenSettings();
            SettingsForVerification.ShowStorageSettings();
            await Task.Delay(90);
            check("OfflineStorageSettingsReachable", ShowingSettings && SettingsForVerification.StorageSettingsVisible);
            ReturnFromSettings();
            await Task.Delay(90);
            check("OfflineStateRestoresAfterSettings", !ShowingSettings && offlineView.Visibility == Visibility.Visible);
        } finally { Directory.Move(displaced, library); }
        await RefreshLibraryStatus(userInitiated: true);
        check("NativeLibraryRetryRestoresOriginalGrid", !store.LibraryOffline && !store.ReadOnly && files.Visibility == Visibility.Visible
            && offlineView.Visibility == Visibility.Collapsed && File.Exists(Path.Combine(right, name))
            && (await File.ReadAllBytesAsync(catalog)).SequenceEqual(before));
    }
}

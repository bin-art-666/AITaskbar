using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Halo;

internal sealed partial class SettingsPage
{
    readonly TextBlock storagePath = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly TextBlock storageResult = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Button openStorage = new() { Content = "打开文件夹" };
    readonly Button changeStorage = new() { Content = "更改位置" };
    readonly Button cancelMigration = new() { Content = "取消迁移", Visibility = Visibility.Collapsed };
    readonly TextBlock storageHeading = Section("文件存储");
    Task pendingStorage = Task.CompletedTask;
    CancellationTokenSource? migrationCancellation;
    bool editingStorage;
    internal Task PendingOperation => pendingStorage.IsCompleted && pendingTrash.IsCompleted
        ? PendingSave : Task.WhenAll(PendingSave, pendingStorage, pendingTrash);

    void BuildStorageSettings(StackPanel body)
    {
        body.Children.Add(storageHeading);
        body.Children.Add(storagePath);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(openStorage);
        actions.Children.Add(changeStorage);
        actions.Children.Add(cancelMigration);
        body.Children.Add(actions);
        body.Children.Add(storageResult);
        openStorage.Click += async (_, _) => {
            try {
                string folder = App.Station.DesktopDirectory;
                if (!await Windows.System.Launcher.LaunchFolderPathAsync(folder)) throw new IOException("无法打开文件库。");
            } catch (Exception error) { ShowStorageResult(error.Message); }
        };
        changeStorage.Click += (_, _) => {
            if (PendingOperation.IsCompleted) pendingStorage = ChooseStorageLocation();
        };
        cancelMigration.Click += (_, _) => {
            cancelMigration.IsEnabled = false;
            migrationCancellation?.Cancel();
        };
    }

    void RefreshStoragePath()
    {
        storagePath.Text = App.Station.DesktopDirectory;
        openStorage.IsEnabled = migrationCancellation == null && !App.Station.LibraryOffline && Directory.Exists(storagePath.Text);
        changeStorage.IsEnabled = migrationCancellation == null && !App.Station.ReadOnly;
    }

    internal void ShowStorageSettings()
    {
        nav.Select(storageSettingsPage);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => {
            if (Visibility != Visibility.Visible || !ReferenceEquals(nav.SelectedPage, storageSettingsPage)) return;
            storageSettingsPage.UpdateLayout();
            storageHeading.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        });
    }

    internal bool StorageSettingsVisible => ReferenceEquals(nav.SelectedPage, storageSettingsPage) && !openStorage.IsEnabled && !changeStorage.IsEnabled;

    internal void RefreshStorageAvailability()
    {
        RefreshStoragePath();
        RefreshTrashSettings();
        if (!saving) sortMode.IsEnabled = !App.Station.ReadOnly;
        UpdateSaveState();
    }

    async Task ChooseStorageLocation()
    {
        using var lease = owner.Retain();
        editingStorage = true;
        changeStorage.IsEnabled = false;
        save.IsEnabled = false;
        storageResult.Visibility = Visibility.Collapsed;
        try {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(settingsHost));
            var parent = await picker.PickSingleFolderAsync();
            if (parent == null) return;
            string destination = StationStore.IsVolumeRoot(parent.Path) ? parent.Path : StationStore.ProposeStorageDirectory(parent.Path);
            var plan = await Task.Run(() => App.Station.PreviewStorageMigration(destination));
            var renamed = plan.Entries.Where(entry => !entry.InTrash && Path.GetFileName(entry.Source) != entry.Relative).ToArray();
            var summary = new StackPanel { Spacing = 8 };
            summary.Children.Add(new TextBlock { Text = $"新位置\n{plan.Destination}\n\n将复制并校验 {plan.Entries.Length} 项文件、文件夹及回收站内容，然后切换为平铺文件库。旧文件库会保留。", TextWrapping = TextWrapping.Wrap });
            if (plan.Volume != null)
                summary.Children.Add(new TextBlock { Text = $"面板文件会直接放在磁盘根目录。待复制数据为 {plan.RequiredBytes:N0} 字节。", TextWrapping = TextWrapping.Wrap });
            if (renamed.Length > 0) {
                summary.Children.Add(new TextBlock { Text = $"{renamed.Length} 项需要自动编号" });
                summary.Children.Add(new ScrollViewer {
                    MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = new TextBlock { Text = string.Join("\n", renamed.Select(entry => $"{Path.GetFileName(entry.Source)} → {entry.Relative}")), TextWrapping = TextWrapping.Wrap },
                });
            }
            var confirmation = new ContentDialog {
                XamlRoot = XamlRoot, Title = "迁移 Halo 文件库？",
                Content = summary,
                PrimaryButtonText = "复制并切换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close,
            };
            if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;

            using var cancellation = new CancellationTokenSource();
            migrationCancellation = cancellation;
            openStorage.IsEnabled = false;
            cancelMigration.IsEnabled = true;
            cancelMigration.Visibility = Visibility.Visible;
            ShowStorageResult("正在复制并校验。新的拖入将在迁移完成后处理。");
            var app = App.Current;
            try {
                await app.ChangeStationStorage(plan, cancellation.Token);
                ShowStorageResult("文件库已切换，旧副本已保留。");
            }
            finally { app.ResumeQueuedDrops(); }
        }
        catch (OperationCanceledException) { ShowStorageResult("迁移已取消，继续使用原文件库。"); }
        catch (Exception error) { ShowStorageResult("迁移未完成。 " + error.Message); }
        finally {
            editingStorage = false;
            migrationCancellation = null;
            cancelMigration.Visibility = Visibility.Collapsed;
            RefreshStoragePath();
            RefreshTrashSettings();
            UpdateSaveState();
        }
    }

    void ShowStorageResult(string message)
    {
        storageResult.Text = message;
        storageResult.Visibility = Visibility.Visible;
    }
}

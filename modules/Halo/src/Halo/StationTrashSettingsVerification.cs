using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyTrashSettings(Action<string, bool> check)
    {
        var store = App.Station;
        string source = Directory.CreateDirectory(Path.Combine(App.DataDirectory, "回收站测试源文件夹")).FullName;
        await File.WriteAllTextAsync(Path.Combine(source, "可读.txt"), "仍可恢复");
        await File.WriteAllTextAsync(Path.Combine(source, "占用.txt"), "打开的文件");
        string file = Path.Combine(App.DataDirectory, "回收站测试源文件.txt");
        await File.WriteAllTextAsync(file, "原文件");
        var before = store.Items.Select(item => item.Id).ToHashSet();
        await store.ImportCopies([new(StationKind.Folder, source), new(StationKind.File, file)]);
        await store.Add([new(StationKind.File, file)]);
        var added = store.Items.Where(item => !before.Contains(item.Id)).ToArray();
        await store.RemoveArchived(added);
        var archivedFolder = store.Trash.Single(item => item.Id == added[0].Id);
        var archivedFile = store.Trash.Single(item => item.Id == added[1].Id);
        int count = store.Trash.Count;
        OpenSettings();
        await Task.Delay(180);
        try {
            var page = SettingsForVerification;
            page.ShowFolderMenuSettingsForVerification();
            check("OperationSettingsStartsWithTrashCollapsed", !page.TrashSectionForVerification.IsExpanded);
            page.TrashSectionForVerification.IsExpanded = true;
            await Task.Delay(100);
            var clear = page.FindButton("清空回收站…");
            var open = page.FindButton("打开回收站");
            var launch = page.FindButton("打开存储目录");
            static void Click(Button button) =>
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            check("TrashSettingsShowsCurrentCountAndDirectory", page.TrashSettingsForVerification.Count == $"{count} 项"
                && page.TrashSettingsForVerification.Path == store.RequireTrashDirectory() && clear.IsEnabled && launch.IsEnabled);
            var slider = page.FindSlider("文件图标大小");
            double pendingSize = slider.Value == slider.Maximum ? slider.Value - 1 : slider.Value + 1;
            slider.Value = pendingSize;
            Click(open);
            await Task.Delay(160);
            check("TrashSettingsOpensExistingTrashAndKeepsDraft", activeContentDialog?.Content is ListView list
                && list.Items.Count == count && ShowingSettings);
            activeContentDialog!.Hide();
            await PendingSettingsSave;
            await Task.Delay(120);
            check("TrashSettingsCloseReturnsToDraft", ShowingSettings && slider.Value == pendingSize && store.Trash.Count == count);

            Click(clear);
            await Task.Delay(160);
            check("TrashSettingsClearRequiresConfirmation", activeContentDialog is { DefaultButton: ContentDialogButton.Close } confirmation
                && Equals(confirmation.Title, $"永久删除回收站中的 {count} 项？") && File.Exists(archivedFile.Value));
            activeContentDialog!.Hide();
            await PendingSettingsSave;
            await Task.Delay(120);
            check("TrashSettingsCancelKeepsEveryEntry", store.Trash.Count == count && File.Exists(archivedFile.Value)
                && slider.Value == pendingSize);

            async Task ConfirmClear()
            {
                Click(clear);
                await Task.Delay(160);
                var button = Descendants<Button>(activeContentDialog!).Single(candidate => Equals(candidate.Content, "永久删除"));
                Click(button);
                await PendingSettingsSave;
                await Task.Delay(120);
            }
            string locked = Path.Combine(archivedFolder.Value, "占用.txt");
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None)) {
                await ConfirmClear();
                var reopened = new StationStore(Path.Combine(App.DataDirectory, "station.json"));
                check("TrashSettingsBatchRetainsLockedFolderAndDeletesOtherEntries", store.Trash.SequenceEqual([archivedFolder])
                    && reopened.Trash.SequenceEqual([archivedFolder]) && File.Exists(locked)
                    && File.Exists(Path.Combine(archivedFolder.Value, "可读.txt")) && !File.Exists(archivedFile.Value)
                    && File.Exists(file) && Directory.Exists(source)
                    && page.TrashSettingsForVerification.Count == "1 项"
                    && page.TrashSettingsForVerification.Result.Contains("1 项删除失败"));
            }
            await ConfirmClear();
            check("TrashSettingsCanRetryAndDisablesClearWhenEmpty", store.Trash.Count == 0 && !clear.IsEnabled
                && page.TrashSettingsForVerification.Count == "0 项" && !Directory.Exists(archivedFolder.Value)
                && File.Exists(file) && ShowingSettings && slider.Value == pendingSize);
            Click(page.FindButton("取消"));
        } finally {
            activeContentDialog?.Hide();
            await PendingSettingsSave;
            ReturnFromSettings();
        }
    }
}

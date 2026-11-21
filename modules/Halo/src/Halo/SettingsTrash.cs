using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Halo;

internal sealed partial class SettingsPage
{
    readonly TextBlock trashCount = new();
    readonly TextBlock trashPath = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    readonly TextBlock trashResult = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly Button openTrash = new() { Content = "打开回收站" };
    readonly Button openTrashStorage = new() { Content = "打开存储目录" };
    readonly Button clearTrash = new() { Content = "清空回收站…" };
    readonly Expander trashSection = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    Task pendingTrash = Task.CompletedTask;
    bool editingTrash;

    void BuildTrashSettings(StackPanel body)
    {
        var title = Section("Halo 回收站");
        title.VerticalAlignment = trashCount.VerticalAlignment = VerticalAlignment.Center;
        trashCount.Opacity = 0.72;
        trashSection.Header = ActionRow(title, trashCount);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(trashPath);
        content.Children.Add(AdaptivePair(ActionRow(openTrash, openTrashStorage), clearTrash, 398, equalColumns: false));
        content.Children.Add(trashResult);
        trashSection.Content = content;
        body.Children.Add(trashSection);
        foreach (var button in new[] { openTrash, openTrashStorage, clearTrash }) {
            PanelTheme.StyleTextAction(button);
            RegisterButton(button);
        }
        AutomationProperties.SetName(trashPath, "回收站存储路径");
        AutomationProperties.SetLiveSetting(trashResult, AutomationLiveSetting.Polite);
        openTrash.Click += (_, _) => StartTrashAction(() => owner.ShowTrash(XamlRoot, settingsHost));
        openTrashStorage.Click += (_, _) => StartTrashAction(async () => {
            if (!await Launcher.LaunchFolderPathAsync(App.Station.RequireTrashDirectory()))
                throw new IOException("无法打开回收站存储目录。");
        });
        clearTrash.Click += (_, _) => StartTrashAction(async () => {
            if (await owner.ConfirmTrashDeletion(App.Station.Trash.ToArray(), XamlRoot) is { } result)
                ShowTrashResult(StationWindow.TrashDeletionMessage(result));
        });
    }

    void StartTrashAction(Func<Task> action)
    {
        if (!PendingOperation.IsCompleted || editingTrash) return;
        pendingTrash = RunTrashAction(action);
    }

    async Task RunTrashAction(Func<Task> action)
    {
        using var lease = owner.Retain();
        editingTrash = true;
        trashResult.Visibility = Visibility.Collapsed;
        RefreshTrashSettings();
        UpdateSaveState();
        try { await action(); }
        catch (Exception error) { ShowTrashResult(error.Message); }
        finally {
            editingTrash = false;
            RefreshTrashSettings();
            UpdateSaveState();
        }
    }

    internal void RefreshTrashSettings()
    {
        var store = App.Station;
        trashCount.Text = $"{store.Trash.Count} 项";
        trashPath.Text = store.TrashDirectory;
        openTrash.IsEnabled = !editingTrash;
        openTrashStorage.IsEnabled = !editingTrash && Directory.Exists(store.TrashDirectory);
        clearTrash.IsEnabled = !editingTrash && !store.ReadOnly && store.Trash.Count > 0;
    }

    void ShowTrashResult(string message)
    {
        trashResult.Text = message;
        trashResult.Visibility = Visibility.Visible;
        if (FrameworkElementAutomationPeer.FromElement(trashResult) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal (string Count, string Path, string Result) TrashSettingsForVerification =>
        (trashCount.Text, trashPath.Text, trashResult.Text);
    internal Expander TrashSectionForVerification => trashSection;
}

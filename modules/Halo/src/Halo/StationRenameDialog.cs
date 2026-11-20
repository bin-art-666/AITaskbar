using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task ShowRenameDialog(StationItem item, GridView grid, Func<string, Task> rename)
    {
        string name = item.Kind is StationKind.File or StationKind.Folder ? Path.GetFileName(item.Value) : DesktopName(item);
        int suffixLength = item.Kind == StationKind.File ? Path.GetExtension(name).Length : 0;
        int selectedLength = suffixLength < name.Length ? name.Length - suffixLength : name.Length;
        var input = new TextBox { Text = name, MaxLength = Math.Max(StationStore.MaximumDesktopNameLength, name.Length), TabIndex = 0 };
        StyleStationDialogInput(input);
        AutomationProperties.SetName(input, "新名称");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        var dialog = new ContentDialog {
            Title = "重命名", Content = new StackPanel { Spacing = 6, Children = { input, error } },
            PrimaryButtonText = "保存", CloseButtonText = "取消",
        };
        PanelTheme.CompactDialog(dialog);
        bool saving = false;
        void ShowError(string message)
        {
            error.Text = message;
            error.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (error.Visibility == Visibility.Visible && FrameworkElementAutomationPeer.FromElement(error) is { } peer)
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        bool Validate()
        {
            try { StationStore.ValidDesktopName(input.Text); ShowError(""); return true; }
            catch (IOException ex) { ShowError(ex.Message); return false; }
        }
        async Task<bool> Save()
        {
            if (saving || !Validate()) return false;
            saving = true;
            input.IsReadOnly = true;
            dialog.IsPrimaryButtonEnabled = false;
            try { await rename(input.Text); return true; }
            catch (Exception ex) { ShowError(ex.Message); return false; }
            finally {
                input.IsReadOnly = false;
                dialog.IsPrimaryButtonEnabled = true;
                saving = false;
            }
        }
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = Validate() && !saving;
        input.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(async (_, e) => {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            if (await Save()) dialog.Hide();
        }), true);
        dialog.PrimaryButtonClick += async (_, e) => {
            var deferral = e.GetDeferral();
            try { e.Cancel = !await Save(); }
            finally { deferral.Complete(); }
        };
        dialog.Closing += (_, e) => e.Cancel = saving;
        dialog.IsPrimaryButtonEnabled = Validate();
        await ShowStationDialog(dialog, grid, () => {
            input.Focus(FocusState.Programmatic);
            input.Select(0, selectedLength);
        });
    }
}

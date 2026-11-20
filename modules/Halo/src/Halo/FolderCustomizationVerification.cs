using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyFolderColorDialog(Action<string, bool> check, StationItem item, StationItem parent, GridView grid)
    {
        var card = grid.Items.Cast<GridViewItem>().Single(card => ((StationItem)card.Tag).Id == item.Id);
        var icon = ((Preview)((ContentControl)card.Content).Tag).Image;
        static void Invoke(Button button) =>
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        async Task ChangeColor(bool save, bool reset = false)
        {
            var pending = ChooseFolderColor(item, parent, grid);
            try {
                await Task.Delay(240);
                var dialog = activeContentDialog!;
                var input = Descendants<TextBox>(dialog).Single();
                var defaults = Descendants<CheckBox>(dialog).Single();
                var confirm = Descendants<Button>(dialog).Single(button => Equals(button.Content, "保存"));
                var cancel = Descendants<Button>(dialog).Single(button => Equals(button.Content, "取消"));
                if (!reset) {
                    string theme = input.ActualTheme.ToString();
                    var border = Descendants<Border>(input).Single(element => element.Name == "BorderElement");
                    defaults.Focus(FocusState.Keyboard);
                    await Task.Delay(80);
                    check($"FolderColorInputUniformBorder_{theme}", border.BorderThickness == new Thickness(1)
                        && border.BorderBrush is SolidColorBrush normal && input.BorderBrush is SolidColorBrush stroke
                        && normal.Color == stroke.Color);
                    bool focused = input.Focus(FocusState.Keyboard);
                    await Task.Delay(80);
                    check($"FolderColorInputAcceptsKeyboardFocus_{theme}", focused && input.FocusState == FocusState.Keyboard);
                    check($"FolderColorInputAccentFocusRing_{theme}", border.BorderThickness == new Thickness(1)
                        && border.BorderBrush is SolidColorBrush ring
                        && input.Resources["TextControlBorderBrushFocused"] is SolidColorBrush accentStroke
                        && ring.Color == accentStroke.Color && input.BorderBrush is SolidColorBrush unfocused
                        && ring.Color != unfocused.Color);
                }
                if (reset) defaults.IsChecked = true;
                else if (save) {
                    var surface = Descendants<Border>(dialog).Single(border => border.Name == "BackgroundElement");
                    check("FolderColorDialogUsesDarkHaloSurface", dialog.ActualTheme == ElementTheme.Dark
                        && surface.Background is SolidColorBrush dark && dark.Color == PanelTheme.SolidContentSurface(true));
                    input.Text = "#845AD6";
                    await Task.Delay(100);
                    check("FolderColorDialogPreviewsInDarkTheme", Equals(icon.Tag, "#845AD6")
                        && App.Station.FolderColorOverride(item) == null);
                } else {
                    var blank = grid.TransformToVisual(null).TransformPoint(new(grid.ActualWidth - 32, grid.ActualHeight - 24));
                    var occupied = card.TransformToVisual(null).TransformPoint(new(card.ActualWidth / 2, card.ActualHeight / 2));
                    var windowButton = close.TransformToVisual(null).TransformPoint(new(close.ActualWidth / 2, close.ActualHeight / 2));
                    check("FolderColorBackdropOnlyAcceptsGridBlank", DialogGridBlankAt(grid, blank)
                        && !DialogGridBlankAt(grid, occupied) && !DialogGridBlankAt(grid, windowButton));
                    var surface = Descendants<Border>(dialog).Single(border => border.Name == "BackgroundElement");
                    check("FolderColorDialogUsesLightHaloSurface", dialog.ActualTheme == ElementTheme.Light
                        && surface.Background is SolidColorBrush light && light.Color == PanelTheme.SolidContentSurface(false)
                        && surface.CornerRadius == new CornerRadius(App.Appearance.BackplateRadius));
                    check("FolderColorDialogPlacesCancelLeftOfSave", cancel.TransformToVisual(dialog).TransformPoint(default).X
                        < confirm.TransformToVisual(dialog).TransformPoint(default).X
                        && confirm.Background is SolidColorBrush accent
                        && accent.Color == PanelTheme.PaletteColor(UiAppearance.ColorValue(App.Appearance.ButtonColor, 0x1CFFFFFF))
                        && confirm.CornerRadius == PanelTheme.Value<CornerRadius>("ControlRadius"));
                    var colors = Descendants<ToggleButton>(dialog).Where(button => button is not CheckBox).ToArray();
                    check("FolderColorDialogExposesNamedSwatches", colors.Length == 6
                        && colors.Select(AutomationProperties.GetName).SequenceEqual(["蓝色", "青色", "绿色", "紫色", "橙色", "珊瑚红"]));
                    var picker = Descendants<Button>(dialog).Single(button => Equals(button.Content, "选取颜色"));
                    Control[] expected = [defaults, .. colors, input, picker, cancel, confirm];
                    bool keyboardOrder = true;
                    for (int index = 0; index < expected.Length; index++) {
                        var control = expected[index];
                        keyboardOrder &= control.TabIndex == index && control.IsTabStop
                            && (ReferenceEquals(control, input) || control.UseSystemFocusVisuals)
                            && control.Focus(FocusState.Keyboard)
                            && ReferenceEquals(FocusManager.GetFocusedElement(dialog.XamlRoot), control)
                            && control.FocusState == FocusState.Keyboard;
                    }
                    check("FolderColorDialogKeyboardFocusFollowsVisualOrder", keyboardOrder);
                    input.Text = "#ZZZZZZ";
                    await Task.Delay(80);
                    check("FolderColorDialogDisablesSaveForInvalidHex", !confirm.IsEnabled);
                    var purple = colors.Single(button => AutomationProperties.GetName(button) == "紫色");
                    ((IToggleProvider)new ToggleButtonAutomationPeer(purple).GetPattern(PatternInterface.Toggle)).Toggle();
                    await Task.Delay(100);
                    check("FolderColorDialogPreviewsBeforeSave", purple.IsChecked == true && input.Text == "#845AD6"
                        && Equals(icon.Tag, "#845AD6") && App.Station.FolderColorOverride(item) == null);
                }
                await Task.Delay(100);
                Invoke(save ? confirm : cancel);
                await pending;
                await Task.Delay(120);
            } finally {
                activeContentDialog?.Hide();
                await pending;
            }
        }
        var previous = root.RequestedTheme;
        try {
            root.RequestedTheme = ElementTheme.Light;
            await ChangeColor(false);
            check("FolderColorDialogCancelRestoresIcon", Equals(icon.Tag, App.Appearance.FolderColor)
                && App.Station.FolderColorOverride(item) == null);
            root.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(160);
            await ChangeColor(true);
            check("FolderColorDialogSaveUpdatesIcon", Equals(icon.Tag, "#845AD6")
                && App.Station.FolderColorOverride(item) == "#845AD6");
            await ChangeColor(true, reset: true);
            check("FolderColorDialogResetRestoresDefault", Equals(icon.Tag, App.Appearance.FolderColor)
                && App.Station.FolderColorOverride(item) == null);
        } finally { root.RequestedTheme = previous; }
    }

    async Task VerifyFolderMenuSettings(SettingsPage page, Action<string, bool> check, Action<Button> click)
    {
        var initial = App.Settings.Data.EffectiveFolderMenu;
        var preview = page.FindButton("预览文件夹菜单");
        var (primary, more) = page.FolderMenuListsForVerification;
        var sample = new StationItem(Guid.NewGuid(), StationKind.Folder, "", "", DateTimeOffset.Now, true);
        page.ShowFolderMenuSettingsForVerification();
        await Task.Delay(100);

        static string[] Labels(IEnumerable<MenuFlyoutItemBase> rows) => rows.Select(row => row switch {
            MenuFlyoutItem item => item.Text, MenuFlyoutSeparator => "|", MenuFlyoutSubItem submenu => submenu.Text, _ => "",
        }).ToArray();
        static MenuFlyoutSubItem Overflow(MenuFlyout menu) => menu.Items.OfType<MenuFlyoutSubItem>().Single();
        void Select(ListView list, FolderMenuAction action) =>
            list.SelectedItem = list.Items.Cast<MenuLayoutRow>().First(row => Equals(row.Action, action));
        void Press(string label) => click(page.FolderMenuButtonForVerification("文件夹 " + label));
        FolderMenuAction[] Rows(ListView list) => list.Items.Cast<MenuLayoutRow>().Select(row => (FolderMenuAction)row.Action).ToArray();

        var legacy = System.Text.Json.JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1,\"FolderMenuPrimary\":133}")!;
        check("LegacySettingsRetainCustomFolderMenu",
            legacy.EffectiveFolderMenu.Primary.SequenceEqual([FolderMenuAction.Open, FolderMenuAction.Copy, FolderMenuAction.Dissolve])
            && Rows(primary).SequenceEqual(initial.Primary) && Rows(more).SequenceEqual(initial.More));
        check("FolderMenuLayersSupportNativeDragReorder", primary.CanReorderItems && primary.CanDragItems && primary.AllowDrop
            && more.CanReorderItems && more.CanDragItems && more.AllowDrop);

        void Edit()
        {
            Select(primary, FolderMenuAction.Explorer);
            Press("上移");
            Select(primary, FolderMenuAction.Copy);
            Press("移到「…」");
            Select(more, FolderMenuAction.Preview);
            Press("移到一级菜单");
            Select(primary, FolderMenuAction.Explorer);
            Press("添加分隔线");
            Select(more, FolderMenuAction.Copy);
            Press("上移");
            Press("下移");
            Press("上移");
            Select(more, FolderMenuAction.CopyPath);
            Press("添加分隔线");
            Press("删除分隔线");
            Select(more, FolderMenuAction.CopyPath);
            Press("添加分隔线");
        }

        var savedMenu = BuildItemMenu([sample]);
        string[] expectedPrimary = ["打开文件夹", "在资源管理器中打开", "|", "重命名…", "设置文件夹颜色", "快速预览", MoreFolderActions];
        string[] expectedMore = ["复制路径", "|", "解散文件夹", "移到 Halo 回收站", "复制文件夹", "移动到…"];
        Edit();
        var draft = (MenuFlyout)preview.Flyout;
        check("FolderMenuOrderAndSeparatorsPreviewWithoutSaving", draft.Items[1] is MenuFlyoutSeparator
            && Labels(draft.Items.Skip(2)).SequenceEqual(expectedPrimary)
            && Labels(Overflow(draft).Items).SequenceEqual(expectedMore)
            && Labels(BuildItemMenu([sample]).Items).SequenceEqual(Labels(savedMenu.Items))
            && App.Settings.Data.EffectiveFolderMenu == initial);
        click(page.FindButton("取消"));
        check("FolderMenuCancelRestoresBothLayers", Rows(primary).SequenceEqual(initial.Primary)
            && Rows(more).SequenceEqual(initial.More)
            && Labels(((MenuFlyout)preview.Flyout).Items).SequenceEqual(Labels(savedMenu.Items)));

        Edit();
        click(page.FindButton("保存"));
        await page.PendingSave;
        await App.Settings.Flush();
        var actual = BuildItemMenu([sample]);
        var reloaded = new SettingsStore(Path.Combine(App.DataDirectory, "settings.json")).Data;
        check("FolderMenuLayoutPersistsAfterSave", reloaded == App.Settings.Data
            && reloaded.EffectiveFolderMenu.Primary.SequenceEqual(Rows(primary))
            && reloaded.EffectiveFolderMenu.More.SequenceEqual(Rows(more))
            && Labels(actual.Items.Skip(2)).SequenceEqual(expectedPrimary)
            && Labels(Overflow(actual).Items).SequenceEqual(expectedMore)
            && actual.Items.Last() == Overflow(actual));

        var filtered = new MenuFlyout();
        MenuHeading(filtered, "已选 2 项");
        AppendFolderMenu(filtered, new(
            [FolderMenuAction.Separator, FolderMenuAction.Copy, FolderMenuAction.Separator, FolderMenuAction.Open,
                FolderMenuAction.Separator, FolderMenuAction.Trash, FolderMenuAction.Separator],
            [FolderMenuAction.Separator, FolderMenuAction.Rename, FolderMenuAction.Separator]),
            action => action is FolderMenuAction.Copy or FolderMenuAction.Trash ? new MenuFlyoutItem { Text = action.ToString() } : null);
        check("FolderMenuSkipsEmptyGroupsAndOverflow", Labels(filtered.Items).SequenceEqual(["已选 2 项", "|", "Copy", "|", "Trash"]));

        click(page.FindButton("恢复默认菜单"));
        click(page.FindButton("保存"));
        await page.PendingSave;
    }
}

public partial class App
{
    static async Task VerifyFolderColorLifecycle(Action<string, bool> check)
    {
        string fixture = Directory.CreateDirectory(Path.Combine(DataDirectory, "folder-colors")).FullName;
        string catalog = Path.Combine(fixture, "station.json");
        var store = new StationStore(catalog);
        await store.NewFolder("父目录");
        var parent = store.Items.Single();
        await store.SetFolderColor(parent, null, "#C54D9F");
        string childPath = await store.CreateFolderIn(parent, parent.Value, "子文件夹", default);
        var child = store.ReadFolderContents(parent, parent.Value, default).Single();
        await store.SetFolderColor(child, parent, "#396AC8");
        check("FolderColorsAreIndependent", child.Value == childPath && store.FolderColorOverride(parent) == "#C54D9F"
            && new StationStore(catalog).FolderColorOverride(child) == "#396AC8");
        childPath = await store.RenameFolderEntry(parent, parent.Value, child, "改名后的子文件夹", 3);
        child = store.ReadFolderContents(parent, parent.Value, default).Single();
        check("NestedFolderColorFollowsRename", child.Value == childPath && store.FolderColorOverride(child) == "#396AC8");
        await store.MoveFolderEntriesToTrash(parent, parent.Value, [child], 3);
        var archived = store.Trash.Single();
        await store.CreateFolderIn(parent, parent.Value, Path.GetFileName(childPath), default);
        await store.Restore([archived]);
        child = store.ReadFolderContents(parent, parent.Value, default).Single(item => item.Value != childPath);
        check("NestedFolderColorFollowsNumberedTrashRestore", store.FolderColorOverride(child) == "#396AC8"
            && store.FolderColorOverride(store.ReadFolderContents(parent, parent.Value, default).Single(item => item.Value == childPath)) == null);
        await store.RenameOwned(parent.Id, "改名后的父目录");
        parent = store.Items.Single();
        child = store.ReadFolderContents(parent, parent.Value, default).Single(item => Path.GetFileName(item.Value).EndsWith(" (2)"));
        check("ParentRenameCarriesDescendantFolderColors", store.FolderColorOverride(parent) == "#C54D9F"
            && store.FolderColorOverride(child) == "#396AC8");
        await store.RemoveArchived([parent]);
        await store.Restore([store.Trash.Single()]);
        parent = store.Items.Single();
        check("RootFolderColorFollowsTrashRestore", store.FolderColorOverride(parent) == "#C54D9F");
        await store.DissolveFolder(parent.Id);
        var moved = store.Items.Single(item => Path.GetFileName(item.Value).EndsWith(" (2)"));
        check("FolderColorFollowsMoveFromSubfolderToMainGrid", new StationStore(catalog).FolderColorOverride(moved) == "#396AC8");
        await store.MoveStorage(Path.Combine(fixture, "migrated"), default);
        moved = store.Items.Single(item => item.Id == moved.Id);
        check("FolderColorFollowsLibraryMigration", store.FolderColorOverride(moved) == "#396AC8");
        string backup = Path.Combine(fixture, "backup.zip");
        await store.ExportDesktop(backup);
        await store.SetFolderColor(moved, null, null);
        check("FolderColorCanInheritDefaultAgain", new StationStore(catalog).FolderColorOverride(moved) == null);
        await store.RestoreDesktop(backup);
        moved = store.Items.Single(item => item.Id == moved.Id);
        check("FolderColorIsIncludedInDesktopBackup", new StationStore(catalog).FolderColorOverride(moved) == "#396AC8");
    }
}

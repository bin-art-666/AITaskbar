using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;

namespace Halo;

internal sealed partial class SettingsPage
{
    readonly ListView primaryFolderMenu = new();
    readonly ListView moreFolderMenu = new();
    readonly Button folderMenuPreview = new() { Content = "预览文件夹菜单" };

    FolderMenuLayout DraftFolderMenu => new(Actions<FolderMenuAction>(primaryFolderMenu), Actions<FolderMenuAction>(moreFolderMenu));

    static ObservableCollection<MenuLayoutRow> Rows(ListView list) => (ObservableCollection<MenuLayoutRow>)list.ItemsSource;

    static T[] Actions<T>(ListView list) where T : Enum => Rows(list).Select(row => (T)row.Action).ToArray();

    void BuildFolderMenuSettings(StackPanel body)
    {
        var restore = new Button { Content = "恢复默认菜单" };
        restore.Click += (_, _) => LoadFolderMenu(FolderMenuLayout.FromLegacy(FolderMenuAction.Default));
        RegisterButton(restore);
        RegisterButton(folderMenuPreview);
        BuildMenuEditor(body, primaryFolderMenu, moreFolderMenu, FolderMenuAction.Separator, FolderMenuEdited, "文件夹");
        body.Children.Add(ActionRow(folderMenuPreview, restore));
    }

    static Border BuildMenuLayer(ListView list, string title, string menu)
    {
        AutomationProperties.SetName(list, $"{menu} {title}");
        list.CanDragItems = list.CanReorderItems = list.AllowDrop = true;
        list.SelectionMode = ListViewSelectionMode.Single;
        list.ItemsSource = new ObservableCollection<MenuLayoutRow>();
        list.DisplayMemberPath = nameof(MenuLayoutRow.Label);
        list.Height = 204;
        var itemStyle = new Style(typeof(ListViewItem));
        itemStyle.Setters.Add(new Setter(MinHeightProperty, 34d));
        list.ItemContainerStyle = itemStyle;
        ScrollViewer.SetHorizontalScrollMode(list, ScrollMode.Disabled);
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(Section(title));
        var empty = new TextBlock {
            Text = "暂无操作", Opacity = 0.72, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        var surface = new Grid();
        surface.Children.Add(list);
        surface.Children.Add(empty);
        Rows(list).CollectionChanged += (_, _) => empty.Visibility = Rows(list).Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        content.Children.Add(surface);
        return PanelTheme.Card(content);
    }

    void BuildMenuEditor(StackPanel body, ListView primary, ListView more, Enum separatorMarker, Action edited, string menu)
    {
        body.Children.Add(AdaptivePair(BuildMenuLayer(primary, "一级菜单", menu), BuildMenuLayer(more, "更多菜单（…）", menu), 410));
        ListView active = primary;

        Button Tool(string label, Action use) {
            var button = new Button { Content = label };
            AutomationProperties.SetName(button, $"{menu} {label}");
            button.Click += (_, _) => use();
            PanelTheme.StyleTextAction(button);
            RegisterButton(button);
            return button;
        }
        var up = Tool("上移", () => MoveMenuRow(active, -1, edited));
        var down = Tool("下移", () => MoveMenuRow(active, 1, edited));
        var transfer = Tool("移到「…」", () => {
            var list = active;
            var other = list == primary ? more : primary;
            if (list.SelectedItem is not MenuLayoutRow row || row.Action.Equals(separatorMarker)) return;
            Rows(list).RemoveAt(list.SelectedIndex);
            Rows(other).Add(row);
            SelectMenuRow(other, row);
            edited();
        });
        var separator = Tool("添加分隔线", () => {
            var list = active;
            int index = list.SelectedIndex;
            if (list.SelectedItem is not MenuLayoutRow row) return;
            if (row.Action.Equals(separatorMarker)) {
                Rows(list).RemoveAt(index);
                list.SelectedIndex = Math.Min(index, Rows(list).Count - 1);
            } else {
                var line = new MenuLayoutRow(separatorMarker, "── 分隔线 ──");
                Rows(list).Insert(index + 1, line);
                SelectMenuRow(list, line);
            }
            edited();
        });
        body.Children.Add(AdaptivePair(ActionRow(up, down), ActionRow(transfer, separator), 362, equalColumns: false));
        BindSelection(primary, more);
        BindSelection(more, primary);
        Refresh();

        void BindSelection(ListView list, ListView other)
        {
            list.SelectionChanged += (_, _) => {
                if (list.SelectedIndex >= 0) {
                    active = list;
                    if (other.SelectedIndex >= 0) other.SelectedIndex = -1;
                }
                Refresh();
            };
            list.DragItemsCompleted += (_, _) => { Refresh(); edited(); };
        }

        void Refresh()
        {
            int index = active.SelectedIndex;
            bool selected = index >= 0;
            bool isSeparator = active.SelectedItem is MenuLayoutRow row && row.Action.Equals(separatorMarker);
            up.IsEnabled = index > 0;
            down.IsEnabled = selected && index < active.Items.Count - 1;
            transfer.IsEnabled = selected && !isSeparator;
            separator.IsEnabled = selected && (isSeparator || Rows(active).Count(item => item.Action.Equals(separatorMarker)) < 9);
            UpdateLabel(transfer, active == more ? "移到一级菜单" : "移到「…」");
            UpdateLabel(separator, isSeparator ? "删除分隔线" : "添加分隔线");
        }

        void UpdateLabel(Button button, string label)
        {
            if (Equals(button.Content, label)) return;
            button.Content = label;
            AutomationProperties.SetName(button, $"{menu} {label}");
        }
    }

    static StackPanel ActionRow(params UIElement[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }

    static Grid AdaptivePair(FrameworkElement first, FrameworkElement second, double horizontalWidth, bool equalColumns = true)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        var firstColumn = new ColumnDefinition();
        grid.ColumnDefinitions.Add(firstColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(first);
        grid.Children.Add(second);
        Arrange(false);
        grid.SizeChanged += (_, args) => Arrange(args.NewSize.Width >= horizontalWidth);
        return grid;

        void Arrange(bool horizontal)
        {
            grid.RowSpacing = horizontal ? 0 : 8;
            firstColumn.Width = horizontal && !equalColumns ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            SetColumnSpan(first, horizontal ? 1 : 2);
            SetColumnSpan(second, horizontal ? 1 : 2);
            SetColumn(second, horizontal ? 1 : 0);
            SetRow(second, horizontal ? 0 : 1);
        }
    }

    static void SelectMenuRow(ListView list, MenuLayoutRow row)
    {
        list.SelectedItem = row;
        if (list.IsLoaded) list.ScrollIntoView(row);
    }

    static void MoveMenuRow(ListView list, int offset, Action edited)
    {
        int index = list.SelectedIndex, destination = index + offset;
        if (index < 0 || destination < 0 || destination >= list.Items.Count) return;
        var row = Rows(list)[index];
        Rows(list).Move(index, destination);
        SelectMenuRow(list, row);
        edited();
    }

    void LoadFolderMenu(FolderMenuLayout layout)
    {
        string Label(FolderMenuAction action) => StationWindow.FolderMenuOrder.Single(item => item.Action == action).Label;
        LoadMenuLayer(primaryFolderMenu, layout.Primary, FolderMenuAction.Separator, Label);
        LoadMenuLayer(moreFolderMenu, layout.More, FolderMenuAction.Separator, Label);
        FolderMenuEdited();
    }

    static void LoadMenuLayer<T>(ListView list, T[] actions, T separator, Func<T, string> label) where T : struct, Enum
    {
        var rows = Rows(list);
        rows.Clear();
        foreach (var action in actions)
            rows.Add(new(action, EqualityComparer<T>.Default.Equals(action, separator) ? "── 分隔线 ──" : label(action)));
    }

    void FolderMenuEdited()
    {
        var preview = new MenuFlyout();
        StationWindow.MenuHeading(preview, "文件夹操作");
        StationWindow.AppendFolderMenu(preview, DraftFolderMenu,
            action => new MenuFlyoutItem {
                Text = StationWindow.FolderMenuOrder.Single(item => item.Action == action).Label,
                KeyboardAcceleratorTextOverride = StationWindow.FolderMenuShortcut(action) ?? "",
            });
        folderMenuPreview.Flyout = preview;
        UpdateSaveState();
    }

    internal (ListView Primary, ListView More) FolderMenuListsForVerification => (primaryFolderMenu, moreFolderMenu);
    internal Button FolderMenuButtonForVerification(string name) =>
        allButtons.Single(button => AutomationProperties.GetName(button) == name);
    internal void ShowFolderMenuSettingsForVerification(bool showFiles = false)
    {
        nav.SelectedItem = nav.MenuItems.OfType<NavigationViewItem>().Single(item => Equals(item.Tag, "actions"));
        menuTarget.SelectedIndex = showFiles ? 0 : 1;
    }
}

internal sealed class MenuLayoutRow(Enum action, string label)
{
    internal Enum Action { get; } = action;
    public string Label { get; } = label;
}

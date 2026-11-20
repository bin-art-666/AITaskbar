using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Halo;

internal sealed partial class StationWindow
{
    internal enum GridCommand { Open, Preview, Rename, SelectAll, Copy, Cut, Paste, Trash, NewFolder, Undo, Search, ContextMenu }

    internal static GridCommand? GridShortcut(VirtualKey key, VirtualKeyModifiers modifiers) => (key, modifiers) switch {
        (VirtualKey.Enter, VirtualKeyModifiers.None) => GridCommand.Open,
        (VirtualKey.Space, VirtualKeyModifiers.None) => GridCommand.Preview,
        (VirtualKey.F2, VirtualKeyModifiers.None) => GridCommand.Rename,
        (VirtualKey.A, VirtualKeyModifiers.Control) => GridCommand.SelectAll,
        (VirtualKey.C, VirtualKeyModifiers.Control) => GridCommand.Copy,
        (VirtualKey.X, VirtualKeyModifiers.Control) => GridCommand.Cut,
        (VirtualKey.V, VirtualKeyModifiers.Control) => GridCommand.Paste,
        (VirtualKey.Delete, VirtualKeyModifiers.None) => GridCommand.Trash,
        (VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) => GridCommand.NewFolder,
        (VirtualKey.Z, VirtualKeyModifiers.Control) => GridCommand.Undo,
        (VirtualKey.F, VirtualKeyModifiers.Control) => GridCommand.Search,
        (VirtualKey.F10, VirtualKeyModifiers.Shift) or (VirtualKey.Application, VirtualKeyModifiers.None) => GridCommand.ContextMenu,
        _ => null,
    };

    static VirtualKeyModifiers CurrentModifiers()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var modifiers = VirtualKeyModifiers.None;
        if (Down(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
        if (Down(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
        if (Down(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    void HandleStationShortcut(KeyRoutedEventArgs e)
    {
        if (e.Handled || destroyed || changingMode || resizing || DragActive || ModalActive || activeContentDialog != null) return;
        var source = e.OriginalSource as DependencyObject;
        var modifiers = CurrentModifiers();
        bool escape = e.Key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None;
        if (escape && e.KeyStatus.WasKeyDown) { e.Handled = true; return; }
        if (folderBrowser is { } browser) {
            browser.HandleShortcut(e, source, modifiers);
            return;
        }
        if (ShowingSettings) {
            if (escape || e.Key == VirtualKey.S && modifiers == VirtualKeyModifiers.Control)
                e.Handled = e.KeyStatus.WasKeyDown || settingsPage!.HandleShortcut(saveChanges: !escape);
            return;
        }
        if (escape) {
            e.Handled = true;
            if (desktopSearch.Visibility == Visibility.Visible) {
                HideSearch();
                FocusGridSelection(files);
            } else Hide();
            return;
        }
        if (source != null && InsideElement(desktopSearch, source) && e.Key == VirtualKey.Down && modifiers == VirtualKeyModifiers.None) {
            e.Handled = FocusDesktopSearchResult();
            return;
        }
        if (App.Station.LibraryOffline || Searching && !(e.Key == VirtualKey.F && modifiers == VirtualKeyModifiers.Control)) return;
        if (TextInputTarget(source)) return;
        bool inGrid = FromGrid(files, source) || source == root;
        if (inGrid && MoveGridFocus(files, source, e.Key, modifiers, ref selectionAnchor)) {
            e.Handled = true;
            return;
        }
        bool writable = !App.Station.ReadOnly;
        var items = Selected();
        bool single = items.Length == 1;
        var command = GridShortcut(e.Key, modifiers);
        Func<Task>? action = command switch {
            GridCommand.Open when inGrid && single && (items[0].Kind != StationKind.Text || writable) => () => OpenItem(items[0]),
            GridCommand.Preview when inGrid && single => () => items[0].Kind == StationKind.Folder ? OpenItem(items[0]) : PreviewItem(items[0]),
            GridCommand.Rename when inGrid && single && writable && CanRenameStationItem(items[0]) => () => RenameItem(items[0]),
            GridCommand.Copy when items.Length > 0 => () => CopyDesktopItems(items),
            GridCommand.Cut when inGrid && writable && StationStore.CanMoveLibraryItems(items)
                => () => CutLibrarySelection(App.Station.LibraryRoot, items),
            GridCommand.Paste when writable => PasteContent,
            GridCommand.Trash when inGrid && writable && items.Length > 0 => () => TrashDesktopItems(items),
            GridCommand.NewFolder when writable => NewDesktopFolder,
            GridCommand.Undo when writable && LatestLibraryMove != null => UndoLibraryMove,
            GridCommand.Undo when writable && RecoverableDesktopUndo() is { Length: > 0 } recoverable => () => UndoDesktopRemoval(recoverable),
            _ => null,
        };
        if (action != null) {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown) _ = Run(action);
            return;
        }
        switch (command) {
            case GridCommand.SelectAll:
                e.Handled = true;
                files.SelectAll();
                break;
            case GridCommand.Search:
                e.Handled = true;
                ShowSearch();
                break;
            case GridCommand.ContextMenu when inGrid:
                e.Handled = true;
                if (!e.KeyStatus.WasKeyDown) ShowKeyboardMenu(files, source, SelectContextCard, () => BuildItemMenu(Selected()), BuildBackgroundMenu);
                break;
        }
    }

    internal static bool FromGrid(GridView grid, DependencyObject? source) => InsideElement(grid, source);

    internal static bool InsideElement(DependencyObject element, DependencyObject? source)
    {
        for (; source != null; source = VisualTreeHelper.GetParent(source))
            if (source == element) return true;
        return false;
    }

    internal static bool FocusSearchResult(GridView grid, ref Guid? anchor)
    {
        if (grid.Items.Cast<GridViewItem>().OrderBy(StationGridPanel.Cell).FirstOrDefault() is not { } first) return false;
        SelectGridCard(grid, first, false, false, ref anchor);
        grid.ScrollIntoView(first);
        return first.Focus(FocusState.Keyboard);
    }

    static GridViewItem? FocusedGridCard(GridView grid, DependencyObject? source)
    {
        for (; source != null && source != grid; source = VisualTreeHelper.GetParent(source))
            if (source is GridViewItem card && grid.Items.Contains(card)) return card;
        return null;
    }

    internal static void FocusGridSelection(GridView grid)
    {
        if (grid.SelectedItem is not GridViewItem card || !card.Focus(FocusState.Keyboard))
            grid.Focus(FocusState.Keyboard);
    }

    internal static bool MoveGridFocus(GridView grid, DependencyObject? source, VirtualKey key, VirtualKeyModifiers modifiers, ref Guid? anchor)
    {
        if (modifiers is not (VirtualKeyModifiers.None or VirtualKeyModifiers.Shift)
            || key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return false;
        var cards = grid.Items.Cast<GridViewItem>().OrderBy(StationGridPanel.Cell).ToArray();
        if (cards.Length == 0) return false;
        var current = FocusedGridCard(grid, source) ?? grid.SelectedItem as GridViewItem;
        var next = current == null ? cards[0] : NextGridCard(cards, current, Math.Max(1, (grid.ItemsPanelRoot as StationGridPanel)?.Columns ?? 1), key);
        if (next == null) return true;
        bool extend = modifiers == VirtualKeyModifiers.Shift;
        var anchorId = anchor;
        if (extend && current != null && !cards.Any(card => ((StationItem)card.Tag).Id == anchorId))
            anchor = ((StationItem)current.Tag).Id;
        SelectGridCard(grid, next, false, extend && current != null, ref anchor);
        grid.ScrollIntoView(next);
        next.Focus(FocusState.Keyboard);
        return true;
    }

    internal static GridViewItem? NextGridCard(GridViewItem[] cards, GridViewItem current, int columns, VirtualKey key)
    {
        int cell = StationGridPanel.Cell(current);
        int row = cell / columns, column = cell % columns;
        var candidates = cards.Select(card => (Card: card, Cell: StationGridPanel.Cell(card)));
        return key switch {
            VirtualKey.Left => candidates.Where(item => item.Cell / columns == row && item.Cell < cell)
                .OrderByDescending(item => item.Cell).FirstOrDefault().Card,
            VirtualKey.Right => candidates.Where(item => item.Cell / columns == row && item.Cell > cell)
                .OrderBy(item => item.Cell).FirstOrDefault().Card,
            VirtualKey.Up => candidates.Where(item => item.Cell / columns < row).OrderByDescending(item => item.Cell / columns)
                .ThenBy(item => Math.Abs(item.Cell % columns - column)).FirstOrDefault().Card,
            VirtualKey.Down => candidates.Where(item => item.Cell / columns > row).OrderBy(item => item.Cell / columns)
                .ThenBy(item => Math.Abs(item.Cell % columns - column)).FirstOrDefault().Card,
            _ => null,
        };
    }

    internal static void ShowKeyboardMenu(GridView grid, DependencyObject? source, Action<GridViewItem> select,
        Func<MenuFlyout> itemMenu, Func<MenuFlyout> backgroundMenu)
    {
        if ((FocusedGridCard(grid, source) ?? grid.SelectedItem as GridViewItem) is { } card) {
            select(card);
            itemMenu().ShowAt(card);
        } else backgroundMenu().ShowAt(grid);
    }
}

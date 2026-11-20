using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Halo;

internal sealed partial class StationWindow
{
    internal void VerifyGridShortcuts(Action<string, bool> check)
    {
        check("GridShortcutsMatchExactModifiers", GridShortcut(VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift) == GridCommand.NewFolder
            && GridShortcut(VirtualKey.N, VirtualKeyModifiers.Control) == null
            && GridShortcut(VirtualKey.Delete, VirtualKeyModifiers.None) == GridCommand.Trash
            && GridShortcut(VirtualKey.Delete, VirtualKeyModifiers.Shift) == null
            && GridShortcut(VirtualKey.C, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu) == null
            && GridShortcut(VirtualKey.F10, VirtualKeyModifiers.Shift) == GridCommand.ContextMenu);

        var first = new GridViewItem();
        var last = new GridViewItem();
        var lower = new GridViewItem();
        GridViewItem[] cards = [first, last, lower];
        StationGridPanel.Position(first, 0, 4);
        StationGridPanel.Position(last, 3, 4);
        StationGridPanel.Position(lower, 9, 4);
        check("GridShortcutsNavigateSparseRowsWithoutWrap",
            NextGridCard(cards, first, 4, VirtualKey.Right) == last
            && NextGridCard(cards, last, 4, VirtualKey.Right) == null
            && NextGridCard(cards, first, 4, VirtualKey.Down) == lower
            && NextGridCard(cards, lower, 4, VirtualKey.Up) == first);
    }
}

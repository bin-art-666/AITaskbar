using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Halo;
internal sealed partial class StationWindow
{
    GridMarqueeSelection selectionMarquee = null!;

    internal static bool ContentBlankTarget(DependencyObject? target, DependencyObject boundary) {
        while (target != null) {
            if (target is GridViewItem or ButtonBase or Thumb or ScrollBar or TextBox or PasswordBox) return false;
            if (ReferenceEquals(target, boundary)) return true;
            target = VisualTreeHelper.GetParent(target);
        }
        return false;
    }
    void InitializeBlankSelection() {
        selectionMarquee = new(root, files, statusHost,
            () => !ShowingSettings && !Searching && !App.Station.LibraryOffline && folderBrowser == null && !changingMode && !resizing && !DragActive && !ModalActive
                && activeContentDialog == null && cardPress == null,
            KeepOpenForDrag, ClearDesktopSelection, anchor => selectionAnchor = anchor, UpdateSelectionDetails);
    }

    internal void VerifyMarqueeSelection(Action<string, bool> check) => selectionMarquee.Verify(check, "Station");
    internal Task VerifyMarqueeEdgeScroll(Action<string, bool> check) => selectionMarquee.VerifyEdgeScroll(check);
}

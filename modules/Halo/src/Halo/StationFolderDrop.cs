using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Halo;

internal sealed partial class StationWindow
{
    const string FolderDropCaption = FolderDropIntent.Caption;
    static TimeSpan FolderDropDelay => FolderDropIntent.Delay;
    FolderDropIntent? folderDrop;
    DispatcherQueueTimer? folderDropTimer => folderDrop?.Timer;
    Guid? folderDropCandidate => folderDrop?.Candidate;
    Guid? armedFolderDrop => folderDrop?.Armed;
    StackPanel folderDropCue => folderDrop!.Cue;

    void InitializeFolderDrop() =>
        folderDrop = new(DispatcherQueue, insertion, () => FolderTargetAt(orderPointer), card => DrawDropFrame(FolderCardFrame(card)));

    Rect FolderCardFrame(GridViewItem card) => FolderDropIntent.CardFrame(files, card);

    static bool OverlapsMostOfPreview(Rect preview, Rect target) => FolderDropIntent.OverlapsMostOfPreview(preview, target);

    GridViewItem? FolderTargetAt(Point point)
    {
        if (!CanReorderGrid || movingIds.Count != 1 || !movingIds.Contains(dragAnchor)
            || !StationStore.CanGroupFile(App.Station.Items.FirstOrDefault(item => item.Id == dragAnchor))
            || DragPreviewFrameAt(point) is not { } preview) return null;
        return FolderDropIntent.FindTarget(files, preview, dragAnchor, position => GridTarget(position, false));
    }

    void TrackFolderDrop(Point point) => folderDrop!.Track(FolderTargetAt(point));
    Guid? FolderDropOnRelease(Point point) => folderDrop?.OnRelease(FolderTargetAt(point));
    void ResetFolderDrop() => folderDrop?.Reset();
}

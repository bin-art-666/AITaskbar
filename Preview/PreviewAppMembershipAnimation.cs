using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AITaskbar;

internal sealed partial class PreviewApplicationStrip
{
    private static readonly StringComparer AppKeys = StringComparer.OrdinalIgnoreCase;
    private const int MembershipExitMs = 155, MembershipSlideMs = 220, MembershipTransferMs = 255, MembershipFillDelayMs = 30;
    private readonly List<MembershipMotion> membershipMotions = [];
    private readonly List<(FrameworkElement Element, TranslateTransform Offset, double X)> membershipChrome = [];
    private long membershipStarted;

    private sealed record MembershipSnapshot(List<IconPosition> Icons, Dictionary<FrameworkElement, Point> Chrome,
        nint Foreground, string? RevealRunningKey);

    private sealed record IconPosition(AppButton View, Point Position, double Opacity, double Scale, Canvas? Visual = null);

    private sealed record MembershipMotion(AppButton View, AppButton? Destination, Canvas Visual, CompositeTransform Transform,
        Point From, Point To, double FromOpacity, double FromScale, bool CrossesDivider);

    private MembershipSnapshot? CaptureMembershipChange(AppGroup[] nextGroups)
    {
        if (!Surface.IsLoaded || Shown.FirstOrDefault() is null || !system.AnimationsEnabled || accessibility.HighContrast)
            return null;
        // A window count or an icon reorder within one section does not change section membership.
        var oldGroups = groups.ToDictionary(group => group.Key, AppKeys);
        if (nextGroups.Length == groups.Length && nextGroups.All(group => oldGroups.TryGetValue(group.Key, out var previous) &&
            (previous.Pin is null) == (group.Pin is null))) return null;

        var ongoing = membershipMotions.Where(motion => motion.Destination is not null)
            .ToDictionary(motion => motion.Destination!.Button);
        var icons = new List<IconPosition>();
        foreach (var view in Shown)
        {
            if (ongoing.TryGetValue(view.Button, out var motion))
                icons.Add(CurrentMembershipPosition(motion, view));
            else
                icons.Add(new(view, PositionInMotionLayer(view.Button), view.Button.Opacity, 1));
        }
        foreach (var motion in membershipMotions.Where(motion => motion.Destination is null))
            icons.Add(CurrentMembershipPosition(motion, motion.View));

        var chrome = new[] { (FrameworkElement)AppCenterButton, taskViewButton, separator }
            .Where(element => element.Visibility == Visibility.Visible)
            .ToDictionary(element => element, PositionInMotionLayer);
        var reveal = nextGroups.FirstOrDefault(group => group.Pin is null &&
            icons.Any(icon => icon.View.Group.Pin is not null && SameApp(icon.View.Group, group)))?.Key;
        return new(icons, chrome, foreground, reveal);
    }

    private void AnimateMembershipChange(MembershipSnapshot snapshot)
    {
        StopMembershipAnimation();
        // Read final positions after both sections have laid out and the strip has recentered.
        Surface.UpdateLayout();
        foreach (var (element, from) in snapshot.Chrome)
        {
            if (element.Visibility != Visibility.Visible) continue;
            double offset = from.X - PositionInMotionLayer(element).X;
            if (Math.Abs(offset) < .5) continue;
            var transform = new TranslateTransform { X = offset };
            element.RenderTransform = transform;
            membershipChrome.Add((element, transform, offset));
        }

        var previous = snapshot.Icons;
        foreach (var view in Shown)
        {
            var end = PositionInMotionLayer(view.Button);
            int index = previous.FindIndex(icon => AppKeys.Equals(icon.View.Group.Key, view.Group.Key));
            if (index < 0) index = previous.FindIndex(icon => SameApp(icon.View.Group, view.Group));
            if (index < 0)
            {
                AddMembershipMotion(new(view, end, 0, .9), view, end, foreground);
                continue;
            }
            var start = previous[index];
            previous.RemoveAt(index);
            bool transfer = (start.View.Group.Pin is null) != (view.Group.Pin is null);
            if (transfer || Math.Abs(start.Position.X - end.X) >= .5 || Math.Abs(start.Position.Y - end.Y) >= .5 ||
                start.Opacity < view.Button.Opacity - .01 || Math.Abs(start.Scale - 1) > .01)
                AddMembershipMotion(start, view, end, foreground, transfer);
        }
        foreach (var icon in previous)
            if (icon.Opacity > .01) AddMembershipMotion(icon, null, icon.Position, snapshot.Foreground);
        attentionBadges.Reposition();

        if (membershipMotions.Count == 0 && membershipChrome.Count == 0) return;
        iconHover.Suspend(true);
        membershipStarted = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += RenderMembershipAnimation;
    }

    private void AddMembershipMotion(IconPosition start, AppButton? destination, Point end, nint activeWindow, bool transfer = false)
    {
        var visual = start.Visual ?? CreateFloatingIcon(destination ?? start.View, activeWindow);
        var transform = new CompositeTransform
        {
            CenterX = iconSize / 2d, CenterY = TaskbarAppFeedback.TopSpace + iconSize / 2d,
            ScaleX = start.Scale, ScaleY = start.Scale
        };
        visual.RenderTransform = transform;
        visual.Opacity = start.Opacity;
        Canvas.SetLeft(visual, start.Position.X);
        Canvas.SetTop(visual, start.Position.Y);
        Canvas.SetZIndex(visual, transfer ? 2 : destination is null ? 0 : 1);
        AppDragSurface.Children.Add(visual);
        if (destination is not null) destination.Feedback.Surface.Opacity = 0;
        membershipMotions.Add(new(start.View, destination, visual, transform, start.Position, end, start.Opacity, start.Scale, transfer));
    }

    private void RenderMembershipAnimation(object? sender, object args)
    {
        if (!system.AnimationsEnabled || accessibility.HighContrast)
        {
            StopMembershipAnimation();
            return;
        }
        double elapsed = Stopwatch.GetElapsedTime(membershipStarted).TotalMilliseconds;
        bool complete = true;
        foreach (var motion in membershipMotions)
        {
            bool exiting = motion.Destination is null;
            int duration = exiting ? MembershipExitMs : motion.CrossesDivider ? MembershipTransferMs : MembershipSlideMs;
            double delay = exiting || motion.CrossesDivider ? 0 : MembershipFillDelayMs;
            double time = Math.Clamp((elapsed - delay) / duration, 0, 1);
            complete &= time == 1;
            double progress = time * time * (3 - 2 * time);
            double lift = motion.CrossesDivider ? 3 * Math.Sin(Math.PI * time) : 0;
            motion.Transform.TranslateX = (motion.To.X - motion.From.X) * progress;
            motion.Transform.TranslateY = (motion.To.Y - motion.From.Y) * progress - lift;
            motion.Visual.Opacity = Mix(motion.FromOpacity, motion.Destination?.Button.Opacity ?? 0, progress);
            motion.Transform.ScaleX = motion.Transform.ScaleY = Mix(motion.FromScale, exiting ? .78 : 1, progress);
        }
        double chrome = Math.Clamp((elapsed - MembershipFillDelayMs) / MembershipSlideMs, 0, 1);
        foreach (var (_, offset, from) in membershipChrome)
            offset.X = from * (1 - chrome * chrome * (3 - 2 * chrome));
        if (complete && (membershipChrome.Count == 0 || chrome == 1)) StopMembershipAnimation();
    }

    private void StopMembershipAnimation()
    {
        if (membershipMotions.Count == 0 && membershipChrome.Count == 0) return;
        CompositionTarget.Rendering -= RenderMembershipAnimation;
        foreach (var motion in membershipMotions)
        {
            if (motion.Destination is { } view) view.Feedback.Surface.Opacity = 1;
            AppDragSurface.Children.Remove(motion.Visual);
        }
        foreach (var (element, offset, _) in membershipChrome)
            if (ReferenceEquals(element.RenderTransform, offset)) element.RenderTransform = null;
        membershipMotions.Clear();
        membershipChrome.Clear();
        attentionBadges.Reposition();
        iconHover.Suspend(false);
    }

    private Point PositionInMotionLayer(FrameworkElement element) =>
        element.TransformToVisual(AppDragSurface).TransformPoint(new());

    private static IconPosition CurrentMembershipPosition(MembershipMotion motion, AppButton view) =>
        new(view, new(motion.From.X + motion.Transform.TranslateX, motion.From.Y + motion.Transform.TranslateY),
            motion.Visual.Opacity, motion.Transform.ScaleX, motion.Visual);

    private static bool SameApp(AppGroup previous, AppGroup next) =>
        AppKeys.Equals(previous.Key, next.Key) ||
        (previous.Pin is null) != (next.Pin is null) &&
        previous.Windows.Any(before => next.Windows.Any(after => before.Handle == after.Handle && before.ProcessId == after.ProcessId));
}

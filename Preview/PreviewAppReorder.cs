using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed partial class PreviewApplicationStrip
{
    private const double AppDragThreshold = 5, AppBoundaryTolerance = 4;
    private const double AppLift = 3, AppLiftScale = 1.06;
    private const double LiftDurationMs = 100, ShiftDurationMs = 130, LandDurationMs = 150;
    private AppDragPress? dragPress;
    private AppDrag? appDrag;
    private Button? suppressedClick;
    internal Canvas AppDragSurface { get; } = new() { IsHitTestVisible = false };
    private bool AppDragInteraction => dragPress is not null || appDrag is not null;

    private sealed record AppDragPress(AppRange Range, AppButton View, uint PointerId, Point Start);

    private sealed class AppDrag(AppButton[] views, Point[] slots, int source, Rect bounds,
        CompositeTransform floating, TranslateTransform[] transforms, bool animate)
    {
        internal readonly AppButton[] Views = views;
        internal readonly Point[] Slots = slots;
        internal readonly int Source = source;
        internal readonly Rect Bounds = bounds;
        internal readonly CompositeTransform Floating = floating;
        internal readonly bool Animate = animate;
        internal readonly TranslateTransform[] Transforms = transforms;
        internal readonly double[] BaseOffset = transforms.Select(transform => transform.X).ToArray();
        internal readonly double[] From = transforms.Select(transform => transform.X).ToArray();
        internal readonly double[] To = transforms.Select(transform => transform.X).ToArray();
        internal readonly long LiftStarted = Stopwatch.GetTimestamp();
        internal int Target = source;
        internal long ShiftStarted = Stopwatch.GetTimestamp();
        internal long? LandStarted;
        internal double LandFromX, LandFromY, LandFromScale;
    }

    private bool AppClickSuppressed(Button button) => appDrag is not null || ReferenceEquals(suppressedClick, button);

    private void EnableAppReordering(Button button, AppRange range)
    {
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var pointer = args.GetCurrentPoint(items);
            if (pointer.PointerDeviceType != PointerDeviceType.Mouse || !pointer.Properties.IsLeftButtonPressed ||
                AppDragInteraction || scrollStarted is not null) return;
            var view = range.Buttons.Find(item => ReferenceEquals(item.Button, button));
            if (view is null) return;
            suppressedClick = null;
            if (!button.CapturePointer(args.Pointer)) return;
            DismissThumbnails();
            dragPress = new(range, view, args.Pointer.PointerId, pointer.Position);
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (dragPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            var point = args.GetCurrentPoint(items).Position;
            if (appDrag is null && Math.Abs(point.X - press.Start.X) < AppDragThreshold) return;
            if (appDrag is null) BeginAppDrag(press);
            MoveAppDrag(press, point);
            args.Handled = true;
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (dragPress is not { } press || press.PointerId != args.Pointer.PointerId) return;
            bool dragged = appDrag is not null;
            FinishAppDragPress(press, args.GetCurrentPoint(items).Position);
            if (dragged) args.Handled = true;
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
        {
            if (dragPress is { } press && press.PointerId == args.Pointer.PointerId)
                Surface.DispatcherQueue.TryEnqueue(() => FinishAppDragPress(press));
        }), handledEventsToo: true);
        button.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Escape || dragPress is not { } press || appDrag is null) return;
            args.Handled = true;
            FinishAppDragPress(press);
            button.ReleasePointerCaptures();
        }), handledEventsToo: true);
    }

    private void BeginAppDrag(AppDragPress press)
    {
        StopMembershipAnimation();
        var views = press.Range.Buttons.ToArray();
        int source = Array.FindIndex(views, view => ReferenceEquals(view.Button, press.View.Button));
        if (source < 0) return;
        var slots = views.Select(view => view.Button.TransformToVisual(items).TransformPoint(new Point())).ToArray();
        var start = slots[0];
        var bounds = new Rect(start.X, start.Y, slots[^1].X + iconSize - start.X, press.Range.Panel.ActualHeight);
        var transform = new CompositeTransform { CenterX = iconSize / 2d, CenterY = TaskbarAppFeedback.TopSpace + iconSize / 2d };
        var floating = CreateFloatingIcon(press.View, foreground);
        floating.RenderTransform = transform;
        var origin = press.View.Button.TransformToVisual(AppDragSurface).TransformPoint(new Point());
        Canvas.SetLeft(floating, origin.X);
        Canvas.SetTop(floating, origin.Y);
        AppDragSurface.Children.Add(floating);

        var neighbors = views.Select(view => view.Button.RenderTransform as TranslateTransform ?? new TranslateTransform()).ToArray();
        var drag = appDrag = new(views, slots, source, bounds, transform, neighbors, system.AnimationsEnabled && !accessibility.HighContrast);
        for (int index = 0; index < views.Length; index++) views[index].Button.RenderTransform = drag.Transforms[index];
        press.View.Button.Opacity = 0;
        attentionBadges.Reposition();
        suppressedClick = press.View.Button;
        press.View.Button.Focus(FocusState.Pointer);
        DismissThumbnails();
        iconHover.Suspend(true);
        if (drag.Animate) CompositionTarget.Rendering += AnimateAppDrag;
    }

    private Canvas CreateFloatingIcon(AppButton view, nint activeWindow)
    {
        var source = view.IconPlate;
        var graphic = source.Artwork.Child;
        var plate = new TaskbarIconPlate(iconSize, graphic is Image image
            ? new Image { Source = image.Source, Width = image.Width, Height = image.Height, Stretch = image.Stretch }
            : new FontIcon { Glyph = ((FontIcon)graphic).Glyph, FontSize = ((FontIcon)graphic).FontSize,
                Foreground = ((FontIcon)graphic).Foreground });
        plate.Artwork.Background = source.Artwork.Background;
        TaskbarIconOutline.Apply(plate.Outline, iconLayout, accessibility.HighContrast);
        var feedback = new TaskbarAppFeedback(plate, iconSize);
        feedback.ApplyStyle(feedbackStyle);
        var (active, background) = TaskbarAppFeedback.DotColors(feedbackStyle, system, accessibility.HighContrast);
        feedback.ShowState(IndicatorState(view.Group.Windows.Count > 0, view.Group.Windows.Any(window => window.Handle == activeWindow)),
            active, background, feedbackLight);
        if (HasAppAttention(view.Group))
        {
            var badge = TaskbarAttentionIndicator.CreateBadge();
            if (accessibility.HighContrast)
                TaskbarAttentionIndicator.ColorBadge(badge, system.UIElementColor(UIElementType.Highlight));
            Canvas.SetLeft(badge, TaskbarAttentionIndicator.BadgeLeft(iconSize));
            Canvas.SetTop(badge, TaskbarAppFeedback.TopSpace - TaskbarAttentionIndicator.BadgeOverhang);
            feedback.Surface.Children.Add(badge);
        }
        return feedback.Surface;
    }

    private void MoveAppDrag(AppDragPress press, Point point)
    {
        if (appDrag is not { } drag) return;
        drag.Floating.TranslateX = point.X - press.Start.X;
        int target = drag.Target;
        if (!drag.Bounds.Contains(point)) target = drag.Source;
        else
        {
            while (target + 1 < drag.Slots.Length && point.X >= Boundary(target) + AppBoundaryTolerance) target++;
            while (target > 0 && point.X <= Boundary(target - 1) - AppBoundaryTolerance) target--;
        }
        RetargetNeighbors(drag, target);
        if (!drag.Animate) attentionBadges.Reposition();

        // Boundaries use the original slots so a moving neighbor cannot move its own hit threshold.
        double Boundary(int left) => drag.Slots[left < drag.Source ? left : left + 1].X + iconSize / 2d;
    }

    private void RetargetNeighbors(AppDrag drag, int target)
    {
        if (target == drag.Target) return;
        drag.Target = target;
        drag.ShiftStarted = Stopwatch.GetTimestamp();
        for (int index = 0; index < drag.Views.Length; index++)
        {
            int slot = drag.Source < index && index <= target ? index - 1
                : target <= index && index < drag.Source ? index + 1 : index;
            drag.From[index] = drag.Transforms[index].X;
            drag.To[index] = drag.BaseOffset[index] + drag.Slots[slot].X - drag.Slots[index].X;
            if (!drag.Animate) drag.Transforms[index].X = drag.To[index];
        }
    }

    private void FinishAppDragPress(AppDragPress press, Point? release = null)
    {
        if (!ReferenceEquals(dragPress, press)) return;
        dragPress = null;
        if (appDrag is not { } drag)
        {
            QueueAppDragRefresh();
            return;
        }
        int destination = drag.Source;
        if (release is { } point && drag.Bounds.Contains(point))
        {
            MoveAppDrag(press, point);
            if (drag.Target != drag.Source)
            {
                try
                {
                    var source = press.View.Group;
                    var target = drag.Views[drag.Target].Group;
                    bool moved = source.Pin is { } pin
                        ? pins.Move(pin, target.Pin!, after: drag.Target > drag.Source)
                        : runningOrder.Move(source.Key, target.Key);
                    if (moved)
                        destination = drag.Target;
                    report("");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    report("无法保存固定应用顺序。");
                }
            }
        }
        RetargetNeighbors(drag, destination);
        if (!drag.Animate)
        {
            ClearAppDrag();
            QueueAppDragRefresh();
            return;
        }
        drag.LandFromX = drag.Floating.TranslateX;
        drag.LandFromY = drag.Floating.TranslateY;
        drag.LandFromScale = drag.Floating.ScaleX;
        drag.LandStarted = Stopwatch.GetTimestamp();
    }

    private void AnimateAppDrag(object? sender, object args)
    {
        if (appDrag is not { } drag) return;
        double shift = EaseOut(drag.ShiftStarted, ShiftDurationMs);
        for (int index = 0; index < drag.Transforms.Length; index++)
            drag.Transforms[index].X = Mix(drag.From[index], drag.To[index], shift);
        attentionBadges.Reposition();
        if (drag.LandStarted is not { } landing)
        {
            double lift = EaseOut(drag.LiftStarted, LiftDurationMs);
            drag.Floating.TranslateY = -AppLift * lift;
            drag.Floating.ScaleX = drag.Floating.ScaleY = Mix(1, AppLiftScale, lift);
            return;
        }
        double land = EaseOut(landing, LandDurationMs);
        drag.Floating.TranslateX = Mix(drag.LandFromX, drag.Slots[drag.Target].X - drag.Slots[drag.Source].X, land);
        drag.Floating.TranslateY = Mix(drag.LandFromY, 0, land);
        drag.Floating.ScaleX = drag.Floating.ScaleY = Mix(drag.LandFromScale, 1, land);
        if (land == 1)
        {
            ClearAppDrag();
            suppressedClick = null;
            Refresh(preserveRangeStart: true);
        }
    }

    private static double EaseOut(long start, double duration)
    {
        double remaining = 1 - Math.Clamp(Stopwatch.GetElapsedTime(start).TotalMilliseconds / duration, 0, 1);
        return 1 - remaining * remaining * remaining;
    }

    private static double Mix(double from, double to, double weight) => from + (to - from) * weight;

    private void ClearAppDrag()
    {
        if (appDrag is not { } drag) return;
        appDrag = null;
        if (drag.Animate) CompositionTarget.Rendering -= AnimateAppDrag;
        for (int index = 0; index < drag.Transforms.Length; index++)
            drag.Transforms[index].X = drag.BaseOffset[index];
        drag.Views[drag.Source].Button.Opacity = 1;
        AppDragSurface.Children.Clear();
        attentionBadges.Reposition();
        iconHover.Suspend(false);
    }

    private void ResetAppDragInteraction()
    {
        var pressed = dragPress?.View.Button;
        dragPress = null;
        ClearAppDrag();
        pressed?.ReleasePointerCaptures();
        suppressedClick = null;
    }

    private void QueueAppDragRefresh() => Surface.DispatcherQueue.TryEnqueue(() =>
    {
        if (AppDragInteraction) return;
        suppressedClick = null;
        Refresh(preserveRangeStart: true);
    });
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal readonly record struct NotificationTiming(int NormalSeconds, int ErrorSeconds)
{
    internal static NotificationTiming From(HaloSettings settings) => new(settings.NormalNotificationSeconds, settings.ErrorNotificationSeconds);
}

internal sealed partial class StationWindow
{
    NotificationTiming? notificationTimingPreview;
    bool notificationImportant;
    readonly Button statusUndo = new() { Visibility = Visibility.Collapsed, Margin = new(0, 3, 6, 3) };
    Func<Task>? notificationUndo;
    internal NotificationTiming NotificationTiming => notificationTimingPreview ?? Halo.NotificationTiming.From(App.Settings.Data);

    internal void PreviewNotificationTiming(NotificationTiming? timing)
    {
        var previous = NotificationTiming;
        notificationTimingPreview = timing;
        if (previous != NotificationTiming && feedback.IsRunning) StartNotificationTimer();
    }
    void StartNotificationTimer()
    {
        feedback.Stop();
        feedback.Interval = TimeSpan.FromSeconds(notificationImportant ? NotificationTiming.ErrorSeconds : NotificationTiming.NormalSeconds);
        feedback.Start();
    }
    internal void Notify(string text, bool important = false, string? undoLabel = null, Func<Task>? undoAction = null)
    {
        if (destroyed) return;
        notificationImportant = important; status.Text = text; statusHost.Visibility = Visibility.Visible;
        statusWarning.Visibility = important ? Visibility.Visible : Visibility.Collapsed;
        SetNotificationUndo(undoLabel, undoAction);
        StartNotificationTimer();
    }
    void NotifyProgress(string text)
    {
        if (destroyed) return;
        feedback.Stop(); status.Text = text; statusHost.Visibility = Visibility.Visible;
        statusWarning.Visibility = Visibility.Collapsed;
        SetNotificationUndo(null, null);
    }
    void ClearNotification()
    { feedback.Stop(); statusHost.Visibility = Visibility.Collapsed; status.Text = ""; SetNotificationUndo(null, null); }

    void SetNotificationUndo(string? label, Func<Task>? action)
    {
        notificationUndo = action;
        statusUndo.Content = label;
        statusUndo.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
        statusHost.IsHitTestVisible = action != null;
        ToolTipService.SetToolTip(statusUndo, action == null ? null : "Ctrl+Z");
    }
}

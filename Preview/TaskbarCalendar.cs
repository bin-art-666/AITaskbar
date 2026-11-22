using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AITaskbar;

internal sealed class TaskbarCalendar : IDisposable
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private readonly TaskbarPanelFlyout panel;
    private readonly SolidColorBrush ink = new();
    private readonly TextBlock heading;
    private readonly CalendarView calendar;
    private QuickControlAppearance style;
    private LumaBar.BarAppearance? taskbar;
    private bool light;

    internal TaskbarCalendar(Button anchor, QuickControlAppearance appearance, Action opening)
    {
        style = appearance;
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var scene = new Grid { Width = 326 };
        var backplate = new Border { CornerRadius = new CornerRadius(TaskbarPanelFlyout.Radius) };
        heading = new TextBlock { Foreground = ink, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 5, 0) };
        calendar = new CalendarView { Language = Chinese.Name, SelectionMode = CalendarViewSelectionMode.None,
            IsGroupLabelVisible = false, Height = 310, BorderThickness = new Thickness(0), Background = clear,
            CalendarItemBackground = clear, OutOfScopeBackground = clear, Foreground = ink, CalendarItemForeground = ink };
        AutomationProperties.SetName(calendar, "日历");
        var body = new StackPanel { Margin = new Thickness(13), Spacing = 5 };
        body.Children.Add(heading);
        body.Children.Add(calendar);
        scene.Children.Add(backplate);
        scene.Children.Add(body);
        panel = new(anchor, scene, backplate);
        panel.Opening += () =>
        {
            opening();
            ApplyPalette();
            var now = DateTimeOffset.Now;
            heading.Text = now.ToString("M月d日 dddd", Chinese);
            calendar.DisplayMode = CalendarViewDisplayMode.Month;
            calendar.SetDisplayDate(now);
            AutomationProperties.SetItemStatus(anchor, "已展开");
        };
        panel.Opened += () => calendar.Focus(FocusState.Programmatic);
        panel.Closed += () => AutomationProperties.SetItemStatus(anchor, "已收起");
        scene.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            Hide();
        }), true);
        AutomationProperties.SetItemStatus(anchor, "已收起");
        anchor.Click += (_, _) => { if (panel.IsOpen) Hide(); else panel.Show(); };
    }

    internal void Hide() => panel.Hide();

    internal void SetAppearance(QuickControlAppearance appearance)
    {
        style = appearance;
        if (panel.IsOpen) ApplyPalette();
    }

    internal void SetTaskbarAppearance(bool light, LumaBar.BarAppearance? appearance)
    {
        this.light = light;
        taskbar = appearance;
        if (panel.IsOpen) ApplyPalette();
    }

    private void ApplyPalette() => ink.Color = panel.ApplyPalette(style, light, taskbar).Text;

    public void Dispose() => panel.Dispose();
}

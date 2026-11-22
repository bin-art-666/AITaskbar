using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
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
        var scene = new Grid { Width = 350 };
        var backplate = new Border { CornerRadius = new CornerRadius(TaskbarPanelFlyout.Radius) };
        heading = new TextBlock { Foreground = ink, FontSize = 16, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(5, 0, 5, 0) };
        calendar = new CalendarView { Language = Chinese.Name, SelectionMode = CalendarViewSelectionMode.None,
            IsGroupLabelVisible = false, IsTodayHighlighted = true, Height = 360,
            BorderThickness = new Thickness(0), Background = clear,
            CalendarItemBackground = clear, OutOfScopeBackground = clear, Foreground = ink, CalendarItemForeground = ink,
            VerticalDayItemAlignment = VerticalAlignment.Top, CalendarViewDayItemStyle = DayItemStyle() };
        calendar.CalendarViewDayItemChanging += UpdateDayItem;
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

    private static Style DayItemStyle() => (Style)XamlReader.Load("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="CalendarViewDayItem">
            <Setter Property="MinHeight" Value="44" />
            <Setter Property="Padding" Value="0,5,0,0" />
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="CalendarViewDayItem">
                        <Grid Height="42" VerticalAlignment="Top">
                            <ContentPresenter Content="{TemplateBinding Tag}" IsHitTestVisible="False"
                                HorizontalAlignment="Center" VerticalAlignment="Top" Margin="0,23,0,0" />
                        </Grid>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
        """);

    private void UpdateDayItem(CalendarView sender, CalendarViewDayItemChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Phase != 0) return;
        var item = args.Item;
        var day = item.Date.Date;
        var note = TaskbarCalendarNotes.ForDate(day);
        var label = item.Tag as TextBlock ?? new TextBlock { FontSize = 11, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis };
        label.Text = note.Label;
        label.FontWeight = note.Emphasized ? FontWeights.SemiBold : FontWeights.Normal;
        bool today = day == DateTime.Today;
        label.Opacity = today || note.Emphasized ? 1 : .8;
        if (today)
            label.SetBinding(TextBlock.ForegroundProperty, new Binding { Source = sender, Path = new PropertyPath(nameof(CalendarView.TodayForeground)) });
        else label.Foreground = ink;
        if (item.Tag is null) item.Tag = label;
        ToolTipService.SetToolTip(item, note.Details.Length == 0 ? null : note.Details);
        AutomationProperties.SetHelpText(item, note.Details);
    }

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

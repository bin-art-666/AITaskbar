using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Globalization;
using Windows.System;
using Windows.UI.ViewManagement;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace AITaskbar;

internal sealed class TaskbarCalendar : IDisposable
{
    private static readonly CultureInfo Chinese = CultureInfo.GetCultureInfo("zh-CN");
    private readonly TaskbarPanelFlyout panel;
    private readonly SolidColorBrush ink = new();
    private readonly SolidColorBrush adjacentInk = new();
    private readonly SolidColorBrush todayButtonFill = new();
    private readonly SolidColorBrush detailFill = new(), detailOutline = new(), detailLabelInk = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly HashSet<CalendarViewDayItem> dayItems = [];
    private readonly Button todayButton;
    private readonly CalendarView calendar;
    private readonly Border dayDetail;
    private readonly TextBlock detailDate, detailLunar, detailTerm, detailFestivals, detailSchedule;
    private readonly DispatcherQueueTimer monthObserver;
    private QuickControlAppearance style;
    private LumaBar.BarAppearance? taskbar;
    private bool light;
    private DateTime displayedMonth;
    private DateTime? inspectedDay;
    private string? monthHeader;

    internal TaskbarCalendar(Button anchor, QuickControlAppearance appearance, Action opening)
    {
        style = appearance;
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var scene = new Grid { Width = 324 };
        var backplate = new Border { CornerRadius = new CornerRadius(TaskbarPanelFlyout.Radius) };
        todayButton = new Button { Content = "今", Width = 32, Height = 28,
            Padding = new Thickness(0), Margin = new Thickness(0, 9, 5, 0), CornerRadius = new CornerRadius(8),
            Foreground = ink, Background = todayButtonFill, BorderThickness = new Thickness(0),
            FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(todayButton, "回到今天");
        todayButton.Click += (_, _) => GoToToday();
        calendar = new CalendarView { Language = Chinese.Name, SelectionMode = CalendarViewSelectionMode.Single,
            CalendarIdentifier = CalendarIdentifiers.Gregorian,
            IsGroupLabelVisible = false, IsTodayHighlighted = true, Height = 360,
            BorderThickness = new Thickness(0), Background = clear,
            CalendarItemBackground = clear, OutOfScopeBackground = clear, Foreground = ink, CalendarItemForeground = ink,
            OutOfScopeForeground = adjacentInk, VerticalDayItemAlignment = VerticalAlignment.Top, CalendarViewDayItemStyle = DayItemStyle() };
        // Reserve the width of the Today button inside the native header's navigation row.
        calendar.Resources["CalendarViewNavigationNextButtonMargin"] = new Thickness(3, 6, todayButton.Width + 7, 7);
        calendar.CalendarViewDayItemChanging += UpdateDayItem;
        AutomationProperties.SetName(calendar, "日历");

        detailDate = new TextBlock { Foreground = ink, FontSize = 15, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetHeadingLevel(detailDate, AutomationHeadingLevel.Level1);
        var close = new Button { Content = new SymbolIcon(Symbol.Cancel), Width = 28, Height = 28,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = clear, Foreground = ink,
            HorizontalAlignment = HorizontalAlignment.Right };
        AutomationProperties.SetName(close, "关闭日期详情");
        ToolTipService.SetToolTip(close, "关闭日期详情");
        close.Click += (_, _) => { CloseDayDetail(); calendar.Focus(FocusState.Programmatic); };
        var detailHeader = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        detailDate.Margin = new Thickness(0, 0, 34, 0);
        detailHeader.Children.Add(detailDate);
        detailHeader.Children.Add(close);
        var detailRows = new Grid { ColumnSpacing = 12, RowSpacing = 3 };
        detailRows.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        detailRows.ColumnDefinitions.Add(new());
        detailLunar = AddDetailRow(detailRows, "农历");
        detailTerm = AddDetailRow(detailRows, "节气");
        detailFestivals = AddDetailRow(detailRows, "节日");
        detailSchedule = AddDetailRow(detailRows, "休班");
        var detailBody = new StackPanel();
        detailBody.Children.Add(detailHeader);
        detailBody.Children.Add(detailRows);
        dayDetail = new Border { Child = detailBody, Padding = new Thickness(11, 7, 11, 10),
            CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(1),
            Background = detailFill, BorderBrush = detailOutline, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(dayDetail, "日期详情");
        calendar.SelectedDatesChanged += ShowSelectedDay;

        var body = new Grid { Margin = new Thickness(13) };
        body.Children.Add(calendar);
        body.Children.Add(todayButton);
        body.Children.Add(dayDetail);
        scene.Children.Add(backplate);
        scene.Children.Add(body);
        monthObserver = anchor.DispatcherQueue.CreateTimer();
        monthObserver.Interval = TimeSpan.FromMilliseconds(120);
        monthObserver.Tick += (_, _) => UpdateDisplayedMonth();
        panel = new(anchor, scene, backplate);
        panel.Opening += () =>
        {
            opening();
            ApplyPalette();
            GoToToday();
            AutomationProperties.SetItemStatus(anchor, "已展开");
        };
        panel.Opened += () =>
        {
            monthObserver.Start();
            calendar.Focus(FocusState.Programmatic);
        };
        panel.Closed += () =>
        {
            monthObserver.Stop();
            CloseDayDetail();
            AutomationProperties.SetItemStatus(anchor, "已收起");
        };
        scene.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            if (inspectedDay.HasValue) { CloseDayDetail(); calendar.Focus(FocusState.Programmatic); }
            else Hide();
        }), true);
        AutomationProperties.SetItemStatus(anchor, "已收起");
        anchor.Click += (_, _) => { if (panel.IsOpen) Hide(); else panel.Show(); };
    }

    internal void Hide() => panel.Hide();

    private void GoToToday()
    {
        CloseDayDetail();
        var today = DateTimeOffset.Now;
        ToolTipService.SetToolTip(todayButton, $"回到今天 · {today.ToString("M月d日 dddd", Chinese)}");
        monthHeader = calendar.TemplateSettings?.HeaderText;
        SetDisplayedMonth(new DateTime(today.Year, today.Month, 1));
        calendar.DisplayMode = CalendarViewDisplayMode.Month;
        calendar.SetDisplayDate(today);
    }

    private TextBlock AddDetailRow(Grid rows, string title)
    {
        int row = rows.RowDefinitions.Count;
        rows.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var label = new TextBlock { Text = title, Foreground = detailLabelInk, FontSize = 12 };
        var value = new TextBlock { Foreground = ink, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(label, row);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 1);
        rows.Children.Add(label);
        rows.Children.Add(value);
        return value;
    }

    private void ShowSelectedDay(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
    {
        if (args.AddedDates.Count == 0) return;
        var day = args.AddedDates[0].Date;
        sender.SelectedDates.Clear();
        var note = TaskbarCalendarNotes.ForDate(day);
        detailDate.Text = day.ToString("yyyy年M月d日 dddd", Chinese);
        detailLunar.Text = note.LunarDate ?? "暂无数据";
        detailTerm.Text = note.SolarTerm ?? "无";
        detailFestivals.Text = note.Festivals ?? "无";
        detailSchedule.Text = note.ScheduleText ?? "无休班标记";
        // Keep the selected date visible by opening the card across from its row.
        var item = dayItems.FirstOrDefault(candidate => candidate.Date.Date == day);
        bool showAbove = item is not null && item.TransformToVisual(calendar).TransformPoint(new()).Y > calendar.ActualHeight * .58;
        dayDetail.VerticalAlignment = showAbove ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        dayDetail.Margin = showAbove ? new Thickness(7, 85, 7, 0) : new Thickness(7, 0, 7, 8);
        inspectedDay = day;
        dayDetail.Visibility = Visibility.Visible;
    }

    private void CloseDayDetail()
    {
        inspectedDay = null;
        dayDetail.Visibility = Visibility.Collapsed;
    }

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
        var item = args.Item;
        if (args.InRecycleQueue) { dayItems.Remove(item); return; }
        if (args.Phase != 0) return;
        dayItems.Add(item);
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
        else ApplyDayTone(day, label);
        if (item.Tag is null) item.Tag = label;
        ToolTipService.SetToolTip(item, note.Details.Length == 0 ? null : note.Details);
        AutomationProperties.SetHelpText(item, note.Details);
    }

    private void UpdateDisplayedMonth()
    {
        // CalendarView exposes the displayed month in its header, with no date-change event.
        string? header = calendar.TemplateSettings?.HeaderText;
        if (header == monthHeader) return;
        monthHeader = header;
        if (header is null) return;
        string date = string.Concat(header.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format));
        if (DateTime.TryParseExact(date + "1日", "yyyy年M月d日", Chinese, DateTimeStyles.AllowWhiteSpaces, out var month))
            SetDisplayedMonth(month);
        else if (calendar.DisplayMode != CalendarViewDisplayMode.Month) CloseDayDetail();
    }

    private void SetDisplayedMonth(DateTime month)
    {
        if (displayedMonth == month) return;
        displayedMonth = month;
        if (inspectedDay is { } day && (day.Year != month.Year || day.Month != month.Month)) CloseDayDetail();
        foreach (var item in dayItems)
            if (item.Tag is TextBlock label && item.Date.Date != DateTime.Today)
                ApplyDayTone(item.Date.Date, label);
    }

    private void ApplyDayTone(DateTime day, TextBlock label)
    {
        label.Foreground = day.Year == displayedMonth.Year && day.Month == displayedMonth.Month ? ink : adjacentInk;
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

    private void ApplyPalette()
    {
        var (text, raised) = panel.ApplyPalette(style, light, taskbar);
        ink.Color = text;
        detailFill.Color = TaskbarPanelFlyout.Alpha(raised, 255);
        detailOutline.Color = TaskbarPanelFlyout.Alpha(text, accessibility.HighContrast ? (byte)255 : (byte)45);
        detailLabelInk.Color = TaskbarPanelFlyout.Alpha(text, accessibility.HighContrast ? (byte)255 : (byte)190);
        adjacentInk.Color = TaskbarPanelFlyout.Alpha(ink.Color, accessibility.HighContrast ? (byte)255 : (byte)135);
        todayButtonFill.Color = TaskbarPanelFlyout.Alpha(ink.Color, 36);
    }

    public void Dispose()
    {
        monthObserver.Stop();
        panel.Dispose();
    }
}

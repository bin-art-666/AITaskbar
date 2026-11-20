using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Halo;

internal sealed partial class SettingsPage
{
    readonly GridView gridSample = new() { IsHitTestVisible = false, IsTabStop = false, SelectionMode = ListViewSelectionMode.Single, IsMultiSelectCheckBoxEnabled = false };
    readonly Image folderSample = new() { Stretch = Stretch.Uniform };
    readonly CheckBox showInteractionBorders = new() { Content = "显示选中与拖拽边框" };
    readonly Border dropSample = new() { CornerRadius = new CornerRadius(10),
        Child = new TextBlock { Text = "落点", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
    readonly Ellipse lightRingSample = new();
    readonly Ellipse darkRingSample = new();
    readonly InfoBar notificationSample = new() { IsClosable = true };
    Microsoft.UI.Dispatching.DispatcherQueueTimer? notificationTimer;
    internal GridView GridSampleForVerification => gridSample;
    internal InfoBar NotificationSampleForVerification => notificationSample;

    static TextBlock Section(string label) => new() { Text = label, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    FrameworkElement BuildGridPreview()
    {
        gridSample.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["StationGridItemsPanel"];
        ScrollViewer.SetHorizontalScrollMode(gridSample, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollMode(gridSample, ScrollMode.Disabled);
        foreach (var (label, glyph, cell) in new[] { ("文件夹", "", 0), ("文档", "\uE8A5", 1), ("链接", "\uE71B", 2) }) {
            var content = new StackPanel { Padding = new Thickness(6), Spacing = 4 };
            content.Children.Add(cell == 0 ? folderSample : PanelTheme.Glyph(glyph, 24));
            content.Children.Add(new TextBlock { Text = label, FontSize = 12, Height = 30, TextAlignment = TextAlignment.Center });
            var card = new GridViewItem { Content = content, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            card.Loaded += (_, _) => SampleInteractionColors.ApplySelection(card);
            StationGridPanel.Position(card, cell, 2);
            gridSample.Items.Add(card);
        }
        var target = new GridViewItem { Content = dropSample, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, IsTabStop = false };
        StationGridPanel.Position(target, 3, 2);
        gridSample.Items.Add(target);
        gridSample.SelectedIndex = 1;
        return new ScrollViewer { Content = gridSample, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    FrameworkElement BuildRingPreviews()
    {
        var pair = new Grid { ColumnSpacing = 8 };
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        pair.ColumnDefinitions.Add(new ColumnDefinition());
        AddSwatch(lightRingSample, true, 0);
        AddSwatch(darkRingSample, false, 1);
        return pair;

        void AddSwatch(Ellipse ring, bool light, int column)
        {
            var background = new SolidColorBrush(light ? ColorHelper.FromArgb(255, 244, 244, 244) : ColorHelper.FromArgb(255, 32, 32, 32));
            var label = new TextBlock { Text = light ? "亮色任务栏" : "暗色任务栏", Foreground = new SolidColorBrush(light ? Colors.Black : Colors.White), HorizontalAlignment = HorizontalAlignment.Center };
            var box = new Grid { Height = 44 }; box.Children.Add(ring);
            ring.HorizontalAlignment = HorizontalAlignment.Center; ring.VerticalAlignment = VerticalAlignment.Center;
            var content = new StackPanel { Spacing = 4 }; content.Children.Add(box); content.Children.Add(label);
            var card = new Border { Background = background, CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = content };
            Grid.SetColumn(card, column); pair.Children.Add(card);
        }
    }

    void UpdateSamples(HaloSettings settings)
    {
        var metrics = StationGridAppearance.From(settings);
        folderSample.Width = folderSample.Height = metrics.IconSize;
        if (UiAppearance.ValidRingColor(settings.FolderColor))
            _ = FolderIcon.Apply(folderSample, settings.FolderColor);
        gridSample.Width = 2 * metrics.CellWidth;
        gridSample.Height = 2 * metrics.CellHeight;
        foreach (GridViewItem card in gridSample.Items) {
            card.Width = metrics.CardWidth; card.Height = metrics.CardHeight; card.Margin = metrics.Margin;
            if (card.Content is StackPanel content && content.Children[0] is FontIcon icon) {
                icon.Height = metrics.IconSize; icon.FontSize = metrics.IconSize * .72;
            }
        }
        RefreshInteractionSample();
        UpdateRing(lightRingSample, settings.RingLightColor);
        UpdateRing(darkRingSample, settings.RingColor);

        void UpdateRing(Ellipse ring, string color)
        {
            ring.Width = ring.Height = settings.RingSize;
            ring.StrokeThickness = settings.RingSize * settings.RingThickness / 100d;
            if (!UiAppearance.ValidRingColor(color)) return;
            uint rgb = UiAppearance.ColorValue(color, 0);
            ring.Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        }
    }

    StationInteractionColors SampleInteractionColors => StationInteractionColors.From(
        ValidButtonColor(buttonColor.Text) ? buttonColor.Text : App.Appearance.ButtonColor,
        showInteractionBorders.IsChecked == true, ActualTheme == ElementTheme.Dark);

    internal void RefreshInteractionSample()
    {
        var colors = SampleInteractionColors;
        foreach (GridViewItem card in gridSample.Items) colors.ApplySelection(card);
        colors.ApplyBorder(dropSample, colors.Target, 2);
        ((TextBlock)dropSample.Child).Foreground = new SolidColorBrush(colors.NameText);
        StationWindow.ApplyDragPreviewColors(dragSampleCard, colors);
    }

    internal bool VerifyInteractionSample(StationInteractionColors colors)
    {
        var selected = StationWindow.Child<Microsoft.UI.Xaml.Controls.Primitives.ListViewItemPresenter>((GridViewItem)gridSample.Items[1]);
        return selected?.SelectedBackground is SolidColorBrush fill && fill.Color == colors.Selection
            && selected.SelectedBorderBrush is SolidColorBrush outline && outline.Color == colors.Outline
            && selected.SelectedBorderThickness == new Thickness(colors.ShowBorders ? 1 : 0)
            && Matches(dropSample, colors.Target, 2) && Matches(dragSampleCard, colors.Drag, 1);

        bool Matches(Border element, Windows.UI.Color expected, int thickness) =>
            element.Background is SolidColorBrush background && background.Color == expected
            && element.BorderBrush is SolidColorBrush border && border.Color == colors.Outline
            && element.BorderThickness == new Thickness(colors.ShowBorders ? thickness : 0);
    }

    void ShowNotificationSample(bool important)
    {
        string message = important ? "这是失败提示预览" : "这是普通提示预览";
        notificationSample.Severity = important ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        notificationSample.Message = message;
        notificationSample.IsOpen = true;
        if (notificationTimer == null) {
            notificationTimer = DispatcherQueue.CreateTimer();
            notificationTimer.IsRepeating = false;
            notificationTimer.Tick += (_, _) => notificationSample.IsOpen = false;
        }
        notificationTimer.Stop();
        notificationTimer.Interval = TimeSpan.FromSeconds(SliderValue(important ? "失败提示显示时长" : "普通提示显示时长"));
        notificationTimer.Start();
    }
}

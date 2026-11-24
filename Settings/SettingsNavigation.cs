using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AITaskbar;

internal static class SettingsNavigation
{
    internal static NavigationView Sidebar(double width)
    {
        var nav = new NavigationView
        {
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed, IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false, IsPaneOpen = true, OpenPaneLength = width,
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left
        };
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        nav.Background = transparent;
        nav.Resources["NavigationViewDefaultPaneBackground"] = transparent;
        nav.Resources["NavigationViewExpandedPaneBackground"] = transparent;
        nav.Resources["NavigationViewContentBackground"] = transparent;
        return nav;
    }

    internal static ScrollViewer Subpage(string title, StackPanel content)
    {
        content.Children.Insert(0, PageTitle(title));
        return ScrollContent(content, new Thickness(16));
    }

    internal static Grid TabbedSubpage(string title, params (string Label, StackPanel Content)[] sections)
    {
        var header = new StackPanel { Padding = new Thickness(16, 16, 16, 4), Spacing = 8 };
        header.Children.Add(PageTitle(title));
        var tabs = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(tabs, title + "页签");
        var bodies = new Dictionary<SelectorBarItem, ScrollViewer>();
        foreach (var (label, content) in sections)
        {
            var tab = new SelectorBarItem { Text = label };
            tabs.Items.Add(tab);
            var scroll = ScrollContent(content, new Thickness(16, 8, 16, 16));
            AutomationProperties.SetName(scroll, label);
            bodies.Add(tab, scroll);
        }
        var strip = new ScrollViewer
        {
            Content = tabs, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        header.Children.Add(strip);
        var body = new Border();
        void RevealSelection() =>
            tabs.SelectedItem?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is not { } selected) return;
            body.Child = bodies[selected];
            RevealSelection();
        };
        strip.SizeChanged += (_, args) =>
        {
            if (args.PreviousSize.Width != args.NewSize.Width) strip.DispatcherQueue.TryEnqueue(RevealSelection);
        };
        tabs.SelectedItem = tabs.Items[0];
        body.Child ??= bodies[tabs.Items[0]];
        var page = new Grid();
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        page.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        page.Children.Add(header);
        Grid.SetRow(body, 1);
        page.Children.Add(body);
        return page;
    }

    private static TextBlock PageTitle(string title) =>
        new() { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold };

    private static ScrollViewer ScrollContent(StackPanel content, Thickness padding)
    {
        content.Padding = padding;
        content.Spacing = 12;
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled };
    }

    internal static Grid Footer(TextBlock status, Button cancel, Button save)
    {
        var footer = new Grid { ColumnSpacing = 12, Padding = new Thickness(16, 10, 16, 12) };
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        status.VerticalAlignment = VerticalAlignment.Center;
        status.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(actions, 1);
        footer.Children.Add(status);
        footer.Children.Add(actions);
        return footer;
    }
}

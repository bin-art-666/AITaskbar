using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
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
        content.Children.Insert(0, new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold });
        content.Padding = new Thickness(16);
        content.Spacing = 12;
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled };
    }

    internal static ComboBox ConnectSubpages(NavigationView nav, Func<NavigationViewItem, object> getPage)
    {
        var compact = new ComboBox();
        foreach (NavigationViewItem item in nav.MenuItems) compact.Items.Add(item.Content);
        nav.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem item) return;
            nav.Content = getPage(item);
            compact.SelectedIndex = nav.MenuItems.IndexOf(item);
        };
        compact.SelectionChanged += (_, _) =>
        {
            if (compact.SelectedIndex >= 0) nav.SelectedItem = nav.MenuItems[compact.SelectedIndex];
        };
        nav.SelectedItem = nav.MenuItems[0];
        nav.Content = getPage((NavigationViewItem)nav.SelectedItem);
        compact.SelectedIndex = 0;
        return compact;
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

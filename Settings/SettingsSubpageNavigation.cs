using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AITaskbar;

internal sealed class SettingsSubpageNavigation : ContentControl
{
    private readonly ListView sidebar = new() { SelectionMode = ListViewSelectionMode.Single, Padding = new Thickness(4, 4, 4, 0) };
    private readonly ComboBox compact = new();
    private readonly Border pageHost = new();
    private readonly FrameworkElement[] pages;
    internal FrameworkElement? SelectedPage => pageHost.Child as FrameworkElement;
    internal event Action? SelectionChanged;

    internal SettingsSubpageNavigation(IEnumerable<(string Label, FrameworkElement Page)> entries,
        SettingsPaneWidths? widths, SettingsSection section, string label, double initialWidth = 132)
    {
        var items = entries.ToArray();
        pages = items.Select(item => item.Page).ToArray();
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        AutomationProperties.SetName(sidebar, label);
        ScrollViewer.SetHorizontalScrollMode(sidebar, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(sidebar, ScrollBarVisibility.Disabled);
        var layout = new Grid();
        var navigationColumn = new ColumnDefinition { Width = new GridLength(initialWidth) };
        layout.ColumnDefinitions.Add(navigationColumn);
        layout.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(pageHost, 1);
        layout.Children.Add(sidebar);
        layout.Children.Add(pageHost);

        foreach (var (name, _) in items)
        {
            var item = new ListViewItem
            {
                Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                Padding = new Thickness(12, 6, 10, 6), MinHeight = 40, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(item, name);
            sidebar.Items.Add(item);
            compact.Items.Add(name);
        }

        sidebar.SelectionChanged += (_, _) =>
        {
            int index = sidebar.SelectedIndex;
            if (index < 0) return;
            pageHost.Child = pages[index];
            compact.SelectedIndex = index;
            SelectionChanged?.Invoke();
        };
        compact.SelectionChanged += (_, _) =>
        {
            if (compact.SelectedIndex >= 0) sidebar.SelectedIndex = compact.SelectedIndex;
        };
        sidebar.SelectedIndex = 0;

        Content = widths is null ? layout : new SettingsSplitView(layout, widths, section, (length, condensed) =>
        {
            navigationColumn.Width = new GridLength(length);
            sidebar.Visibility = condensed ? Visibility.Collapsed : Visibility.Visible;
        }, compact);
    }

    internal void Select(FrameworkElement page) => sidebar.SelectedIndex = Array.IndexOf(pages, page);
}

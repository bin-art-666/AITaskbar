using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

internal sealed partial class SettingsPage
{
    internal async Task VerifySubpageNavigation(Action<string, bool> check)
    {
        await Task.Delay(260);
        var split = (AITaskbar.SettingsSplitView)nav.Content;
        var sidebar = StationWindow.Descendants<ListView>(split).First(list => list.Items.Count == pageByTag.Count
            && list.Items.OfType<ListViewItem>().All(item => item.Content is TextBlock));
        var dropdown = split.Children.OfType<ComboBox>().Single();
        var labels = sidebar.Items.OfType<ListViewItem>().Select(item => (TextBlock)item.Content).ToArray();
        bool visible = sidebar.Visibility == Visibility.Visible
            && dropdown.Visibility == Visibility.Collapsed
            && labels.Length == pageByTag.Count && labels.All(TextFits);
        if (!visible)
            throw new InvalidOperationException($"Halo secondary navigation does not fit: sidebar={sidebar.ActualWidth:0.##}/{sidebar.Visibility}, dropdown={dropdown.Visibility}, labels={string.Join(", ", labels.Select(label => $"{label.Text}={label.ActualWidth:0.##}x{label.ActualHeight:0.##}"))}.");
        check("HaloWideSettingsShowsCompleteSubpageLabels", true);
        ShowStorageSettings();
        check("HaloStorageShortcutSelectsItsSubpage", nav.SelectedPage == pageByTag["window"]);
        nav.Width = 390;
        await Task.Delay(180);
        check("HaloNarrowSettingsKeepsSelectedPageInDropdown", sidebar.Visibility == Visibility.Collapsed
            && dropdown.Visibility == Visibility.Visible && nav.SelectedPage == pageByTag["window"]);
        dropdown.SelectedIndex = 0;
        nav.Width = double.NaN;
        await Task.Delay(180);
        check("HaloWideSettingsRestoresSelectionFromDropdown", sidebar.Visibility == Visibility.Visible
            && sidebar.SelectedIndex == 0 && nav.SelectedPage == pageByTag["appearance"]);
    }

    static bool TextFits(TextBlock label)
    {
        if (label.ActualWidth < Math.Min(label.Text.Length * 10, 48) || label.ActualHeight <= 0) return false;
        var measurement = new TextBlock
        {
            Text = label.Text, FontSize = label.FontSize, FontFamily = label.FontFamily,
            FontWeight = label.FontWeight, TextWrapping = label.TextWrapping
        };
        measurement.Measure(new Windows.Foundation.Size(label.ActualWidth, double.PositiveInfinity));
        return measurement.DesiredSize.Height <= label.ActualHeight + 1;
    }
}

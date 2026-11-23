using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AIEnter;

internal static class AiControls
{
    internal static Button IconButton(string glyph, string name)
    {
        var button = new Button();
        PanelTheme.FramedIconStyle(button, name);
        SetIcon(button, glyph);
        return button;
    }

    internal static void SetIcon(ButtonBase button, string glyph)
    {
        string? data = glyph switch
        {
            "\uE740" => "M 3 11 L 3 17 L 9 17 M 3 17 L 17 3 M 11 3 L 17 3 L 17 9",
            "\uE73F" => "M 2 9 L 8 9 L 8 3 M 2 15 L 8 9 M 12 17 L 12 11 L 18 11 M 12 11 L 18 5",
            "\uE718" or "\uE77A" => "M 12 2 L 18 8 L 14 10 L 12 14 L 9 11 L 4 16 M 9 11 L 6 8 L 10 6 L 12 2",
            "\uE8C8" => "M 7 7 L 17 7 L 17 17 L 7 17 Z M 4 13 L 3 13 L 3 3 L 13 3 L 13 4",
            "\uE8BB" => "M 3 3 L 17 17 M 17 3 L 3 17",
            "\uE712" => "M 4 10 L 4.1 10 M 10 10 L 10.1 10 M 16 10 L 16.1 10",
            _ => null
        };
        if (data is null) { button.Content = PanelTheme.Glyph(glyph); return; }
        var path = (Microsoft.UI.Xaml.Shapes.Path)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='" + data + "' StrokeThickness='1.4' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>");
        path.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, new Binding { Source = button, Path = new PropertyPath("Foreground") });
        VisualStateGroup? commonStates = null;
        void StateChanged(object sender, VisualStateChangedEventArgs args) => RefreshToolbarIcon(button);
        path.Loaded += (_, _) => path.DispatcherQueue.TryEnqueue(() =>
        {
            if (commonStates is not null || !path.IsLoaded) return;
            commonStates = ButtonStates(button);
            if (commonStates is not null) commonStates.CurrentStateChanged += StateChanged;
            RefreshToolbarIcon(button);
        });
        path.Unloaded += (_, _) =>
        {
            if (commonStates is not null) commonStates.CurrentStateChanged -= StateChanged;
            commonStates = null;
        };
        path.StrokeThickness = PanelTheme.Value<double>("IconStroke");
        var canvas = new Canvas { Width = 20, Height = 20, IsHitTestVisible = false };
        canvas.Children.Add(path);
        button.Content = PanelTheme.Track(new Viewbox { Child = canvas, IsHitTestVisible = false },
            static view => view.Width = view.Height = PanelTheme.Value<double>("IconSize"));
        if (glyph == "\uE8BB")
            foreach (string prefix in new[] { "Button", "ToggleButton" })
            {
                button.Resources[prefix + "ForegroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 53));
                button.Resources[prefix + "BackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 232, 69, 80));
            }
    }

    static VisualStateGroup? ButtonStates(ButtonBase button) =>
        VisualTreeHelper.GetChildrenCount(button) != 0 && VisualTreeHelper.GetChild(button, 0) is FrameworkElement template
            ? VisualStateManager.GetVisualStateGroups(template).FirstOrDefault(group => group.Name == "CommonStates") : null;

    internal static void RefreshToolbarIcon(ButtonBase button)
    {
        if (!PanelTheme.HasToolbarPalette(button) || button.Content is not Viewbox { Child: Canvas canvas }
            || canvas.Children[0] is not Microsoft.UI.Xaml.Shapes.Path path) return;
        var state = ButtonStates(button)?.CurrentState?.Name;
        if (state == null && button is ToggleButton { IsChecked: true }) state = "Checked";
        string prefix = button is ToggleButton ? "ToggleButton" : "Button";
        if (state != null && button.Resources.TryGetValue(prefix + "Foreground" + (state == "Normal" ? "" : state), out var value)
            && value is Brush brush)
        {
            path.Stroke = brush;
            return;
        }
        // High contrast and disabled states retain the WinUI template's ink.
        for (var parent = VisualTreeHelper.GetParent(path); parent is not null && parent != button;
            parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not ContentPresenter presenter) continue;
            path.Stroke = presenter.Foreground;
            return;
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task<ContentDialogResult> ShowPanelDialog(ContentDialog dialog)
    {
        if (TaskbarFlyout is null || dialog.XamlRoot != root.XamlRoot)
            return await dialog.ShowAsync();

        using var lease = KeepOpen();
        Grid.SetRowSpan(dialog, root.RowDefinitions.Count);
        Canvas.SetZIndex(dialog, 10);
        root.Children.Add(dialog);
        try { return await dialog.ShowAsync(ContentDialogPlacement.InPlace); }
        finally { root.Children.Remove(dialog); }
    }

    async Task<ContentDialogResult> ShowStationDialog(ContentDialog dialog, GridView grid,
        Action? focusContent = null, Flyout? childFlyout = null, Action? refreshPreview = null)
    {
        dialog.XamlRoot = root.XamlRoot;
        dialog.RequestedTheme = root.ActualTheme;
        PanelTheme.StyleDialog(dialog);
        var existingPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot).ToHashSet();
        bool shadeReady = false, footerReady = false;
        void FinishLayout(object? sender, object e)
        {
            if (activeContentDialog != dialog) return;
            if (!shadeReady) {
                // Popup placement moves the native shade into a different popup.
                var shade = VisualTreeHelper.GetOpenPopupsForXamlRoot(dialog.XamlRoot)
                    .Where(popup => !existingPopups.Contains(popup)).Select(popup => popup.Child)
                    .OfType<Rectangle>().FirstOrDefault(element => element.Name == "SmokeLayerBackground");
                if (shade != null) {
                    shadeReady = true;
                    RegisterDialogBlankDismiss(shade, dialog, grid, childFlyout);
                }
            }
            if (!footerReady) SetupFooter();
            if (shadeReady && footerReady) root.LayoutUpdated -= FinishLayout;
        }
        void SetupFooter()
        {
            var closeButton = Descendants<Button>(dialog).SingleOrDefault(button => button.Name == "CloseButton");
            var actionButton = Descendants<Button>(dialog).SingleOrDefault(button => button.Name == "PrimaryButton");
            bool hasAction = !string.IsNullOrEmpty(dialog.PrimaryButtonText);
            if (closeButton == null || hasAction && actionButton == null) return;
            footerReady = true;
            closeButton.TabIndex = 9;
            closeButton.IsTabStop = true;
            PanelTheme.StyleTextAction(closeButton);
            if (hasAction && actionButton != null) {
                int left = Grid.GetColumn(actionButton);
                Grid.SetColumn(actionButton, Grid.GetColumn(closeButton));
                Grid.SetColumn(closeButton, left);
                actionButton.TabIndex = 10;
                actionButton.IsTabStop = true;
                PanelTheme.StyleTextAction(actionButton, useButtonColor: true);
            }
            if (focusContent != null) focusContent();
            else closeButton.Focus(FocusState.Programmatic);
        }
        dialog.Opened += (_, e) => FinishLayout(dialog, e);

        using var lease = KeepOpen();
        TypedEventHandler<FrameworkElement, object> themeChanged = (_, _) => {
            dialog.RequestedTheme = root.ActualTheme;
            if (refreshPreview != null)
                DispatcherQueue.TryEnqueue(() => { if (activeContentDialog == dialog) refreshPreview(); });
        };
        root.ActualThemeChanged += themeChanged;
        activeContentDialog = dialog;
        root.LayoutUpdated += FinishLayout;
        try {
            var showing = ShowPanelDialog(dialog);
            DispatcherQueue.TryEnqueue(() => FinishLayout(dialog, EventArgs.Empty));
            return await showing;
        }
        finally {
            root.LayoutUpdated -= FinishLayout;
            root.ActualThemeChanged -= themeChanged;
            activeContentDialog = null;
            if (grid.IsLoaded && grid.SelectedItem is GridViewItem selected)
                selected.Focus(FocusState.Programmatic);
        }
    }

    static void RegisterDialogBlankDismiss(Rectangle shade, ContentDialog dialog, GridView grid, Flyout? childFlyout)
    {
        const double clickSlop = 4;
        (uint Id, Point Position)? press = null;
        // Match both ends of the click so a release from a child flyout cannot dismiss the dialog.
        shade.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => {
            var point = e.GetCurrentPoint(null);
            press = childFlyout?.IsOpen != true && point.Properties.IsLeftButtonPressed && DialogGridBlankAt(grid, point.Position)
                ? (point.PointerId, point.Position) : null;
            e.Handled = true;
        }), true);
        shade.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => {
            var point = e.GetCurrentPoint(null);
            bool dismiss = press is { } start && start.Id == point.PointerId && childFlyout?.IsOpen != true
                && Math.Abs(start.Position.X - point.Position.X) <= clickSlop
                && Math.Abs(start.Position.Y - point.Position.Y) <= clickSlop
                && DialogGridBlankAt(grid, point.Position);
            press = null;
            e.Handled = true;
            if (dismiss) dialog.Hide();
        }), true);
        shade.PointerExited += (_, _) => press = null;
        shade.PointerCanceled += (_, _) => press = null;
    }

    static bool DialogGridBlankAt(GridView grid, Point point)
    {
        var target = VisualTreeHelper.FindElementsInHostCoordinates(point, grid).FirstOrDefault();
        return ContentBlankTarget(target, grid);
    }

    static void StyleStationDialogInput(TextBox input)
    {
        var border = new SolidColorBrush();
        var focus = new SolidColorBrush();
        input.UseSystemFocusVisuals = false;
        input.CornerRadius = PanelTheme.Value<CornerRadius>("ControlRadius");
        input.BorderBrush = border;
        input.BorderThickness = new Thickness(1);
        input.Resources["TextControlBorderBrushPointerOver"] = border;
        input.Resources["TextControlBorderBrushFocused"] = focus;
        input.Resources["TextControlBorderThemeThicknessFocused"] = input.BorderThickness;

        void Update()
        {
            bool dark = input.ActualTheme == ElementTheme.Dark;
            if (PanelTheme.HighContrastPalette is { } system) {
                border.Color = system.UIElementColor(Windows.UI.ViewManagement.UIElementType.WindowText);
                focus.Color = system.UIElementColor(Windows.UI.ViewManagement.UIElementType.Highlight);
            } else {
                border.Color = PanelTheme.PaletteColor(dark ? 0xFF677683 : 0xFFA1AEB5);
                focus.Color = Application.Current.Resources.TryGetValue(dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1", out var value)
                    && value is Windows.UI.Color accent ? accent : PanelTheme.PaletteColor(dark ? 0xFF6CBFFF : 0xFF0067C0);
            }
        }
        Update();
        input.ActualThemeChanged += (_, _) => Update();
    }
}

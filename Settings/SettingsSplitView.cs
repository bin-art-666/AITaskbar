using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class SettingsSplitView : Grid
{
    private const double DividerWidth = 12;
    private readonly Grid body = new();
    private readonly SettingsPaneWidths widths;
    private readonly SettingsSection section;
    private readonly Action<double, bool> updatePane;
    private readonly ComboBox? compactMenu;
    private readonly Divider divider = new();
    private readonly double minimum, maximum, defaultWidth, contentMinimum;
    private double preferredWidth, renderedWidth, upperBound, startX, startWidth;
    private double? draggedWidth;
    private uint? pointerId;

    internal SettingsSplitView(FrameworkElement content, SettingsPaneWidths widths, SettingsSection section,
        Action<double, bool> updatePane, ComboBox? compactMenu = null)
    {
        this.widths = widths;
        this.section = section;
        this.updatePane = updatePane;
        this.compactMenu = compactMenu;
        string sectionLabel = section switch
        {
            SettingsSection.AppCenter => "应用中心",
            SettingsSection.TaskbarSubpages => "任务栏设置",
            _ => section.ToString()
        };
        (minimum, maximum, defaultWidth, contentMinimum) = section switch
        {
            SettingsSection.Taskbar => (128, 224, 144, 440),
            SettingsSection.TaskbarSubpages => (132, 208, 144, 300),
            SettingsSection.Halo => (108, 208, 108, 414),
            _ => (108, 208, 132, 414)
        };
        preferredWidth = Math.Clamp(widths.Get(section, defaultWidth), minimum, maximum);
        RowDefinitions.Add(new() { Height = GridLength.Auto });
        RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        if (compactMenu is not null)
        {
            compactMenu.Visibility = Visibility.Collapsed;
            compactMenu.Margin = new Thickness(12, 8, 12, 4);
            compactMenu.HorizontalAlignment = HorizontalAlignment.Stretch;
            AutomationProperties.SetName(compactMenu, $"{sectionLabel} 二级菜单");
            Children.Add(compactMenu);
        }
        SetRow(body, 1);
        body.Children.Add(content);
        body.Children.Add(divider);
        Children.Add(body);
        AutomationProperties.SetName(divider, section == SettingsSection.Taskbar ? "一级导航分隔线" : $"{sectionLabel} 二级菜单分隔线");
        AutomationProperties.SetHelpText(divider, "拖动调整宽度，双击恢复默认宽度。");
        divider.PointerPressed += BeginDrag;
        divider.PointerMoved += Drag;
        divider.PointerReleased += EndDrag;
        divider.PointerCanceled += (_, _) => CancelDrag();
        divider.PointerCaptureLost += (_, _) => CancelDrag();
        divider.DoubleTapped += (_, args) => { CancelDrag(); Commit(defaultWidth); args.Handled = true; };
        SizeChanged += (_, _) => UpdateLayoutWidths();
        Loaded += (_, _) => UpdateLayoutWidths();
        Unloaded += (_, _) => CancelDrag();
    }

    private void UpdateLayoutWidths()
    {
        if (ActualWidth <= 0) return;
        upperBound = Math.Min(maximum, ActualWidth - contentMinimum);
        bool compact = upperBound < minimum;
        renderedWidth = compact ? section == SettingsSection.Taskbar ? 48 : 0 :
            Math.Clamp(draggedWidth ?? preferredWidth, minimum, upperBound);
        updatePane(renderedWidth, compact);
        if (compactMenu is not null) compactMenu.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        divider.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        divider.Margin = new Thickness(Math.Max(0, renderedWidth - DividerWidth / 2), 0, 0, 0);
    }

    private void BeginDrag(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(body);
        if (pointerId is not null || !point.Properties.IsLeftButtonPressed || !divider.CapturePointer(args.Pointer)) return;
        pointerId = args.Pointer.PointerId;
        startX = point.Position.X;
        startWidth = renderedWidth;
        divider.SetDragging(true);
    }

    private void Drag(object sender, PointerRoutedEventArgs args)
    {
        if (pointerId != args.Pointer.PointerId || upperBound < minimum) return;
        double delta = args.GetCurrentPoint(body).Position.X - startX;
        if (draggedWidth is null && Math.Abs(delta) < 2) return;
        draggedWidth = Math.Clamp(startWidth + delta, minimum, upperBound);
        UpdateLayoutWidths();
        args.Handled = true;
    }

    private void EndDrag(object sender, PointerRoutedEventArgs args)
    {
        if (pointerId != args.Pointer.PointerId) return;
        double? next = draggedWidth is not null && upperBound >= minimum
            ? Math.Clamp(startWidth + args.GetCurrentPoint(body).Position.X - startX, minimum, upperBound)
            : null;
        CancelDrag();
        if (next is not null && Math.Abs(next.Value - startWidth) >= 1) Commit(next.Value);
    }

    private void CancelDrag()
    {
        if (pointerId is null) return;
        pointerId = null;
        draggedWidth = null;
        divider.SetDragging(false);
        divider.ReleasePointerCaptures();
        UpdateLayoutWidths();
    }

    private void Commit(double width)
    {
        preferredWidth = Math.Round(width);
        widths.Set(section, preferredWidth);
        UpdateLayoutWidths();
    }

    private sealed class Divider : ContentControl
    {
        private readonly Border line = new() { Width = 1, HorizontalAlignment = HorizontalAlignment.Center,
            CornerRadius = new CornerRadius(1) };
        private readonly UISettings colors = new();
        private readonly AccessibilitySettings accessibility = new();
        private bool hovered, dragging;

        internal Divider()
        {
            Width = DividerWidth;
            HorizontalAlignment = HorizontalAlignment.Left;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            IsTabStop = false;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
            var hitArea = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            hitArea.Children.Add(line);
            Content = hitArea;
            PointerEntered += (_, _) => { hovered = true; RefreshLine(); };
            PointerExited += (_, _) => { hovered = false; RefreshLine(); };
            ActualThemeChanged += (_, _) => RefreshLine();
            Loaded += (_, _) => RefreshLine();
        }

        internal void SetDragging(bool value)
        {
            dragging = value;
            RefreshLine();
        }

        private void RefreshLine()
        {
            bool active = hovered || dragging;
            line.Width = active ? 2 : 1;
            var color = colors.GetColorValue(active ? UIColorType.Accent : UIColorType.Foreground);
            line.Background = new SolidColorBrush(active || accessibility.HighContrast ? color : Color.FromArgb(54, color.R, color.G, color.B));
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new DividerPeer(this);

        private sealed class DividerPeer(Divider owner) : FrameworkElementAutomationPeer(owner)
        {
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Thumb;
        }
    }
}

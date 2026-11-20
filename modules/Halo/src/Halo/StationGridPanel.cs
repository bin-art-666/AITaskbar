using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Halo;

// Explicit cells keep empty positions without manufacturing selectable placeholder items.
public sealed class StationGridPanel : Panel
{
    static readonly DependencyProperty CellProperty = DependencyProperty.RegisterAttached("Cell", typeof(int), typeof(StationGridPanel), new PropertyMetadata(0));
    static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached("Columns", typeof(int), typeof(StationGridPanel), new PropertyMetadata(0));
    internal static void Position(FrameworkElement item, int cell, int columns) { item.SetValue(CellProperty, cell); item.SetValue(ColumnsProperty, columns); }
    internal static int Cell(GridViewItem item) => (int)item.GetValue(CellProperty);
    internal int Columns { get; private set; } = 1;
    internal double CellWidth { get; private set; } = HaloDefaults.IconSize + 24 + HaloDefaults.HorizontalSpacing;
    internal double CellHeight { get; private set; } = HaloDefaults.IconSize + 46 + HaloDefaults.VerticalSpacing;
    protected override Size MeasureOverride(Size available) {
        if (Children.Count == 0) return new(0, 0);
        var first = (FrameworkElement)Children[0]; CellWidth = first.Width + first.Margin.Left + first.Margin.Right; CellHeight = first.Height + first.Margin.Top + first.Margin.Bottom;
        int fixedColumns = (int)first.GetValue(ColumnsProperty);
        Columns = fixedColumns > 0 ? fixedColumns : Math.Clamp((int)(double.IsFinite(available.Width) ? available.Width / CellWidth : 1), 1, 64);
        foreach (var child in Children) child.Measure(new(CellWidth, CellHeight));
        int max = Children.Cast<UIElement>().Max(c => (int)c.GetValue(CellProperty));
        return new(Columns * CellWidth, (max / Columns + 1) * CellHeight);
    }
    protected override Size ArrangeOverride(Size finalSize) {
        foreach (var child in Children) { int cell = (int)child.GetValue(CellProperty); child.Arrange(new Rect(cell % Columns * CellWidth, cell / Columns * CellHeight, CellWidth, CellHeight)); }
        return finalSize;
    }
}

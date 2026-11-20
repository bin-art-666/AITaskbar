using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Halo;

// Card bounds exclude the gaps. Layout, drag targets and previews share these metrics.
internal readonly record struct StationGridAppearance(int IconSize, int HorizontalSpacing, int VerticalSpacing)
{
    internal static StationGridAppearance From(HaloSettings settings) => new(settings.IconSize, settings.HorizontalSpacing, settings.VerticalSpacing);
    internal double CardWidth => IconSize + 24;
    internal double CardHeight => IconSize + 46; // 12 padding + 4 icon/title gap + 30 for two title lines
    internal double CellWidth => CardWidth + HorizontalSpacing;
    internal double CellHeight => CardHeight + VerticalSpacing;
    internal Thickness Margin => new(HorizontalSpacing / 2d, VerticalSpacing / 2d, HorizontalSpacing / 2d, VerticalSpacing / 2d);
}

internal sealed partial class StationWindow
{
    StationGridAppearance? gridPreview;
    internal StationGridAppearance GridAppearance => gridPreview ?? StationGridAppearance.From(App.Settings.Data);

    internal void PreviewGrid(StationGridAppearance? value)
    {
        if (gridPreview == value) return;
        gridPreview = value;
        foreach (GridViewItem card in files.Items) ApplyCardMetrics(card);
        ApplyGridPositions();
        folderBrowser?.RefreshAppearance(GridAppearance);
    }

    void ApplyCardMetrics(GridViewItem card) => ApplyCardMetrics(card, GridAppearance);
    internal static void ApplyCardMetrics(GridViewItem card, StationGridAppearance metrics)
    {
        card.Width = metrics.CardWidth; card.Height = metrics.CardHeight; card.Margin = metrics.Margin;
        if (card.Content is not ContentControl { Content: StackPanel content, Tag: Preview preview } surface) return;
        surface.Width = metrics.CardWidth;
        surface.Height = metrics.CardHeight;
        content.Width = metrics.CardWidth;
        preview.Image.Width = preview.Image.Height = metrics.IconSize;
        preview.Fallback.FontSize = metrics.IconSize * .72;
        if (content.Children[0] is FrameworkElement host) host.Width = host.Height = metrics.IconSize;
    }
}

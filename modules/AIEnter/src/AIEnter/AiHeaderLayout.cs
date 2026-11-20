using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AIEnter;

internal sealed partial class AiWindow
{
    readonly Grid aiNavigation = new() { ColumnSpacing = 6 };
    Grid aiHeader = null!;
    void InitializeAiHeaderLayout()
    {
        aiHeader.SizeChanged += (_, _) => UpdateAiHeaderLayout();
        aiNavigation.SizeChanged += (_, _) => UpdateFloatingCaption();
        PanelTheme.Changed += UpdateAiHeaderLayout;
        Closed += (_, _) => PanelTheme.Changed -= UpdateAiHeaderLayout;
    }
    void UpdateAiHeaderLayout()
    {
        if (destroyed || aiHeader == null) return;
        aiHeader.Margin = new Thickness(0, Detached ? 6 : 0, 0, 0);
        aiNavigation.MinWidth = 0;
        aiNavigation.HorizontalAlignment = HorizontalAlignment.Stretch;
        QueueProviderReveal(); UpdateFloatingCaption(); RefreshBackplateCorners();
    }
}

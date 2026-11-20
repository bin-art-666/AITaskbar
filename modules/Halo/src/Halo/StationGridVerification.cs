using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Halo;

public partial class App
{
    async Task VerifyGridAppearance(Action<string, bool> check)
    {
        var panel = Panel!; var initial = Settings.Data;
        using var retain = panel.Retain();
        // Let the preceding search's queued TextChanged rebuilds finish before retaining containers.
        await Task.Delay(200);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1,\"IconSize\":48}")!;
        check("LegacyGridSpacingDefaults", legacy.HorizontalSpacing == 12 && legacy.VerticalSpacing == 8 && legacy.IconSize == 48);
        foreach (var bad in new[] { initial with { HorizontalSpacing = 3 }, initial with { HorizontalSpacing = 33 }, initial with { VerticalSpacing = 3 }, initial with { VerticalSpacing = 33 } }) {
            bool rejected = false; try { bad.Validate(); } catch (InvalidDataException) { rejected = true; }
            check($"GridSpacingRejects{bad.HorizontalSpacing}_{bad.VerticalSpacing}", rejected);
        }
        var cards = panel.Files.Items.Cast<GridViewItem>().ToArray();
        var selection = panel.Selected().Select(item => item.Id).ToHashSet();
        panel.Files.SelectedItems.Clear(); panel.Files.SelectedItems.Add(cards[0]); panel.Files.SelectedItems.Add(cards[1]);
        string stationBefore = await File.ReadAllTextAsync(Path.Combine(DataDirectory, "station.json"));
        OpenSettings(); await Task.Delay(100);
        var window = panel.SettingsForVerification;
        var sample = window.GridSampleForVerification;
        Slider Slider(string name) => window.FindSlider(name);
        void Set(int size, int horizontal, int vertical) {
            Slider("文件图标大小").Value = size; Slider("水平间距").Value = horizontal; Slider("垂直间距").Value = vertical;
        }
        void Invoke(string title) {
            var button = window.FindButton(title);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }
        var cells = Station.Layout(initial.Detached, 1, true).Cells;
        foreach (var metrics in new[] { new StationGridAppearance(32, 4, 32), new StationGridAppearance(96, 32, 4) }) {
                Set(metrics.IconSize, metrics.HorizontalSpacing, metrics.VerticalSpacing); await Task.Delay(80);
                ((FrameworkElement)panel.Content).UpdateLayout();
                var card = (GridViewItem)sample.Items[0];
                check($"GridPreviewNativeMetrics{metrics.IconSize}", sample.Width == 2 * metrics.CellWidth && sample.Height == 2 * metrics.CellHeight
                    && Math.Abs(card.ActualWidth - metrics.CardWidth) < .01 && Math.Abs(card.ActualHeight - metrics.CardHeight) < .01);
                check($"GridPreviewKeepsCellsAndSelection{metrics.IconSize}", Station.Layout(initial.Detached, 1, true).Cells.SequenceEqual(cells)
                    && cards.SequenceEqual(panel.Files.Items.Cast<GridViewItem>()) && panel.Files.SelectedItems.Count == 2 && panel.Files.SelectedItems.Contains(cards[0]) && panel.Files.SelectedItems.Contains(cards[1]));
                check($"GridPreviewDoesNotSave{metrics.IconSize}", StationGridAppearance.From(Settings.Data) == StationGridAppearance.From(initial)
                    && await File.ReadAllTextAsync(Path.Combine(DataDirectory, "station.json")) == stationBefore);
        }
        Invoke("取消"); await Task.Delay(100);
        check("GridSettingsCancelRestoresAllMetrics", panel.GridAppearance == StationGridAppearance.From(initial)
            && sample.Width == 2 * StationGridAppearance.From(initial).CellWidth);
        OpenSettings(); Set(48, 20, 16); Invoke("保存"); await Settings.Flush();
        var saved = new SettingsStore(Path.Combine(DataDirectory, "settings.json")).Data;
        check("GridSettingsSaveAndReload", saved.IconSize == 48 && saved.HorizontalSpacing == 20 && saved.VerticalSpacing == 16 && panel.GridAppearance == StationGridAppearance.From(saved));
        OpenSettings();
        check("GridSettingsReopenShowsSavedValues", Slider("文件图标大小").Value == 48 && Slider("水平间距").Value == 20 && Slider("垂直间距").Value == 16);
        Set(initial.IconSize, initial.HorizontalSpacing, initial.VerticalSpacing); Invoke("保存"); await Settings.Flush();
        panel.ReturnFromSettings(); await Task.Delay(120);
        var grid = (StationGridPanel)panel.Files.ItemsPanelRoot;
        var origin = grid.TransformToVisual(panel.Files).TransformPoint(new(0, 0));
        check("GridRestoredMetricsAndDropTarget", grid.CellWidth == StationGridAppearance.From(initial).CellWidth
            && panel.GridTarget(new(origin.X + grid.CellWidth * 1.5, origin.Y + grid.CellHeight * 2.5), false) == (1, 2));
        panel.Files.SelectedItems.Clear(); foreach (var card in cards) if (selection.Contains(((StationItem)card.Tag).Id)) panel.Files.SelectedItems.Add(card);
        var presenter = Find<ListViewItemPresenter>(cards[0]);
        var colors = StationInteractionColors.From(initial.ButtonColor, initial.ShowInteractionBorders, cards[0].ActualTheme == ElementTheme.Dark);
        check("GridNativeSelectionPaletteAndKeyboardFocus", presenter is not null
            && presenter.SelectedBorderThickness == new Thickness(initial.ShowInteractionBorders ? 1 : 0)
            && presenter.SelectedBackground is SolidColorBrush background && background.Color == colors.Selection
            && cards[0].UseSystemFocusVisuals);

        static T? Find<T>(DependencyObject root) where T : DependencyObject {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) {
                var child = VisualTreeHelper.GetChild(root, i); if (child is T found) return found;
                if (Find<T>(child) is {} nested) return nested;
            }
            return null;
        }
    }
}

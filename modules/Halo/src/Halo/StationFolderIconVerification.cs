using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifyFolderIcon(Action<string, bool> check)
    {
        var card = files.Items.Cast<GridViewItem>().First(item => item.Tag is StationItem { Kind: StationKind.Folder });
        var folder = (Preview)((ContentControl)card.Content).Tag;
        var selected = files.SelectedItems.ToArray();
        var theme = root.RequestedTheme;
        try {
            foreach (var (mode, focus) in new[] { (ElementTheme.Light, false), (ElementTheme.Dark, false), (ElementTheme.Light, true) }) {
                root.RequestedTheme = mode;
                files.SelectedItems.Clear();
                if (focus) files.SelectedItems.Add(card);
                await Task.Delay(100);
                var rendered = new RenderTargetBitmap();
                await rendered.RenderAsync(folder.Image);
                using var reader = DataReader.FromBuffer(await rendered.GetPixelsAsync());
                var pixels = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(pixels);
                int row = (rendered.PixelHeight / 2) * rendered.PixelWidth * 4;
                int center = row + rendered.PixelWidth / 2 * 4;
                int visibleWidth = Enumerable.Range(0, rendered.PixelWidth).Count(x => pixels[row + x * 4 + 3] >= 128);
                check($"FolderIconRenders_{mode}_{focus}", folder.Image.ActualTheme == mode
                    && folder.Image.Source is WriteableBitmap && folder.Fallback.Visibility == Visibility.Collapsed
                    && pixels[3] == 0 && pixels[center + 3] >= 245
                    && pixels[center] > pixels[center + 1] && pixels[center + 1] > pixels[center + 2]
                    && (!focus || card.IsSelected
                        && Child<ListViewItemPresenter>(card)?.SelectedBackground is SolidColorBrush fill
                        && fill.Color == interactionColors.Selection));
                check($"FolderIconFillsGridSlot_{mode}_{focus}", visibleWidth >= rendered.PixelWidth * .93);
            }
        } finally {
            root.RequestedTheme = theme;
            files.SelectedItems.Clear();
            foreach (var item in selected) files.SelectedItems.Add(item);
        }
    }

    static async Task VerifyFolderColorSettings(SettingsPage page, Action<string, bool> check, Action<Button> click)
    {
        var original = App.Settings.Data;
        var color = page.FolderColorForVerification;
        var sample = (Image)((StackPanel)((GridViewItem)page.GridSampleForVerification.Items[0]).Content).Children[0];
        var originalIcon = sample.Source;
        check("LegacySettingsUseApprovedBlueFolder", System.Text.Json.JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1}")!.FolderColor == HaloDefaults.FolderColor);
        color.Text = "#C54D9F";
        await Task.Delay(80);
        var previewIcon = sample.Source;
        check("FolderColorPreviewsIndependently", previewIcon is WriteableBitmap && previewIcon != originalIcon
            && App.Appearance.FolderColor == color.Text && App.Appearance.ButtonColor == original.ButtonColor
            && App.Settings.Data.FolderColor == original.FolderColor);
        click(page.FindButton("取消"));
        await Task.Delay(80);
        check("FolderColorCancelRestoresBlue", color.Text == original.FolderColor && sample.Source == originalIcon);
        color.Text = "#C54D9F";
        await Task.Delay(80);
        click(page.FindButton("保存"));
        await page.PendingSave;
        await App.Settings.Flush();
        check("FolderColorSaveAndReload", new SettingsStore(Path.Combine(App.DataDirectory, "settings.json")).Data.FolderColor == "#C54D9F");
        color.Text = original.FolderColor;
        await Task.Delay(80);
        click(page.FindButton("保存"));
        await page.PendingSave;
    }
}

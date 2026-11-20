using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Foundation;

namespace Halo;

internal sealed partial class StationWindow
{
    internal async Task VerifySettingsWindow(Action<string, bool> check)
    {
        var initial = App.Settings.Data;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        foreach (bool expanded in new[] { false, true })
        {
            SetDetached(expanded);
            Open();
            await Task.Delay(150);
            var originalSize = AppWindow.Size;
            OpenSettings();
            await Task.Delay(150);
            await VerifyFloatingCaption(true);
            VerifyResize();
            check($"SettingsReusesExpandedWindow_{expanded}", IsOpen && ShowingSettings && Detached
                && WinRT.Interop.WindowNative.GetWindowHandle(this) == handle && AppWindow.IsShownInSwitchers
                && files.Visibility == Visibility.Collapsed && App.Settings.Data.Detached == expanded
                && HasWindowButtons && FullBackdrop && !DismissOnOutside
                && AutomationProperties.GetName(expand) == "返回中转站"
                && AutomationProperties.GetName(close) == "关闭设置窗口");

            var page = SettingsForVerification;
            if (!expanded)
            {
                var mode = page.DragModeForVerification;
                var distance = page.DragDistanceForVerification;
                var saved = DragPreviewPlacement.From(initial);
                check("LegacyDragPreviewUsesGrabPointByDefault", System.Text.Json.JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1}")!.DragPreviewMode == DragPreviewMode.GrabPoint);
                mode.SelectedIndex = 1;
                distance.Value = 15;
                check("DragSettingsBelowPreviewUpdatesBeforeSave", page.DragSampleOffsetForVerification.Equals(new Point(-24, 15))
                    && DragPreviewPlacement.From(App.Settings.Data) == saved);
                mode.SelectedIndex = 2;
                check("DragSettingsRightPreviewUpdates", page.DragSampleOffsetForVerification.Equals(new Point(15, 15)));
                Click(page.FindButton("取消"));
                check("DragSettingsCancelRestoresSavedValues", Equals(((Microsoft.UI.Xaml.Controls.ComboBoxItem)mode.SelectedItem).Tag, saved.Mode)
                    && distance.Value == saved.Distance && (saved.Mode != DragPreviewMode.GrabPoint || !distance.IsEnabled));
                mode.SelectedIndex = 2;
                distance.Value = saved == new DragPreviewPlacement(DragPreviewMode.LowerRight, 18) ? 20 : 18;
                int configuredDistance = (int)distance.Value;
                Click(page.FindButton("保存"));
                await page.PendingSave;
                await App.Settings.Flush();
                var reloaded = new SettingsStore(Path.Combine(App.DataDirectory, "settings.json")).Data;
                check("DragSettingsSaveAndReload", reloaded.DragPreviewMode == DragPreviewMode.LowerRight && reloaded.DragPreviewDistance == configuredDistance);
                mode.SelectedItem = mode.Items.OfType<Microsoft.UI.Xaml.Controls.ComboBoxItem>().Single(item => Equals(item.Tag, saved.Mode));
                distance.Value = saved.Distance;
                Click(page.FindButton("保存"));
                await page.PendingSave;

                var input = page.ButtonColorForVerification;
                var border = page.FindCheckBox("显示选中与拖拽边框");
                check("LegacyInteractionBordersDefaultOff", !System.Text.Json.JsonSerializer.Deserialize<HaloSettings>("{\"Version\":1}")!.ShowInteractionBorders);
                string color = initial.ButtonColor == "#201E18" ? "#DDEEF0" : "#201E18";
                input.Text = color;
                border.IsChecked = !initial.ShowInteractionBorders;
                await Task.Delay(80);
                check("ButtonColorPreviewsWithoutSaving", App.Settings.Data.ButtonColor == initial.ButtonColor
                    && UiAppearance.ColorValue(App.Appearance.ButtonColor, 0) == UiAppearance.ColorValue(color, 0));
                check("NoticeSharesButtonPreviewColor", ((SolidColorBrush)expand.Background).Color.Equals(((SolidColorBrush)statusHost.Background).Color));
                check("InteractionColorsAndBordersPreviewBeforeSave", App.Settings.Data.ShowInteractionBorders == initial.ShowInteractionBorders
                    && MatchesInteraction(color, !initial.ShowInteractionBorders));
                Click(page.FindButton("取消"));
                check("NoticeColorCancelRestores", App.Appearance.ButtonColor == initial.ButtonColor);
                check("InteractionColorAndBorderCancelRestores", border.IsChecked == initial.ShowInteractionBorders
                    && MatchesInteraction(initial.ButtonColor, initial.ShowInteractionBorders));
                input.Text = color;
                border.IsChecked = !initial.ShowInteractionBorders;
                await Task.Delay(80);
                bool saveShortcutHandled = page.HandleShortcut(saveChanges: true);
                await page.PendingSave;
                await App.Settings.Flush();
                check("SettingsSaveShortcutKeepsCurrentPage", saveShortcutHandled && ShowingSettings);
                check("NoticeColorSavePersists", ShowingSettings && new SettingsStore(Path.Combine(App.DataDirectory, "settings.json")).Data.ButtonColor == color);
                check("InteractionColorAndBorderSavePersists", new SettingsStore(Path.Combine(App.DataDirectory, "settings.json")).Data.ShowInteractionBorders != initial.ShowInteractionBorders
                    && MatchesInteraction(color, !initial.ShowInteractionBorders));
                input.Text = initial.ButtonColor;
                border.IsChecked = initial.ShowInteractionBorders;
                await Task.Delay(80);
                Click(page.FindButton("保存"));
                await page.PendingSave;

                await VerifyFolderColorSettings(page, check, Click);
                await VerifyFolderMenuSettings(page, check, Click);
                await VerifyFileMenuSettings(page, check, Click);

                bool MatchesInteraction(string buttonColor, bool show)
                {
                    var colors = StationInteractionColors.From(buttonColor, show, root.ActualTheme == ElementTheme.Dark);
                    return interactionColors == colors && page.VerifyInteractionSample(colors)
                        && insertion.Background is SolidColorBrush background && background.Color == colors.Target
                        && insertion.BorderThickness == new Thickness(show ? 2 : 0);
                }
            }
            var blur = page.FindSlider("背板模糊程度");
            int preview = initial.Blur == 65 ? 64 : 65;
            blur.Value = preview;
            OpenSettings();
            check($"SettingsReopenKeepsDraft_{expanded}", blur.Value == preview && DesktopBlur == preview);
            Click(page.FindButton("取消"));
            check($"SettingsCancelRestoresPreview_{expanded}", ShowingSettings && blur.Value == initial.Blur && DesktopBlur == initial.Blur);
            TogglePin();
            check($"SettingsPinUsesSharedPresenter_{expanded}", presenter.IsAlwaysOnTop != initial.Pinned
                && page.FindCheckBox("置顶面板").IsChecked == !initial.Pinned);
            TogglePin();

            if (expanded)
            {
                blur.Value = preview;
                var position = AppWindow.Position;
                var work = Native.MonitorInformation(Native.MonitorForWindow(handle)).Work;
                int dx = position.X + AppWindow.Size.Width + 8 <= work.Right ? 8 : -8;
                int dy = position.Y + AppWindow.Size.Height + 8 <= work.Bottom ? 8 : -8;
                AppWindow.Move(new PointInt32(position.X + dx, position.Y + dy));
                var movedPosition = AppWindow.Position;
                Click(close);
                await Task.Delay(150);
                check("SettingsCloseRestoresPreviewAndHides", !IsOpen && !ShowingSettings && Detached && DesktopBlur == initial.Blur
                    && App.Settings.Data.ExpandedX == movedPosition.X && App.Settings.Data.ExpandedY == movedPosition.Y);
                OpenSettings();
                await Task.Delay(150);
                check("SettingsOpenFromHiddenUsesSavedPosition", IsOpen && ShowingSettings && AppWindow.Position.Equals(movedPosition));
            }
            blur.Value = preview;
            if (expanded) Click(expand);
            else check("SettingsEscapeShortcutHandlesUnsavedDraft", page.HandleShortcut(saveChanges: false));
            await Task.Delay(150);
            await VerifyFloatingCaption(expanded);
            check($"SettingsReturnRestoresMode_{expanded}", IsOpen && !ShowingSettings && Detached == expanded
                && AppWindow.IsShownInSwitchers == expanded && App.Settings.Data.Detached == expanded && AppWindow.Size.Equals(originalSize)
                && files.Visibility == Visibility.Visible && DesktopBlur == initial.Blur);
        }
        SetDetached(initial.Detached);
        await Task.Delay(150);
        App.Settings.SaveSoon(initial);

        static void Click(Microsoft.UI.Xaml.Controls.Button button) =>
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    }
}

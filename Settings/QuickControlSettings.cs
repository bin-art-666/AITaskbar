using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private FrameworkElement quickAppearanceRow = null!;
    private ComboBox quickBackdropMode = null!;

    internal void UpdateQuickControlAppearance() => quickBackdropMode.SelectedIndex = (int)app.QuickAppearance.Backdrop;

    internal void ShowQuickControlAppearance()
    {
        taskbarNav.Select(taskbarAppearancePage);
        DispatcherQueue.TryEnqueue(() =>
        {
            void BringIntoView() => quickAppearanceRow.StartBringIntoView(new BringIntoViewOptions
                { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            if (quickAppearanceRow.IsLoaded) BringIntoView();
            else
            {
                void Loaded(object sender, RoutedEventArgs args)
                {
                    quickAppearanceRow.Loaded -= Loaded;
                    BringIntoView();
                }
                quickAppearanceRow.Loaded += Loaded;
            }
        });
    }

    private void AddQuickControlSettings(StackPanel page)
    {
        var custom = new StackPanel();
        var transparency = new Slider { Width = 140, Minimum = 0, Maximum = 65, StepFrequency = 1,
            Value = app.QuickAppearance.Transparency };
        AutomationProperties.SetName(transparency, "快捷控制背板透明度");
        var opacityValue = new TextBlock { Text = $"{app.QuickAppearance.Transparency}%",
            VerticalAlignment = VerticalAlignment.Center };
        var opacityControl = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        opacityControl.Children.Add(transparency);
        opacityControl.Children.Add(opacityValue);
        bool resetting = false;
        transparency.ValueChanged += (_, args) =>
        {
            if (resetting) return;
            int value = (int)Math.Round(args.NewValue);
            if (!app.TrySetQuickAppearance(app.QuickAppearance with { Transparency = value }))
            {
                resetting = true;
                transparency.Value = app.QuickAppearance.Transparency;
                resetting = false;
            }
            opacityValue.Text = $"{app.QuickAppearance.Transparency}%";
        };
        var modes = quickBackdropMode = new ComboBox { Width = 132, ItemsSource = QuickControlAppearance.Modes.Select(item => item.Label).ToArray(),
            SelectedIndex = (int)app.QuickAppearance.Backdrop };
        AutomationProperties.SetName(modes, "快捷控制背板模式");
        void SyncCustom() => custom.Visibility = app.QuickAppearance.Backdrop == QuickBackdrop.Custom
            ? Visibility.Visible : Visibility.Collapsed;
        modes.SelectionChanged += (_, _) =>
        {
            if (modes.SelectedIndex < 0) return;
            if (!app.TrySetQuickAppearance(app.QuickAppearance with { Backdrop = (QuickBackdrop)modes.SelectedIndex }))
                modes.SelectedIndex = (int)app.QuickAppearance.Backdrop;
            SyncCustom();
        };
        SyncCustom();
        quickAppearanceRow = SettingRow("快捷控制背板", "Wi‑Fi 和音量入口打开的面板。", modes);
        page.Children.Add(quickAppearanceRow);
        custom.Children.Add(SettingRow("背板颜色", "快捷控制主背板与连接浮窗共用配色。",
            ColorControl("背板颜色", () => app.QuickAppearance.Tint, rgb => app.QuickAppearance with { Tint = rgb })));
        custom.Children.Add(SettingRow("背板透明度", "仅用于自定义背板。", opacityControl));
        page.Children.Add(custom);
        page.Children.Add(SettingRow("快捷控制按钮颜色", "可单独设置各按钮开启时的填充色。",
            Actions(ColorControl("Wi‑Fi", () => app.QuickAppearance.Wifi, rgb => app.QuickAppearance with { Wifi = rgb }),
                ColorControl("蓝牙", () => app.QuickAppearance.Bluetooth, rgb => app.QuickAppearance with { Bluetooth = rgb }),
                ColorControl("音量", () => app.QuickAppearance.Volume, rgb => app.QuickAppearance with { Volume = rgb }))));

        Button ColorControl(string label, Func<uint> read, Func<uint, QuickControlAppearance> change)
        {
            Color FromRgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            var swatch = new Border { Width = 13, Height = 13, CornerRadius = new CornerRadius(6.5),
                VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(FromRgb(read())) };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            content.Children.Add(swatch);
            content.Children.Add(new TextBlock { Text = label });
            var button = new Button { Content = content };
            var picker = new ColorPicker { Color = FromRgb(read()), IsAlphaEnabled = false, IsAlphaSliderVisible = false,
                IsColorChannelTextInputVisible = false, IsHexInputVisible = true };
            var apply = new Button { Content = "应用" };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(picker);
            panel.Children.Add(apply);
            var popover = new Flyout { Content = panel };
            popover.Opening += (_, _) => picker.Color = FromRgb(read());
            apply.Click += (_, _) =>
            {
                var color = picker.Color;
                uint rgb = ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                if (!app.TrySetQuickAppearance(change(rgb))) return;
                swatch.Background = new SolidColorBrush(color);
                popover.Hide();
            };
            button.Flyout = popover;
            AutomationProperties.SetName(button, label + "颜色");
            return button;
        }
    }
}

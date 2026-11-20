using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AIEnter;

internal sealed partial class SettingsWindow
{
    string normalButtonColor = UiDefaults.ButtonColor, selectedButtonColor = UiDefaults.SelectedButtonColor;
    readonly Button normalColorButton = new(), selectedColorButton = new();
    void InitializeButtonColorSettings(StackPanel fields)
    {
        fields.Children.Add(Section("顶栏按钮颜色"));
        fields.Children.Add(PanelTheme.Hint("可选择普通按钮和选中 AI 按钮的颜色及透明度；文字自动匹配深浅色。只调整应用顶栏，不修改 AI 网页内容。"));
        fields.Children.Add(normalColorButton); fields.Children.Add(selectedColorButton);
        normalColorButton.Click += (_, _) => ChooseButtonColor(false);
        selectedColorButton.Click += (_, _) => ChooseButtonColor(true);
        var reset = new Button { Content = "按钮颜色跟随系统" };
        reset.Click += (_, _) => { normalButtonColor = selectedButtonColor = ""; UpdateColorLabels(); Preview(); };
        fields.Children.Add(reset); UpdateColorLabels();
    }
    void LoadButtonColors(UiAppearance value)
    {
        normalButtonColor = value.ButtonColor; selectedButtonColor = value.SelectedButtonColor; UpdateColorLabels();
    }
    void UpdateColorLabels()
    {
        void Label(Button button, string title, string value, uint fallback)
        {
            uint color = UiAppearance.ColorValue(value, fallback);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray), Background = new SolidColorBrush(Color.FromArgb((byte)(color >> 24),(byte)(color >> 16),(byte)(color >> 8),(byte)color)) });
            row.Children.Add(new TextBlock { Text = title + "：" + (value.Length == 0 ? "跟随系统" : value), VerticalAlignment = VerticalAlignment.Center });
            button.Content = row;
        }
        Label(normalColorButton, "普通按钮", normalButtonColor, 0x1CFFFFFF);
        Label(selectedColorButton, "选中 AI 按钮", selectedButtonColor, 0xFF0067C0);
    }
    void ChooseButtonColor(bool selected)
    {
        string current = selected ? selectedButtonColor : normalButtonColor;
        uint argb = UiAppearance.ColorValue(current, selected ? 0xFF0067C0u : 0x1CFFFFFFu);
        var picker = new ColorPicker { IsAlphaEnabled = true, IsAlphaSliderVisible = true, IsAlphaTextInputVisible = true,
            IsHexInputVisible = true, IsColorChannelTextInputVisible = true,
            Color = Color.FromArgb((byte)(argb >> 24),(byte)(argb >> 16),(byte)(argb >> 8),(byte)argb), MaxWidth = 320 };
        picker.ColorChanged += (_, args) =>
        {
            var c = args.NewColor; string value = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
            if (selected) selectedButtonColor = value; else normalButtonColor = value;
            UpdateColorLabels(); Preview();
        };
        var flyout = new Flyout { Content = picker };
        flyout.ShowAt(selected ? selectedColorButton : normalColorButton);
    }
}

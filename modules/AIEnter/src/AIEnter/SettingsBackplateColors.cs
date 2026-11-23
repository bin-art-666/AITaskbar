using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIEnter;

internal sealed partial class SettingsPage
{
    readonly ComboBox backplateColorMode = new() { Header = "背板颜色" };
    readonly ComboBox backplatePreviewTone = new() { Header = "预览主题" };
    readonly StackPanel backplateColorFields = new() { Spacing = 8 };
    readonly Button lightBackplateButton = new(), darkBackplateButton = new();
    string lightBackplateColor = "", darkBackplateColor = "";

    void InitializeBackplateColorSettings(StackPanel fields)
    {
        backplateColorMode.Items.Add("跟随任务栏");
        backplateColorMode.Items.Add("自定义配色");
        backplateColorMode.SelectionChanged += (_, _) => { RefreshBackplateColorFields(); Preview(); };
        fields.Children.Add(backplateColorMode);
        backplateColorFields.Children.Add(ColorRow(false, lightBackplateButton, "亮色底色"));
        backplateColorFields.Children.Add(ColorRow(true, darkBackplateButton, "暗色底色"));
        backplatePreviewTone.Items.Add("亮色");
        backplatePreviewTone.Items.Add("暗色");
        backplatePreviewTone.SelectedIndex = Application.Current.RequestedTheme == ApplicationTheme.Dark ? 1 : 0;
        backplatePreviewTone.SelectionChanged += (_, _) => UpdateAppearancePreview();
        fields.Children.Add(backplateColorFields);
    }

    StackPanel ColorRow(bool dark, Button choose, string name)
    {
        choose.Click += (_, _) => ChooseBackplateColor(dark, choose);
        var clear = new Button { Content = "重置" };
        AutomationProperties.SetName(clear, "重置" + name);
        clear.Click += (_, _) => SetBackplateColor(dark, "");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(choose);
        row.Children.Add(clear);
        return row;
    }

    void ChooseBackplateColor(bool dark, Button anchor)
    {
        backplatePreviewTone.SelectedIndex = dark ? 1 : 0;
        var picker = new ColorPicker
        {
            IsAlphaEnabled = false, IsHexInputVisible = true, MaxWidth = 320,
            Color = PanelTheme.PaletteColor(UiAppearance.ColorValue(dark ? darkBackplateColor : lightBackplateColor,
                PreviewBackplateBase(dark)))
        };
        picker.ColorChanged += (_, args) =>
        {
            var c = args.NewColor;
            SetBackplateColor(dark, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
        };
        new Flyout { Content = picker }.ShowAt(anchor);
    }

    void SetBackplateColor(bool dark, string value)
    {
        if (dark) darkBackplateColor = value; else lightBackplateColor = value;
        backplatePreviewTone.SelectedIndex = dark ? 1 : 0;
        RefreshBackplateColorFields();
        Preview();
    }

    void LoadBackplateColors(UiAppearance appearance)
    {
        lightBackplateColor = appearance.LightBackplateColor;
        darkBackplateColor = appearance.DarkBackplateColor;
        backplateColorMode.SelectedIndex = appearance.CustomBackplate ? 1 : 0;
        RefreshBackplateColorFields();
    }

    static uint PreviewBackplateBase(bool dark) => dark ? 0xFF1E222Au : 0xFFF3F6FAu;

    void RefreshBackplateColorFields()
    {
        backplatePreviewTone.Visibility = backplateColorFields.Visibility =
            backplateColorMode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SetColorLabel(lightBackplateButton, "亮色底色", lightBackplateColor, PreviewBackplateBase(false), "跟随任务栏");
        SetColorLabel(darkBackplateButton, "暗色底色", darkBackplateColor, PreviewBackplateBase(true), "跟随任务栏");
    }
}

using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AIEnter;

internal sealed partial class SettingsPage
{
    TextBlock? dirtyStatus;
    Button? saveButton, discardButton;
    string savedSnapshot = "";
    BackplateSurface? previewPlate;
    Border? previewSelectedButton, previewNormalButton, previewStatusButton;
    static bool HighContrast => new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
    static Windows.UI.Color SystemColor(Windows.UI.ViewManagement.UIColorType type, Windows.UI.Color fallback)
    {
        if (!HighContrast) return fallback;
        try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(type); }
        catch { return fallback; }
    }
    static Windows.UI.Color SurfaceColor() => SystemColor(Windows.UI.ViewManagement.UIColorType.Background, Microsoft.UI.Colors.White);
    static Windows.UI.Color TextColor() => SystemColor(Windows.UI.ViewManagement.UIColorType.Foreground, Windows.UI.Color.FromArgb(255, 35, 43, 48));
    bool IsDirty => savedSnapshot.Length > 0 && Snapshot() != savedSnapshot;

    string Snapshot() => JsonSerializer.Serialize(new
    {
        defaultProvider = provider.SelectedItem is ComboBoxItem selected ? selected.Tag : providerDraft.DefaultProvider,
        order = providerOrder.ToArray(), providers = providerDraft.AddedProviders,
        floating = floatingMode.IsOn, pinnedValue = pinned.IsOn, hotkeyValue = hotkey.Text, followGlobalCorners = followCorners.IsOn, webRadius = radius.Value,
        appearance = ReadAppearance(), resetPanelSize, resetLayouts
    });

    void RefreshDirty()
    {
        if (syncing || dirtyStatus is null) return;
        bool dirty = IsDirty;
        dirtyStatus.Text = dirty ? "有未保存的更改" : "所有更改已保存";
        saveButton!.IsEnabled = discardButton!.IsEnabled = dirty && !options.ReadOnly;
    }

    void SelectPage(string name, Grid host, Dictionary<string, StackPanel> pages, StackPanel sidebar)
    {
        foreach (var element in host.Children.OfType<ScrollViewer>())
            element.Visibility = Equals(element.Tag, name) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in sidebar.Children.OfType<Button>())
        {
            bool selected = Equals(button.Tag, name);
            button.Background = new SolidColorBrush(selected ? SystemColor(Windows.UI.ViewManagement.UIColorType.Accent, Windows.UI.Color.FromArgb(35, 0, 160, 190)) : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            button.Foreground = new SolidColorBrush(selected && !HighContrast ? Windows.UI.Color.FromArgb(255, 0, 115, 145) : TextColor());
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    void ArrangeSettingsPages(UIElement[] elements, Dictionary<string, StackPanel> pages)
    {
        string pageName = "任务栏", cardTitle = "", title = "";
        var cardContent = new StackPanel { Spacing = 8 };
        void Flush()
        {
            if (cardContent.Children.Count == 0) return;
            var body = new StackPanel { Spacing = 8 };
            if (cardTitle.Length > 0) body.Children.Add(new TextBlock { Text = cardTitle, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
            foreach (var child in cardContent.Children.ToArray())
            {
                cardContent.Children.Remove(child);
                if (child is Slider slider) body.Children.Add(SliderRow(slider)); else body.Children.Add(child);
            }
            pages[pageName].Children.Add(new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(HighContrast ? TextColor() : Windows.UI.Color.FromArgb(255, 224, 231, 234)), Background = new SolidColorBrush(SurfaceColor()), Child = body });
            cardContent = new StackPanel { Spacing = 8 };
        }
        foreach (var element in elements)
        {
            if (element is TextBlock heading && heading.FontSize >= 18)
            {
                title = heading.Text;
                if (title == "AI 与窗口") continue;
                Flush();
                if (title == "任务栏图标") { pageName = "任务栏"; cardTitle = "任务栏入口"; }
                else if (title == "截图") { pageName = "任务栏"; cardTitle = title; }
                else if (title == "全局快捷键") { pageName = "窗口与操作"; cardTitle = title; }
                else if (title == "管理 AI 与顶栏排序") { pageName = "AI 服务"; cardTitle = "AI 列表与顺序"; }
                else if (title == "窗口外观") { pageName = "外观"; cardTitle = "窗口外观"; }
                else if (title == "顶栏按钮颜色") { pageName = "外观"; cardTitle = title; }
                else if (title == "设置备份") { pageName = "备份与恢复"; cardTitle = title; }
                continue;
            }
            if (ReferenceEquals(element, provider)) { Flush(); pageName = "AI 服务"; cardTitle = "默认 AI"; }
            if (ReferenceEquals(element, floatingMode)) { Flush(); pageName = "窗口与操作"; cardTitle = "窗口行为"; }
            if (ReferenceEquals(element, backplateTransparency)) { Flush(); pageName = "外观"; cardTitle = "顶栏背板"; }
            if (ReferenceEquals(element, buttonRadius)) { Flush(); pageName = "外观"; cardTitle = "按钮与提示"; }
            if (ReferenceEquals(element, followCorners)) { Flush(); pageName = "外观"; cardTitle = "网页圆角"; }
            if (ReferenceEquals(element, taskbarSize)) { Flush(); pageName = "任务栏"; cardTitle = "任务栏入口"; }
            cardContent.Children.Add(element);
        }
        Flush();
        pages["外观"].Children.Insert(0, CreateAppearancePreview());
        foreach (var name in pages.Keys)
        {
            if (name == "备份与恢复") continue;
            var restore = new Button { Content = "恢复本页默认", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0), Padding = new Thickness(12, 7, 12, 7) };
            restore.Click += (_, _) => RestorePage(name);
            pages[name].Children.Add(restore);
        }
    }

    FrameworkElement SliderRow(Slider slider)
    {
        // Keep WinUI's native slider template (including keyboard, focus and high-contrast states),
        // while giving the track and thumb the softer proportions used by the settings references.
        slider.Height = 42;
        slider.CornerRadius = new CornerRadius(999);
        slider.Resources["SliderTrackThemeHeight"] = 7d;
        slider.Resources["SliderHorizontalThumbWidth"] = 26d;
        slider.Resources["SliderHorizontalThumbHeight"] = 26d;

        string label = slider.Header?.ToString() ?? ""; slider.Header = null;
        var row = new Grid { ColumnSpacing = 10, RowSpacing = 3 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.RowDefinitions.Add(new() { Height = GridLength.Auto }); row.RowDefinitions.Add(new() { Height = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var value = new TextBox { Text = ((int)slider.Value).ToString(), Width = 64, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(4, 2, 4, 2) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(value, label + "数值");
        Grid.SetColumn(value, 1); row.Children.Add(value);
        Grid.SetRow(slider, 1); Grid.SetColumnSpan(slider, 2); row.Children.Add(slider);
        slider.ValueChanged += (_, _) => { string next = ((int)Math.Round(slider.Value)).ToString(); if (value.Text != next) value.Text = next; };
        value.TextChanged += (_, _) =>
        {
            if (int.TryParse(value.Text, out int number) && number >= slider.Minimum && number <= slider.Maximum && slider.Value != number) slider.Value = number;
        };
        return row;
    }

    FrameworkElement CreateAppearancePreview()
    {
        previewSelectedButton = new Border { Height = 32, MinWidth = 92, Padding = new Thickness(12, 0, 12, 0), Child = new TextBlock { Text = "DeepSeek", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
        previewNormalButton = new Border { Height = 32, MinWidth = 66, Padding = new Thickness(12, 0, 12, 0), Child = new TextBlock { Text = "豆包", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center } };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        top.Children.Add(previewSelectedButton); top.Children.Add(previewNormalButton);
        previewStatusButton = new Border { Height = 32, Padding = new Thickness(10, 0, 10, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new TextBlock { Text = "AI 网页载入中…", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis } };
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(top); content.Children.Add(previewStatusButton);
        previewPlate = new BackplateSurface { Padding = new Thickness(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 228, 231)),
            CornerRadius = new CornerRadius(UiDefaults.BackplateRadius) };
        previewPlate.SetSurfaceContent(content);
        var card = new StackPanel { Spacing = 8 };
        card.Children.Add(new TextBlock { Text = "实时预览", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        card.Children.Add(new TextBlock { Text = "按钮、底部状态提示和背板会随设置即时更新。", Foreground = new SolidColorBrush(HighContrast ? TextColor() : Windows.UI.Color.FromArgb(255, 95, 107, 112)), TextWrapping = TextWrapping.Wrap });
        var previewBackdrop = new Grid { MinHeight = 110 };
        previewBackdrop.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        previewBackdrop.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        previewBackdrop.Children.Add(new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 58, 74)) });
        var lightBackdrop = new Border { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 218, 230, 234)) };
        Grid.SetColumn(lightBackdrop, 1); previewBackdrop.Children.Add(lightBackdrop);
        Grid.SetColumnSpan(previewPlate, 2);
        previewBackdrop.Children.Add(previewPlate);
        card.Children.Add(previewBackdrop);
        return new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(HighContrast ? TextColor() : Windows.UI.Color.FromArgb(255, 224, 231, 234)), Background = new SolidColorBrush(SurfaceColor()), Child = card };
    }

    void UpdateAppearancePreview()
    {
        if (previewPlate is null || previewSelectedButton is null || previewNormalButton is null || previewStatusButton is null) return;
        var appearance = ReadAppearance();
        static Windows.UI.Color ColorValue(string value, uint fallback)
        {
            uint color = UiAppearance.ColorValue(value, fallback);
            return Windows.UI.Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
        }
        previewPlate.CornerRadius = new CornerRadius(appearance.BackplateRadius);
        previewPlate.Padding = new Thickness(appearance.WindowPadding + 8);
        previewSelectedButton.CornerRadius = previewNormalButton.CornerRadius = previewStatusButton.CornerRadius = new CornerRadius(appearance.ButtonRadius);
        previewSelectedButton.Height = previewNormalButton.Height = previewStatusButton.Height = appearance.ButtonSize;
        previewPlate.UpdateAppearance(appearance);
        previewPlate.BorderBrush = new SolidColorBrush(HighContrast ? TextColor() : Windows.UI.Color.FromArgb(255, 218, 228, 231));
        var normal = HighContrast ? SystemColor(Windows.UI.ViewManagement.UIColorType.Background, Microsoft.UI.Colors.White) : ColorValue(appearance.ButtonColor, 0x1C14DDFF);
        var accent = HighContrast ? SystemColor(Windows.UI.ViewManagement.UIColorType.Accent, Microsoft.UI.Colors.DodgerBlue) : ColorValue(appearance.SelectedButtonColor, 0xFF32D4EB);
        previewSelectedButton.Background = previewStatusButton.Background = new SolidColorBrush(accent);
        previewNormalButton.Background = new SolidColorBrush(normal);
        previewSelectedButton.BorderThickness = previewNormalButton.BorderThickness = previewStatusButton.BorderThickness = new Thickness(1);
        previewSelectedButton.BorderBrush = previewStatusButton.BorderBrush = new SolidColorBrush(HighContrast ? TextColor() : accent);
        previewNormalButton.BorderBrush = new SolidColorBrush(HighContrast ? TextColor() : normal);
    }

    void RestorePage(string page)
    {
        var defaults = UiDefaults.CreateOptions();
        syncing = true;
        if (page == "AI 服务")
        {
            LoadProviderOrder(defaults); provider.SelectedItem = provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == UiDefaults.Provider);
        }
        else if (page == "窗口与操作")
        {
            floatingMode.IsOn = UiDefaults.FloatingPanel; pinned.IsOn = UiDefaults.Pinned; hotkey.Text = UiDefaults.Hotkey;
        }
        else if (page == "任务栏")
        {
            var d = defaults.Appearance ?? new(); taskbarSize.Value = d.TaskbarIconSize; flowSpeed.Value = d.FlowSpeed; screenshotDoubleClick.IsOn = d.ScreenshotDoubleClick; screenshotSeconds.Value = d.ScreenshotSeconds; screenshotFolder.Text = d.ScreenshotFolder; glow.IsOn = d.GlowEnabled; glowRange.Value = d.GlowRange; glowIntensity.Value = d.GlowIntensity;
        }
        else if (page == "外观")
        {
            LoadVisualDefaults(); followCorners.IsOn = UiDefaults.FollowGlobalCorners; radius.Value = UiDefaults.WebCornerRadius; resetPanelSize = true;
        }
        syncing = false; RefreshFields(); Preview(); RefreshDirty(); status.Text = $"已预览“{page}”默认值；保存后生效。";
    }

    void DiscardDraft()
    {
        syncing = true; resetPanelSize = resetLayouts = false;
        var value = options.Data; LoadProviderOrder(value);
        provider.SelectedItem = provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == (value.DefaultProvider ?? UiDefaults.Provider));
        floatingMode.IsOn = value.FloatingPanel; pinned.IsOn = value.Pinned; followCorners.IsOn = value.FollowGlobalCorners;
        radius.Value = value.WebCornerRadius; LoadAppearance(value.Appearance ?? new()); hotkey.Text = value.Hotkey;
        syncing = false; RefreshFields(); Preview(); savedSnapshot = Snapshot(); RefreshDirty(); status.Text = "已放弃未保存的修改。";
    }

}

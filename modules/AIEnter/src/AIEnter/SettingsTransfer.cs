using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace AIEnter;

internal sealed partial class SettingsWindow
{
    sealed record PortableSettings(int FormatVersion = 1, string[]? ProviderOrder = null, AiProvider[]? AddedProviders = null,
        string? DefaultProvider = null, bool FloatingPanel = true, bool Pinned = false,
        bool FollowGlobalCorners = false, int WebCornerRadius = 28, UiAppearance? Appearance = null, string Hotkey = UiDefaults.Hotkey);
    bool resetLayouts;

    void InitializeSettingsTransfer(StackPanel fields)
    {
        fields.Children.Add(Section("设置备份"));
        fields.Children.Add(PanelTheme.Hint("导出文件只包含 AI 列表、默认服务、快捷键和界面设置，不包含网页登录、聊天记录或窗口位置。导入和恢复默认需再点“保存设置”才会生效。"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var export = new Button { Content = "导出设置" }; export.Click += async (_, _) => await ExportSettings();
        var import = new Button { Content = "导入设置" }; import.Click += async (_, _) => await ImportSettings();
        var reset = new Button { Content = "恢复全部默认" }; reset.Click += (_, _) => ResetAllSettings();
        row.Children.Add(export); row.Children.Add(import); row.Children.Add(reset); fields.Children.Add(row);
    }

    nint PickerWindow => WinRT.Interop.WindowNative.GetWindowHandle(this);
    async Task ExportSettings()
    {
        var picker = new FileSavePicker { SuggestedFileName = "AIEnter-settings", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("AIEnter 设置", [".json"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerWindow);
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        var value = AiPolicy.WithProviderSettings(options.Data, providerDraft.AddedProviders ?? [], providerOrder.ToArray(),
            provider.SelectedItem is ComboBoxItem item ? (string)item.Tag : providerDraft.DefaultProvider ?? UiDefaults.Provider);
        value = value with { FloatingPanel = floatingMode.IsOn, Pinned = pinned.IsOn, FollowGlobalCorners = followCorners.IsOn,
            WebCornerRadius = (int)Math.Round(radius.Value), Appearance = ReadAppearance(), Hotkey = hotkey.Text.Trim() };
        var dto = new PortableSettings(1, value.ProviderOrder, value.AddedProviders, value.DefaultProvider, value.FloatingPanel,
            value.Pinned, value.FollowGlobalCorners, value.WebCornerRadius, value.Appearance, value.Hotkey);
        string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(file.Path, json); status.Text = "设置已导出；文件不含网页资料和窗口位置。";
    }

    async Task ImportSettings()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json"); WinRT.Interop.InitializeWithWindow.Initialize(picker, PickerWindow);
        var file = await picker.PickSingleFileAsync(); if (file is null) return;
        try
        {
            if ((await file.GetBasicPropertiesAsync()).Size > 1024 * 1024) throw new InvalidDataException("设置文件超过 1 MB。");
            var json = await File.ReadAllTextAsync(file.Path);
            var dto = JsonSerializer.Deserialize<PortableSettings>(json, new JsonSerializerOptions
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("设置文件为空。");
            if (dto.FormatVersion != 1) throw new NotSupportedException("此设置文件版本不受支持。");
            var draft = options.Data with { ProviderOrder = dto.ProviderOrder, AddedProviders = dto.AddedProviders,
                DefaultProvider = dto.DefaultProvider, FloatingPanel = dto.FloatingPanel, Pinned = dto.Pinned,
                FollowGlobalCorners = dto.FollowGlobalCorners, WebCornerRadius = dto.WebCornerRadius,
                Appearance = dto.Appearance, Hotkey = dto.Hotkey };
            draft = AiPolicy.Upgrade(draft);
            string preferred = draft.DefaultProvider ?? UiDefaults.Provider;
            if (!AiPolicy.AvailableProviders(draft).Contains(preferred)) throw new InvalidDataException("默认 AI 不在导入的 AI 列表中。");
            draft = draft with { Provider = AiPolicy.AvailableProviders(draft).Contains(options.Data.Provider) ? options.Data.Provider : preferred };
            if (!HotkeyPolicy.TryNormalize(draft.Hotkey, out string normalizedHotkey)) throw new InvalidDataException("导入文件中的快捷键格式无效。");
            draft = draft with { Hotkey = normalizedHotkey };
            AiPolicy.Validate(draft);
            LoadProviderOrder(draft);
            provider.SelectedItem = provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == preferred);
            floatingMode.IsOn = draft.FloatingPanel; pinned.IsOn = draft.Pinned;
            followCorners.IsOn = draft.FollowGlobalCorners; radius.Value = draft.WebCornerRadius;
            LoadAppearance(draft.Appearance ?? new()); hotkey.Text = draft.Hotkey; resetPanelSize = false; resetLayouts = false;
            RefreshFields(); Preview(); RefreshDirty(); status.Text = "设置已载入预览；检查后点击“保存设置”应用。网页登录资料和窗口位置未导入。";
        }
        catch (Exception error) when (error is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or JsonException)
        { status.Text = "导入失败：" + error.Message; }
    }

    void ResetAllSettings()
    {
        var defaults = UiDefaults.CreateOptions();
        LoadProviderOrder(defaults);
        provider.SelectedItem = provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == UiDefaults.Provider);
        floatingMode.IsOn = UiDefaults.FloatingPanel; pinned.IsOn = UiDefaults.Pinned;
        followCorners.IsOn = UiDefaults.FollowGlobalCorners; radius.Value = UiDefaults.WebCornerRadius;
        LoadAppearance(defaults.Appearance ?? new()); hotkey.Text = UiDefaults.Hotkey;
        resetPanelSize = true; resetLayouts = true; RefreshFields(); Preview();
        RefreshDirty(); status.Text = "已预览全部默认设置；保存后重置 AI 列表、界面和窗口位置。网页登录资料不变。";
    }
}

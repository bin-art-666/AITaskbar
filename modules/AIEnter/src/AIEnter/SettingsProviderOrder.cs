using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AIEnter;

internal sealed partial class SettingsWindow
{
    readonly StackPanel providerOrderRows = new() { Spacing = 6 };
    readonly TextBox providerSearch = new() { PlaceholderText = "查找 AI 名称或网址", MaxLength = 200 };
    readonly List<string> providerOrder = [];
    AiOptions providerDraft = new();
    void InitializeProviderManagement(StackPanel fields)
    {
        fields.Children.Add(provider);
        fields.Children.Add(PanelTheme.Hint("默认 AI 在下次启动应用时打开；当前对话保持不变。"));
        fields.Children.Add(Section("管理 AI 与顶栏排序"));
        var add = new Button { Content = "＋ 添加 AI", IsEnabled = !options.ReadOnly };
        add.Click += async (_, _) => await EditProvider(null);
        fields.Children.Add(add);
        fields.Children.Add(PanelTheme.Hint("添加数量不限。上移／下移调整顶栏顺序；编辑、移除及排序均在保存设置后生效，关闭放弃未保存修改。"));
        providerSearch.TextChanged += (_, _) => RenderProviderOrder();
        fields.Children.Add(providerSearch);
        fields.Children.Add(new ScrollViewer { Content = providerOrderRows, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }
    void LoadProviderOrder(AiOptions value)
    {
        providerDraft = AiPolicy.Upgrade(value);
        providerOrder.Clear(); providerOrder.AddRange(AiPolicy.OrderedProviders(providerDraft));
        provider.Items.Clear(); providerSearch.Text = ""; RenderProviderOrder();
    }
    void MoveProvider(string id, int direction)
    {
        int from = providerOrder.IndexOf(id), to = from + direction;
        if (from < 0 || to < 0 || to >= providerOrder.Count) return;
        (providerOrder[from], providerOrder[to]) = (providerOrder[to], providerOrder[from]);
        RenderProviderOrder();
        var row = providerOrderRows.Children.OfType<Grid>().FirstOrDefault(row => (string)row.Tag == id);
        if (row != null)
        {
            var control = (Button)row.Children[direction < 0 ? 1 : 2];
            if (!control.IsEnabled) control = (Button)row.Children[direction < 0 ? 2 : 1];
            control.Focus(FocusState.Programmatic);
        }
        status.Text = "AI 顺序已调整，点击保存设置后生效。"; RefreshDirty();
    }
    void RenderProviderOrder()
    {
        string preferred = provider.SelectedItem is ComboBoxItem choice ? (string)choice.Tag : providerDraft.DefaultProvider ?? UiDefaults.Provider;
        provider.Items.Clear(); providerOrderRows.Children.Clear();
        string query = providerSearch.Text.Trim();
        for (int index = 0; index < providerOrder.Count; index++)
        {
            string id = providerOrder[index];
            var endpoint = AiPolicy.Resolve(providerDraft with { Provider = id });
            var item = new ComboBoxItem { Content = endpoint.Name, Tag = id }; provider.Items.Add(item);
            if (id == preferred) provider.SelectedItem = item;
            if (query.Length > 0 && !endpoint.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !endpoint.Url.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var row = new Grid { ColumnSpacing = 4, Tag = id };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int column = 0; column < 4; column++) row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var label = new TextBlock { Text = $"{index + 1}. {endpoint.Name}", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTipService.SetToolTip(label, endpoint.Url); row.Children.Add(label);
            var up = new Button { Content = "上移", Padding = new Thickness(6,4,6,4), IsEnabled = index > 0 && !options.ReadOnly };
            var down = new Button { Content = "下移", Padding = new Thickness(6,4,6,4), IsEnabled = index < providerOrder.Count - 1 && !options.ReadOnly };
            AutomationProperties.SetName(up, $"上移 {endpoint.Name}"); AutomationProperties.SetName(down, $"下移 {endpoint.Name}");
            up.Click += (_, _) => MoveProvider(id, -1); down.Click += (_, _) => MoveProvider(id, 1);
            Grid.SetColumn(up, 1); Grid.SetColumn(down, 2); row.Children.Add(up); row.Children.Add(down);
            if ((providerDraft.AddedProviders ?? []).FirstOrDefault(p => p.Id == id) is { } added)
            {
                var edit = new Button { Content = "编辑", Padding = new Thickness(6,4,6,4), IsEnabled = !options.ReadOnly };
                var remove = new Button { Content = "移除", Padding = new Thickness(6,4,6,4), IsEnabled = !options.ReadOnly };
                AutomationProperties.SetName(edit, $"编辑 {endpoint.Name}"); AutomationProperties.SetName(remove, $"移除 {endpoint.Name}");
                edit.Click += async (_, _) => await EditProvider(added);
                remove.Click += async (_, _) => await RemoveProvider(added);
                Grid.SetColumn(edit, 3); Grid.SetColumn(remove, 4); row.Children.Add(edit); row.Children.Add(remove);
            }
            providerOrderRows.Children.Add(row);
        }
        if (provider.SelectedItem == null) provider.SelectedItem = provider.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == UiDefaults.Provider);
        if (providerOrderRows.Children.Count == 0) providerOrderRows.Children.Add(PanelTheme.Hint("没有匹配的 AI。"));
    }
    async Task EditProvider(AiProvider? original)
    {
        using var lease = KeepOpen();
        var name = new TextBox { Header = "AI 名称", PlaceholderText = "例如：我的 AI", MaxLength = 40, Text = original?.Name ?? "" };
        var url = new TextBox { Header = "AI 网页地址", PlaceholderText = "https://…", MaxLength = 2048, Text = original?.Url ?? "" };
        var error = PanelTheme.Hint("");
        var form = new StackPanel { Spacing = 12 }; form.Children.Add(name); form.Children.Add(url); form.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = original == null ? "添加 AI" : "编辑 AI", Content = form,
            PrimaryButtonText = original == null ? "添加" : "应用修改", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        AiProvider? edited = null;
        url.TextChanged += (_, _) =>
        {
            try { string normalized = AiPolicy.Https(url.Text); error.Text = AiPolicy.AvailableProviders(providerDraft).Any(id => id != original?.Id && AiPolicy.Resolve(providerDraft with { Provider = id }).Url == normalized) ? "该网址已存在，添加后会共享同一网站会话。" : ""; }
            catch (InvalidDataException) { error.Text = "请输入完整的 HTTPS 网页地址。"; }
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                edited = new(original?.Id ?? "ai-" + Guid.NewGuid().ToString("N"), name.Text.Trim(), AiPolicy.Https(url.Text));
                var candidates = (providerDraft.AddedProviders ?? []).Where(p => p.Id != edited.Id).Append(edited).ToArray();
                AiPolicy.Validate(providerDraft with { AddedProviders = candidates });
            }
            catch (InvalidDataException failure) { args.Cancel = true; error.Text = failure.Message; edited = null; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || edited == null) return;
        providerDraft = providerDraft with { AddedProviders = (providerDraft.AddedProviders ?? []).Where(p => p.Id != edited.Id).Append(edited).ToArray() };
        if (!providerOrder.Contains(edited.Id)) providerOrder.Add(edited.Id);
        providerSearch.Text = ""; RenderProviderOrder(); status.Text = "AI 列表已修改，点击保存设置后生效。"; RefreshDirty();
    }
    async Task RemoveProvider(AiProvider item)
    {
        using var lease = KeepOpen();
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = $"移除 {item.Name}？", Content = "仅移除入口，保留网页登录资料。保存设置后生效。",
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string preferred = provider.SelectedItem is ComboBoxItem chosen ? (string)chosen.Tag : UiDefaults.Provider;
        providerOrder.Remove(item.Id);
        providerDraft = AiPolicy.WithProviderSettings(providerDraft, (providerDraft.AddedProviders ?? []).Where(p => p.Id != item.Id).ToArray(), providerOrder.ToArray(), preferred);
        provider.Items.Clear(); RenderProviderOrder(); status.Text = "AI 入口已标记移除，点击保存设置后生效。"; RefreshDirty();
    }
}

using LumaBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private static readonly string[] transitions = ["standard", "soft", "off"];
    private readonly ToggleSwitch glassEnabled = new();
    private readonly ToggleSwitch wallpaperRegion = new();
    private readonly ToggleSwitch contrastProtection = new();
    private readonly ToggleSwitch primaryOverride = new();
    private readonly ToggleSwitch automaticTint = new();
    private readonly ToggleSwitch topLine = new();
    private readonly Button chooseTint = new();
    private readonly ToggleSwitch mouseGlow = new();
    private readonly Slider glowRadius = new() { Minimum = 5, Maximum = 200, StepFrequency = 1, Width = 140 };
    private readonly Slider glowStrength = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 140 };
    private readonly ComboBox pace = new() { Width = 156 };
    private readonly ComboBox transition = new() { Width = 156 };
    private readonly TextBox rules = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 170 };

    private void BuildGlassAdvanced(StackPanel advanced)
    {
        AddSwitch(advanced, "启用背景材质", "关闭时使用预览栏的默认背景。", glassEnabled,
            draft => draft.Glass.Enabled = glassEnabled.IsOn);
        AddSwitch(advanced, "优先分析栏后壁纸", "用任务栏区域判断壁纸明暗。", wallpaperRegion,
            draft => draft.Glass.TaskbarRegion = wallpaperRegion.IsOn);
        AddSwitch(advanced, "自动对比度保护", "复杂壁纸下自动提高背景遮盖程度。", contrastProtection,
            draft => draft.Glass.AutoContrast = contrastProtection.IsOn);
        AddSwitch(advanced, "主屏单独设置", "为预览栏所在的主屏保存三套独立场景。", primaryOverride, UpdatePrimaryOverride);
        AddSwitch(advanced, "自动染色", "跟随当前场景的明暗判断。", automaticTint,
            _ => SelectedMaterial!.AutomaticColor = automaticTint.IsOn);
        AutomationProperties.SetName(chooseTint, "选择任务栏背景色");
        chooseTint.Click += (_, _) => PickTint();
        advanced.Children.Add(SettingRow("背景色", "用于关闭自动染色的场景。", chooseTint));
        AddSwitch(advanced, "顶部分隔线", "在预览栏顶边显示细线。", topLine,
            _ => SelectedMaterial!.ShowLine = topLine.IsOn);
    }

    private void BuildLumaPreferences(StackPanel page)
    {
        var options = new StackPanel { Spacing = 6 };
        AddSwitch(options, "鼠标光效", "指针在预览栏上移动时显示光晕。", mouseGlow,
            draft => draft.MouseGlow = mouseGlow.IsOn);
        AddSlider("光晕半径", glowRadius, (draft, value) => draft.MouseGlowRadius = value);
        AddSlider("光效强度", glowStrength, (draft, value) => draft.MouseGlowStrength = value);
        foreach (var choice in new[] { "保留当前节奏", "灵敏", "均衡", "舒缓" }) pace.Items.Add(choice);
        foreach (var choice in new[] { "标准", "柔和", "关闭" }) transition.Items.Add(choice);
        AutomationProperties.SetName(pace, "明暗判断速度");
        AutomationProperties.SetName(transition, "背景过渡");
        pace.SelectionChanged += (_, _) =>
        {
            if (pace.SelectedIndex > 0) ChangeLuma(draft => draft.SetPace((string)pace.SelectedItem));
        };
        transition.SelectionChanged += (_, _) =>
        {
            if (transition.SelectedIndex >= 0) ChangeLuma(draft => draft.Transition = transitions[transition.SelectedIndex]);
        };
        options.Children.Add(SettingRow("明暗判断速度", "选择一套响应节奏，原有参数会保留到下一次保存。", pace));
        options.Children.Add(SettingRow("背景过渡", "控制外观切换时的过渡速度。", transition));
        rules.Header = "应用明暗规则";
        rules.PlaceholderText = "例如 Code.exe=dark";
        rules.TextChanged += (_, _) => ChangeLuma(_ => { }, preview: false);
        options.Children.Add(rules);
        options.Children.Add(new TextBlock
        {
            Text = "每行一条。auto 自动识别，light 浅色，dark 深色，hold 保持进入前的明暗。规则在应用最大化或全屏时生效。",
            TextWrapping = TextWrapping.Wrap, Opacity = .75
        });
        page.Children.Add(new Expander { Header = "鼠标光效、明暗与应用规则", Content = options,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });

        void AddSlider(string label, Slider slider, Action<Settings, int> change)
        {
            var value = new TextBlock { Width = 42, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(slider, label);
            slider.ValueChanged += (_, _) =>
            {
                value.Text = $"{slider.Value:0}%";
                ChangeLuma(draft => change(draft, (int)slider.Value));
            };
            options.Children.Add(SettingRow(label, "", Actions(slider, value)));
        }
    }

    private void AddSwitch(StackPanel page, string label, string help, ToggleSwitch control, Action<Settings> edit)
    {
        AutomationProperties.SetName(control, label);
        control.Toggled += (_, _) => ChangeLuma(edit);
        page.Children.Add(SettingRow(label, help, control));
    }

    private void ChangeLuma(Action<Settings> edit, bool preview = true)
    {
        if (syncingGlass || glassSaving || glassDraft is null) return;
        edit(glassDraft);
        glassDirty = true;
        SyncGlassControls();
        if (preview) _ = PublishGlassDraft();
    }

    private void UpdatePrimaryOverride(Settings draft)
    {
        if (previewScreen is not { } display) return;
        var glass = draft.Glass;
        if (primaryOverride.IsOn)
        {
            glass.PerMonitor = true;
            if (!glass.MonitorOverrides.TryGetValue(display, out var target))
                glass.MonitorOverrides[display] = target = new MonitorAppearanceSet
                {
                    Desktop = glass.Desktop.Copy(), Window = glass.Window.Copy(), Maximized = glass.Maximized.Copy()
                };
            target.Enabled = true;
        }
        else if (glass.MonitorOverrides.TryGetValue(display, out var existing)) existing.Enabled = false;
    }

    private void PickTint()
    {
        if (SelectedMaterial is not { } material) return;
        var picker = new ColorPicker
        {
            Color = TintColor(material.Color), IsAlphaEnabled = false, IsHexInputVisible = true,
            IsColorChannelTextInputVisible = false, MaxWidth = 250
        };
        var flyout = new Flyout();
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(picker);
        body.Children.Add(ActionButton("使用此背景色", () =>
        {
            var value = picker.Color;
            ChangeLuma(_ => material.Color = $"#{value.R:X2}{value.G:X2}{value.B:X2}");
            flyout.Hide();
        }));
        flyout.Content = body;
        flyout.ShowAt(chooseTint);
    }

    private static Color TintColor(string value)
    {
        var rgb = Convert.ToUInt32(value[1..], 16);
        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    private void SyncLumaControls(bool available, TaskbarAppearance? material)
    {
        glassEnabled.IsEnabled = wallpaperRegion.IsEnabled = contrastProtection.IsEnabled =
            automaticTint.IsEnabled = topLine.IsEnabled = mouseGlow.IsEnabled =
            pace.IsEnabled = transition.IsEnabled = rules.IsEnabled = available;
        primaryOverride.IsEnabled = available && previewScreen is not null;
        chooseTint.IsEnabled = available && material?.AutomaticColor == false;
        if (glassDraft is not { } draft || material is null)
        {
            glowRadius.IsEnabled = glowStrength.IsEnabled = false;
            return;
        }
        glassEnabled.IsOn = draft.Glass.Enabled;
        wallpaperRegion.IsOn = draft.Glass.TaskbarRegion;
        contrastProtection.IsOn = draft.Glass.AutoContrast;
        primaryOverride.IsOn = draft.Glass.PerMonitor && previewScreen is { } display &&
            draft.Glass.MonitorOverrides.TryGetValue(display, out var custom) && custom.Enabled;
        automaticTint.IsOn = material.AutomaticColor;
        topLine.IsOn = material.ShowLine;
        chooseTint.Content = material.Color;
        mouseGlow.IsOn = draft.MouseGlow;
        glowRadius.IsEnabled = glowStrength.IsEnabled = available && draft.MouseGlow;
        glowRadius.Value = draft.MouseGlowRadius;
        glowStrength.Value = draft.MouseGlowStrength;
        transition.SelectedIndex = Array.IndexOf(transitions, draft.Transition);
    }

    private void LoadLumaPreferences(Settings snapshot)
    {
        pace.SelectedIndex = 0;
        rules.Text = string.Join(Environment.NewLine, snapshot.ApplicationRules.Select(rule => $"{rule.Key}={rule.Value}"));
    }

    private Dictionary<string, string> ReadApplicationRules()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in rules.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || Settings.ProcessKey(parts[0]) is not { Length: > 0 } name ||
                parts[1] is not ("auto" or "light" or "dark" or "hold") || !result.TryAdd(name, parts[1]))
                throw new InvalidDataException("应用规则须使用不重复的「进程名=auto/light/dark/hold」。");
        }
        return result;
    }

    private static void CopyLumaPreferences(Settings source, Settings target)
    {
        target.Glass = source.Glass;
        target.MouseGlow = source.MouseGlow;
        target.MouseGlowRadius = source.MouseGlowRadius;
        target.MouseGlowStrength = source.MouseGlowStrength;
        target.Transition = source.Transition;
        target.ApplicationRules = source.ApplicationRules;
        target.TimingVersion = source.TimingVersion;
        target.PollMilliseconds = source.PollMilliseconds;
        target.StableMilliseconds = source.StableMilliseconds;
        target.ContentStableMilliseconds = source.ContentStableMilliseconds;
        target.MinimumSwitchMilliseconds = source.MinimumSwitchMilliseconds;
        target.SourceStableMilliseconds = source.SourceStableMilliseconds;
        target.SourceMinimumSwitchMilliseconds = source.SourceMinimumSwitchMilliseconds;
    }
}

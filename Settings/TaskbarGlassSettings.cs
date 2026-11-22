using LumaBar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AITaskbar;

internal sealed partial class TaskbarSettingsWindow
{
    private static readonly string[] materials = ["normal", "clear", "blur", "acrylic", "opaque"];
    private static readonly MaterialScene[] scenes = [MaterialScene.Desktop, MaterialScene.Window, MaterialScene.Maximized];
    private readonly ComboBox materialChoice = new() { Width = 156 };
    private readonly ComboBox sceneChoice = new() { Width = 156 };
    private readonly Slider transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1 };
    private readonly Slider blur = new() { Minimum = 0, Maximum = 100, StepFrequency = .1, SmallChange = .1,
        IsThumbToolTipEnabled = false };
    private readonly CheckBox contrastProtection = new() { Content = "自动对比度保护", MinWidth = 0 };
    private readonly TextBlock transparencyValue = new() { MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock blurValue = new() { MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock effectiveTransparency = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = .75 };
    private readonly TextBlock sceneHint = new() { Opacity = .75 };
    private readonly StackPanel glassFields = new() { Spacing = 6 };
    private readonly StackPanel glassActions = Actions();
    private Button saveGlass = null!, cancelGlass = null!;
    private Settings? glassDraft;
    private string? previewScreen;
    private bool syncingGlass, glassDirty, glassSaving;
    private Task glassCommit = Task.CompletedTask;
    private int glassSession;

    private MaterialScene SelectedScene => scenes[Math.Max(0, sceneChoice.SelectedIndex)];
    private TaskbarAppearance? SelectedMaterial => glassDraft?.Glass.For(SelectedScene,
        glassDraft.Glass.PerMonitor ? previewScreen : null);

    private void BuildGlassSettings(StackPanel page)
    {
        foreach (var material in materials) materialChoice.Items.Add(material == "normal" ? "默认外观" : MaterialNames.Name(material));
        foreach (var scene in scenes) sceneChoice.Items.Add(MaterialNames.Scene(scene));
        AutomationProperties.SetName(materialChoice, "任务栏材质");
        AutomationProperties.SetName(sceneChoice, "任务栏场景");
        AutomationProperties.SetName(transparency, "任务栏透明度");
        AutomationProperties.SetName(blur, "任务栏模糊强度");
        AutomationProperties.SetHelpText(blur, "低强度占更多滑块行程。右侧显示实际模糊强度。");
        ToolTipService.SetToolTip(contrastProtection, "复杂壁纸下自动增加染色，以保持图标和文字清晰。");
        contrastProtection.Checked += (_, _) => ChangeLuma(draft => draft.Glass.AutoContrast = true);
        contrastProtection.Unchecked += (_, _) => ChangeLuma(draft => draft.Glass.AutoContrast = false);
        glassFields.Children.Add(sceneHint);
        glassFields.Children.Add(SettingRow("材质", "", materialChoice));
        glassFields.Children.Add(GlassSliderRow("透明度", transparency, transparencyValue,
            Actions(effectiveTransparency, contrastProtection)));
        glassFields.Children.Add(GlassSliderRow("模糊强度", blur, blurValue));
        var advanced = new Expander { Header = "场景设置", HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var scope = new StackPanel { Spacing = 8 };
        scope.Children.Add(SettingRow("编辑场景", "打开设置时选中当前场景。", sceneChoice));
        scope.Children.Add(ActionButton("应用当前材质与数值到所有场景", ApplyGlassToAllScenes));
        BuildGlassAdvanced(scope);
        advanced.Content = scope;
        glassFields.Children.Add(advanced);
        cancelGlass = ActionButton("取消外观更改", () => _ = ResetGlassAsync(keepScene: true));
        saveGlass = ActionButton("保存任务栏外观", () => glassCommit = SaveGlassAsync());
        glassActions.Children.Add(cancelGlass);
        glassActions.Children.Add(saveGlass);
        page.Children.Add(glassFields);
        materialChoice.SelectionChanged += (_, _) => EditGlass(item => item.Material = materials[materialChoice.SelectedIndex]);
        transparency.ValueChanged += (_, _) => EditGlass(item => item.TintOpacity = 100 - (int)transparency.Value);
        blur.ValueChanged += (_, _) =>
        {
            int radius = BlurAtPosition(blur.Value);
            if (SelectedMaterial?.BlurRadius != radius) EditGlass(item => item.BlurRadius = radius);
        };
        sceneChoice.SelectionChanged += (_, _) =>
        {
            if (syncingGlass) return;
            SyncGlassControls();
            if (glassDirty) _ = PublishGlassDraft();
        };
        SyncGlassControls();
    }

    private static StackPanel GlassSliderRow(string title, Slider slider, TextBlock value, UIElement? details = null)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 1);
        header.Children.Add(value);
        var section = new StackPanel { Spacing = 3, Margin = new Thickness(0, 4, 0, 6) };
        section.Children.Add(header);
        section.Children.Add(slider);
        if (details is not null) section.Children.Add(details);
        return section;
    }

    internal async Task ResetGlassAsync(bool keepScene = false)
    {
        int session = ++glassSession;
        MaterialScene? editing = keepScene ? SelectedScene : null;
        glassDraft = null;
        glassDirty = false;
        SyncGlassControls();
        try
        {
            var state = await app.EditAppearance(worker =>
            {
                worker.PreviewFromHost(null);
                return (Draft: worker.CopySettings(), Scene: worker.CurrentScene,
                    Display: System.Windows.Forms.Screen.PrimaryScreen?.DeviceName);
            });
            if (session != glassSession) return;
            glassDraft = state.Draft;
            previewScreen = state.Display;
            syncingGlass = true;
            sceneChoice.SelectedIndex = Array.IndexOf(scenes, editing ?? state.Scene);
            LoadLumaPreferences(state.Draft);
            syncingGlass = false;
            SyncGlassControls();
        }
        catch (Exception error) { if (session == glassSession) SetStatus(error.Message); }
    }

    private void EditGlass(Action<TaskbarAppearance> change)
    {
        if (syncingGlass || glassSaving || SelectedMaterial is not { } item) return;
        change(item);
        glassDraft!.Glass.Enabled = true;
        glassDirty = true;
        SyncGlassControls();
        _ = PublishGlassDraft();
    }

    private void ApplyGlassToAllScenes()
    {
        if (SelectedMaterial is not { } selection || glassSaving) return;
        var glass = glassDraft!.Glass;
        foreach (var scene in scenes)
        {
            var item = glass.For(scene, glass.PerMonitor ? previewScreen : null);
            item.Material = selection.Material;
            item.TintOpacity = selection.TintOpacity;
            item.BlurRadius = selection.BlurRadius;
        }
        glass.Enabled = glassDirty = true;
        SyncGlassControls();
        _ = PublishGlassDraft();
    }

    private Settings SnapshotGlassDraft() => Files.Json.Deserialize<Settings>(Files.Json.Serialize(glassDraft));

    private async Task PublishGlassDraft()
    {
        int session = glassSession;
        var snapshot = SnapshotGlassDraft();
        var scene = SelectedScene;
        try { await app.EditAppearance(worker => { worker.PreviewFromHost(snapshot, scene); return true; }); }
        catch (Exception error) { if (session == glassSession) SetStatus(error.Message); }
    }

    private async Task SaveGlassAsync()
    {
        if (!glassDirty || glassSaving) return;
        var snapshot = SnapshotGlassDraft();
        glassSaving = true;
        SyncGlassControls();
        try
        {
            snapshot.ApplicationRules = ReadApplicationRules();
            await app.EditAppearance(worker =>
            {
                worker.SaveFromHost(settings => CopyLumaPreferences(snapshot, settings));
                return true;
            });
            glassDirty = false;
            SetStatus("任务栏外观已保存。");
        }
        catch (Exception error) { SetStatus(error.Message); }
        finally { glassSaving = false; SyncGlassControls(); }
    }

    internal void HideSettings()
    {
        if (!PendingSettings.IsCompleted) return;
        if (glassDirty) _ = ResetGlassAsync();
        haloSettings?.Reset();
        aiSettings?.ResetPreview();
        appCenterSettings?.Reset();
        AppWindow.Hide();
    }

    private void SyncGlassControls()
    {
        var item = SelectedMaterial;
        syncingGlass = true;
        if (item is not null)
        {
            materialChoice.SelectedIndex = Array.IndexOf(materials, item.Material);
            transparency.Value = 100 - item.TintOpacity;
            if (BlurAtPosition(blur.Value) != item.BlurRadius) blur.Value = PositionForBlur(item.BlurRadius);
        }
        bool available = item is not null && !glassSaving;
        sceneChoice.IsEnabled = materialChoice.IsEnabled = available;
        transparency.IsEnabled = available && item!.Material is "clear" or "blur" or "acrylic";
        blur.IsEnabled = available && item!.Material == "blur";
        transparencyValue.Text = transparency.IsEnabled ? $"{transparency.Value:0}%" : "—";
        blurValue.Text = blur.IsEnabled ? $"{item!.BlurRadius}" : "—";
        AutomationProperties.SetItemStatus(blur, blur.IsEnabled ? $"模糊强度 {item!.BlurRadius}" : "");
        sceneHint.Text = item is null ? "正在读取外观…" : $"编辑场景 · {MaterialNames.Scene(SelectedScene)}";
        saveGlass.IsEnabled = cancelGlass.IsEnabled = available && glassDirty;
        SyncLumaControls(available, item);
        syncingGlass = false;
        UpdateEffectiveTransparency();
    }

    private void PreviewAppearanceChanged() => DispatcherQueue.TryEnqueue(UpdateEffectiveTransparency);

    private void UpdateEffectiveTransparency()
    {
        var effective = app.BarAppearance;
        if (!app.PreviewVisible || effective is null)
        {
            effectiveTransparency.Text = app.PreviewVisible ? "实际透明度 —" : "开启预览栏后显示实际透明度";
            return;
        }

        bool solid = accessibility.HighContrast || !colors.AdvancedEffectsEnabled || effective.Material is "normal" or "opaque";
        int value = solid ? 0 : 100 - (int)Math.Round((effective.Abgr >> 24) * 100d / 255);
        string label = !solid && effective.Material == "acrylic" && Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()
            ? "实际染色透明度" : "实际透明度";
        effectiveTransparency.Text = $"{label} {value}%" + (!solid && effective.ContrastProtected ? " · 保护中" : "");
    }

    // The stored value remains the actual radius, including values saved with the old range.
    internal static int BlurAtPosition(double position) => (int)Math.Round(position switch
    {
        <= 35 => position * 15 / 35,
        <= 60 => 15 + (position - 35),
        <= 80 => 40 + (position - 60) * 3,
        _ => 100 + (position - 80) * (TaskbarAppearance.MaximumBlurRadius - 100) / 20
    });

    internal static double PositionForBlur(int radius) => radius switch
    {
        <= 15 => radius * 35d / 15,
        <= 40 => 35 + (radius - 15),
        <= 100 => 60 + (radius - 40) / 3d,
        _ => 80 + (radius - 100) * 20d / (TaskbarAppearance.MaximumBlurRadius - 100)
    };
}

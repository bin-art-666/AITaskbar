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
    private readonly Slider transparency = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 136 };
    private readonly Slider blur = new() { Minimum = 0, Maximum = 100, StepFrequency = 1, Width = 136 };
    private readonly TextBlock transparencyValue = new() { MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock blurValue = new() { MinWidth = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock effectiveTransparency = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
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
        glassFields.Children.Add(sceneHint);
        glassFields.Children.Add(SettingRow("材质", "", materialChoice));
        glassFields.Children.Add(SettingRow("透明度", "数值越高，任务栏背景越通透。", Actions(transparency, transparencyValue)));
        glassFields.Children.Add(SettingRow("模糊强度", "选择模糊玻璃后可调节背景虚化。", Actions(blur, blurValue)));
        glassFields.Children.Add(effectiveTransparency);
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
        blur.ValueChanged += (_, _) => EditGlass(item => item.BlurRadius = (int)blur.Value);
        sceneChoice.SelectionChanged += (_, _) =>
        {
            if (syncingGlass) return;
            SyncGlassControls();
            if (glassDirty) _ = PublishGlassDraft();
        };
        SyncGlassControls();
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
            blur.Value = item.BlurRadius;
        }
        bool available = item is not null && !glassSaving;
        sceneChoice.IsEnabled = materialChoice.IsEnabled = available;
        transparency.IsEnabled = available && item!.Material is "clear" or "blur" or "acrylic";
        blur.IsEnabled = available && item!.Material == "blur";
        transparencyValue.Text = transparency.IsEnabled ? $"{transparency.Value:0}%" : "—";
        blurValue.Text = blur.IsEnabled ? $"{blur.Value:0}" : "—";
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
        effectiveTransparency.Text = glassDirty && transparency.IsEnabled && effective is not null
            ? $"预览栏当前透明度 {Math.Round(100 - (effective.Abgr >> 24) * 100d / 255)}%" +
                (effective.Material == "blur" ? $"，模糊强度 {effective.Blur}" : "")
            : "";
        effectiveTransparency.Visibility = effectiveTransparency.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}

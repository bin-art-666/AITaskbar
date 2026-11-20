namespace AIEnter;

public partial class App
{
    static UiAppearance? appearancePreview;
    static TaskbarAppearanceBridge? taskbarAppearance;
    internal static event Action? TaskbarAppearanceChanged;
    internal static UiAppearance Appearance => appearancePreview ?? Ai.Options.Data.Appearance ?? new();
    internal static void PreviewAppearance(UiAppearance? value)
    {
        value?.Validate(); appearancePreview = value;
        PanelTheme.ApplyAppearance(Appearance);
        PublishTaskbarAppearance();
    }
    static void PublishTaskbarAppearance()
    {
        taskbarAppearance?.Publish(Appearance, ai?.IsOpen == true);
        TaskbarAppearanceChanged?.Invoke();
    }
    void StartAppearanceBridge()
    {
        taskbarAppearance = new();
        PanelWindow.PresentationChanged += PublishTaskbarAppearance;
        Ai.Changed += PublishTaskbarAppearance;
        PublishTaskbarAppearance();
    }
    void StopAppearanceBridge()
    {
        PanelWindow.PresentationChanged -= PublishTaskbarAppearance;
        if (ai != null) ai.Changed -= PublishTaskbarAppearance;
        taskbarAppearance?.Publish(Appearance, false);
        taskbarAppearance?.Dispose(); taskbarAppearance = null;
    }
}

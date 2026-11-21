namespace AIEnter;

public partial class App
{
    static UiAppearance? appearancePreview;
    internal static event Action? TaskbarAppearanceChanged;
    internal static UiAppearance Appearance => appearancePreview ?? Ai.Options.Data.Appearance ?? new();
    internal static void PreviewAppearance(UiAppearance? value)
    {
        value?.Validate(); appearancePreview = value;
        PanelTheme.ApplyAppearance(Appearance);
        PublishTaskbarAppearance();
    }
    static void PublishTaskbarAppearance() => TaskbarAppearanceChanged?.Invoke();
    void StartAppearanceBridge()
    {
        PanelWindow.PresentationChanged += PublishTaskbarAppearance;
        Ai.Changed += PublishTaskbarAppearance;
        PublishTaskbarAppearance();
    }
    void StopAppearanceBridge()
    {
        PanelWindow.PresentationChanged -= PublishTaskbarAppearance;
        if (ai != null) ai.Changed -= PublishTaskbarAppearance;
    }
}

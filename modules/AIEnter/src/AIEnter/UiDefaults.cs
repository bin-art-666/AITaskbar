namespace AIEnter;

// Captured from saved UI preferences for new installs and restore-defaults. Only canonical public AI URLs; no query parameters, browser data or screen positions.
internal static class UiDefaults
{
    internal const int PanelEnterMilliseconds = 180;
    internal const int PanelExitMilliseconds = 130;
    internal const int PanelMotionOffset = 16;
    internal const int ButtonRadius = 16;
    internal const int ButtonSize = 30;
    internal const int WindowPadding = 4;
    internal const int FlowSpeed = 200;
    internal const int TaskbarIconSize = 25;
    internal const bool GlowEnabled = true;
    internal const int GlowRange = 6;
    internal const int GlowIntensity = 100;
    internal const int BackplateRadius = 20;
    internal const int BackplateTransparency = 20;
    internal const int BackplateTransparencyReference = 17;
    internal const int BackplateBlur = 0;
    internal const string ButtonColor = "#1C14DDFF";
    internal const string SelectedButtonColor = "#FF32D4EB";
    internal const string Provider = "deepseek";
    internal const bool Pinned = false;
    internal const bool FloatingPanel = true;
    internal const double DockWidth = 388.5;
    internal const double DockHeight = 816.5;
    internal const bool FollowGlobalCorners = false;
    internal const int WebCornerRadius = 28;
    internal const string Hotkey = "Alt+SPACE";
    internal static string[] ProviderOrder => ["deepseek", "chatgpt", "doubao", "xiaoyi", "ai-gemini", "ai-grok", "ai-kimi"];
    internal static AiProvider[] AddedProviders =>
    [
        new("ai-gemini", "Gemini", "https://gemini.google.com/app"),
        new("ai-grok", "Grok", "https://grok.com/"),
        new("ai-kimi", "Kimi", "https://www.kimi.com/en")
    ];
    internal static AiOptions CreateOptions() => new(Layouts: [], ProviderOrder: ProviderOrder,
        AddedProviders: AddedProviders, DefaultProvider: Provider);
}

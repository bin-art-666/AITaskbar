namespace Halo;

// Shared Halo control defaults. Saved station preferences live in HaloDefaults; no user content or positions.
internal static class UiDefaults
{
    internal const int PanelEnterMilliseconds = 180;
    internal const int PanelExitMilliseconds = 130;
    internal const int PanelMotionOffset = 16;
    internal const int ButtonRadius = 16;
    internal const int ButtonSize = 30;
    internal const int WindowPadding = 4;
    internal const int FlowSpeed = 200;
    internal const int TaskbarIconSize = HaloDefaults.RingSize;
    internal const bool GlowEnabled = true;
    internal const int GlowRange = 6;
    internal const int GlowIntensity = 100;
    internal const int BackplateRadius = HaloDefaults.Radius;
    internal const int BackplateTransparency = HaloDefaults.Transparency;
    internal const int BackplateTransparencyReference = 17;
    internal const int BackplateBlur = HaloDefaults.Blur;
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
}

namespace Halo;

internal sealed record UiAppearance(int ButtonRadius = UiDefaults.ButtonRadius, int ButtonSize = UiDefaults.ButtonSize, int WindowPadding = UiDefaults.WindowPadding,
    int TaskbarIconSize = UiDefaults.TaskbarIconSize, bool GlowEnabled = UiDefaults.GlowEnabled, int GlowRange = UiDefaults.GlowRange, int GlowIntensity = UiDefaults.GlowIntensity, int BackplateRadius = UiDefaults.BackplateRadius, string ButtonColor = UiDefaults.ButtonColor, string SelectedButtonColor = UiDefaults.SelectedButtonColor,
    int BackplateTransparency = UiDefaults.BackplateTransparency, int BackplateBlur = UiDefaults.BackplateBlur, int FlowSpeed = UiDefaults.FlowSpeed, bool ScreenshotDoubleClick = true, int ScreenshotSeconds = 20, string ScreenshotFolder = "", int RingThickness = HaloDefaults.RingThickness, string RingColor = HaloDefaults.RingColor, bool RingHoverFlow = HaloDefaults.RingHoverFlow, int RingFlowSpeed = HaloDefaults.RingFlowSpeed, string RingLightColor = HaloDefaults.RingLightColor, int RingFlowLength = HaloDefaults.RingFlowLength, bool ShowInteractionBorders = false, string FolderColor = HaloDefaults.FolderColor,
    string LightBackplateColor = "", string DarkBackplateColor = "")
{
    internal static bool ValidColor(string? value) => value != null && (value.Length == 0
        || (value.Length is 7 or 9 && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit)));
    internal static bool ValidRingColor(string? value) => value is { Length: 7 } && ValidColor(value);
    internal static bool ValidBackplateColor(string? value) => value == "" || ValidRingColor(value);
    internal static uint ColorValue(string value, uint fallback) => value.Length == 0 ? fallback
        : Convert.ToUInt32(value[1..], 16) | (value.Length == 7 ? 0xFF000000u : 0u);
    internal void Validate()
    {
        if (ButtonRadius is < 0 or > 20 || ButtonSize is < 28 or > 44 || WindowPadding is < 0 or > 16
            || BackplateRadius is < 12 or > 40 || BackplateTransparency is < 0 or > 80 || BackplateBlur is < 0 or > 100
            || ScreenshotSeconds is < 0 or > 120
            || FlowSpeed is < 0 or > 1800 || RingThickness is < 5 or > 35
            || TaskbarIconSize is < 16 or > 36 || GlowRange is < 0 or > 12 || GlowIntensity is < 0 or > 100)
            throw new InvalidDataException("外观参数超出范围。");
        if (ScreenshotFolder is null || ScreenshotFolder.Length > 240 || (ScreenshotFolder.Length > 0 && !Path.IsPathFullyQualified(ScreenshotFolder)))
            throw new InvalidDataException("截图目录需为完整的本地路径，留空使用图片目录。");
        if (!ValidRingColor(RingColor) || !ValidRingColor(RingLightColor) || RingFlowSpeed is < 50 or > 200 || RingFlowLength is < 10 or > 80)
            throw new InvalidDataException("圆环颜色需为 #RRGGBB，速度为 50–200，长度为 10–80%。");
        if (!ValidRingColor(FolderColor)) throw new InvalidDataException("文件夹颜色需为 #RRGGBB。");
        if (!ValidBackplateColor(LightBackplateColor) || !ValidBackplateColor(DarkBackplateColor))
            throw new InvalidDataException("窗口背板颜色需为 #RRGGBB，留空时跟随任务栏。");
        if (!ValidColor(ButtonColor) || !ValidColor(SelectedButtonColor))
            throw new InvalidDataException("按钮颜色需为 #RRGGBB 或 #AARRGGBB。");
    }
}

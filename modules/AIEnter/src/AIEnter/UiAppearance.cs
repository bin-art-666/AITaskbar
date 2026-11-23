namespace AIEnter;

internal sealed record UiAppearance(int ButtonRadius = UiDefaults.ButtonRadius, int ButtonSize = UiDefaults.ButtonSize, int WindowPadding = UiDefaults.WindowPadding,
    int TaskbarIconSize = UiDefaults.TaskbarIconSize, bool GlowEnabled = UiDefaults.GlowEnabled, int GlowRange = UiDefaults.GlowRange, int GlowIntensity = UiDefaults.GlowIntensity, int BackplateRadius = UiDefaults.BackplateRadius, string ButtonColor = UiDefaults.ButtonColor, string SelectedButtonColor = UiDefaults.SelectedButtonColor,
    int BackplateTransparency = UiDefaults.BackplateTransparency, int BackplateBlur = UiDefaults.BackplateBlur, int FlowSpeed = UiDefaults.FlowSpeed, bool ScreenshotDoubleClick = true, int ScreenshotSeconds = 20, string ScreenshotFolder = "",
    bool CustomBackplate = false, string LightBackplateColor = "", string DarkBackplateColor = "")
{
    internal string BackplateColorFor(bool dark) => CustomBackplate ? dark ? DarkBackplateColor : LightBackplateColor : "";
    internal static bool ValidColor(string? value) => value != null && (value.Length == 0
        || (value.Length is 7 or 9 && value[0] == '#' && value.AsSpan(1).ToString().All(Uri.IsHexDigit)));
    internal static uint ColorValue(string value, uint fallback) => value.Length == 0 ? fallback
        : Convert.ToUInt32(value[1..], 16) | (value.Length == 7 ? 0xFF000000u : 0u);
    internal void Validate()
    {
        if (ButtonRadius is < 0 or > 20 || ButtonSize is < 28 or > 44 || WindowPadding is < 0 or > 16
            || BackplateRadius is < 12 or > 40 || BackplateTransparency is < 0 or > 80 || BackplateBlur is < 0 or > 100
            || ScreenshotSeconds is < 0 or > 120
            || FlowSpeed is < 0 or > 1800
            || TaskbarIconSize is < 16 or > 36 || GlowRange is < 0 or > 12 || GlowIntensity is < 0 or > 100)
            throw new InvalidDataException("外观参数超出范围。");
        if (ScreenshotFolder is null || ScreenshotFolder.Length > 240 || (ScreenshotFolder.Length > 0 && !Path.IsPathFullyQualified(ScreenshotFolder)))
            throw new InvalidDataException("截图目录需为完整的本地路径，留空使用图片目录。");
        if (!ValidColor(ButtonColor) || !ValidColor(SelectedButtonColor))
            throw new InvalidDataException("按钮颜色需为 #RRGGBB 或 #AARRGGBB。");
        if (LightBackplateColor is not { Length: 0 or 7 } || !ValidColor(LightBackplateColor)
            || DarkBackplateColor is not { Length: 0 or 7 } || !ValidColor(DarkBackplateColor))
            throw new InvalidDataException("背板颜色需为 #RRGGBB，留空则沿用当前主题。");
    }
}

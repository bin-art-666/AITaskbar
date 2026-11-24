using System.Text.Json;

namespace AITaskbar;

internal sealed record AppCenterIconStyle
{
    internal const int SizeMin = 48, SizeMax = 84, RadiusMax = 50, GapMax = 30;
    internal const int LengthMin = 4, LengthMax = 100, LapMin = 5, LapMax = 40;

    public bool ShowInTaskbar { get; init; } = true;
    public bool AnimateOutline { get; init; }
    public int Size { get; init; } = 65;
    public int Radius { get; init; } = 15;
    public int Gap { get; init; } = 9;
    public int LineLength { get; init; } = 93;
    public int LapTenths { get; init; } = 40;

    internal AppCenterIconStyle Normalize() => this with
    {
        Size = Math.Clamp(Size, SizeMin, SizeMax),
        Radius = Math.Clamp(Radius, 0, RadiusMax),
        Gap = Math.Clamp(Gap, 0, GapMax),
        LineLength = Math.Clamp(LineLength, LengthMin, LengthMax),
        LapTenths = Math.Clamp(LapTenths, LapMin, LapMax)
    };
}

internal sealed record AppCenterPanelStyle
{
    internal const QuickBackdrop DefaultBackdrop = QuickBackdrop.Dark;

    public bool Spacious { get; init; }
    public QuickBackdrop Backdrop { get; init; } = DefaultBackdrop;
    public bool ManualOrder { get; init; }
}

internal sealed record AppCenterAppearance
{
    public AppCenterIconStyle Icon { get; init; } = new();
    public AppCenterPanelStyle Panel { get; init; } = new();

    internal static AppCenterAppearance Read(string root)
    {
        try
        {
            var value = JsonSerializer.Deserialize<AppCenterAppearance>(File.ReadAllBytes(FilePath(root)));
            if (value is not null)
            {
                var panel = value.Panel ?? new();
                return new()
                {
                    Icon = (value.Icon ?? new()).Normalize(),
                    Panel = panel.Backdrop is QuickBackdrop.Light or QuickBackdrop.Dark or QuickBackdrop.FollowTaskbar
                        ? panel : panel with { Backdrop = AppCenterPanelStyle.DefaultBackdrop }
                };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal void Save(string root)
    {
        Directory.CreateDirectory(root);
        string file = FilePath(root);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this));
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static string FilePath(string root) => Path.Combine(root, "app-center-appearance.json");
}

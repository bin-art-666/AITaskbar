using System.Text.Json;

namespace AITaskbar;

internal enum QuickBackdrop { FollowTaskbar, Light, Dark, Custom }

internal sealed record QuickControlAppearance(
    QuickBackdrop Backdrop = QuickBackdrop.FollowTaskbar, uint Tint = 0x555476, int Transparency = 22,
    uint Wifi = 0x2F80F7, uint Bluetooth = 0x2F80F7, uint Volume = 0x2F80F7)
{
    private const string Name = "quick-control-appearance.json";
    internal static IReadOnlyList<(QuickBackdrop Mode, string Label)> Modes { get; } =
        [(QuickBackdrop.FollowTaskbar, "跟随任务栏"), (QuickBackdrop.Light, "亮色"),
         (QuickBackdrop.Dark, "暗色"), (QuickBackdrop.Custom, "自定义")];

    internal static QuickControlAppearance Read(string root)
    {
        try
        {
            if (JsonSerializer.Deserialize<QuickControlAppearance>(File.ReadAllBytes(Path.Combine(root, Name))) is { } value)
                return value with { Backdrop = Enum.IsDefined(value.Backdrop) ? value.Backdrop : QuickBackdrop.FollowTaskbar,
                    Transparency = Math.Clamp(value.Transparency, 0, 65) };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return new();
    }

    internal void Save(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, Name);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

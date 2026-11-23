using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIEnter;

internal sealed record AiEndpoint(string Name, string Url)
{
    internal string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Url)))[..24];
}
internal sealed record AiBounds(int X, int Y, int Width, int Height);
internal sealed record AiExpandedLayout(double Width, double Height, string? Display = null, double X = 0, double Y = 0);
[Flags]
internal enum AiPanelEdge
{
    Left = 1, Top = 2, Right = 4, Bottom = 8,
    TopLeft = Top | Left, TopRight = Top | Right, BottomLeft = Bottom | Left, BottomRight = Bottom | Right
}
internal sealed record AiOptions(int Version = 2, string Provider = UiDefaults.Provider, string CustomName = "自定义 AI", string CustomUrl = "", bool Pinned = UiDefaults.Pinned,
    Dictionary<string, AiBounds>? Layouts = null, double DockWidth = UiDefaults.DockWidth, double DockHeight = UiDefaults.DockHeight, double DockTopOffset = 0,
    bool FollowGlobalCorners = UiDefaults.FollowGlobalCorners, int WebCornerRadius = UiDefaults.WebCornerRadius, bool FloatingPanel = UiDefaults.FloatingPanel, UiAppearance? Appearance = null, string[]? ProviderOrder = null, AiProvider[]? AddedProviders = null, string? DefaultProvider = null, string Hotkey = UiDefaults.Hotkey,
    AiExpandedLayout? ExpandedLayout = null);
internal static partial class AiPolicy
{
    internal static double CornerRadius(AiOptions options,double global,double width,double height)
        =>Math.Max(0,Math.Min(options.FollowGlobalCorners?global:options.WebCornerRadius,Math.Min(width,height)/2));
    internal static readonly string[] Providers = ["deepseek", "doubao", "xiaoyi", "chatgpt", "custom"];
    internal static readonly string[] Names = ["DeepSeek", "豆包", "小艺", "ChatGPT", "自定义 AI"];
    internal static string[] OrderedProviders(AiOptions value) => (value.ProviderOrder ?? UiDefaults.ProviderOrder)
        .Concat(UiDefaults.ProviderOrder).Concat(AvailableProviders(value)).Distinct(StringComparer.Ordinal).Where(AvailableProviders(value).Contains).ToArray();
    internal static IEnumerable<string> BuiltInProviders => Providers.Where(id => id != "custom");
    internal const double DefaultDockWidth = UiDefaults.DockWidth, MinimumDockWidth = 360, MaximumDockWidth = 1000;
    internal const double DefaultDockHeight = UiDefaults.DockHeight, MinimumDockHeight = 360, MaximumDockHeight = 1200;
    internal static int DockWidthPixels(double preferred, int workWidth, uint dpi)
        => Math.Max(1, Math.Min(workWidth, (int)Math.Round(Math.Clamp(preferred, MinimumDockWidth, MaximumDockWidth) * Math.Max(96, dpi) / 96d)));
    internal static AiBounds DockBounds(double width, double height, AiBounds work, uint dpi)
    {
        double scale = Math.Max(96, dpi) / 96d;
        int gap = Math.Max(0, Math.Min((int)Math.Round(12 * scale), (Math.Min(work.Width, work.Height) - 1) / 2));
        int actualWidth = Math.Min(DockWidthPixels(width, work.Width, dpi), Math.Max(1, work.Width - 2 * gap));
        int actualHeight = Math.Min((int)Math.Round(Math.Clamp(height, MinimumDockHeight, MaximumDockHeight) * scale), Math.Max(1, work.Height - 2 * gap));
        return new(work.X + work.Width - gap - actualWidth, work.Y + work.Height - gap - actualHeight, actualWidth, actualHeight);
    }
    internal static AiBounds ResizeDockBounds(AiBounds start, AiBounds work, AiPanelEdge edge, int delta, uint dpi)
    {
        double scale = Math.Max(96, dpi) / 96d;
        int right = start.X + start.Width, bottom = start.Y + start.Height;
        if (edge == AiPanelEdge.Left)
        {
            int maximum = Math.Max(1, Math.Min((int)Math.Round(MaximumDockWidth * scale), right - work.X));
            int minimum = Math.Min(maximum, (int)Math.Round(MinimumDockWidth * scale));
            int width = (int)Math.Clamp((long)start.Width - delta, minimum, maximum);
            return start with { X = right - width, Width = width };
        }
        int available = edge == AiPanelEdge.Top ? bottom - work.Y : work.Y + work.Height - start.Y;
        int maxHeight = Math.Max(1, Math.Min((int)Math.Round(MaximumDockHeight * scale), available));
        int minHeight = Math.Min(maxHeight, (int)Math.Round(MinimumDockHeight * scale));
        int newHeight = (int)Math.Clamp((long)start.Height + (edge == AiPanelEdge.Top ? -(long)delta : delta), minHeight, maxHeight);
        return start with { Y = edge == AiPanelEdge.Top ? bottom - newHeight : start.Y, Height = newHeight };
    }
    internal static AiBounds ResizeExpandedBounds(AiBounds start, AiBounds work, AiPanelEdge edge, long dx, long dy, double scale)
    {
        static (int Position, int Length) ResizeAxis(int position, int length, int lower, int upper, int minimum,
            bool fromStart, bool fromEnd, long delta)
        {
            if (!fromStart && !fromEnd) return (position, length);
            int opposite = position + length;
            int available = fromStart ? opposite - lower : upper - position;
            int resized = (int)Math.Clamp(length + (fromStart ? -delta : delta), Math.Min(minimum, available), available);
            return (fromStart ? opposite - resized : position, resized);
        }

        var x = ResizeAxis(start.X, start.Width, work.X, work.X + work.Width, (int)Math.Round(420 * scale),
            edge.HasFlag(AiPanelEdge.Left), edge.HasFlag(AiPanelEdge.Right), dx);
        var y = ResizeAxis(start.Y, start.Height, work.Y, work.Y + work.Height, (int)Math.Round(360 * scale),
            edge.HasFlag(AiPanelEdge.Top), edge.HasFlag(AiPanelEdge.Bottom), dy);
        return new(x.Position, y.Position, x.Length, y.Length);
    }
    internal static AiEndpoint Resolve(AiOptions value) => value.Provider switch
    {
        "deepseek" => new("DeepSeek", "https://chat.deepseek.com/"),
        "doubao" => new("豆包", "https://www.doubao.com/chat/"),
        "xiaoyi" => new("小艺", "https://xiaoyi.huawei.com/"),
        "chatgpt" => new("ChatGPT", "https://chatgpt.com/"),
        "custom" => new(value.CustomName.Trim(), Https(value.CustomUrl)),
        _ => value.AddedProviders?.FirstOrDefault(p => p.Id == value.Provider) is { } added
            ? new(added.Name.Trim(), Https(added.Url)) : throw new InvalidDataException("请选择支持的 AI 服务。")
    };
    internal static string Https(string text)
    {
        if (text.Length > 2048 || text.Any(char.IsControl) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.UserInfo.Length > 0)
            throw new InvalidDataException("请输入完整的 HTTPS 网页地址，不支持带账号密码的地址。");
        return uri.AbsoluteUri;
    }
    internal static void Validate(AiOptions value)
    {
        if (value.Version != 2) throw new NotSupportedException("AI 设置来自更新版本，已停止写入。");
        if (!double.IsFinite(value.DockWidth) || value.DockWidth is < MinimumDockWidth or > MaximumDockWidth
            || value.WebCornerRadius is < 0 or > 40
            || !double.IsFinite(value.DockHeight) || value.DockHeight is < MinimumDockHeight or > MaximumDockHeight
            || !double.IsFinite(value.DockTopOffset) || value.DockTopOffset is < 0 or > 20000
            || value.CustomName is null || string.IsNullOrWhiteSpace(value.CustomName) || value.CustomName.Length > 40 || value.CustomName.Any(char.IsControl)
            || value.CustomUrl is null || value.CustomUrl.Length > 2048 || value.CustomUrl.Any(char.IsControl)
            || value.Hotkey is null || !HotkeyPolicy.TryNormalize(value.Hotkey, out _)
            || value.Layouts is { Count: > 32 } || value.Layouts?.Any(p => p.Key.Length != 24 || !p.Key.All(Uri.IsHexDigit) || p.Value is null || p.Value.Width is < 100 or > 20000 || p.Value.Height is < 100 or > 20000 || Math.Abs((long)p.Value.X) > 100000 || Math.Abs((long)p.Value.Y) > 100000) == true)
            throw new InvalidDataException("AI 设置无效，请检查名称、网址和窗口位置。");
        if (value.ExpandedLayout is { } expanded && (!double.IsFinite(expanded.Width) || expanded.Width is < 1 or > 20000
            || !double.IsFinite(expanded.Height) || expanded.Height is < 1 or > 20000
            || expanded.Display is { Length: > 128 } || !double.IsFinite(expanded.X) || Math.Abs(expanded.X) > 20000
            || !double.IsFinite(expanded.Y) || Math.Abs(expanded.Y) > 20000))
            throw new InvalidDataException("AI 展开面板的尺寸或位置无效。");
        if (value.ProviderOrder is { } order && (order.Length > AvailableProviders(value).Count()
            || order.Any(id => id == null || !AvailableProviders(value).Contains(id)) || order.Distinct(StringComparer.Ordinal).Count() != order.Length))
            throw new InvalidDataException("AI 按钮顺序包含重复或未知服务。");
        ValidateAddedProviders(value);
        value.Appearance?.Validate();
        _ = Resolve(value);
        if (value.DefaultProvider != null) _ = Resolve(value with { Provider = value.DefaultProvider });
        if (value.CustomUrl.Length > 0) _ = Https(value.CustomUrl);
    }
    internal static AiBounds Fit(AiBounds? saved, AiBounds work, uint dpi)
    {
        int width = Math.Min(work.Width, saved?.Width ?? (int)(900 * dpi / 96d));
        int height = Math.Min(work.Height, saved?.Height ?? (int)(760 * dpi / 96d));
        width = Math.Max(1, width); height = Math.Max(1, height);
        return new(Math.Clamp(saved?.X ?? work.X + (work.Width - width) / 2, work.X, work.X + work.Width - width),
            Math.Clamp(saved?.Y ?? work.Y + (work.Height - height) / 2, work.Y, work.Y + work.Height - height), width, height);
    }
    internal static bool AllowedNavigation(string address) => address.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
        || Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
        || address.StartsWith("blob:https://", StringComparison.OrdinalIgnoreCase);
}
internal sealed class AiOptionsStore
{
    readonly string path;
    internal AiOptions Data { get; private set; } = UiDefaults.CreateOptions();
    internal bool ReadOnly { get; }
    internal string? Warning { get; }
    bool preserveBackup;
    internal event Action? Changed;
    internal AiOptionsStore(string path)
    {
        this.path = path;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return;
        try { Data = Read(path); }
        catch (NotSupportedException ex) { ReadOnly = true; Warning = ex.Message; }
        catch
        {
            try { Data = Read(path + ".bak"); preserveBackup = true; Warning = "已从备份恢复 AI 设置。"; }
            catch { ReadOnly = true; Warning = "AI 设置无法读取，原文件已保留，暂用默认服务。"; }
        }
    }
    static AiOptions Read(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new InvalidDataException();
        var value = JsonSerializer.Deserialize<AiOptions>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        // Missing fields receive defaults; an explicit empty list keeps user removals.
        // Legacy custom settings migrate independently, preserving their original entry.
        if (value.Version == 2 && value.AddedProviders == null && value.CustomUrl.Length == 0 && value.Provider != "custom")
            value = value with { AddedProviders = UiDefaults.AddedProviders };
        value = AiPolicy.Upgrade(value); AiPolicy.Validate(value); return value with { Layouts = value.Layouts ?? [] };
    }
    internal void Save(AiOptions value)
    {
        // Missing fields receive defaults; an explicit empty list keeps user removals.
        // Legacy custom settings migrate independently, preserving their original entry.
        if (value.Version == 2 && value.AddedProviders == null && value.CustomUrl.Length == 0 && value.Provider != "custom")
            value = value with { AddedProviders = UiDefaults.AddedProviders };
        value = AiPolicy.Upgrade(value); AiPolicy.Validate(value); if (ReadOnly) throw new IOException(Warning);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, preserveBackup ? null : path + ".bak"); else File.Move(temporary, path);
            Data = value with { Layouts = value.Layouts is null ? [] : new(value.Layouts), ProviderOrder = value.ProviderOrder?.ToArray(), AddedProviders = value.AddedProviders?.ToArray() }; preserveBackup = false; Changed?.Invoke();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void SaveBounds(string key, AiBounds bounds)
    {
        if (ReadOnly || Data.Layouts?.GetValueOrDefault(key) == bounds) return;
        var layouts = new Dictionary<string, AiBounds>(Data.Layouts ?? []);
        if (!layouts.ContainsKey(key) && layouts.Count >= 32) layouts.Remove(layouts.Keys.First());
        layouts[key] = bounds; Save(Data with { Layouts = layouts });
    }
}

using System.Text.Json;

namespace Halo;

internal sealed record HaloSettings(int Version = 1, double Width = HaloDefaults.Width, double Height = HaloDefaults.Height,
    int IconSize = HaloDefaults.IconSize, bool NewestFirst = HaloDefaults.NewestFirst, bool Pinned = HaloDefaults.Pinned, bool Detached = HaloDefaults.Detached,
    int Transparency = HaloDefaults.Transparency, int Blur = HaloDefaults.Blur, int Radius = HaloDefaults.Radius,
    double ExpandedWidth = HaloDefaults.ExpandedWidth, double ExpandedHeight = HaloDefaults.ExpandedHeight, int? ExpandedX = null, int? ExpandedY = null,
    int RingSize = HaloDefaults.RingSize, int RingThickness = HaloDefaults.RingThickness, string RingColor = HaloDefaults.RingColor, bool RingHoverFlow = HaloDefaults.RingHoverFlow, int RingFlowSpeed = HaloDefaults.RingFlowSpeed, string RingLightColor = HaloDefaults.RingLightColor, int RingFlowLength = HaloDefaults.RingFlowLength, bool DetachedHideOnBlur = HaloDefaults.DetachedHideOnBlur,
    int HorizontalSpacing = HaloDefaults.HorizontalSpacing, int VerticalSpacing = HaloDefaults.VerticalSpacing,
    int NormalNotificationSeconds = HaloDefaults.NormalNotificationSeconds, int ErrorNotificationSeconds = HaloDefaults.ErrorNotificationSeconds,
    string ButtonColor = UiDefaults.ButtonColor,
    DragPreviewMode DragPreviewMode = DragPreviewMode.GrabPoint, int DragPreviewDistance = DragPreviewPlacement.DefaultDistance,
    bool ShowInteractionBorders = false, int? CompactX = null, int? CompactY = null, string FolderColor = HaloDefaults.FolderColor,
    FolderMenuAction FolderMenuPrimary = FolderMenuAction.Default, FolderMenuLayout? FolderMenu = null, FileMenuLayout? FileMenu = null,
    string LightBackplateColor = "", string DarkBackplateColor = "")
{
    internal FolderMenuLayout EffectiveFolderMenu => FolderMenu?.WithMove() ?? FolderMenuLayout.FromLegacy(FolderMenuPrimary);
    internal FileMenuLayout EffectiveFileMenu => FileMenu?.WithMove() ?? FileMenuLayout.Default;

    internal void Validate()
    {
        if (Version != 1) throw new NotSupportedException("设置来自更新版本。");
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width is < 280 or > 1600 || Height is < 200 or > 1400
            || !double.IsFinite(ExpandedWidth) || !double.IsFinite(ExpandedHeight) || ExpandedWidth is < 280 or > 1600 || ExpandedHeight is < 200 or > 1400
            || new[] { CompactX, CompactY, ExpandedX, ExpandedY }.Any(value => value is < -1000000 or > 1000000)
            || IconSize is < 32 or > 96 || HorizontalSpacing is < 4 or > 32 || VerticalSpacing is < 4 or > 32
            || !Enum.IsDefined(DragPreviewMode) || DragPreviewDistance is < 0 or > DragPreviewPlacement.MaximumDistance
            || NormalNotificationSeconds is < 1 or > 30 || ErrorNotificationSeconds is < 1 or > 30
            || Transparency is < 0 or > 80 || Blur is < 0 or > 100 || Radius is < 12 or > 40
            || RingSize is < 16 or > 36 || RingThickness is < 5 or > 35
            || (FolderMenuPrimary & ~FolderMenuAction.All) != 0)
            throw new InvalidDataException("设置参数无效。");
        Appearance.Validate();
        EffectiveFolderMenu.Validate();
        EffectiveFileMenu.Validate();
    }
    internal UiAppearance Appearance => new(ButtonColor: ButtonColor, BackplateTransparency: Transparency, BackplateBlur: Blur, BackplateRadius: Radius, TaskbarIconSize: RingSize, RingThickness: RingThickness, RingColor: RingColor, RingHoverFlow: RingHoverFlow, RingFlowSpeed: RingFlowSpeed, RingLightColor: RingLightColor, RingFlowLength: RingFlowLength, ShowInteractionBorders: ShowInteractionBorders, FolderColor: FolderColor,
        LightBackplateColor: LightBackplateColor, DarkBackplateColor: DarkBackplateColor);
}
internal sealed class SettingsStore
{
    readonly string path;
    bool preserveBackup;
    internal HaloSettings Data { get; private set; } = new();
    internal bool ReadOnly { get; }
    internal string? Warning { get; }
    readonly bool existingProfile;
    internal SettingsStore(string path)
    {
        this.path = path;
        existingProfile = File.Exists(path) || File.Exists(path + ".bak");
        if (!existingProfile) return;
        try { Data = Read(path); }
        catch (NotSupportedException) { ReadOnly = true; Warning = "设置版本较新，已停止写入。"; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            try { Data = Read(path + ".bak"); preserveBackup = true; Warning = "已从备份恢复设置。"; }
            catch { ReadOnly = true; Warning = "设置损坏，已保留原文件并停止写入。"; }
        }
    }
    internal void UseIntegratedWindowDefaults()
    {
        if (!existingProfile && revision == 0) Data = Data with { DetachedHideOnBlur = false };
    }
    static HaloSettings Read(string path)
    {
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("设置过大。");
        var data = JsonSerializer.Deserialize<HaloSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException();
        data.Validate(); return data;
    }
    readonly object writeGate = new();
    int revision, persistedRevision;
    Task pending = Task.CompletedTask;
    internal event Action<Exception>? SaveFailed;
    void Write(HaloSettings data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, data); stream.Flush(true); }
        if (File.Exists(path)) File.Replace(path + ".tmp", path, preserveBackup ? null : path + ".bak");
        else File.Move(path + ".tmp", path);
        preserveBackup = false;
    }
    internal void Save(HaloSettings data)
    {
        if (ReadOnly) throw new IOException(Warning);
        data.Validate(); if (data == Data && revision == Volatile.Read(ref persistedRevision) && File.Exists(path)) return;
        int current = Interlocked.Increment(ref revision);
        lock (writeGate) { Write(data); Volatile.Write(ref persistedRevision, current); }
        Data = data;
    }
    internal void SaveSoon(HaloSettings data)
    {
        if (ReadOnly) throw new IOException(Warning); data.Validate();
        if (data == Data && revision == Volatile.Read(ref persistedRevision) && File.Exists(path)) return;
        Data = data; int current = Interlocked.Increment(ref revision);
        pending = Task.Run(async () => {
            await Task.Delay(180);
            try { lock (writeGate) { if (current != Volatile.Read(ref revision)) return; Write(data); Volatile.Write(ref persistedRevision, current); } }
            catch (Exception error) { SaveFailed?.Invoke(error); throw; }
        });
        _ = pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
    internal Task Flush() => revision == Volatile.Read(ref persistedRevision) ? Task.CompletedTask : pending;
}

using System.Text.Json;

namespace Halo;

public partial class App
{
    async Task VerifyDefaultProfile(Action<string, bool> check)
    {
        string root = Path.Combine(DataDirectory, "default-profile"); Directory.CreateDirectory(root);
        var fresh = new SettingsStore(Path.Combine(root, "fresh.json")); fresh.Data.Validate();
        check("FreshProfileUsesCapturedWindowSizes", fresh.Data is { Width: 606.5, Height: 608, ExpandedWidth: 801.5, ExpandedHeight: 642.5 });
        check("FreshProfileUsesCapturedGridAndBackdrop", fresh.Data is { IconSize: 36, HorizontalSpacing: 12, VerticalSpacing: 8, Transparency: 75, Blur: 2, Radius: 20 });
        check("FreshProfileUsesCapturedRing", fresh.Data is { RingSize: 30, RingThickness: 17, RingColor: "#E6E6E6", RingLightColor: "#505050", RingHoverFlow: true, RingFlowLength: 80, RingFlowSpeed: 100 });
        check("FreshProfileUsesCapturedWindowBehavior", fresh.Data is { Detached: false, Pinned: false, DetachedHideOnBlur: true, NewestFirst: true });
        check("DefaultProfileOmitsMonitorPositions", fresh.Data.ExpandedX == null && fresh.Data.ExpandedY == null);
        check("AppearanceDefaultsMatchStationProfile", new UiAppearance() == fresh.Data.Appearance);
        var empty = new StationStore(Path.Combine(root, "station.json"));
        check("FreshDesktopUsesManualSortWithoutPersonalData", empty.Sort(true) == StationSort.Manual && empty.Items.Count == 0 && empty.Trash.Count == 0 && !empty.HasLayout(false) && !empty.HasLayout(true) && empty.ArchiveDirectory.Length == 0 && empty.Capacity == 200);
        fresh.Save(fresh.Data);
        check("CapturedDefaultsPersistAndReload", new SettingsStore(Path.Combine(root, "fresh.json")).Data == fresh.Data);
        string legacy = Path.Combine(root, "legacy-settings.json");
        await File.WriteAllTextAsync(legacy, "{\"Version\":1,\"Width\":420,\"IconSize\":64,\"Transparency\":20,\"Blur\":0,\"RingSize\":22,\"RingThickness\":11,\"RingColor\":\"#112233\",\"RingFlowLength\":25,\"DetachedHideOnBlur\":false}");
        var saved = new SettingsStore(legacy).Data;
        check("CapturedDefaultsNeverOverwriteSavedPreferences", saved is { Width: 420, IconSize: 64, Transparency: 20, Blur: 0, RingSize: 22, RingThickness: 11, RingColor: "#112233", RingFlowLength: 25, DetachedHideOnBlur: false });
        check("MissingLegacyFieldsUseCurrentProfile", saved.Height == HaloDefaults.Height && saved.ExpandedX == null && saved.RingLightColor == HaloDefaults.RingLightColor);
    }
}

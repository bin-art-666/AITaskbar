using AIEnter;

string root = Path.GetFullPath(Path.Combine(".build", "ai-checks-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root); int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
void Reject(Action action)
{ bool rejected = false; try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException) { rejected = true; } Check(rejected, "Invalid mutation accepted"); }
try
{
    string path = Path.Combine(root, "ai.json"); var store = new AiOptionsStore(path);
    Check(store.Data.Provider == "deepseek" && !store.Data.Pinned && !File.Exists(path), "Defaults do not write or preload browser");
    Check(store.Data.AddedProviders!.Select(p => p.Name).SequenceEqual(new[] { "Gemini", "Grok", "Kimi" })
        && AiPolicy.AvailableProviders(store.Data).Count() == 7 && store.Data.DefaultProvider == "deepseek", "Fresh profiles include all seven captured AI entries");
    Check(AiPolicy.OrderedProviders(store.Data).SequenceEqual(UiDefaults.ProviderOrder), "Fresh profiles use the captured seven-provider order");
    Check(HotkeyPolicy.TryNormalize("alt + control + space", out var normalizedHotkey) && normalizedHotkey == "Ctrl+Alt+SPACE", "Hotkey aliases normalize and include safe modifiers");
    Check(HotkeyPolicy.TryNormalize("", out _) && HotkeyPolicy.TryNormalize("Ctrl+Shift+F24", out _), "Hotkey can be disabled and supports function keys");
    foreach (string invalid in new[] { "A", "Shift+A", "Ctrl+Ctrl+A", "Ctrl+NoSuchKey", "Ctrl+Alt+" })
        Check(!HotkeyPolicy.TryNormalize(invalid, out _), "Unsafe or malformed shortcut rejected: " + invalid);
    Reject(() => store.Save(store.Data with { Hotkey = "Shift+A" }));
    store.Save(store.Data with { Hotkey = "Ctrl+Alt+SPACE" });
    Check(new AiOptionsStore(path).Data.Hotkey == "Ctrl+Alt+SPACE", "Global shortcut preference persists");
    var independentDefaults = new AiOptionsStore(Path.Combine(root, "independent.json"));
    Check(!ReferenceEquals(independentDefaults.Data.AddedProviders, store.Data.AddedProviders)
        && !ReferenceEquals(independentDefaults.Data.ProviderOrder, store.Data.ProviderOrder), "Profiles do not share mutable default arrays");
    string removedDefaults = Path.Combine(root, "removed-defaults.json");
    File.WriteAllText(removedDefaults, "{\"Version\":2,\"AddedProviders\":[]}");
    var removedStore = new AiOptionsStore(removedDefaults);
    removedStore.Save(removedStore.Data);
    Check(new AiOptionsStore(removedDefaults).Data.AddedProviders!.Length == 0, "Deleted default AI stays removed after save and restart");
    Check(!store.Data.FollowGlobalCorners&&AiPolicy.CornerRadius(store.Data,21,430,660)==28,"Fresh settings use the user-approved web corner defaults");
    string legacyCorners=Path.Combine(root,"legacy-corners.json");File.WriteAllText(legacyCorners,"{\"Provider\":\"deepseek\"}");
    Check(new AiOptionsStore(legacyCorners).Data.AddedProviders!.Length == 3 && File.ReadAllText(legacyCorners) == "{\"Provider\":\"deepseek\"}", "Missing service defaults load without rewriting existing files");
    Check(!new AiOptionsStore(legacyCorners).Data.FollowGlobalCorners,"Missing corner fields use the user-approved default");
    var customCorners=store.Data with{FollowGlobalCorners=false,WebCornerRadius=40};
    Check(AiPolicy.CornerRadius(customCorners,12,430,660)==40&&AiPolicy.CornerRadius(customCorners,12,30,20)==10,"Custom corners are independent and clamped to small viewports");
    store.Save(customCorners);Check(new AiOptionsStore(path).Data.WebCornerRadius==40&&!new AiOptionsStore(path).Data.FollowGlobalCorners,"Custom corners persist");
    Reject(()=>store.Save(customCorners with{WebCornerRadius=41}));Reject(()=>store.Save(customCorners with{WebCornerRadius=-1}));
    store.Save(store.Data with{FollowGlobalCorners=true});
    Check(store.Data.DockWidth == 388.5, "Older/missing settings use the captured dock width");
    Check(store.Data.DockHeight == 816.5 && store.Data.DockTopOffset == 0, "Legacy configurations retain default panel dimensions");
    var dock = AiPolicy.DockBounds(430, 660, new(0, 24, 1920, 1200), 144);
    Check(dock == new AiBounds(1257, 216, 645, 990), "Panel anchors to bottom-right with a 12 DIP gap at 150 percent scale");
    dock = AiPolicy.DockBounds(430, 1200, new(0, 24, 700, 500), 96);
    Check(dock == new AiBounds(258, 36, 430, 476), "Oversized panel fits a short work area and avoids taskbar space");
    dock = AiPolicy.DockBounds(430, 660, new(-400, 24, 300, 200), 192);
    Check(dock == new AiBounds(-376, 48, 252, 152), "Tiny monitor keeps the full panel visible with scaled gaps");
    dock = AiPolicy.DockBounds(430, 660, new(-1920, -1080, 1920, 1040), 96);
    Check(dock.X + dock.Width == -12 && dock.Y + dock.Height == -52, "Negative monitor coordinates still use the monitor bottom-right");
    Check(AiPolicy.DockBounds(430, 660, new(5, 7, 1, 1), 192) == new AiBounds(5, 7, 1, 1), "Minimal work area never creates negative dimensions");
    var resizeStart = new AiBounds(1000, 500, 430, 660);
    var resizeWork = new AiBounds(0, 0, 1920, 1200);
    Check(AiPolicy.ResizeDockBounds(resizeStart, resizeWork, AiPanelEdge.Left, -100, 96) == new AiBounds(900, 500, 530, 660), "Left resize preserves the opposite edge");
    Check(AiPolicy.ResizeDockBounds(resizeStart, resizeWork, AiPanelEdge.Top, -100, 96) == new AiBounds(1000, 400, 430, 760), "Top resize preserves the bottom edge");
    Check(AiPolicy.ResizeDockBounds(resizeStart, resizeWork, AiPanelEdge.Bottom, -100, 96) == new AiBounds(1000, 500, 430, 560), "Bottom resize preserves the top edge");
    var limited = AiPolicy.ResizeDockBounds(resizeStart, resizeWork, AiPanelEdge.Bottom, int.MaxValue, 96);
    Check(limited.Y == 500 && limited.Height == 700, "Bottom resize remains inside the work area");
    Check(AiPolicy.DockWidthPixels(530, 2000, 144) == 795, "Preferred width scales in logical pixels");
    Check(AiPolicy.DockWidthPixels(1000, 700, 192) == 700, "Narrow monitor caps actual width without modifying preference");
    Check(AiPolicy.DockWidthPixels(1, 2000, 96) == 360 && AiPolicy.DockWidthPixels(9999, 2000, 96) == 1000, "Dragging clamps to supported width range");
    Check(AiPolicy.Resolve(store.Data).Url == "https://chat.deepseek.com/", "Official DeepSeek URL");
    Check(AiPolicy.Resolve(new(Provider: "doubao")).Url == "https://www.doubao.com/chat/", "Official Doubao URL");
    Check(AiPolicy.Resolve(new(Provider: "xiaoyi")).Url == "https://xiaoyi.huawei.com/", "Official Xiaoyi URL");
    Check(AiPolicy.Resolve(new(Provider: "chatgpt")).Url == "https://chatgpt.com/", "Official ChatGPT URL");
    Check(AiPolicy.BuiltInProviders.Contains("chatgpt") && !AiPolicy.BuiltInProviders.Contains("custom") && AiPolicy.Providers[^1] == "custom", "ChatGPT is built-in while custom remains distinct");
    string chatPath = Path.Combine(root, "chatgpt.json"); var chatStore = new AiOptionsStore(chatPath);
    chatStore.Save(new(Provider: "chatgpt")); Check(new AiOptionsStore(chatPath).Data.Provider == "chatgpt", "ChatGPT selection survives restart");
    var custom = store.Data with { Provider = "custom", CustomName = "我的 AI", CustomUrl = "https://example.com/chat?q=hello%20world", Pinned = true };
    foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 359d, 1001d }) Reject(() => store.Save(custom with { DockWidth = invalid }));
    foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 359d, 1201d }) Reject(() => store.Save(custom with { DockHeight = invalid }));
    foreach (double invalid in new[] { double.NaN, -1d, 20001d }) Reject(() => store.Save(custom with { DockTopOffset = invalid }));
    store.Save(custom); var reload = new AiOptionsStore(path);
    Check(reload.Data.Provider == "ai-legacy" && reload.Data.Pinned && AiPolicy.Resolve(reload.Data).Name == "我的 AI", "Custom provider and pin survive restart");
    Check(AiPolicy.Resolve(custom).Key == AiPolicy.Resolve(custom with { CustomName = "别名" }).Key, "Renaming does not lose website profile or existing window");
    Check(AiPolicy.Resolve(custom).Key != AiPolicy.Resolve(custom with { CustomUrl = "https://example.com/other" }).Key, "Changed custom URL identifies a distinct window");
    foreach (string url in new[] { "http://example.com", "javascript:alert(1)", "file:///C:/test", "https://user:password@example.com/", "https://example.com/\n--flag", "", "--app=https://example.com" })
        Reject(() => store.Save(custom with { CustomUrl = url }));
    Reject(() => store.Save(custom with { Provider = "missing" }));
    Reject(() => store.Save(custom with { CustomName = "" }));
    Check(AiPolicy.AllowedNavigation("https://example.com/auth"), "HTTPS login redirects remain in panel");
    Check(AiPolicy.AllowedNavigation("about:blank"), "Empty popup can initialize before navigation");
    Check(AiPolicy.AllowedNavigation("blob:https://example.com/document"), "Secure in-memory documents supported");
    Check(!AiPolicy.AllowedNavigation("file:///C:/private.txt"), "Website cannot navigate to local files");
    Check(!AiPolicy.AllowedNavigation("microsoft-edge:https://example.com"), "Website cannot spawn external browser through protocol navigation");
    var fitted = AiPolicy.Fit(new(99999, -99999, 2000, 1500), new(-1280, 0, 1280, 720), 96);
    Check(fitted == new AiBounds(-1280, 0, 1280, 720), "Disconnected monitor geometry clamps to available display");
    fitted = AiPolicy.Fit(null, new(0, 24, 1920, 1000), 96);
    Check(fitted.Width == 900 && fitted.Height == 760 && fitted.Y >= 24, "New window centered below appbar work area");
    string key = AiPolicy.Resolve(custom).Key; var bounds = new AiBounds(100, 100, 900, 700);
    store.SaveBounds(key, bounds); store.Save(store.Data with { Provider = "doubao" });
    store.Save(store.Data with { DockWidth = 620 });
    store.Save(store.Data with { DockHeight = 580, DockTopOffset = 30 });
    Check(new AiOptionsStore(path).Data.DockHeight == 580 && new AiOptionsStore(path).Data.DockTopOffset == 30, "Legacy offset remains readable while dimensions survive restart");
    Check(new AiOptionsStore(path).Data.DockWidth == 620 && store.Data.Layouts![key] == bounds, "Dock width persists independently of floating geometry");
    store.SaveBounds(key, bounds with { Width = 940 }); store.Save(store.Data with { Provider = "xiaoyi" });
    Check(store.Data.DockWidth == 620, "Floating resize and service switching retain dock width");
    store.SaveBounds(key, bounds);
    Check(new AiOptionsStore(path).Data.Layouts![key] == bounds, "Service switching preserves window geometry");
    var before = File.ReadAllText(path);
    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) Reject(() => store.Save(store.Data with { Pinned = false }));
    Check(File.ReadAllText(path) == before && store.Data.Pinned && !File.Exists(path + ".tmp"), "Failed save preserves live and disk settings");
    File.WriteAllText(path, "{broken"); var recovered = new AiOptionsStore(path);
    Check(!recovered.ReadOnly && recovered.Warning != null, "Corrupt primary can recover from valid backup");
    string future = Path.Combine(root, "future.json"); File.WriteAllText(future, "{\"Version\":3}");
    var newer = new AiOptionsStore(future); Check(newer.ReadOnly, "Unknown versions remain read-only"); Reject(() => newer.Save(new()));
    Check(File.ReadAllText(future) == "{\"Version\":3}", "Future record preserved");
    string modePath = Path.Combine(root, "window-mode.json");
    File.WriteAllText(modePath, "{\"Provider\":\"deepseek\"}");
    var modes = new AiOptionsStore(modePath);
    Check(modes.Data.FloatingPanel, "Existing configurations without a mode restore floating panels");
    modes.Save(modes.Data with { FloatingPanel = false });
    Check(!new AiOptionsStore(modePath).Data.FloatingPanel, "Independent window mode survives restart");
    modes.SaveBounds(key, bounds);
    Check(!modes.Data.FloatingPanel && modes.Data.Layouts![key] == bounds, "Saving window geometry preserves independent mode");
    modes.Save(modes.Data with { FloatingPanel = true, DockWidth = 530 });
    var restoredMode = new AiOptionsStore(modePath).Data;
    Check(restoredMode.FloatingPanel && restoredMode.DockWidth == 530 && restoredMode.Layouts![key] == bounds,
        "Restoring floating mode preserves both panel and independent window sizes");
    var appearancePath = Path.Combine(root, "appearance.json");
    File.WriteAllText(appearancePath, "{\"Provider\":\"deepseek\"}");
    var appearanceStore = new AiOptionsStore(appearancePath);
    Check(appearanceStore.Data.Appearance == null && new UiAppearance().ButtonSize == 30, "Legacy data receives compact appearance defaults without rewriting");
    var maximumAppearance = new UiAppearance(20, 44, 16, 36, true, 12, 100, 40);
    appearanceStore.Save(appearanceStore.Data with { Appearance = maximumAppearance });
    Check(new AiOptionsStore(appearancePath).Data.Appearance == maximumAppearance, "Appearance settings survive reload");
    Check(new UiAppearance().BackplateRadius == 20, "Missing backplate setting uses the approved default");
    foreach (var invalid in new[] { maximumAppearance with { BackplateRadius = 11 }, maximumAppearance with { BackplateRadius = 41 }, maximumAppearance with { ButtonRadius = 21 }, maximumAppearance with { ButtonSize = 27 },
        maximumAppearance with { ButtonSize = 45 }, maximumAppearance with { WindowPadding = 17 }, maximumAppearance with { TaskbarIconSize = 15 },
        maximumAppearance with { TaskbarIconSize = 37 }, maximumAppearance with { GlowRange = 13 }, maximumAppearance with { GlowIntensity = 101 } })
        Reject(() => appearanceStore.Save(appearanceStore.Data with { Appearance = invalid }));

    Check(new UiAppearance() == new UiAppearance(16, 30, 4, 25, true, 6, 100, 20, "#1C14DDFF", "#FF32D4EB"), "Reset appearance matches the captured user settings");
    Check(new AiOptions().DockWidth == 388.5 && new AiOptions().DockHeight == 816.5
        && !new AiOptions().FollowGlobalCorners && new AiOptions().WebCornerRadius == 28,
        "Fresh panel dimensions and web corners match the captured defaults");
    string explicitCorners = Path.Combine(root, "explicit-corners.json");
    File.WriteAllText(explicitCorners, "{\"FollowGlobalCorners\":true,\"WebCornerRadius\":7,\"DockWidth\":520,\"DockHeight\":700}");
    var explicitOptions = new AiOptionsStore(explicitCorners).Data;
    Check(explicitOptions.FollowGlobalCorners && explicitOptions.WebCornerRadius == 7 && explicitOptions.DockWidth == 520
        && explicitOptions.DockHeight == 700, "Existing explicit choices remain unchanged by new defaults");

    Check(AiPolicy.OrderedProviders(new()).SequenceEqual(new[] { "deepseek", "chatgpt", "doubao", "xiaoyi" }), "Missing provider order uses captured preferences");
    var reordered = appearanceStore.Data with { ProviderOrder = ["chatgpt", "doubao", "xiaoyi", "deepseek"] };
    appearanceStore.Save(reordered);
    Check(AiPolicy.OrderedProviders(new AiOptionsStore(appearancePath).Data).SequenceEqual(reordered.ProviderOrder!.Concat(UiDefaults.AddedProviders.Select(p => p.Id))), "AI button order survives reload");
    Check(AiPolicy.OrderedProviders(new(ProviderOrder: ["chatgpt"])).SequenceEqual(new[] { "chatgpt", "deepseek", "doubao", "xiaoyi" }), "Missing providers append in default order");
    Reject(() => appearanceStore.Save(reordered with { ProviderOrder = ["chatgpt", "chatgpt"] }));
    Reject(() => appearanceStore.Save(reordered with { ProviderOrder = ["unknown"] }));
    Check(appearanceStore.Data.Provider == reordered.Provider, "Reordering does not change selected AI");
    var colorAppearance = new UiAppearance(ButtonColor: "#80445566", SelectedButtonColor: "#AA2255");
    appearanceStore.Save(appearanceStore.Data with { Appearance = colorAppearance });
    Check(new AiOptionsStore(appearancePath).Data.Appearance == colorAppearance, "Button colors and alpha survive reload");
    Check(new UiAppearance().ButtonColor == "#1C14DDFF" && new UiAppearance().SelectedButtonColor == "#FF32D4EB", "Missing colors use captured preferences");
    Check(UiAppearance.ColorValue("#112233", 0) == 0xFF112233 && UiAppearance.ColorValue("#80112233", 0) == 0x80112233, "Color channels preserve opacity");
    foreach(var invalidColor in new string?[] { null, "red", "#GG0022", "#12345", "#123456789" })
        Reject(() => appearanceStore.Save(appearanceStore.Data with { Appearance = colorAppearance with { ButtonColor = invalidColor! } }));
    var explicitTheme = appearanceStore.Data with { Appearance = new UiAppearance(ButtonColor: "", SelectedButtonColor: ""), ProviderOrder = ["xiaoyi", "chatgpt"], Provider = "xiaoyi", Pinned = true, FloatingPanel = false };
    appearanceStore.Save(explicitTheme);
    var loadedTheme = new AiOptionsStore(appearancePath).Data;
    Check(loadedTheme.Appearance!.ButtonColor == "" && loadedTheme.Appearance.SelectedButtonColor == "", "Explicit system colors remain automatic after default update");
    Check(loadedTheme.Provider == "xiaoyi" && loadedTheme.Pinned && !loadedTheme.FloatingPanel && loadedTheme.ProviderOrder!.SequenceEqual(explicitTheme.ProviderOrder!), "Explicit service, mode and order survive changed defaults");
    var many = Enumerable.Range(0, 150).Select(i => new AiProvider("ai-" + i, "AI " + i, "https://example.com/" + i)).ToArray();
    var multiple = AiPolicy.WithProviderSettings(new(), many, many.Select(p => p.Id).Reverse().ToArray(), many[1].Id);
    appearanceStore.Save(multiple);
    var manyReload = new AiOptionsStore(appearancePath).Data;
    Check(manyReload.AddedProviders!.Length == 150 && manyReload.DefaultProvider == many[1].Id, "More than one hundred AI entries persist without count limits");
    Check(AiPolicy.OrderedProviders(manyReload).Take(150).SequenceEqual(many.Reverse().Select(p => p.Id)), "Added AI shares dynamic toolbar order");
    var selectedMany = manyReload with { Provider = many[0].Id };
    var removedCurrent = AiPolicy.WithProviderSettings(selectedMany, many[1..], [], many[1].Id);
    Check(removedCurrent.Provider == many[1].Id, "Removing current AI selects the chosen default");
    var removedDefault = AiPolicy.WithProviderSettings(removedCurrent, many[2..], [], many[1].Id);
    Check(removedDefault.Provider == "deepseek" && removedDefault.DefaultProvider == "deepseek", "Removing default AI falls back to DeepSeek");
    var oldFile = Path.Combine(root, "legacy-custom.json");
    File.WriteAllText(oldFile, "{\"Version\":1,\"Provider\":\"custom\",\"CustomName\":\"Legacy\",\"CustomUrl\":\"https://example.com/legacy\",\"ProviderOrder\":[\"custom\",\"deepseek\"]}");
    var migrated = new AiOptionsStore(oldFile);
    Check(!migrated.ReadOnly && migrated.Data.Version == 2 && migrated.Data.Provider == "ai-legacy" && migrated.Data.AddedProviders!.Length == 1, "Legacy custom AI migrates to a stable list entry");
    Check(migrated.Data.DefaultProvider == "ai-legacy" && AiPolicy.OrderedProviders(migrated.Data)[0] == "ai-legacy", "Migration keeps current default and order");
    Check(AiPolicy.Resolve(migrated.Data).Key == AiPolicy.Resolve(new(Provider: "custom", CustomName: "Legacy", CustomUrl: "https://example.com/legacy")).Key, "Migration retains browser profile key");
    migrated.Save(migrated.Data);
    Check(new AiOptionsStore(oldFile).Data.AddedProviders!.Length == 1, "Migration is idempotent");
    Reject(() => appearanceStore.Save(multiple with { AddedProviders = [many[0], many[0]] }));
    Reject(() => appearanceStore.Save(multiple with { AddedProviders = [many[0] with { Url = "http://example.com" }] }));
    Reject(() => appearanceStore.Save(multiple with { AddedProviders = [many[0] with { Name = "" }] }));
    Reject(() => appearanceStore.Save(multiple with { AddedProviders = [many[0] with { Id = "deepseek" }] }));
    Console.WriteLine($"PASS: {checks} AI configuration, navigation and geometry checks");
}
finally { Directory.Delete(root, true); }

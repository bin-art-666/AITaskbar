using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Halo;

public partial class App
{
    async Task VerifyStation()
    {
        var checks = new Dictionary<string, bool>(); string? failure = null;
        try {
            if (File.Exists(Path.Combine(DataDirectory, "station.json"))) throw new IOException("验证必须使用全新的隔离数据目录。");
            Directory.CreateDirectory(DataDirectory);
            using var progress = new StreamWriter(Path.Combine(DataDirectory, "station-checks.log")) { AutoFlush = true };
            void Check(string name, bool pass)
            {
                checks[name] = pass;
                progress.WriteLine($"{name}: {pass}");
                if (!pass) throw new Exception(name);
            }
            await VerifyDefaultProfile(Check);
            string fixture = Path.Combine(DataDirectory, "用于验证单击选中文件时在原图标下换行显示完整名称和扩展名的测试文件.txt");
            await File.WriteAllTextAsync(fixture, "Halo 验证，不使用真实用户文件。");
            string folder = Path.Combine(DataDirectory, "测试文件夹"); Directory.CreateDirectory(folder);
            var package = new DataPackage(); package.SetStorageItems(new IStorageItem[] { await StorageFile.GetFileFromPathAsync(fixture), await StorageFolder.GetFolderFromPathAsync(folder) }, true);
            using (var import = await StationTransfer.Read(package.GetView(), Station.AttachmentDirectory)) {
                Check("NativeStorageInput", import.Inputs.Length == 2 && import.Inputs[1].Kind == StationKind.Folder);
                await Station.Add(import.Inputs); import.Commit();
            }
            var browser = new DataPackage(); browser.SetWebLink(new Uri("https://example.com/halo")); browser.SetText("https://example.com/halo");
            using (var import = await StationTransfer.Read(browser.GetView(), Station.AttachmentDirectory)) { await Station.Add(import.Inputs); import.Commit(); }
            await Station.Add([StationStore.Text("测试文字")]);
            Check("Deduplication", (await Station.Add([StationStore.Text("测试文字")])).Duplicates == 1 && Station.Items.Count == 4);
            string bitmapFile = Path.Combine(DataDirectory, "测试图片.png");
            await File.WriteAllBytesAsync(bitmapFile, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl9sAAAAASUVORK5CYII="));
            var bitmapPackage = new DataPackage(); bitmapPackage.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(bitmapFile)));
            using (var bitmapImport = await StationTransfer.Read(bitmapPackage.GetView(), Station.AttachmentDirectory)) {
                Check("NativeBitmapInput", bitmapImport.Inputs.Length == 1 && File.Exists(bitmapImport.Inputs[0].Value));
                // Disposing an uncommitted import must roll back its generated file.
                string generated = bitmapImport.Inputs[0].Value; bitmapImport.Dispose(); Check("BitmapFailureRollback", !File.Exists(generated));
            }
            var mixed = new DataPackage(); await StationTransfer.FillMany(mixed, Station.Items.ToArray(), Station.AttachmentDirectory);
            Check("NativeMixedOutput", (await mixed.GetView().GetStorageItemsAsync()).Count == 4 && mixed.RequestedOperation == DataPackageOperation.Copy);
            Check("InternalDragIdentity", StationTransfer.IsInternal(mixed.GetView()) && (await StationTransfer.InternalItems(mixed.GetView(), Station))!.Select(i => i.Id).SequenceEqual(Station.Items.Select(i => i.Id)));
            var dragSources = Station.Items.Where(i => i.Kind is StationKind.File or StationKind.Folder).ToArray();
            var dragFiles = StationTransfer.PrepareDragFiles(dragSources);
            var dragPackage = new DataPackage();
            await StationTransfer.FillMany(dragPackage, dragSources, Station.AttachmentDirectory, exportNoteFiles: true, preparedFiles: dragFiles);
            Check("PreparedFileDragPreservesCopyAndOrder", dragPackage.RequestedOperation == DataPackageOperation.Copy
                && (await dragPackage.GetView().GetStorageItemsAsync()).Select(i => i.Path).SequenceEqual(dragSources.Select(i => i.Value))
                && (await StationTransfer.InternalItems(dragPackage.GetView(), Station))!.Select(i => i.Id).SequenceEqual(dragSources.Select(i => i.Id)));
            await VerifyOrdering(Check); await VerifyGridLayout(Check); await VerifyDesktopStorage(Check); await VerifyStorageMigration(Check);
            await VerifyFileGrouping(Check);
            await VerifyFolderColorLifecycle(Check);
            int before = Station.Items.Count; await Station.UpdateOptions(before, "", true);
            try { await Station.Add([StationStore.Text("超过容量的内容")]); } catch (InvalidDataException) { }
            Check("CapacityBatchAtomic", Station.Items.Count == before); await Station.UpdateOptions(200, "", true);
            var removed = Station.Items.Take(2).ToArray(); await Station.RemoveArchived(removed); await Station.Restore(removed);
            Check("UndoAndSourceIntact", Station.Items.Count == before && File.Exists(fixture) && Directory.Exists(folder));
            string incoming = Path.Combine(DataDirectory, "incoming-drops"); Directory.CreateDirectory(incoming);
            string id = Guid.NewGuid().ToString(); string batch = Path.Combine(incoming, id); Directory.CreateDirectory(batch);
            string owned = Path.Combine(batch, "虚拟附件.txt"); await File.WriteAllTextAsync(owned, "附件");
            string packetPath = Path.Combine(incoming, id + ".json");
            await File.WriteAllTextAsync(packetPath, JsonSerializer.Serialize(new TaskbarPacket(Files: [owned], OwnedFiles: [owned])));
            string committedAttachment = Path.Combine(Station.AttachmentDirectory, "taskbar-" + id, Path.GetFileName(owned));
            Directory.CreateDirectory(Path.GetDirectoryName(committedAttachment)!);
            await File.WriteAllTextAsync(committedAttachment + ".tmp", "模拟中断的复制");
            await ImportPacket(packetPath); int imported = Station.Items.Count;
            Check("TaskbarPartialCopyRecovery", await File.ReadAllTextAsync(committedAttachment) == "附件" && !File.Exists(committedAttachment + ".tmp"));
            // Replaying a queued packet must not duplicate a managed attachment.
            await ImportPacket(packetPath); Check("TaskbarQueueIdempotent", Station.Items.Count == imported);
            File.Delete(packetPath);
            Panel = new StationWindow();
            using (Panel.Retain()) {
                Panel.Open(); await Task.Delay(400);
                Panel.VerifyResize(); Check("CompactResizeGrips", true);
                var first = (Microsoft.UI.Xaml.Controls.GridViewItem)Panel.Files.Items[0];
                var firstPoint = first.TransformToVisual(Panel.Files).TransformPoint(new(0, 0));
                Check("GridInsertionBeforeFirst", Panel.InsertionTarget(new(firstPoint.X + 1, firstPoint.Y + first.ActualHeight / 2), false) == ((StationItem)first.Tag).Id);
                Check("GridInsertionAtEnd", Panel.InsertionTarget(new(Panel.Files.ActualWidth - 1, Panel.Files.ActualHeight - 1), false) == null);
                Panel.VerifyCardSelection(); Check("RepeatedCardCtrlShiftAndSingleSelection", true); Check("BlankClickClearsSelection", true);
                Panel.VerifySelectionDetails(Check);
                await Panel.VerifyFolderIcon(Check);
                await Panel.VerifyDragPreview(Check);
                Check("NativeGridAndBackdrop", Panel.Files.Items.Count == Station.Items.Count && Panel.HasBackdrop);
            }
            var display = Native.Monitors().First(m => m.Primary); var work = display.Info.Work; double scale = display.Dpi / 96d;
            Check("BottomLeftPlacement", Math.Abs(Panel.AppWindow.Position.X - work.Left - 12 * scale) <= 2 && Math.Abs(work.Bottom - Panel.AppWindow.Position.Y - Panel.AppWindow.Size.Height - 12 * scale) <= 2);
            var ids = Station.Items.Select(x => x.Id).Order().ToArray();
            Panel.Hide(); await Task.Delay(200); Panel.Open();
            Check("ReopenRetainsItems", Panel.IsOpen && ids.SequenceEqual(Station.Items.Select(x => x.Id).Order()));
            var selectedContainer = (Microsoft.UI.Xaml.Controls.GridViewItem)Panel.Files.Items[0]; Panel.Files.SelectedItems.Add(selectedContainer);
            var compactSize = Panel.AppWindow.Size;
            Check("WindowControls", Panel.HasWindowButtons);
            Panel.SetDetached(true); await Task.Delay(200); Panel.VerifyResize();
            var expandedWork = display.Info.Work;
            Check("StandaloneCenteredInitially", Math.Abs(Panel.AppWindow.Position.X - expandedWork.Left - (expandedWork.Right - expandedWork.Left - Panel.AppWindow.Size.Width) / 2) <= 2
                && Math.Abs(Panel.AppWindow.Position.Y - expandedWork.Top - (expandedWork.Bottom - expandedWork.Top - Panel.AppWindow.Size.Height) / 2) <= 2);
            Check("StandaloneWindowAndNotTopmost", Panel.AppWindow.IsShownInSwitchers && !((Microsoft.UI.Windowing.OverlappedPresenter)Panel.AppWindow.Presenter).IsAlwaysOnTop);
            bool autoHide = Settings.Data.DetachedHideOnBlur;
            Settings.SaveSoon(Settings.Data with { DetachedHideOnBlur = false });
            Panel.Open();
            var other = new Microsoft.UI.Xaml.Window { Title = "Halo 隔离焦点检查", Content = new Microsoft.UI.Xaml.Controls.TextBlock { Text = "独立窗口焦点验证" } };
            other.AppWindow.IsShownInSwitchers = false; other.Activate(); await Task.Delay(180);
            Check("StandaloneStaysOpenOnOutsideFocus", Panel.IsOpen);
            Panel.Open(); Settings.SaveSoon(Settings.Data with { DetachedHideOnBlur = autoHide });
            if (autoHide) { other.Activate(); await Task.Delay(180); Check("StandaloneUsesDefaultOutsideDismissPreference", !Panel.IsOpen); }
            other.Close(); Panel.Open();
            await VerifyFloatingCaptionForStation(); Check("NativeStandaloneCaption", true);
            bool geometryPinned = App.Settings.Data.Pinned;
            if (!geometryPinned) Panel.TogglePin();
            Check("ExpandedResizeGrips", true);
            var expandedSize = Panel.AppWindow.Size;
            Panel.AppWindow.Resize(new(expandedSize.Width - 40, expandedSize.Height - 40)); await Task.Delay(550);
            var resizedExpanded = Panel.AppWindow.Size;
            Panel.SetDetached(false); await Task.Delay(180);
            Check("CompactSizeRestored", Panel.AppWindow.Size.Width == compactSize.Width && Panel.AppWindow.Size.Height == compactSize.Height);
            Panel.AppWindow.Resize(new(compactSize.Width + 32, compactSize.Height + 24)); await Task.Delay(550);
            var resizedCompact = Panel.AppWindow.Size;
            Panel.SetDetached(true); await Task.Delay(180);
            Check("ExpandedSizeRemembered", Panel.AppWindow.Size.Width == resizedExpanded.Width && Panel.AppWindow.Size.Height == resizedExpanded.Height);
            Panel.SetDetached(false); await Task.Delay(180);
            Check("CompactSizeRemembered", Panel.AppWindow.Size.Width == resizedCompact.Width && Panel.AppWindow.Size.Height == resizedCompact.Height);
            var compactBounds = new Windows.Graphics.RectInt32(work.Left, work.Bottom - resizedCompact.Height,
                resizedCompact.Width, resizedCompact.Height);
            Panel.AppWindow.MoveAndResize(compactBounds);
            Panel.Open();
            Check("AlreadyOpenCompactDoesNotResetBounds", MatchesWindowBounds(compactBounds));
            Panel.Hide(); Panel.Open();
            Check("CompactQuickReopenKeepsBoundsAtWorkAreaEdge", MatchesWindowBounds(compactBounds));
            Panel.SetDetached(true); await Task.Delay(180);
            var expandedBounds = new Windows.Graphics.RectInt32(work.Right - resizedExpanded.Width, work.Top,
                resizedExpanded.Width, resizedExpanded.Height);
            Panel.AppWindow.MoveAndResize(expandedBounds);
            Panel.Hide(); Panel.Open();
            Check("ExpandedQuickReopenKeepsBoundsAtWorkAreaEdge", MatchesWindowBounds(expandedBounds));
            Panel.SetDetached(false); await Task.Delay(180);
            await Settings.Flush();
            var geometry = new SettingsStore(Path.Combine(DataDirectory, "settings.json")).Data;
            Check("WindowModePositionsAndSizesPersistIndependently", MatchesWindowBounds(compactBounds)
                && Native.Same(StationWindow.RestoreBounds(geometry, false, work, scale), compactBounds)
                && Native.Same(StationWindow.RestoreBounds(geometry, true, work, scale), expandedBounds));
            var secondary = new Native.Rect { Left = -1600, Top = -120, Right = 0, Bottom = 820 };
            var highDpi = geometry with { CompactX = -1500, CompactY = -120, Width = 601 / 1.5, Height = 419 / 1.5 };
            Check("WindowBoundsRoundTripAtFractionalDpiAndNegativeCoordinates",
                Native.Same(StationWindow.RestoreBounds(highDpi, false, secondary, 1.5), new(-1500, -120, 601, 419)));
            bool MatchesWindowBounds(Windows.Graphics.RectInt32 bounds) => Panel!.AppWindow.Position.X == bounds.X
                && Panel.AppWindow.Position.Y == bounds.Y && Panel.AppWindow.Size.Width == bounds.Width && Panel.AppWindow.Size.Height == bounds.Height;
            if (!geometryPinned) Panel.TogglePin();
            Check("ModeKeepsSelectionAndGrid", ReferenceEquals(Panel.Files.Items[0], selectedContainer) && Panel.Files.SelectedItems.Contains(selectedContainer));
            Preview(Settings.Data.Appearance with { BackplateBlur = 65, BackplateTransparency = 45, BackplateRadius = 38 });
            Check("WholeWindowBackdropPreview", Panel.DesktopBlur == 65 && Panel.FullBackdrop && Appearance.BackplateTransparency == 45);
            Panel.VerifyWindowRegion(); Check("BackdropNativeCornersFollowPreviewRadius", true);
            Preview(null); Check("BackdropPreviewReverted", Panel.DesktopBlur == Settings.Data.Blur && Appearance.BackplateTransparency == Settings.Data.Transparency);
            Panel.VerifyWindowRegion(); Check("BackdropNativeCornersRestoreSavedRadius", true);
            var savedRing = (Settings.Data.RingSize, Settings.Data.RingThickness);
            Preview(Settings.Data.Appearance with { TaskbarIconSize = 36, RingThickness = 35, RingColor = "#3478AF", RingHoverFlow = false, RingFlowSpeed = 180, RingLightColor = "#123456", RingFlowLength = 20 });
            Check("RingThemePreviewTransport", RingBridge!.ReadThemeForVerification() == (0xFF123456u, 20) && Settings.Data.RingLightColor == HaloDefaults.RingLightColor && Settings.Data.RingFlowLength == HaloDefaults.RingFlowLength);
            Check("RingStylePreviewTransport", RingBridge!.ReadStyleForVerification() == (0xFF3478AFu, false, 180) && Settings.Data.RingColor == HaloDefaults.RingColor);
            Check("RingPreviewTransport", RingBridge!.ReadForVerification() == (36, 35) && (Settings.Data.RingSize, Settings.Data.RingThickness) == savedRing);
            Preview(null); Check("RingThemePreviewCancel", RingBridge!.ReadThemeForVerification() == (UiAppearance.ColorValue(Settings.Data.RingLightColor, 0), Settings.Data.RingFlowLength)); Check("RingCancelReverts", RingBridge!.ReadForVerification() == savedRing);
            Check("RingStyleCancelReverts", RingBridge.ReadStyleForVerification() == (UiAppearance.ColorValue(Settings.Data.RingColor, 0), Settings.Data.RingHoverFlow, Settings.Data.RingFlowSpeed));
            string ringPath = Path.Combine(DataDirectory, "ring-settings.json");
            await File.WriteAllTextAsync(ringPath, "{\"Version\":1}"); var ringSettings = new SettingsStore(ringPath);
            string legacyTheme = Path.Combine(DataDirectory, "legacy-theme-settings.json");
            await File.WriteAllTextAsync(legacyTheme, "{\"Version\":1,\"RingColor\":\"#C1A2B3\",\"RingSize\":30,\"RingThickness\":17,\"RingFlowSpeed\":130}");
            var migrated = new SettingsStore(legacyTheme).Data;
            Check("LegacyThemeColorPreserved", migrated.RingColor == "#C1A2B3" && migrated.RingSize == 30 && migrated.RingThickness == 17 && migrated.RingFlowSpeed == 130 && migrated.RingLightColor == "#505050" && migrated.RingFlowLength == HaloDefaults.RingFlowLength);
            Check("LegacyRingStyleDefaults", ringSettings.Data.RingColor == HaloDefaults.RingColor && ringSettings.Data.RingHoverFlow && ringSettings.Data.RingFlowSpeed == 100);
            Check("LegacyRingDefaults", ringSettings.Data.RingSize == HaloDefaults.RingSize && ringSettings.Data.RingThickness == HaloDefaults.RingThickness);
            ringSettings.Save(ringSettings.Data with { RingSize = 16, RingThickness = 5 });
            ringSettings.Save(ringSettings.Data with { RingSize = 36, RingThickness = 35, RingColor = "#AF3478", RingHoverFlow = false, RingFlowSpeed = 200, RingLightColor = "#5050AF", RingFlowLength = 10 });
            var ringReload = new SettingsStore(ringPath);
            Check("RingThemeSettingsPersist", ringReload.Data.RingLightColor == "#5050AF" && ringReload.Data.RingFlowLength == 10);
            Check("RingStylePersists", ringReload.Data.RingColor == "#AF3478" && !ringReload.Data.RingHoverFlow && ringReload.Data.RingFlowSpeed == 200);
            foreach (string badColor in new[] { "", "#12345Z", "#00112233" }) {
                bool rejected = false; try { (ringSettings.Data with { RingColor = badColor }).Validate(); } catch (InvalidDataException) { rejected = true; }
                Check("RingColorRejects" + badColor, rejected);
            }
            bool badSpeed = false; try { (ringSettings.Data with { RingFlowSpeed = 0 }).Validate(); } catch (InvalidDataException) { badSpeed = true; }
            Check("RingSpeedRangeProtection", badSpeed);
            foreach (int length in new[] { 9, 81 }) {
                bool rejected = false; try { (ringSettings.Data with { RingFlowLength = length }).Validate(); } catch (InvalidDataException) { rejected = true; }
                Check("RingLengthRejects" + length, rejected);
            }
            bool badLight = false; try { (ringSettings.Data with { RingLightColor = "#FFFFFF00" }).Validate(); } catch (InvalidDataException) { badLight = true; }
            Check("RingLightColorRejectsAlpha", badLight);
            Check("RingSettingsPersist", ringReload.Data.RingSize == 36 && ringReload.Data.RingThickness == 35);
            bool invalidRing = false;
            try { (ringSettings.Data with { RingSize = 15 }).Validate(); } catch (InvalidDataException) { invalidRing = true; }
            Check("RingRangeProtection", invalidRing);
            Panel.SetDetached(true); await Task.Delay(40); Panel.TogglePin();
            Check("WindowModeKeepsData", Panel.AppWindow.IsShownInSwitchers && Station.Items.Count == imported);
            Panel.SetDetached(false); await Task.Delay(40); Panel.TogglePin();
            await Settings.Flush();
            var reloadedSettings = new SettingsStore(Path.Combine(DataDirectory, "settings.json"));
            Check("SeparateSizesPersisted", reloadedSettings.Data.Width == Settings.Data.Width && reloadedSettings.Data.ExpandedWidth == Settings.Data.ExpandedWidth && reloadedSettings.Data.ExpandedHeight != reloadedSettings.Data.Height);
            Check("RestartPersistence", new StationStore(Path.Combine(DataDirectory, "station.json")).Items.Count == Station.Items.Count);
            string recovery = Path.Combine(DataDirectory, "recovery.json"); var recover = new StationStore(recovery);
            await recover.Add([StationStore.Text("备份记录")]); await recover.Add([StationStore.Text("最新记录")]); await File.WriteAllTextAsync(recovery, "broken");
            var restored = new StationStore(recovery); Check("BackupRecovery", !restored.ReadOnly && restored.Items.Count == 1);
            await File.WriteAllTextAsync(recovery, "{\"Version\":999,\"Items\":[]}");
            Check("UnknownVersionReadOnly", new StationStore(recovery).ReadOnly);
            var grid = (StationGridPanel)Panel.Files.ItemsPanelRoot; int columns = grid.Columns;
            var movedId = ((StationItem)((Microsoft.UI.Xaml.Controls.GridViewItem)Panel.Files.Items[0]).Tag).Id;
            await Station.MoveGrid([movedId], movedId, 0, Station.Items.Count + 1, false, columns, true); await Task.Delay(250);
            var movedContainer = Panel.Files.Items.Cast<Microsoft.UI.Xaml.Controls.GridViewItem>().Single(i => ((StationItem)i.Tag).Id == movedId);
            grid = (StationGridPanel)Panel.Files.ItemsPanelRoot;
            var placed = movedContainer.TransformToVisual(grid).TransformPoint(new(0, 0));
            Check("NativeGridPreservesEmptyRows", Math.Abs(placed.Y - (Station.Items.Count + 1) * grid.CellHeight - movedContainer.Margin.Top) < 3);
            var beforeResize = Panel.AppWindow.Size; Panel.AppWindow.Resize(new(Math.Max(560, beforeResize.Width - 80), beforeResize.Height)); await Task.Delay(150);
            Check("NativeGridResizeKeepsColumns", ((StationGridPanel)Panel.Files.ItemsPanelRoot).Columns == columns);
            Panel.SetDetached(true); await Task.Delay(200); Panel.SetDetached(false); await Task.Delay(200);
            Check("NativeGridModeRestoresCells", Station.Layout(false, columns, true).Cells.Single(c => c.Id == movedId).Row == Station.Items.Count + 1);
            await Panel.VerifyMarqueeEdgeScroll(Check);
            var wideSize = Panel.AppWindow.Size; Panel.AppWindow.Resize(new(wideSize.Width + (int)(grid.CellWidth * scale * 2), wideSize.Height)); await Task.Delay(200);
            grid = (StationGridPanel)Panel.Files.ItemsPanelRoot;
            Check("NativeWidenAddsGridColumns", grid.Columns > columns);
            var gridOrigin = grid.TransformToVisual(Panel.Files).TransformPoint(new(0, 0));
            var newTarget = Panel.GridTarget(new(gridOrigin.X + (columns + .5) * grid.CellWidth, gridOrigin.Y + grid.CellHeight / 2), false);
            Check("NativeWidenHitTargetsNewColumn", newTarget == (columns, 0));
            await Station.MoveGrid([movedId], movedId, newTarget.Column, newTarget.Row, false, grid.Columns, true); await Task.Delay(200);
            var placedWide = Station.Layout(false, 1, true).Cells.Single(c => c.Id == movedId);
            Check("NativeDropInAddedColumnPersists", placedWide.Column == columns && placedWide.Row == 0 && new StationStore(Path.Combine(DataDirectory, "station.json")).Layout(false, 1, true).Cells.Single(c => c.Id == movedId) == placedWide);
            await Panel.VerifyRepeatedSwitches(); Check("FiftyModeSwitchesWithButtonInput", true);
            Check("BlankSelectionExcludesItemsButtonsAndGrips", true);
            Panel.VerifyDesktopSearch(); Check("NativeDesktopSearchAndClear", true);
            Panel.VerifyGridShortcuts(Check);
            await Task.Delay(180);
            Panel.VerifyMarqueeSelection(Check);
            await StationStore.VerifyLibraryMoves(Path.Combine(DataDirectory, "library-move-verification"), Check);
            await StationStore.VerifyLibraryCopies(Path.Combine(DataDirectory, "library-copy-verification"), Check);
            await VerifyGridAppearance(Check);
            await VerifySettingsSort(Check);
            await Panel.VerifySettingsWindow(Check);
            Panel.VerifyContextMenus(Check);
            await Panel.VerifyNotifications(Check);
            await VerifyNotificationSettings(Check);
            await Panel.VerifyDropQueue(Check);
            await VerifyContentTypes(Check);
            await Station.ImportCopies([new(StationKind.File, fixture), new(StationKind.File, bitmapFile)]);
            Panel.Open();
            await Task.Delay(150);
            await Panel.VerifyFolderDropIntent(Check);
            await Panel.VerifyMainLibraryDrop(Check);
            await Panel.VerifyFolderBrowser(Check);
            await Panel.VerifyTrashSettings(Check);
            await Panel.VerifyStationDialogs(Check);
            await Panel.VerifySearchAndLibraryAvailability(Path.Combine(DataDirectory, "station.json"), Check);
            await Panel.VerifyImageTextSearch(Check);
            Panel.VerifyImportFeedback(Check);
            await Settings.Flush(); Check("DeferredWindowSettingsPersist", new SettingsStore(Path.Combine(DataDirectory, "settings.json")).Data == Settings.Data);



        } catch (Exception e) { failure = e.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(DataDirectory, "station-result.json"), JsonSerializer.Serialize(new { passed = failure == null, checks, failure }, new JsonSerializerOptions { WriteIndented = true }));
        if (failure != null) Environment.ExitCode = 1;
        if (Environment.GetCommandLineArgs().Contains("--preview")) return;
        await Quit();
    }
    static async Task VerifyOrdering(Action<string, bool> check)
    {
        string path = Path.Combine(DataDirectory, "order-test.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new StationDocument(3, [])));
        var store = new StationStore(path);
        await store.Add([StationStore.Text("A"), StationStore.Text("B"), StationStore.Text("C"), StationStore.Text("D")]);
        var initial = store.Ordered(true).Select(i => i.Id).ToArray();
        check("LegacySortingFallback", store.Sort(true) == StationSort.Newest && store.Sort(false) == StationSort.Oldest);
        await store.Reorder([initial[0]], initial[2], initial);
        var single = new[] { initial[1], initial[0], initial[2], initial[3] };
        check("SingleItemReorder", store.Ordered(true).Select(i => i.Id).SequenceEqual(single));
        await store.Reorder([single[3], single[0]], single[1], single);
        var group = new[] { single[0], single[3], single[1], single[2] };
        check("MultiReorderKeepsRelativeOrder", store.Ordered(true).Select(i => i.Id).SequenceEqual(group));
        await store.SetSort(StationSort.Oldest, true); await store.SetSort(StationSort.Manual, true);
        check("ManualOrderSurvivesAutoSort", store.Ordered(true).Select(i => i.Id).SequenceEqual(group));
        await store.Add([StationStore.Text("E")]);
        check("ManualNewItemsAppend", store.Ordered(true).Last().Value == "E" && store.Ordered(true).Take(4).Select(i => i.Id).SequenceEqual(group));
        var all = store.Ordered(true).Select(i => i.Id).ToArray(); await store.Reorder([all[0]], null, all);
        var expected = all.Skip(1).Append(all[0]).ToArray();
        check("DropAtEndPersists", new StationStore(path).Ordered(true).Select(i => i.Id).SequenceEqual(expected));
        bool rejected = false; try { await store.Reorder([Guid.NewGuid()], null, expected); } catch (InvalidDataException) { rejected = true; }
        check("StaleDragIsAtomic", rejected && store.Ordered(true).Select(i => i.Id).SequenceEqual(expected));
        await store.Add([StationStore.Text("F")]); await store.Reorder([expected[0]], null, expected);
        check("ConcurrentAdditionRetained", store.Items.Count == 6 && store.Items.Any(i => i.Value == "F"));
        var deleted = store.Items.First(); await store.RemoveArchived([deleted]); await store.Restore([deleted]);
        check("ReorderRemoveRestore", new StationStore(path).Items.Count == 6);
    }
    static async Task VerifyGridLayout(Action<string, bool> check)
    {
        string path = Path.Combine(DataDirectory, "grid-test.json"); var store = new StationStore(path);
        await store.Add(Enumerable.Range(0, 6).Select(i => StationStore.Text("格子" + i)).ToArray());
        var ids = store.Ordered(true).Select(i => i.Id).ToArray();
        await store.MoveGrid([ids[4]], ids[4], 1, 1, false, 5, true);
        var layout = store.Layout(false, 2, true);
        check("GridLeavesFirstRowEmpty", layout.Columns == 5 && !layout.Cells.Any(c => c.Column == 4 && c.Row == 0) && layout.Cells.Single(c => c.Id == ids[4]) is { Column: 1, Row: 1 });
        check("GridRestartPreservesHole", new StationStore(path).Layout(false, 1, true).Cells.SequenceEqual(layout.Cells));
        await store.MoveGrid([ids[0]], ids[0], 0, 1, false, 5, true);
        check("GridSingleSwap", store.Layout(false, 5, true).Cells.Single(c => c.Id == ids[5]) is { Column: 0, Row: 0 });
        await store.EnsureLayout(true, 8, true); var expanded = store.Layout(true, 8, true);
        await store.MoveGrid([ids[1], ids[2]], ids[1], 3, 2, false, 5, true);
        var moved = store.Layout(false, 5, true);
        check("GridMultiKeepsShape", moved.Cells.Single(c => c.Id == ids[1]) is { Column: 3, Row: 2 } && moved.Cells.Single(c => c.Id == ids[2]) is { Column: 4, Row: 2 });
        check("GridModesIndependent", store.Layout(true, 8, true).Cells.SequenceEqual(expanded.Cells));
        bool rejected = false; try { await store.MoveGrid([ids[1], ids[2]], ids[1], 0, 0, false, 5, true); } catch (InvalidDataException) { rejected = true; }
        check("GridCollisionAtomic", rejected && store.Layout(false, 5, true).Cells.SequenceEqual(moved.Cells));
        rejected = false; try { await store.MoveGrid([ids[1], ids[2]], ids[1], 4, 2, false, 5, true); } catch (InvalidDataException) { rejected = true; }
        check("GridBoundsAtomic", rejected && store.Layout(false, 5, true).Cells.SequenceEqual(moved.Cells));
        await store.SetSort(StationSort.Newest, true); await store.SetSort(StationSort.Manual, true);
        check("GridAutoSortRetainsHoles", store.Layout(false, 5, true).Cells.SequenceEqual(moved.Cells));
        await store.Add([StationStore.Text("填空")]);
        check("GridNewItemFillsFirstHole", store.Layout(false, 5, true).Cells.Single(c => c.Id == store.Items.Last().Id) is { Column: 1, Row: 0 });
        await store.ArrangeGrid(false, 5, true);
        check("GridTidyPacksCells", store.Layout(false, 5, true).Cells.Select(c => c.Row * 5 + c.Column).Order().SequenceEqual(Enumerable.Range(0, 7)) && store.Layout(true, 5, true).Cells.First(c => c.Id == ids[4]).Row == 1);
        var before = await File.ReadAllTextAsync(path); await store.MoveGrid([ids[0]], ids[0], 0, 3, false, 5, true);
        check("GridSaveBackup", File.Exists(path + ".bak") && await File.ReadAllTextAsync(path + ".bak") == before);
    }
    static async Task VerifyDesktopStorage(Action<string, bool> check)
    {
        string folder = Path.Combine(DataDirectory, "desktop-fixture"); Directory.CreateDirectory(folder);
        string source = Path.Combine(folder, "原件.txt"); await File.WriteAllTextAsync(source, "原始内容");
        string nested = Path.Combine(folder, "目录"); Directory.CreateDirectory(Path.Combine(nested, "空目录")); await File.WriteAllTextAsync(Path.Combine(nested, "子文件.txt"), "子内容");
        string desktopCatalog = Path.Combine(DataDirectory, "desktop-check", "station.json");
        Directory.CreateDirectory(Path.GetDirectoryName(desktopCatalog)!);
        await File.WriteAllTextAsync(desktopCatalog, JsonSerializer.Serialize(new StationDocument(4, [])));
        var store = new StationStore(desktopCatalog);
        await store.ImportCopies([new(StationKind.File, source), new(StationKind.Folder, nested)]);
        var file = store.Items[0]; var directory = store.Items[1];
        check("DesktopCopiesAreIndependent", file.Owned && file.Value != source && await File.ReadAllTextAsync(file.Value) == "原始内容" && directory.Owned && Directory.Exists(Path.Combine(directory.Value, "空目录")));
        await File.WriteAllTextAsync(file.Value, "副本编辑"); check("DesktopEditingLeavesOriginal", await File.ReadAllTextAsync(source) == "原始内容");
        await store.ImportCopies([new(StationKind.File, source)]); check("DesktopSameNamesCoexist", store.Items.Count == 3 && store.Items[0].Value != store.Items[2].Value);
        await store.RenameOwned(file.Id, "新名字.txt"); file = store.Items.Single(i => i.Id == file.Id);
        check("DesktopRenameKeepsIdentity", Path.GetFileName(file.Value) == "新名字.txt" && File.Exists(source) && await File.ReadAllTextAsync(file.Value) == "副本编辑");
        string interrupted = Path.Combine(Path.GetDirectoryName(file.Value)!, "未提交的新名字.txt"); File.Move(file.Value, interrupted);
        string journal = Path.Combine(DataDirectory, "desktop-check", "rename-journals", file.Id.ToString("N") + ".json"); Directory.CreateDirectory(Path.GetDirectoryName(journal)!); await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new[] { file.Value, interrupted }));
        _ = new StationStore(Path.Combine(DataDirectory, "desktop-check", "station.json")); check("DesktopRenameCrashRecovery", File.Exists(file.Value) && !File.Exists(interrupted) && !File.Exists(journal));

        await store.MoveGrid([file.Id], file.Id, 2, 2, false, 4, true);
        await store.RemoveArchived([file]); check("DesktopRecycleRetainsBytes", store.Trash.Any(i => i.Id == file.Id) && File.Exists(file.Value) && new StationStore(Path.Combine(DataDirectory, "desktop-check", "station.json")).Trash.Count == 1);
        await store.Restore([file]); check("DesktopRestoreKeepsIdentity", store.Items.Any(i => i.Id == file.Id) && store.Trash.Count == 0);
        bool failed = false; int before = store.Items.Count;
        try { await store.ImportCopies([new(StationKind.File, source), new(StationKind.File, Path.Combine(folder, "不存在.txt"))]); } catch (IOException) { failed = true; }
        check("DesktopBatchCopyRollback", failed && store.Items.Count == before && Directory.GetDirectories(store.DesktopDirectory).Length == before);
        await store.Add([new(StationKind.File, source)]); var legacy = store.Items.Last(); var owned = await store.OwnReference(legacy.Id);
        check("LegacyConversionPreservesId", owned.Id == legacy.Id && owned.Owned && owned.Value != source && File.Exists(source));
        await store.NewFolder("工作文件夹"); await store.ImportCopies([new(StationKind.Text, "便笺内容", "我的便笺")]); var note = store.Items.Last(); await store.UpdateContent(note.Id, "编辑便笺");
        check("DesktopNewAndEdit", store.Items.Any(i => i.Kind == StationKind.Folder && Path.GetFileName(i.Value) == "工作文件夹") && store.Items.Single(i => i.Id == note.Id).Value == "编辑便笺");
        check("DesktopNameSearch", StationWindow.SearchMatches(file, "新名字") && !StationWindow.SearchMatches(file, "不存在"));
        await store.RemoveArchived([owned]);
        string originalContainer = Path.GetDirectoryName(owned.Value)!, quarantine = Path.Combine(store.DesktopDirectory, Guid.NewGuid().ToString("N")); Directory.Move(originalContainer, quarantine);
        string deletionJournal = Path.Combine(DataDirectory, "desktop-check", "rename-journals", owned.Id.ToString("N") + ".delete.json"); await File.WriteAllTextAsync(deletionJournal, JsonSerializer.Serialize(new[] { owned.Id.ToString(), originalContainer, quarantine }));
        _ = new StationStore(Path.Combine(DataDirectory, "desktop-check", "station.json")); check("DesktopDeleteCrashRecovery", File.Exists(owned.Value) && !Directory.Exists(quarantine));
        await store.Add([new(StationKind.File, source)]); var oldReference = store.Items.Last(); await store.RemoveArchived([oldReference]); await store.DeleteTrash([oldReference]); check("DesktopDeleteReferenceNeverDeletesOriginal", File.Exists(source));
        int afterPermanentDelete = store.Items.Count; await store.Restore([oldReference]); check("DesktopStaleUndoCannotRestoreDeletedContent", store.Items.Count == afterPermanentDelete);
        string backup = Path.Combine(folder, "desktop.zip"); await store.ExportDesktop(backup);
        var restored = new StationStore(Path.Combine(DataDirectory, "desktop-restored", "station.json")); await restored.RestoreDesktop(backup);
        check("DesktopFullBackupRestore", restored.Items.Count == store.Items.Count && restored.Trash.Count == 1 && await File.ReadAllTextAsync(restored.Items.Single(i => i.Id == file.Id).Value) == "副本编辑" && Directory.Exists(restored.Items.First(i => i.Kind == StationKind.Folder).Value));
        check("DesktopBackupLayoutRestore", restored.Layout(false, 1, true).Cells.SequenceEqual(store.Layout(false, 1, true).Cells));
        await restored.DeleteTrash(restored.Trash.ToArray()); check("DesktopPermanentDeleteLeavesSource", restored.Trash.Count == 0 && File.Exists(source) && File.Exists(owned.Value));
        await store.ImportCopies([new(StationKind.File, source)], receipt: "receipt-test"); int receiptCount = store.Items.Count; await store.ImportCopies([new(StationKind.File, source)], receipt: "receipt-test");
        check("DesktopReceiptIdempotence", store.Items.Count == receiptCount);
        var rejected = new StationStore(Path.Combine(DataDirectory, "desktop-reject", "station.json"));
        string badZip = Path.Combine(folder, "bad.zip"); using (var zip = System.IO.Compression.ZipFile.Open(badZip, System.IO.Compression.ZipArchiveMode.Create)) { var entry = zip.CreateEntry("halo-desktop.json"); using (var output = new StreamWriter(entry.Open())) output.Write("{\"Version\":3,\"Items\":[]}"); zip.CreateEntry("files/../../escape.txt"); }
        failed = false; try { await rejected.RestoreDesktop(badZip); } catch (IOException) { failed = true; }
        check("DesktopBackupRejectsTraversal", failed && rejected.Items.Count == 0 && !File.Exists(Path.Combine(DataDirectory, "escape.txt")));
    }
    static Task VerifyFloatingCaptionForStation() => Panel!.VerifyFloatingCaption(true);
}

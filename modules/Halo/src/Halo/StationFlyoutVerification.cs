using System.Text.Json;
using AIEnter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using InputFixture = AIEnter.AiWindow.DesktopInputFixture;

namespace Halo;

public partial class App
{
    async Task VerifyStationFlyout(bool includeFiles)
    {
        Window? taskbar = null, background = null;
        var checks = new List<string>();
        string? error = null;
        try
        {
            Directory.CreateDirectory(DataDirectory);
            Settings.Save(Settings.Data with { Pinned = false, Detached = false, DetachedHideOnBlur = false });
            var folder = Directory.CreateDirectory(Path.Combine(DataDirectory, "样例文件夹")).FullName;
            for (int i = 0; i < 220; i++)
                await File.WriteAllTextAsync(Path.Combine(folder, $"样例-{i:000}.txt"), "隔离验证");
            await Station.ImportCopies([new(StationKind.Folder, folder)]);
            var work = Native.Monitors().First(item => item.Primary).Info.Work;
            var paint = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
            background = AIEnter.App.FixtureWindow(new Border { Background = paint },
                new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), topmost: false);
            var anchor = new Button { Content = "Halo", Width = 70, HorizontalAlignment = HorizontalAlignment.Left };
            var bar = new Grid { Background = paint };
            bar.Children.Add(anchor);
            taskbar = AIEnter.App.FixtureWindow(bar, new(work.Left + 6, work.Bottom - 54, 220, 50));
            PanelToggleTargets.Register(taskbar, anchor);
            AttachTaskbar(anchor);
            Panel = CreatePanel();
            anchor.Click += (_, _) => Handle("Toggle");
            await Task.Delay(250);
            var frame = ScreenshotCapture.Desktop();
            Panel.Open();
            void Check(string name, bool passed)
            {
                if (!passed) throw new InvalidOperationException(name);
                checks.Add(name);
                File.AppendAllText(Path.Combine(DataDirectory, "halo-flyout-progress.log"), name + "\n");
            }
            var entrance = InputCoordinate(work.Left + 6 + (int)Math.Round(anchor.ActualWidth * anchor.XamlRoot.RasterizationScale / 2), work.Bottom - 29);
            await Panel.VerifyFlyoutWindow(frame, entrance, Check);
            await Panel.VerifyFlyoutFocusDismissal(taskbar, background, Check);
            if (includeFiles) await Panel.VerifyFlyoutTransfers(work, DataDirectory, Check);
            await VerifyDefaultProfile(Check);
            Panel.VerifyMixedDpiGeometry(Check);
            await Panel.VerifyFlyoutPalette(Check);
            await VerifySettingsSubpages(work, Check);
            await Settings.Flush();
        }
        catch (Exception failure) { error = failure.ToString(); Environment.ExitCode = 1; }
        finally
        {
            Panel?.Destroy();
            taskbar?.Close();
            background?.Close();
            File.WriteAllText(Path.Combine(DataDirectory, "halo-flyout-result.json"),
                JsonSerializer.Serialize(new { passed = error is null, checks, displayDpi = Native.Monitors().Select(monitor => monitor.Dpi), error }));
            Application.Current.Exit();
        }
    }
    static AIEnter.Native.Point InputCoordinate(int x, int y) => new() { X = x, Y = y };

    async Task VerifySettingsSubpages(Native.Rect work, Action<string, bool> check)
    {
        var host = new Window();
        try
        {
            var page = CreateSettingsPage(host, new AITaskbar.SettingsPaneWidths(DataDirectory, _ => { }));
            host.Content = page;
            var scale = Native.Monitors().First(monitor => monitor.Primary).Dpi / 96d;
            host.AppWindow.MoveAndResize(new(work.Left + 40, work.Top + 40,
                Math.Min(work.Right - work.Left - 80, (int)(660 * scale)),
                Math.Min(work.Bottom - work.Top - 80, (int)(480 * scale))));
            host.Activate();
            await page.VerifySubpageNavigation(check);
        }
        finally { host.Close(); }
    }
}

internal sealed partial class StationWindow
{
    static AIEnter.Native.Point InputPoint(int x, int y) => new() { X = x, Y = y };
    static nint At(int x, int y) => Native.GetAncestor(FlyoutWindowAtPoint(new() { X = x, Y = y }), 2);

    internal void VerifyMixedDpiGeometry(Action<string, bool> check)
    {
        var baseline = new HaloSettings();
        // A 200%-scaled taskbar can host a popup positioned on a 150%-scaled screen.
        var secondary = new Native.Rect { Left = -1920, Top = 0, Right = 0, Bottom = 1040 };
        var bounds = new RectInt32(-1500, 60, 900, 720);
        var saved = SaveBounds(baseline, bounds, true, 1.5);
        check("MixedDpiExpandedSizeRoundTripsOnDestinationMonitor",
            saved is { ExpandedWidth: 600, ExpandedHeight: 480 }
            && Native.Same(RestoreBounds(saved, true, secondary, 1.5), bounds));
        check("SavingExpandedSizePreservesCompactSize", saved.Width == baseline.Width && saved.Height == baseline.Height);
        var primary = new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1040 };
        var fallback = RestoreBounds(saved, true, primary, 2);
        check("RemovedMonitorLayoutIsClampedIntoPrimaryWorkArea",
            fallback.X == 0 && fallback.Y >= primary.Top && fallback.X + fallback.Width <= primary.Right
            && fallback.Y + fallback.Height <= primary.Bottom);
    }

    internal async Task VerifyFlyoutPalette(Action<string, bool> check)
    {
        Open();
        await Task.Delay(240);
        var original = App.Appearance;
        var light = Microsoft.UI.Colors.White;
        var dark = Microsoft.UI.Colors.Black;
        static bool SameRgb(Windows.UI.Color a, Windows.UI.Color b) => (a.R, a.G, a.B) == (b.R, b.G, b.B);
        Windows.UI.Color RenderedTint() => Descendants<Border>(backplate).Select(element => element.Background)
            .OfType<SolidColorBrush>().First(brush => brush.Color.A > 0).Color;
        try
        {
            if (flyoutAccessibility.HighContrast)
            {
                check("HaloHighContrastUsesSystemBackdropColor", RenderedTint() == flyoutVisuals.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
                return;
            }
            var customLight = Windows.UI.Color.FromArgb(255, 226, 241, 249);
            var customDark = Windows.UI.Color.FromArgb(255, 32, 48, 71);
            App.Preview(original with { LightBackplateColor = "#E2F1F9", DarkBackplateColor = "#203047", BackplateTransparency = 80 });
            foreach (var (theme, taskbar, target, foreground) in new[]
            {
                (ElementTheme.Light, light, customLight, dark), (ElementTheme.Dark, dark, customDark, light)
            })
            {
                SetTaskbarPalette(theme, taskbar);
                await Task.Delay(90);
                var tint = RenderedTint();
                check($"Halo{theme}UsesCustomBackdropAndReadableForeground", root.ActualTheme == theme && SameRgb(tint, target)
                    && AIEnter.PanelTheme.Contrast(foreground, AIEnter.PanelTheme.Composite(tint, light)) >= 4.5
                    && AIEnter.PanelTheme.Contrast(foreground, AIEnter.PanelTheme.Composite(tint, dark)) >= 4.5);
            }
            App.Preview(null);
            await Task.Delay(90);
            check("HaloCancelPreviewRestoresSavedBackdrop", App.Appearance == original && SameRgb(RenderedTint(), dark));
        }
        finally { App.Preview(null); }
    }

    internal async Task VerifyFlyoutWindow(ScreenshotFrame frame, AIEnter.Native.Point entrance, Action<string, bool> check)
    {
        await Task.Delay(500);
        var handle = flyoutLayer?.Handle ?? 0;
        check("CompactPopupAndHiddenHost", handle != 0 && IsOpen && !AppWindow.IsVisible && !Detached);
        void CheckCorners(string label)
        {
            var bounds = CurrentBounds();
            var current = ScreenshotCapture.Desktop();
            int changed = 0;
            foreach (int x in new[] { bounds.X + 2, bounds.X + bounds.Width - 3 })
            foreach (int y in new[] { bounds.Y + 2, bounds.Y + bounds.Height - 3 })
            {
                int index = ((y - current.Top) * current.Width + x - current.Left) * 4;
                for (int channel = 0; channel < 3; channel++)
                    if (Math.Abs(current.Pixels[index + channel] - frame.Pixels[index + channel]) > 4) changed++;
            }
            check(label, changed == 0);
        }
        CheckCorners("CompactRoundedCorners");
        var card = (GridViewItem)files.Items[0];
        files.SelectedItems.Add(card);
        var folder = (StationItem)card.Tag;
        await OpenFolderBrowser(folder);
        var browser = folderBrowser;
        var scroll = Child<ScrollViewer>(browser!.GridForWindowLayout);
        await Task.Delay(200);
        check("FolderFixtureCanScroll", scroll != null && scroll.ScrollableHeight > 100);
        scroll!.ChangeView(null, scroll.ScrollableHeight * .6, null, true);
        await Task.Delay(160);
        check("FolderFixtureScrollIsSet", scroll.VerticalOffset > 50);
        var scrollAnchor = browser.GridForWindowLayout.Items.OfType<GridViewItem>()
            .Where(card => card.IsLoaded && card.ActualHeight > 0)
            .Select(card => (Card: card, Y: card.TransformToVisual(scroll).TransformPoint(new()).Y))
            .Where(item => item.Y >= 0 && item.Y < scroll.ActualHeight - 1)
            .OrderBy(item => item.Y).First().Card;
        bool AnchorVisible() => scrollAnchor.IsLoaded
            && scrollAnchor.TransformToVisual(scroll).TransformPoint(new()).Y is var y
            && y > -scrollAnchor.ActualHeight && y < scroll.ActualHeight;
        SetDetached(true);
        await Task.Delay(400);
        var bounds = CurrentBounds();
        var center = InputPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        check("SamePopupFolderSelectionAndNoSwitcher", Detached && flyoutLayer?.Handle == handle
            && AITaskbar.FlyoutWindowLayer.GetLayerWindow(handle, 4) != 0
            && (AITaskbar.FlyoutWindowLayer.GetLayerStyle(handle, -20).ToInt64() & 0x40000) == 0
            && !AppWindow.IsVisible && ReferenceEquals(browser, folderBrowser) && files.SelectedItems.Contains(card)
            && browser.CurrentDirectory == folder.Value);
        check("FolderScrollSurvivesExpansion", AnchorVisible());
        CheckCorners("ExpandedRoundedCorners");
        var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "浮层对话框验证", Content = "测试面板保持打开。", CloseButtonText = "关闭" };
        var dialogTask = ShowPanelDialog(dialog);
        await Task.Delay(270);
        try { check("ContentDialogStaysInHaloPopup", ModalActive && IsOpen && dialog.Parent == root && dialog.ActualHeight > 60); }
        finally { dialog.Hide(); await dialogTask; }
        SetDetached(false);
        await Task.Delay(320);
        check("CompactRestoresSameFolderAndPopup", !Detached && flyoutLayer?.Handle == handle && ReferenceEquals(browser, folderBrowser));
        check("FolderScrollSurvivesCompaction", AnchorVisible());
        SetDetached(true);
        await Task.Delay(320);
        var cover = AIEnter.App.FixtureWindow(new Border { Background = new SolidColorBrush(Microsoft.UI.Colors.Orange) },
            new RectInt32(bounds.X - 24, bounds.Y, bounds.Width + 24, bounds.Height), topmost: false);
        try
        {
            var coverHandle = WinRT.Interop.WindowNative.GetWindowHandle(cover);
            await Task.Run(() => InputFixture.ClickAt(InputPoint(bounds.X - 12, center.Y)));
            await Task.Delay(300);
            check("OrdinaryWindowCanCoverUnpinnedHalo", At(center.X, center.Y) == coverHandle && IsOpen);
            await Task.Run(() => InputFixture.ClickAt(entrance));
            await Task.Delay(350);
            check("TaskbarRecallsSameHaloPopup", At(center.X, center.Y) == handle && IsOpen && ReferenceEquals(browser, folderBrowser));
            TogglePin();
            await Task.Run(() => InputFixture.ClickAt(InputPoint(bounds.X - 12, center.Y)));
            await Task.Delay(280);
            check("PinnedHaloStaysInFront", At(center.X, center.Y) == handle && IsOpen);
            await Task.Run(() => InputFixture.ClickAt(entrance));
            await Task.Delay(300);
            check("TaskbarClosesFrontHalo", !IsOpen);
        }
        finally { cover.Close(); }
    }

    internal async Task VerifyFlyoutFocusDismissal(Window owner, Window other, Action<string, bool> check)
    {
        var settings = App.Settings.Data;
        App.Settings.Save(settings with { Pinned = false, DetachedHideOnBlur = true });
        try
        {
            owner.Activate();
            Open();
            await Task.Delay(300);
            check("ExpandedFlyoutObservesForegroundChanges", Detached && IsOpen && foregroundHook != 0);
            async Task SwitchForeground()
            {
                owner.Activate();
                await Task.Delay(100);
                other.Activate();
                await Task.Delay(250);
            }
            using (KeepOpen())
            {
                await SwitchForeground();
                check("ForegroundChangeKeepsModalInteractionOpen", IsOpen);
            }
            using (KeepOpenForDrag())
            {
                await SwitchForeground();
                check("ForegroundChangeKeepsActiveDragOpen", IsOpen);
            }
            await SwitchForeground();
            check("ForegroundChangeWithoutMouseDismissesExpandedFlyout",
                Native.GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(other) && !IsOpen);
            owner.Activate();
            Open();
            await Task.Delay(300);
            TogglePin();
            await SwitchForeground();
            check("ForegroundChangeKeepsPinnedFlyoutOpen", IsOpen);
            Hide();
            await Task.Delay(250);
        }
        finally { App.Settings.Save(settings); }
    }

    internal async Task VerifyFlyoutTransfers(Native.Rect work, string directory, Action<string, bool> check)
    {
        var pixel = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray([0, 0, 0, 255]);
        var capture = Task.FromResult<DragPreviewPixels?>(new(pixel, 1, 1, default));
        bool triedPreview = false;
        using var rejectedPreview = ApplyDragBitmap(capture, (_, _) =>
        {
            triedPreview = true;
            throw new System.Runtime.InteropServices.COMException("Simulated drag image rejection", CaptureAborted);
        });
        check("RejectedDragImageUsesSystemPreview", triedPreview && rejectedPreview is null);
        Open();
        await Task.Delay(400);
        var bounds = CurrentBounds();
        string fixture = Path.Combine(directory, "双向拖放样例.txt");
        await File.WriteAllTextAsync(fixture, "Halo round trip");
        using var source = new InputFixture(work.Left + 10, work.Top + 160, fixture);
        source.Show();
        await Task.Run(() => InputFixture.Drag(InputPoint(work.Left + 70, work.Top + 195),
            InputPoint(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2)));
        for (int i = 0; i < 35 && !App.Station.Items.Any(item => item.Kind == StationKind.File); i++) await Task.Delay(100);
        var imported = App.Station.Items.FirstOrDefault(item => item.Kind == StationKind.File);
        check("SystemFileDragInCreatesOwnedCopy", imported is { Owned: true } && imported.Value != fixture
            && File.ReadAllText(imported.Value) == File.ReadAllText(fixture));
        for (int i = 0; i < 20 && (DragActive || ModalActive || !files.Items.Cast<GridViewItem>().Any(item => ((StationItem)item.Tag).Id == imported!.Id)); i++)
            await Task.Delay(60);
        check("IncomingDropReleasedItsInteractionLease", !DragActive && !ModalActive);
        await VerifyPointerOrdering(imported!.Id, bounds, check);
        var card = files.Items.Cast<GridViewItem>().Single(item => ((StationItem)item.Tag).Id == imported.Id);
        check("ImportedCardIsLaidOut", card.IsLoaded && card.ActualWidth > 10 && card.ActualHeight > 10);
        var position = card.TransformToVisual(root).TransformPoint(new(card.ActualWidth / 2, card.ActualHeight / 2));
        var from = InputPoint(bounds.X + (int)Math.Round(position.X * WindowLayoutScale), bounds.Y + (int)Math.Round(position.Y * WindowLayoutScale));
        var ready = new TaskCompletionSource<(System.Windows.Forms.Form Form, nint Handle, System.Drawing.Point Point)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        int presses = 0, moves = 0, enters = 0;
        card.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => presses++), true);
        card.AddHandler(UIElement.PointerMovedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => moves++), true);
        void AllowCopy(object? _, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(System.Windows.Forms.DataFormats.FileDrop) == true) e.Effect = System.Windows.Forms.DragDropEffects.Copy;
        }
        // Run the drop recipient on another STA, as Explorer is independent of Halo's UI dispatcher.
        var receiver = new Thread(() =>
        {
            using var destination = new System.Windows.Forms.Form { AllowDrop = true, TopMost = true, ShowInTaskbar = false,
                FormBorderStyle = System.Windows.Forms.FormBorderStyle.None, StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Bounds = new(work.Left + 14, work.Top + 310, 190, 110) };
            destination.Shown += (_, _) => ready.SetResult((destination, destination.Handle, destination.PointToScreen(
                new System.Drawing.Point(destination.ClientSize.Width / 2, destination.ClientSize.Height / 2))));
            destination.DragEnter += (_, e) => { Interlocked.Increment(ref enters); AllowCopy(null, e); };
            destination.DragOver += AllowCopy;
            destination.DragDrop += (_, e) => delivered.TrySetResult(e.Data?.GetData(System.Windows.Forms.DataFormats.FileDrop) as string[] ?? []);
            System.Windows.Forms.Application.Run(destination);
        }) { IsBackground = true };
        receiver.SetApartmentState(ApartmentState.STA);
        receiver.Start();
        var (receiverWindow, receiverHandle, target) = await ready.Task.WaitAsync(TimeSpan.FromSeconds(4));
        try
        {
            check("OutgoingDragStartsOnTheHaloPopup", At(from.X, from.Y) == flyoutLayer?.Handle);
            check("OutgoingDragTargetReceivesClientInput", At(target.X, target.Y) == receiverHandle);
            await Task.Run(() => InputFixture.Drag(from, InputPoint(target.X, target.Y), sendNativeMovement: true));
            string[] received = await Task.WhenAny(delivered.Task, Task.Delay(2000)) == delivered.Task ? await delivered.Task : [];
            if (!received.SequenceEqual(new[] { imported!.Value }) || !File.Exists(fixture))
                throw new InvalidOperationException($"File drag-out failed. Presses={presses}, moves={moves}, selected={Selected().Length}, native={verifiedShellExport}, target enters={enters}, delivered={received.Length}, status={status.Text}");
            check("SystemFileDragOutExportsOwnedCopy", true);
            await Task.Delay(250);
            delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var outsideDropTarget = InputPoint(target.X, target.Y + 130);
            check("CancelDropPointIsOutsideReceiverAndHalo", At(outsideDropTarget.X, outsideDropTarget.Y) != receiverHandle
                && At(outsideDropTarget.X, outsideDropTarget.Y) != flyoutLayer?.Handle);
            await Task.Run(() => InputFixture.Drag(from, outsideDropTarget, sendNativeMovement: true));
            await Task.Delay(250);
            check("CancelledSystemDragDoesNotDeliverOrLeavePreview", !delivered.Task.IsCompleted && !DragActive
                && verifiedShellExport == nameof(Windows.ApplicationModel.DataTransfer.DataPackageOperation.None)
                && dragPreviewLayer.Visibility == Visibility.Collapsed && File.Exists(imported.Value));
            var folderCard = files.Items.Cast<GridViewItem>().Single(item => ((StationItem)item.Tag).Kind == StationKind.Folder);
            var folder = (StationItem)folderCard.Tag;
            var point = folderCard.TransformToVisual(root).TransformPoint(new(folderCard.ActualWidth / 2, folderCard.ActualHeight / 2));
            var folderFrom = InputPoint(bounds.X + (int)Math.Round(point.X * WindowLayoutScale), bounds.Y + (int)Math.Round(point.Y * WindowLayoutScale));
            await Task.Run(() => InputFixture.Drag(folderFrom, InputPoint(target.X, target.Y), sendNativeMovement: true));
            var folderPaths = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check("SystemFolderDragOutExportsOwnedCopy", folderPaths.SequenceEqual(new[] { folder.Value })
                && File.Exists(Path.Combine(folder.Value, "样例-000.txt")));
        }
        finally { receiverWindow.BeginInvoke(receiverWindow.Close); }
        source.Close();
        await VerifyMainLibraryDrop(check);
        var browser = folderBrowser;
        check("DragSettlesWithoutRecreatingPanel", IsOpen && flyoutLayer?.Handle != 0 && browser == folderBrowser);
    }

    async Task VerifyPointerOrdering(Guid importedId, RectInt32 bounds, Action<string, bool> check)
    {
        await App.Station.SetSort(StationSort.Manual, App.Settings.Data.NewestFirst);
        await Task.Delay(250);
        var layout = App.Station.Layout(detached, GridColumns(), App.Settings.Data.NewestFirst);
        var gridPanel = (StationGridPanel)files.ItemsPanelRoot;
        int availableColumns = Math.Min(layout.Columns, (int)((files.ActualWidth - files.Padding.Left - files.Padding.Right) / GridAppearance.CellWidth));
        int emptyColumn = Enumerable.Range(0, availableColumns).Last(column => !layout.Cells.Any(cell => cell.Column == column && cell.Row == 0));
        var drop = gridPanel.TransformToVisual(root).TransformPoint(new((emptyColumn + .5) * GridAppearance.CellWidth, GridAppearance.CellHeight * .55));
        var movedCard = files.Items.OfType<GridViewItem>().Single(item => ((StationItem)item.Tag).Id == importedId);
        var position = movedCard.TransformToVisual(root).TransformPoint(new(movedCard.ActualWidth / 2, movedCard.ActualHeight / 2));
        var from = InputPoint(bounds.X + (int)Math.Round(position.X * WindowLayoutScale), bounds.Y + (int)Math.Round(position.Y * WindowLayoutScale));
        var to = InputPoint(bounds.X + (int)Math.Round(drop.X * WindowLayoutScale), bounds.Y + (int)Math.Round(drop.Y * WindowLayoutScale));
        await Task.Run(() => InputFixture.Drag(from, to));
        await Task.Delay(450);
        check("MouseDragReordersAnExistingCardWithoutImporting", App.Station.Items.Count == 2
            && App.Station.Layout(detached, GridColumns(), App.Settings.Data.NewestFirst).Cells
                .Any(cell => cell.Id == importedId && cell.Column == emptyColumn && cell.Row == 0));
    }
}

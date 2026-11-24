using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.System;
using Windows.Graphics;

namespace Halo;

internal sealed partial class StationWindow : PanelWindow
{
    readonly GridView files = new() { SelectionMode = ListViewSelectionMode.Extended, IsItemClickEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10), IsMultiSelectCheckBoxEnabled = false,
        Background = new SolidColorBrush(Colors.Transparent) };
    readonly Grid root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    readonly Grid header = new() { Margin = new Thickness(8, 6, 8, 0), Background = new SolidColorBrush(Colors.Transparent) };
    readonly StackPanel windowActions = PanelTheme.Actions();
    readonly Button expand = WindowControls.IconButton("\uE740", "展开为独立窗口");
    readonly ToggleButton pin = new();
    readonly Button close = WindowControls.IconButton("\uE8BB", "收起中转站");
    readonly TextBlock empty = new() { Text = "拖入文件、图片、文字或链接", Opacity = .6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 8, 12, 8), FontSize = 12 };
    readonly TextBlock statusWarning = new() { Text = "⚠", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    readonly Border statusHost;
    readonly BackplateSurface backplate = new();
    readonly PanelResizeFrame resizeFrame;
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer feedback, saveSize, dragLeave;
    readonly SemaphoreSlim thumbnailGate = new(4);
    FolderBrowser? folderBrowser;
    Control? focusBeforeFolder;
    CancellationTokenSource thumbnails = new();
    bool positioning, destroyed, openedForDrop, detached, changingMode;
    RectInt32? appliedBounds;
    StationItem[] undo = [];
    protected override bool FloatFadeMotion => !detached;
    protected override bool DismissOnOutside => !ShowingSettings && !App.Settings.Data.Pinned && (!detached || App.Settings.Data.DetachedHideOnBlur);
    internal GridView Files => files;
    internal bool HasBackdrop => backplate.HasTintSurface;
    internal bool Detached => detached;
    internal bool HasWindowButtons => windowActions.Children.Count == 3 && header.ActualHeight > 0;
    internal int DesktopBlur => (SystemBackdrop as GlassBackdrop)?.BlurStrength ?? -1;
    internal bool FullBackdrop => Math.Abs(backplate.ActualHeight - root.ActualHeight) < 1 && Math.Abs(backplate.ActualWidth - root.ActualWidth) < 1;
    internal void VerifyResize() { VerifyResizeGrips(); resizeFrame.VerifyRegion(); }
    internal void VerifyWindowRegion() => resizeFrame.VerifyRegion();
    internal StationWindow(FrameworkElement? taskbarAnchor = null)
    {
        Title = "Halo · 中转站";
        files.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["StationGridItemsPanel"];
        files.Loaded += (_, _) => ApplyGridPositions();
        files.SizeChanged += (_, _) => ApplyGridPositions();
        detached = App.Settings.Data.Detached;
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false); presenter.IsResizable = presenter.IsMaximizable = presenter.IsMinimizable = false;
        if (taskbarAnchor is null) SystemBackdrop = PanelTheme.DesktopBackdrop();
        else InitializeTaskbarFlyout(taskbarAnchor);
        backplate.HorizontalAlignment = HorizontalAlignment.Stretch; backplate.VerticalAlignment = VerticalAlignment.Stretch;
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRowSpan(backplate, 2); Grid.SetRow(files, 1); Grid.SetRow(empty, 1);
        root.Children.Add(backplate); root.Children.Add(files); root.Children.Add(empty);
        PanelTheme.FramedIconStyle(pin, "置顶"); WindowControls.SetIcon(pin, "\uE718");
        expand.Click += (_, _) => { if (ShowingSettings) ReturnFromSettings(); else SetDetached(!detached); };
        pin.Click += (_, _) => TogglePin(); close.Click += (_, _) => Hide();
        windowActions.HorizontalAlignment = HorizontalAlignment.Right;
        windowActions.Children.Add(expand); windowActions.Children.Add(pin); windowActions.Children.Add(close);
        header.Children.Add(windowActions); root.Children.Add(header);
        var notice = new Grid();
        notice.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        notice.ColumnDefinitions.Add(new());
        notice.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        Grid.SetColumn(status, 1);
        Grid.SetColumn(statusUndo, 2);
        PanelTheme.StyleTextAction(statusUndo);
        statusUndo.Click += async (_, _) => { if (notificationUndo is { } action) await Run(action); };
        notice.Children.Add(statusWarning); notice.Children.Add(status); notice.Children.Add(statusUndo);
        statusHost = new Border { Child = notice, CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(10), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        Grid.SetRow(statusHost, 1); root.Children.Add(statusHost);
        root.ActualThemeChanged += (_, _) => RefreshNoticeColors();
        InitializeSelectionDetails();
        InitializeOrdering();
        root.AllowDrop = true;
        void TrackDrag(object sender, DragEventArgs e) {
            if (folderBrowser is { ShowingDesktopDuringDrag: false }) {
                if (!e.Handled) e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }
            e.Handled = true;
            if (HasLibraryDragIdentity(e.DataView) || StationTransfer.IsInternal(e.DataView)) { TrackInternalDrop(e); return; }
            e.AcceptedOperation = !ShowingSettings && Supports(e.DataView) && !App.Station.ReadOnly ? DataPackageOperation.Copy : DataPackageOperation.None;
        }
        root.AddHandler(UIElement.DragEnterEvent, new DragEventHandler((sender, e) => { dragLeave?.Stop(); TrackDrag(sender, e); }), true);
        root.AddHandler(UIElement.DragOverEvent, new DragEventHandler(TrackDrag), true);
        root.DragLeave += (_, _) => ScheduleDragDismiss();
        root.AddHandler(UIElement.DropEvent, new DragEventHandler(async (_, e) => {
            if (folderBrowser is { ShowingDesktopDuringDrag: false }) {
                if (!e.Handled) e.AcceptedOperation = DataPackageOperation.None;
                e.Handled = true;
                return;
            }
            e.Handled = true;
            if (HasLibraryDragIdentity(e.DataView) || StationTransfer.IsInternal(e.DataView)) { await CompleteInternalDrop(e); return; }
            if (ShowingSettings || App.Station.ReadOnly) {
                if (!ShowingSettings && App.Station.LibraryOffline && Supports(e.DataView))
                    ReportImportFailure(StationStore.LibraryOfflineMessage);
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }
            var deferral = e.GetDeferral();
            try { if (await Receive(e.DataView)) { e.AcceptedOperation = DataPackageOperation.Copy; openedForDrop = false; ConfirmIncomingDrop(); } else e.AcceptedOperation = DataPackageOperation.None; }
            finally { deferral.Complete(); }
        }), true);
        root.ContextRequested += (_, e) => { if (ShowingSettings || Searching || App.Station.LibraryOffline || folderBrowser != null || e.Handled || selectionMarquee.IsActive || !ContentBlankTarget(e.OriginalSource as DependencyObject, root)) return; ShowBackgroundMenu(e); e.Handled = true; };
        InitializeDesktopActions(); InitializeBlankSelection(); InitializeCardSelection();
        InitializeLibraryAvailability();
        root.PreviewKeyDown += (_, e) => {
            if (e.Key == Windows.System.VirtualKey.Escape && folderBrowser?.CancelNavigationDrag() == true) e.Handled = true;
        };
        feedback = DispatcherQueue.CreateTimer(); feedback.Interval = TimeSpan.FromSeconds(HaloDefaults.NormalNotificationSeconds); feedback.IsRepeating = false;
        feedback.Tick += (_, _) => ClearNotification();
        saveSize = DispatcherQueue.CreateTimer(); saveSize.IsRepeating = false; saveSize.Interval = TimeSpan.FromMilliseconds(400);
        saveSize.Tick += (_, _) => { saveSize.Stop(); SaveSize(); };
        dragLeave = DispatcherQueue.CreateTimer(); dragLeave.IsRepeating = false; dragLeave.Interval = TimeSpan.FromMilliseconds(550);
        dragLeave.Tick += (_, _) => { if (openedForDrop && !detached && !App.Settings.Data.Pinned && !DragActive && !ModalActive) Hide(); };
        InitializePanel(root);
        InitializeResizeGrips();
        InitializeFloatingCaption(header, windowActions, () => detached, nativeResizeRegions: false);
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // XAML grips own pointer resizing in both modes; native non-client hit tests would steal their presses.
        resizeFrame = new(handle, () => false, QueueFrameRefresh, () => root.CornerRadius);
        AppWindow.Changed += (_, e) => {
            if (e.DidSizeChange) resizeFrame.UpdateRegion();
            if ((e.DidSizeChange || e.DidPositionChange) && !positioning && !changingMode && !resizing && IsOpen) { saveSize.Stop(); saveSize.Start(); }
        };
        App.Station.Changed += Changed;
        Closed += (_, _) => { destroyed = true; selectionMarquee.Cancel(); CloseFolderBrowser(); thumbnails.Cancel(); thumbnails.Dispose(); backplate.Dispose(); resizeFrame.Dispose(); feedback.Stop(); saveSize.Stop(); dragLeave.Stop(); App.Station.Changed -= Changed; };
        ApplyAppearance(); Rebuild();
    }
    void Changed() => DispatcherQueue.TryEnqueue(() => {
        if (destroyed) return;
        if (ShowingSettings) settingsPage!.RefreshTrashSettings();
        _ = RefreshLibraryStatus();
        if (cardPress != null || movingIds.Count > 0 || outgoingHandoff) { orderRebuildPending = true; return; }
        orderRebuildPending = false;
        var ordered = App.Station.Ordered(App.Settings.Data.NewestFirst);
        // Initializing the other mode's manual layout only changes cell positions, not content.
        if (files.Items.Cast<GridViewItem>().Select(card => (StationItem)card.Tag).SequenceEqual(ordered)) ApplyGridPositions();
        else Rebuild();
        if (Searching) _ = RefreshSearch();
    });
    internal void ApplyAppearance()
    {
        if (destroyed) return;
        if (SystemBackdrop is GlassBackdrop glass) PanelTheme.ApplyDesktopBackdrop(root, backplate, glass);
        ApplyStationNoticeColors();
        RefreshInteractionColors();
        foreach (GridViewItem card in files.Items)
            if (card.Content is ContentControl { Tag: Preview { Item.Kind: StationKind.Folder } folder })
                _ = FolderIcon.Apply(folder.Image, App.Station.FolderColorOverride(folder.Item) ?? App.Appearance.FolderColor, folder.Fallback);
        folderBrowser?.RefreshAppearance(GridAppearance);
        if (TaskbarFlyout != null) ApplyTaskbarFlyoutAppearance();
        else
        {
            Native.RemoveWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(this));
            resizeFrame.UpdateRegion();
            if (AppWindow.IsShownInSwitchers != detached) AppWindow.IsShownInSwitchers = detached;
            var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter;
            if (presenter.IsAlwaysOnTop != FlyoutTopmost) presenter.IsAlwaysOnTop = FlyoutTopmost;
        }
        pin.IsChecked = App.Settings.Data.Pinned;
        ToolTipService.SetToolTip(pin, App.Settings.Data.Pinned ? "取消置顶" : "置顶");
        AutomationProperties.SetName(pin, App.Settings.Data.Pinned ? "取消置顶" : "置顶");
        WindowControls.SetIcon(expand, ShowingSettings ? "\uE72B" : detached ? "\uE73F" : "\uE740");
        string expandName = ShowingSettings ? "返回中转站" : detached ? "缩起为任务栏面板" : TaskbarFlyout != null ? "展开中转站" : "展开为独立窗口";
        ToolTipService.SetToolTip(expand, expandName); AutomationProperties.SetName(expand, expandName);
        string closeName = ShowingSettings ? "关闭设置窗口" : "收起中转站";
        ToolTipService.SetToolTip(close, closeName); AutomationProperties.SetName(close, closeName);
        root.SizeChanged -= RootSizeChanged; root.SizeChanged += RootSizeChanged;
        UpdateFloatingCaption();
    }
    void ApplyStationNoticeColors() => statusWarning.Foreground = PanelTheme.ApplyNoticeColors(statusHost, status);
    internal void RefreshNoticeColors()
    {
        if (destroyed) return;
        ApplyStationNoticeColors();
        RefreshInteractionColors();
        settingsPage?.RefreshNoticePreview();
        settingsPage?.RefreshInteractionSample();
    }
    void UpdateWindowClip()
    {
        PanelTheme.ClipDesktopBackdrop(root);
        if (TaskbarFlyout != null) ApplyTaskbarFlyoutAppearance();
        else resizeFrame.UpdateRegion();
    }
    bool frameRefreshQueued;
    void QueueFrameRefresh() {
        if (destroyed || frameRefreshQueued) return; frameRefreshQueued = true;
        DispatcherQueue.TryEnqueue(() => {
            frameRefreshQueued = false; if (destroyed || changingMode) return;
            Native.RemoveWindowFrame(WinRT.Interop.WindowNative.GetWindowHandle(this)); UpdateWindowClip(); UpdateFloatingCaption();
        });
    }
    void RootSizeChanged(object sender, SizeChangedEventArgs e) { if (!destroyed) { UpdateWindowClip(); UpdateFloatingCaption(); } }
    internal void Toggle()
    {
        GetCursorPos(out var point);
        if (ModalActive || DragActive) return;
        if (IsOpen)
        {
            var bounds = CurrentBounds();
            if (FlyoutTopmost || flyoutLayer?.RecallIfCovered(bounds.X, bounds.Y, bounds.Width, bounds.Height) != true) Hide();
            return;
        }
        // The same taskbar press may have just dismissed the panel via the outside watcher.
        if (DismissedByPress(GetMessageTime(), point)) return;
        Open();
    }
    internal void Open(bool activate = true, bool forDrop = false)
    {
        if (destroyed) return;
        openedForDrop = forDrop && !detached && !IsOpen; dragLeave.Stop();
        if (!IsOpen) Position();
        ApplyAppearance(); ShowPanel(activate); ResumeThumbnails();
        StartLibraryMonitoring();
        if (!App.Station.LibraryOffline && App.Station.LoadWarning is {} warning) Notify(warning, true);
        else if (!App.Station.LibraryOffline && App.Settings.Warning is {} settingsWarning) Notify(settingsWarning, true);
    }
    void Position()
    {
        if (flyoutAnchor?.XamlRoot is { } anchorRoot)
            taskbarOwner = Microsoft.UI.Win32Interop.GetWindowFromWindowId(anchorRoot.ContentIslandEnvironment.AppWindowId);
        var settings = App.Settings.Data;
        var x = detached ? settings.ExpandedX : settings.CompactX;
        var y = detached ? settings.ExpandedY : settings.CompactY;
        var monitors = Native.Monitors();
        var savedMonitor = TaskbarFlyout != null && !detached ? Native.MonitorForWindow(taskbarOwner)
            : x is int left && y is int top ? Native.MonitorForPosition(left, top) : IntPtr.Zero;
        var monitor = monitors.FirstOrDefault(m => m.Handle == savedMonitor) ?? monitors.First(m => m.Primary);
        var bounds = RestoreBounds(settings, detached, monitor.Info.Work, monitor.Dpi / 96d,
            docked: TaskbarFlyout != null && !detached);
        positioning = true;
        try { ApplyBounds(bounds); appliedBounds = CurrentBounds(); }
        finally { positioning = false; }
    }
    internal static RectInt32 RestoreBounds(HaloSettings settings, bool detached, Native.Rect work, double scale, bool docked = false)
    {
        var x = detached ? settings.ExpandedX : settings.CompactX;
        var y = detached ? settings.ExpandedY : settings.CompactY;
        // In compact mode the taskbar still chooses the display. A resize may
        // adjust the left or bottom boundary, so keep both on that display.
        if (docked && (x is not int left || y is not int top || left < work.Left || left >= work.Right
            || top < work.Top || top >= work.Bottom))
        {
            x = null;
            y = null;
        }
        int availableWidth = Math.Max(1, work.Right - work.Left), availableHeight = Math.Max(1, work.Bottom - work.Top);
        int gap = x.HasValue && y.HasValue ? 0
            : Math.Min((int)Math.Round(12 * scale), (Math.Min(availableWidth, availableHeight) - 1) / 2);
        int width = Math.Clamp((int)Math.Round((detached ? settings.ExpandedWidth : settings.Width) * scale), 1, availableWidth - (docked ? 0 : 2 * gap));
        int height = Math.Clamp((int)Math.Round((detached ? settings.ExpandedHeight : settings.Height) * scale), 1, availableHeight - (docked ? 0 : 2 * gap));
        int horizontalGap = Math.Min(gap, (availableWidth - width) / 2);
        int verticalGap = Math.Min(gap, (availableHeight - height) / 2);
        int defaultLeft = detached ? work.Left + (availableWidth - width) / 2 : work.Left + horizontalGap;
        int defaultTop = detached ? work.Top + (availableHeight - height) / 2 : work.Bottom - verticalGap - height;
        return new(Math.Clamp(x ?? defaultLeft, work.Left + horizontalGap, work.Left + availableWidth - horizontalGap - width),
            Math.Clamp(y ?? defaultTop, work.Top + verticalGap, work.Top + availableHeight - verticalGap - height), width, height);
    }
    RectInt32 CurrentBounds() => TaskbarFlyout != null ? flyoutBounds : new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
    HaloSettings CaptureSize(RectInt32 bounds) {
        double scale = WindowLayoutScale;
        if (TaskbarFlyout != null)
        {
            var monitorHandle = detached ? Native.MonitorForPosition(bounds.X, bounds.Y) : Native.MonitorForWindow(taskbarOwner);
            scale = (Native.Monitors().FirstOrDefault(monitor => monitor.Handle == monitorHandle)?.Dpi ?? 96) / 96d;
        }
        return SaveBounds(App.Settings.Data, bounds, detached, scale);
    }
    internal static HaloSettings SaveBounds(HaloSettings settings, RectInt32 bounds, bool detached, double scale)
    {
        double width = Math.Clamp(bounds.Width / scale, 280, 1600), height = Math.Clamp(bounds.Height / scale, 200, 1400);
        return detached ? settings with { ExpandedWidth = width, ExpandedHeight = height, ExpandedX = bounds.X, ExpandedY = bounds.Y }
            : settings with { Width = width, Height = height, CompactX = bounds.X, CompactY = bounds.Y };
    }
    void SaveSize()
    {
        saveSize.Stop();
        if (destroyed || appliedBounds is not { } previous || App.Settings.ReadOnly) return;
        var current = CurrentBounds();
        if (Native.Same(previous, current)) return;
        try { App.Settings.SaveSoon(CaptureSize(current)); appliedBounds = current; }
        catch (Exception e) { Notify(e.Message, true); }
    }
    internal void SetDetached(bool value)
    {
        if (destroyed || changingMode || ShowingSettings || DragActive || resizing || value == detached) return;
        if (TaskbarFlyout is null) CloseFolderBrowser();
        var restoreScroll = TaskbarFlyout != null ? PreserveVisibleItem(folderBrowser?.GridForWindowLayout ?? files) : null;
        ChangeWindowMode(() => {
            SaveSize(); App.Settings.SaveSoon(App.Settings.Data with { Detached = value }); detached = value;
            Position();
        });
        if (restoreScroll != null) DispatcherQueue.TryEnqueue(restoreScroll);
    }
    static Microsoft.UI.Dispatching.DispatcherQueueHandler? PreserveVisibleItem(GridView grid)
    {
        if (Child<ScrollViewer>(grid) is not { } scroll) return null;
        double offset = scroll.VerticalOffset;
        var anchor = grid.Items.OfType<GridViewItem>().Where(card => card.IsLoaded && card.ActualHeight > 0)
            .Select(card => (Card: card, Y: card.TransformToVisual(scroll).TransformPoint(new()).Y))
            .Where(item => item.Y > -item.Card.ActualHeight && item.Y < scroll.ActualHeight)
            .OrderBy(item => item.Y).FirstOrDefault();
        return () =>
        {
            grid.UpdateLayout();
            double target = anchor.Card is { IsLoaded: true }
                ? scroll.VerticalOffset + anchor.Card.TransformToVisual(scroll).TransformPoint(new()).Y - anchor.Y : offset;
            scroll.ChangeView(null, Math.Clamp(target, 0, scroll.ScrollableHeight), null, true);
        };
    }
    void ChangeWindowMode(Action update, bool show = false)
    {
        using var lease = KeepOpen(); changingMode = true; SuspendFloatingCaption(); RestorePanelInput();
        try {
            openedForDrop = false; dragLeave.Stop();
            update();
            ApplyAppearance(); ApplyGridPositions(); if (show || IsOpen && TaskbarFlyout is null) ShowPanel();
        } catch (Exception e) { Notify(e.Message, true); }
        finally {
            DispatcherQueue.TryEnqueue(() => {
                if (destroyed) return; root.UpdateLayout(); if (IsOpen) RestorePanelInput(); changingMode = false; ResumeFloatingCaption();
            });
        }
    }
    internal async Task VerifyRepeatedSwitches() {
        bool initial = detached, originallyPinned = App.Settings.Data.Pinned; if (!originallyPinned) TogglePin();
        var selection = Selected().Select(i => i.Id).ToArray(); var items = files.Items.Cast<GridViewItem>().ToArray();
        for (int i = 0; i < 50; i++) {
            bool expected = !detached;
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(expand);
            ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
            await Task.Delay(40); await VerifyFloatingCaption(expected);
            if (detached != expected || changingMode || !root.IsHitTestVisible || !Native.IsWindowEnabled(WinRT.Interop.WindowNative.GetWindowHandle(this)) || !expand.IsEnabled || !pin.IsEnabled || !close.IsEnabled) throw new Exception($"窗口切换后状态异常：轮次={i}, 模式={detached}/{expected}, 切换中={changingMode}, XAML={root.IsHitTestVisible}, 窗口={Native.IsWindowEnabled(WinRT.Interop.WindowNative.GetWindowHandle(this))}, 按钮={expand.IsEnabled}/{pin.IsEnabled}/{close.IsEnabled}");
            if (i % 10 == 9) { TogglePin(); TogglePin();
                ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(close).GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
                await Task.Delay(35); Open(); await Task.Delay(220);
            }
        }
        if (!originallyPinned) TogglePin();
        var currentItems = files.Items.Cast<GridViewItem>().ToArray();
        var currentSelection = Selected().Select(i => i.Id).ToArray();
        if (detached != initial || !items.SequenceEqual(currentItems) || !selection.SequenceEqual(currentSelection))
            throw new Exception($"反复切换改变了网格或选择。模式={detached}/{initial}，卡片数量={currentItems.Length}/{items.Length}，卡片实例相同={items.SequenceEqual(currentItems)}，选择数量={currentSelection.Length}/{selection.Length}，选择集合相同={selection.ToHashSet().SetEquals(currentSelection)}。");
        if (!ContentBlankTarget(root, root) || ContentBlankTarget(expand, root) || ContentBlankTarget(pin, root) || ContentBlankTarget(close, root) || ContentBlankTarget((DependencyObject)files.Items[0], root) || ContentBlankTarget(resizeGrips[0], root)) throw new Exception("空白取消选择命中范围不正确。");
    }
    internal void TogglePin()
    {
        if (destroyed || DragActive || !PendingSettingsSave.IsCompleted) { ApplyAppearance(); return; }
        try {
            App.Settings.SaveSoon(App.Settings.Data with { Pinned = !App.Settings.Data.Pinned });
            if (ShowingSettings) settingsPage!.SyncPin();
            ApplyAppearance();
        }
        catch (Exception e) { ApplyAppearance(); Notify(e.Message, true); }
    }
    protected override void OnHiding()
    {
        selectionMarquee.Cancel();
        CloseFolderBrowser(restoreFocus: false);
        if (ShowingSettings) settingsPage!.ClearPreviews();
        CancelPointerDrag();
        ClearNotification(); HideSearch(); libraryMonitor.Stop(); SaveSize(); thumbnails.Cancel(); dragLeave.Stop(); EndOrdering(); openedForDrop = false;
    }
    protected override void OnHidden()
    {
        if (!ShowingSettings) return;
        ChangeWindowMode(() => {
            RestoreStationView();
            appliedBounds = null;
        });
    }
    internal void ScheduleDragDismiss() { if (openedForDrop) { dragLeave.Stop(); dragLeave.Start(); } }
    internal IDisposable Retain() => KeepOpen();
    internal static bool Supports(DataPackageView data) => StationTransfer.Supported(data) || data.Contains(StandardDataFormats.Bitmap);
    internal async Task<bool> Receive(DataPackageView view)
    {
        using var lease = KeepOpenForDrag();
        StationInput[]? chosen = null;
        try {
            NotifyProgress("正在复制到 Halo，完成后即可编辑副本…");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            using var import = await StationTransfer.Read(view, App.Station.AttachmentDirectory, timeout.Token);
            chosen = await ChooseLinkBatch(import.Inputs); if (chosen == null) { Notify("已取消存入"); return false; }
            var result = await App.Station.ImportCopies(chosen, timeout.Token);
            ReportImportFeedback();
            Notify($"已存入 {result.Added} 项" + (result.Duplicates > 0 ? $"，跳过 {result.Duplicates} 项重复内容" : "")); return true;
        } catch (Exception e) {
            string[] names = chosen?.Where(item => item.Kind is StationKind.File or StationKind.Folder)
                .Select(item => Path.GetFileName(item.Value)).Distinct().Take(5).ToArray() ?? [];
            ReportImportFailure(ImportFailure(e, names.Length == 0 ? null : string.Join("、", names)));
            Notify(e.Message, true);
            return false;
        }
    }
    void ResumeThumbnails()
    {
        if (App.Station.LibraryOffline) return;
        if (thumbnails.IsCancellationRequested) { thumbnails.Dispose(); thumbnails = new(); }
        foreach (GridViewItem container in files.Items) if (container.Content is ContentControl grip && grip.Tag is Preview preview && grip.IsLoaded)
            _ = LoadPreview(preview, thumbnails.Token);
    }
    internal sealed record Preview(StationItem Item, Image Image, FontIcon Fallback, ContentControl Grip)
    {
        internal bool Loaded;
        internal IStorageItem? DragFile;
    }
    internal void Rebuild()
    {
        if (destroyed) return;
        thumbnails.Cancel(); thumbnails.Dispose(); thumbnails = new();
        var scroll = Child<ScrollViewer>(files); double offset = scroll?.VerticalOffset ?? 0;
        bool restoreGridFocus = files.XamlRoot != null && FromGrid(files, FocusManager.GetFocusedElement(files.XamlRoot) as DependencyObject);
        var selected = Selected().Select(i => i.Id).ToHashSet(); files.Items.Clear();
        var ordered = App.Station.Ordered(App.Settings.Data.NewestFirst);
        int size = GridAppearance.IconSize;
        foreach (var item in ordered)
        {
            var (container, preview) = CreateItemCard(item, size);
            var grip = preview.Grip;
            ApplyCardMetrics(container);
            WireCardSelection(grip, container);
            container.Loaded += (_, _) => interactionColors.ApplySelection(container);
            files.Items.Add(container); if (selected.Contains(item.Id)) files.SelectedItems.Add(container);
            grip.Loaded += (_, _) => { if (IsOpen) _ = LoadPreview(preview, thumbnails.Token); };
            grip.DoubleTapped += async (_, e) => { e.Handled = true; if ((Native.GetAsyncKeyState(0x11) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x10) & 0x8000) != 0) return; await Run(() => OpenItem(item)); };
            container.ContextRequested += (_, e) => { e.Handled = true; SelectContextCard(container); ShowItemMenu(container, e); };
            IDisposable? dragLease = null;
            LibraryDrag? nativeDrag = null;
            grip.DragStarting += async (_, e) => {
                if (ModalActive || DragActive && !outgoingHandoff || changingMode || resizing || ShowingSettings) { e.Cancel = true; return; }
                if (!files.SelectedItems.Contains(container)) { files.SelectedItems.Clear(); files.SelectedItems.Add(container); }
                var items = Selected();
                var preparation = preparedDrag is {} pending && pending.Items.SequenceEqual(items) ? pending.Files : null;
                preparedDrag = null;
                dragAnchor = item.Id; movingIds = items.Select(i => i.Id).ToHashSet();
                EndCardPress();
                dragLease = KeepOpenForDrag();
                nativeDrag = BeginNativeLibraryDrag(e.Data, App.Station.LibraryRoot, items, item.Id);
                UpdateSelectionDetails();
                e.AllowedOperations = DataPackageOperation.Copy;
                var image = ApplyOutgoingDragPreview(e.DragUI);
                var fill = StationTransfer.FillMany(e.Data, items, App.Station.AttachmentDirectory, exportNoteFiles: true, preparedFiles: preparation);
                var deferral = image.IsCompleted && fill.IsCompleted ? null : e.GetDeferral();
                try {
                    await image;
                    await fill;
                }
                catch (Exception error) {
                    StationTransfer.Identify(e.Data, items);
                    e.Data.RequestedOperation = DataPackageOperation.Copy;
                    Notify("无法交付原内容，仍可在面板内排序：" + error.Message, true);
                }
                finally { deferral?.Complete(); }
            };
            grip.DropCompleted += (_, e) => {
                if (dragLease == null) return;
                bool handledInside = nativeDrag?.HandledInside == true;
                EndNativeLibraryDrag(nativeDrag);
                nativeDrag = null;
                dragLease.Dispose(); dragLease = null; EndOrdering();
                UpdateSelectionDetails();
                if (!handledInside) Notify(e.DropResult == DataPackageOperation.None ? "已取消拖动，记录保留" : "已交给目标窗口，记录保留");
            };
        }
        ApplyGridPositions();
        UpdateStationContent();
        if (IsOpen) ResumeThumbnails();
        if (restoreGridFocus && !ShowingSettings && folderBrowser == null) FocusGridSelection(files);
        if (scroll != null && offset > 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (!destroyed) scroll.ChangeView(null, Math.Min(offset, scroll.ScrollableHeight), null, true); });
    }
    internal static (GridViewItem Card, Preview Preview) CreateItemCard(StationItem item, int size, bool showReferenceBadge = true, string? folderColor = null)
    {
        var glyph = PanelTheme.Glyph(item.Kind switch { StationKind.Folder => "\uE8B7", StationKind.Link => "\uE71B", StationKind.Text => "\uE8A5", _ => "\uE8A5" }, size * .72);
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform };
        if (item.Kind == StationKind.Folder) _ = FolderIcon.Apply(image, folderColor ?? App.Station.FolderColorOverride(item) ?? App.Appearance.FolderColor, glyph);
        var previewHost = new Grid { Width = size, Height = size }; previewHost.Children.Add(glyph); previewHost.Children.Add(image);
        string title = item.Title.Length > 0 ? item.Title : item.Kind is StationKind.File or StationKind.Folder ? Path.GetFileName(item.Value) : item.Value;
        if (string.IsNullOrWhiteSpace(title)) title = item.Value;
        if (showReferenceBadge && !item.Owned && item.Kind is StationKind.File or StationKind.Folder) previewHost.Children.Add(new Border { Background = new SolidColorBrush(Colors.DimGray), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Child = new TextBlock { Text = "引用", FontSize = 10, Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(3, 0, 3, 0) } });
        if (RichContent.Label(item) is { Length: > 0 } badge) previewHost.Children.Add(new Border { Background = new SolidColorBrush(ColorHelper.FromArgb(210, 40, 80, 100)), CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right, Child = new TextBlock { Text = badge, FontSize = 9, Foreground = new SolidColorBrush(Colors.White), Margin = new Thickness(3, 0, 3, 0) } });
        var content = new StackPanel { Spacing = 4, Padding = new Thickness(6) };
        content.Children.Add(previewHost);
        content.Children.Add(new TextBlock { Text = title, FontSize = 12, LineHeight = 15, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, Height = 30 });
        var grip = new ContentControl { Content = content, CanDrag = true, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(0) };
        var preview = new Preview(item, image, glyph, grip); grip.Tag = preview;
        ToolTipService.SetToolTip(grip, (item.Owned ? "Halo 独立内容\n" : item.Kind is StationKind.File or StationKind.Folder ? "原文件引用（打开时可复制到 Halo）\n" : "Halo 内容\n") + item.Value); AutomationProperties.SetName(grip, title);
        var container = new GridViewItem { Content = grip, Tag = item, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, CornerRadius = new CornerRadius(8) };
        return (container, preview);
    }

    Task LoadPreview(Preview preview, CancellationToken token) => LoadPreview(preview, thumbnailGate, token);
    internal static async Task LoadPreview(Preview preview, SemaphoreSlim thumbnailGate, CancellationToken token)
    {
        if (preview.Loaded || preview.Item.Kind is not (StationKind.File or StationKind.Folder)) return;
        bool held = false;
        try {
            await thumbnailGate.WaitAsync(token); held = true; token.ThrowIfCancellationRequested();
            if (preview.Item.Kind == StationKind.File && RichContent.IsShortcut(preview.Item.Value)) { await LoadShortcutIcon(preview); preview.Loaded = true; return; }
            var item = await StationTransfer.Storage(preview.Item); token.ThrowIfCancellationRequested();
            if (preview.Item.Owned) preview.DragFile = item;
            if (item is not StorageFile file) { preview.Loaded = true; return; }
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 128, ThumbnailOptions.ResizeThumbnail).AsTask(token);
            if (thumb == null) return;
            var bitmap = new BitmapImage { DecodePixelWidth = 128 }; await bitmap.SetSourceAsync(thumb); token.ThrowIfCancellationRequested();
            preview.Image.Source = bitmap; preview.Fallback.Visibility = Visibility.Collapsed; preview.Loaded = true;
        } catch (OperationCanceledException) { }
        catch { if (!token.IsCancellationRequested) { preview.Grip.Opacity = .45; ToolTipService.SetToolTip(preview.Grip, "原位置暂不可用：" + preview.Item.Value); } }
        finally { if (held) thumbnailGate.Release(); }
    }
    IEnumerable<GridViewItem> SelectedCards() => files.Items.Cast<GridViewItem>().Where(files.SelectedItems.Contains);
    internal StationItem[] Selected() => SelectedCards().Select(i => (StationItem)i.Tag).ToArray();
    async Task Run(Func<Task> action) { using var lease = KeepOpen(); try { await action(); } catch (Exception e) { Notify(e.Message, true); } }
    internal MenuFlyout Menu() { var menu = new MenuFlyout(); IDisposable? lease = null; menu.Opening += (_, _) => lease = KeepOpen(); menu.Closed += (_, _) => { lease?.Dispose(); lease = null; }; return menu; }
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    internal IDisposable RetainFolderDrag() => KeepOpenForDrag();

    async Task OpenFolderBrowser(StationItem item, string? location = null, Guid? locateId = null)
    {
        if (folderBrowser != null || ShowingSettings || changingMode || resizing || destroyed) return;
        var browser = new FolderBrowser(this, App.Station, item, GridAppearance);
        focusBeforeFolder = FocusManager.GetFocusedElement(root.XamlRoot) as Control;
        SuspendFloatingCaption();
        folderBrowser = browser;
        if (TaskbarFlyout != null) Grid.SetRow(browser, 1);
        else Grid.SetRowSpan(browser, root.RowDefinitions.Count);
        root.Children.Add(browser);
        if (location == null) await browser.Open();
        else {
            await browser.Navigate(location, locateId: locateId);
            if (folderBrowser == browser && browser.CurrentDirectory == null) {
                CloseFolderBrowser(browser);
                throw new IOException("无法定位项目，请检查文件夹是否仍可访问。");
            }
        }
    }

    internal void CloseFolderBrowser(FolderBrowser? expected = null, bool restoreFocus = true)
    {
        if (folderBrowser is not { } browser || expected != null && browser != expected) return;
        folderBrowser = null;
        browser.DisposeBrowser();
        root.Children.Remove(browser);
        if (!destroyed) {
            ResumeFloatingCaption();
            if (restoreFocus && (focusBeforeFolder == null || !focusBeforeFolder.Focus(FocusState.Programmatic)))
                files.Focus(FocusState.Programmatic);
        }
        focusBeforeFolder = null;
    }
    async Task OpenItem(StationItem item)
    {
        if (!item.Owned && item.Kind is StationKind.File or StationKind.Folder) {
            var choice = await ShowPanelDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "这是原文件引用", Content = "编辑原文件会影响来源。建议先复制到 Halo。", PrimaryButtonText = "复制后打开", SecondaryButtonText = "打开原文件", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary });
            if (choice == ContentDialogResult.None) return; if (choice == ContentDialogResult.Primary) item = await App.Station.OwnReference(item.Id);
        }
        if (item.Kind == StationKind.Link) { if (!await Launcher.LaunchUriAsync(new Uri(item.Value))) throw new IOException("无法打开链接。"); }
        else if (item.Kind == StationKind.Text) await EditDesktopText(item);
        else if (item.Kind == StationKind.Folder) await OpenFolderBrowser(item);
        else await OpenStoredFile(item);
    }
    internal static async Task OpenStoredFile(StationItem item)
    {
        if (RichContent.IsShortcut(item.Value)) {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Value) { UseShellExecute = true });
            return;
        }
        if (!await Launcher.LaunchFileAsync((StorageFile)await StationTransfer.Storage(item)))
            throw new IOException("无法打开该项目，请检查原位置或默认应用。");
    }
    internal static async Task OpenInExplorer(StationItem item)
    {
        if (!await Launcher.LaunchFolderAsync((StorageFolder)await StationTransfer.Storage(item)))
            throw new IOException("无法在资源管理器中打开此文件夹。");
    }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Native.Point point);
    [DllImport("user32.dll")] static extern uint GetMessageTime();
}

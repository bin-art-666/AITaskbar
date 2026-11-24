using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.DragDrop;
using Windows.Foundation;
using Windows.System;

namespace Halo;

internal sealed partial class FolderBrowser : Grid
{
    readonly StationWindow owner;
    readonly StationStore store;
    readonly StationItem folder;
    readonly Grid root = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    readonly GridView files = new() { SelectionMode = ListViewSelectionMode.Extended, IsMultiSelectCheckBoxEnabled = false, Padding = new(10), Background = new SolidColorBrush(Colors.Transparent) };
    internal GridView GridForWindowLayout => files;
    readonly BreadcrumbBar breadcrumbs = new();
    readonly Button desktopDropTarget = new() { Content = "中转站", Visibility = Visibility.Collapsed, IsTabStop = false, IsHitTestVisible = false };
    readonly Button back = WindowControls.IconButton("\uE72B", "关闭文件夹");
    readonly TextBox folderSearch = new() { PlaceholderText = "搜索当前文件夹", Visibility = Visibility.Collapsed };
    readonly SelectionNameOverlay selectionDetails;
    readonly GridMarqueeSelection selectionMarquee;
    readonly TextBlock empty = new() { Text = "此文件夹为空", Opacity = .65, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock message = new() { FontSize = 12, Padding = new(10, 6, 10, 6), TextWrapping = TextWrapping.Wrap };
    readonly Button undoButton = new() { Margin = new(0, 3, 6, 3), Visibility = Visibility.Collapsed };
    FolderUndo? lastUndo;
    readonly Border notice = new() { CornerRadius = new(10), Margin = new(10), VerticalAlignment = VerticalAlignment.Bottom,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim thumbnails = new(4);
    readonly Dictionary<string, (double Offset, string[] Selection)> history = new(StringComparer.OrdinalIgnoreCase);
    StationInteractionColors colors;
    StationGridAppearance metrics;
    string? directory;
    StationItem[] folderEntries = [];
    Guid? selectionAnchor;
    bool busy, dragging, closed;

    readonly Border shade = new();
    IDisposable? dragLease;
    LibraryDrag? nativeLibraryDrag;
    bool refreshAfterNativeDrop;

    internal FolderBrowser(StationWindow owner, StationStore store, StationItem folder, StationGridAppearance metrics)
    {
        this.owner = owner;
        this.store = store;
        this.folder = folder;
        this.metrics = metrics;
        var header = new Grid { Margin = new(8, 6, 8, 0), ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(desktopDropTarget, 1);
        Grid.SetColumn(breadcrumbs, 2);
        PanelTheme.StyleTextAction(desktopDropTarget);
        breadcrumbs.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(back);
        header.Children.Add(desktopDropTarget);
        header.Children.Add(breadcrumbs);
        shade.PointerPressed += (_, e) => {
            if (!e.GetCurrentPoint(shade).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Dismiss();
        };
        root.Margin = new(16);
        root.MaxWidth = 720;
        root.MaxHeight = 560;
        TabFocusNavigation = KeyboardNavigationMode.Cycle;
        Children.Add(shade);
        Children.Add(root);
        back.Click += async (_, _) => await Back();
        breadcrumbs.ItemClicked += async (_, e) => {
            if (e.Item is Crumb crumb) await Navigate(crumb.Path);
        };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(files, 1); Grid.SetRow(empty, 1); Grid.SetRow(notice, 1);
        var noticePanel = new Grid();
        noticePanel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        noticePanel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        SetColumn(undoButton, 1);
        noticePanel.Children.Add(message); noticePanel.Children.Add(undoButton);
        notice.Child = noticePanel;
        ToolTipService.SetToolTip(undoButton, "撤销（Ctrl+Z）");
        undoButton.Click += async (_, _) => await Run(UndoLastOrganization);
        var navigation = new StackPanel { Spacing = 6 };
        folderSearch.Margin = new Thickness(10, 0, 10, 0);
        AutomationProperties.SetName(folderSearch, "搜索当前文件夹");
        folderSearch.RegisterPropertyChangedCallback(TextBox.TextProperty, (_, _) => {
            if (!busy && !closed) RenderFolderEntries(SelectedEntries().Select(item => item.Value).ToArray());
        });
        navigation.Children.Add(header); navigation.Children.Add(folderSearch);
        root.Children.Add(navigation); root.Children.Add(files); root.Children.Add(empty); root.Children.Add(notice);
        selectionDetails = new(root, files, notice, () => closed || dragging || localDrag || selectionMarquee?.IsActive == true);
        files.ItemsPanel = (ItemsPanelTemplate)Application.Current.Resources["StationGridItemsPanel"];
        InitializeOrdering();
        InitializeDragNavigation();
        root.ActualThemeChanged += (_, _) => RefreshAppearance(this.metrics);
        root.SizeChanged += (_, _) => PanelTheme.ClipDesktopBackdrop(root);
        InitializeCommands();
        selectionMarquee = new(root, files, notice, () => CanChangeView && !owner.ModalActive && !owner.DragActive,
            owner.RetainFolderDrag, ClearSelection, anchor => selectionAnchor = anchor, selectionDetails.UpdateSelectionDetails);
        back.Loaded += (_, _) => back.Focus(FocusState.Programmatic);
        var backKey = new KeyboardAccelerator { Key = VirtualKey.Left, Modifiers = VirtualKeyModifiers.Menu };
        backKey.Invoked += async (_, e) => {
            if (!CanChangeView || owner.ModalActive || owner.DragActive) return;
            e.Handled = true;
            await Back();
        };
        root.KeyboardAccelerators.Add(backKey);
        AddHandler(DragEnterEvent, new DragEventHandler(TrackDrag), true);
        AllowDrop = true;
        AddHandler(DragOverEvent, new DragEventHandler(TrackDrag), true);
        AddHandler(DropEvent, new DragEventHandler(HandleDrop), true);
        DragLeave += (_, e) => {
            if (!localDrag && !ShowingDesktopDuringDrag && !InsideStation(e.GetPosition(this))) {
                StopBackHover();
                ClearLibraryDropTarget();
            }
        };
        RefreshAppearance(metrics);
    }

    sealed record Crumb(string Name, string Path)
    {
        public override string ToString() => Name;
    }

    internal async Task Open()
    {
        back.Focus(FocusState.Programmatic);
        if (directory == null) await Navigate(folder.Value);
    }

    internal string? CurrentDirectory => directory;
    internal GridView Files => files;
    internal int BreadcrumbCount => ((IReadOnlyList<Crumb>?)breadcrumbs.ItemsSource)?.Count ?? 0;
    internal Button BackButtonForVerification => back;

    internal void RefreshAppearance(StationGridAppearance appearance)
    {
        if (closed) return;
        metrics = appearance;
        bool dark = root.ActualTheme == ElementTheme.Dark;
        shade.Background = new SolidColorBrush(ColorHelper.FromArgb(dark ? (byte)102 : (byte)64, 0, 0, 0));
        root.Background = new SolidColorBrush(PanelTheme.SolidContentSurface(dark));
        root.CornerRadius = new CornerRadius(App.Appearance.BackplateRadius);
        PanelTheme.ClipDesktopBackdrop(root);
        colors = StationInteractionColors.From(App.Appearance.ButtonColor, App.Appearance.ShowInteractionBorders, dark);
        selectionDetails.ApplyColors(colors);
        selectionMarquee.ApplyColors(colors);
        PanelTheme.ApplyNoticeColors(notice, message);
        PanelTheme.ApplyNoticeColors(backHint, (TextBlock)backHint.Child);
        foreach (GridViewItem card in files.Items) {
            StationWindow.ApplyCardMetrics(card, metrics);
            colors.ApplySelection(card);
            if (((ContentControl)card.Content).Tag is StationWindow.Preview { Item.Kind: StationKind.Folder } preview)
                _ = FolderIcon.Apply(preview.Image, store.FolderColorOverride(preview.Item) ?? App.Appearance.FolderColor, preview.Fallback);
        }
        colors.ApplyBorder(dropFrame, colors.Target, 2);
        libraryDrop.ApplyColors(colors);
        copyFeedback.ApplyColors(colors);
        ApplyPositions();
    }

    internal Task Back()
    {
        if (directory != null && !string.Equals(directory, folder.Value, StringComparison.OrdinalIgnoreCase))
            return Navigate(Path.GetDirectoryName(directory)!);
        Dismiss();
        return Task.CompletedTask;
    }

    internal void Dismiss()
    {
        if (!dragging && !localDrag) owner.CloseFolderBrowser(this);
    }

    internal void DisposeBrowser()
    {
        closed = true;
        StopBackHover();
        selectionMarquee.Cancel();
        lifetime.Cancel();
        lifetime.Dispose();
        if (!CancelLocalDrag()) cancelPress?.Invoke();
        EndDrag();
    }

    internal void ClearSelection()
    {
        files.SelectedItems.Clear();
        selectionAnchor = null;
    }

    internal async Task Navigate(string path, bool refresh = false, bool preserveNotice = false, LibraryDrag? duringDrag = null, Guid? locateId = null)
    {
        bool CanNavigate() => duringDrag == null ? !dragging && !localDrag
            : ReferenceEquals(duringDrag, localLibraryDrag ?? nativeLibraryDrag);
        if (busy || closed || !CanNavigate() || !refresh && string.Equals(directory, path, StringComparison.OrdinalIgnoreCase)) return;
        busy = true;
        var token = lifetime.Token;
        try {
            var result = await Task.Run(() => store.ReadFolderContents(folder, path, token), token);
            token.ThrowIfCancellationRequested();
            if (!CanNavigate()) return;
            if (locateId.HasValue && !result.Any(item => item.Id == locateId.Value))
                throw new IOException("项目已移动或移除，请重新搜索。");
            bool differentDirectory = !string.Equals(directory, path, StringComparison.OrdinalIgnoreCase);
            bool restoreGridFocus = StationWindow.FromGrid(files, FocusManager.GetFocusedElement(root.XamlRoot) as DependencyObject);
            Remember();
            if (differentDirectory) {
                folderSearch.Visibility = Visibility.Collapsed;
                folderSearch.Text = "";
            }
            directory = path;
            if (!preserveNotice) {
                notice.Visibility = Visibility.Collapsed;
                lastUndo = null;
            }
            var old = history.TryGetValue(path, out var previous) ? previous : (0d, Array.Empty<string>());
            folderEntries = StationStore.FolderDisplayOrder(result, store.SortFolder(folder, path)).ToArray();
            RenderFolderEntries(old.Item2);
            UpdateBreadcrumbs();

            files.UpdateLayout();
            StationWindow.Child<ScrollViewer>(files)?.ChangeView(null, old.Item1, null, true);
            if (locateId.HasValue) StationWindow.SelectLocatedCard(files, locateId.Value, ref selectionAnchor);
            else if (restoreGridFocus && duringDrag == null) StationWindow.FocusGridSelection(files);
        } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!closed) Notify(error.Message); }
        finally { busy = false; }
    }

    bool FilteringFolder => !string.IsNullOrWhiteSpace(folderSearch.Text);

    void ShowFolderSearch()
    {
        folderSearch.Visibility = Visibility.Visible;
        folderSearch.Focus(FocusState.Keyboard);
    }

    void HideFolderSearch()
    {
        folderSearch.Text = "";
        folderSearch.Visibility = Visibility.Collapsed;
        StationWindow.FocusGridSelection(files);
    }

    void RenderFolderEntries(string[] selection)
    {
        files.Items.Clear();
        selectionAnchor = null;
        int index = 0;
        foreach (var item in folderEntries.Where(item => !FilteringFolder || StationWindow.SearchMatches(item, folderSearch.Text)))
            AddCard(item, index++, selection, lifetime.Token);
        ApplyPositions();
        empty.Text = FilteringFolder ? "没有匹配的项目" : "此文件夹为空";
        empty.Visibility = files.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Remember()
    {
        if (directory == null) return;
        history[directory] = (StationWindow.Child<ScrollViewer>(files)?.VerticalOffset ?? 0,
            files.SelectedItems.Cast<GridViewItem>().Select(card => ((StationItem)card.Tag).Value).ToArray());
    }

    void UpdateBreadcrumbs()
    {
        var crumbs = new List<Crumb>();
        for (string? path = directory; path != null; path = Path.GetDirectoryName(path)) {
            string name = Path.GetFileName(path);
            crumbs.Add(new(name.Length > 0 ? name : path, path));
            if (string.Equals(path, folder.Value, StringComparison.OrdinalIgnoreCase)) break;
        }
        crumbs.Reverse();
        breadcrumbs.ItemsSource = crumbs;
        string backLabel = crumbs.Count == 1 ? "关闭文件夹" : "返回上一级";
        AutomationProperties.SetName(back, backLabel);
        ToolTipService.SetToolTip(back, backLabel);

    }

    void AddCard(StationItem item, int index, string[] selection, CancellationToken token)
    {
        var (card, visual) = StationWindow.CreateItemCard(item, metrics.IconSize, showReferenceBadge: false,
            folderColor: store.FolderColorOverride(item) ?? App.Appearance.FolderColor);
        ToolTipService.SetToolTip(visual.Grip, Path.GetFileName(item.Value));
        StationWindow.ApplyCardMetrics(card, metrics);
        StationGridPanel.Position(card, index, 0);
        card.Loaded += (_, _) => colors.ApplySelection(card);
        visual.Grip.Loaded += (_, _) => {
            if (item.Kind == StationKind.File) _ = StationWindow.LoadPreview(visual, thumbnails, token);
        };
        visual.Grip.DoubleTapped += async (_, e) => { e.Handled = true; await OpenEntry(item); };
        WireDrag(card, visual.Grip);
        card.ContextRequested += (_, e) => {
            e.Handled = true;
            SelectContextCard(card);
            ShowMenu(BuildItemMenu(SelectedEntries()), card, e);
        };
        files.Items.Add(card);
        if (selection.Contains(item.Value, StringComparer.OrdinalIgnoreCase)) files.SelectedItems.Add(card);
    }

    Task QuickPreview(StationItem item) => Run(async () => {
        store.ValidateFolderPath(folder, item.Value);
        await owner.PreviewItem(item, () => OpenEntry(item), files, folder);
    });

    internal Task OpenEntry(StationItem item) => item.Kind == StationKind.Folder ? Navigate(item.Value)
        : Run(async () => {
            store.ValidateFolderPath(folder, item.Value);
            if (!folder.Owned && await owner.ShowPanelDialog(new ContentDialog { XamlRoot = root.XamlRoot, Title = "打开原文件？",
                Content = "编辑此文件会影响来源。", PrimaryButtonText = "打开原文件", CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close }) != ContentDialogResult.Primary) return;
            await StationWindow.OpenStoredFile(item);
        });

    internal bool CanReceiveShellFiles => folder.Owned && !store.ReadOnly && directory != null && !busy && !dragging && !localDrag;
    bool Accepts(DataPackageView view) => CanReceiveShellFiles && view.Contains(StandardDataFormats.StorageItems);

    void TrackDrag(object sender, DragEventArgs e)
    {
        if (ShowingDesktopDuringDrag) return;
        e.Handled = true;
        if (StationWindow.HasLibraryDragIdentity(e.DataView) || StationTransfer.IsInternal(e.DataView)) {
            e.AcceptedOperation = DataPackageOperation.None;
            if (!StationWindow.HasLibraryDragIdentity(e.DataView) || owner.ActiveNativeLibraryDrag is not { } drag) return;
            TrackBackHover(e.GetPosition(this), drag);
            var point = e.GetPosition(files);
            bool copy = e.Modifiers.HasFlag(DragDropModifiers.Control);
            UpdateTarget(point, copy);
            var target = ResolveFolderDrop(point, drag, copy);
            if (target == null && !libraryDrop.HasCandidate) {
                if (copy) { e.DragUIOverride.Caption = DragCopyFeedback.InvalidTarget; e.DragUIOverride.IsCaptionVisible = true; }
                return;
            }
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.IsGlyphVisible = copy;
            e.DragUIOverride.Caption = copy ? FolderCopyCaption() : libraryDrop.Armed?.Caption ?? (libraryDrop.HasCandidate ? "停留以移入文件夹"
                : target?.Destination != null ? "松开以移入当前文件夹" : target?.Group != null ? FolderDropIntent.Caption : "调整位置");
            e.DragUIOverride.IsCaptionVisible = true;
            return;
        }
        e.AcceptedOperation = LibraryDropCue.Bounds(root, this).Contains(e.GetPosition(this)) && Accepts(e.DataView)
            ? DataPackageOperation.Copy : DataPackageOperation.None;
    }

    async void HandleDrop(object sender, DragEventArgs e)
    {
        if (ShowingDesktopDuringDrag) return;
        e.Handled = true;
        e.AcceptedOperation = DataPackageOperation.None;
        if (StationWindow.HasLibraryDragIdentity(e.DataView) || StationTransfer.IsInternal(e.DataView)) {
            if (!StationWindow.HasLibraryDragIdentity(e.DataView) || owner.ActiveNativeLibraryDrag is not { } drag) return;
            var target = ResolveFolderDrop(e.GetPosition(files), drag, e.Modifiers.HasFlag(DragDropModifiers.Control));
            StopBackHover();
            ClearLibraryDropTarget();
            if (target == null) return;
            var internalDeferral = e.GetDeferral();
            using var internalLease = owner.RetainFolderDrag();
            try {
                if (!await owner.MatchesNativeLibraryDrag(e.DataView, drag)) return;
                drag.HandledInside = true;
                await CommitFolderDrop(drag, target);
                e.AcceptedOperation = DataPackageOperation.Copy;
            } catch (Exception error) { if (!closed) Notify(error.Message); }
            finally { internalDeferral.Complete(); }
            return;
        }
        if (!LibraryDropCue.Bounds(root, this).Contains(e.GetPosition(this)) || !Accepts(e.DataView)) return;
        var deferral = e.GetDeferral();
        using var lease = owner.RetainFolderDrag();
        try {
            int copied = await CopyDrop(e.DataView);
            e.AcceptedOperation = DataPackageOperation.Copy;
            owner.ReportImportFeedback();
            if (!closed) Notify($"已复制 {copied} 项到当前文件夹");
        } catch (OperationCanceledException) when (closed) { }
        catch (Exception error) {
            owner.ReportImportFailure(StationWindow.ImportFailure(error));
            if (!closed) Notify(error.Message);
        }
        finally { deferral.Complete(); }
    }

    internal async Task<int> CopyDrop(DataPackageView view)
    {
        if (!Accepts(view)) throw new IOException("当前文件夹无法接收拖放。");
        string target = directory!;
        var token = lifetime.Token;
        int copied;
        busy = true;
        try {
            using var import = await StationTransfer.ReadFiles(view, store.AttachmentDirectory, token);
            copied = await store.CopyIntoFolder(folder, target, import.Inputs, token);
        } finally { busy = false; }
        await Navigate(target, refresh: true);
        return copied;
    }
    internal async Task ReceiveShellFiles(DataPackageView view)
    {
        int copied = await CopyDrop(view);
        owner.ReportImportFeedback();
        if (!closed) Notify($"已复制 {copied} 项到当前文件夹");
    }

    async Task StartDrag(GridViewItem card, DragStartingEventArgs e)
    {
        if (awaitingNativeDrag is { } source) { await StartNativePayload(e, source); return; }
        if (busy || owner.ModalActive || dragging) { e.Cancel = true; return; }
        if (!card.IsSelected) files.SelectedItem = card;
        if (directory == null) { e.Cancel = true; return; }
        RememberDragOrigin();
        nativeFinished = false;
        await StartNativePayload(e, new(new(folder.Id, directory), SelectedEntries(), ((StationItem)card.Tag).Id));
    }

    async Task StartNativePayload(DragStartingEventArgs e, LibraryDrag source)
    {
        if (closed || dragging) { e.Cancel = true; return; }
        var deferral = e.GetDeferral();
        dragging = true;
        dragLease = owner.RetainFolderDrag();
        selectionDetails.UpdateSelectionDetails();
        try {
            e.AllowedOperations = DataPackageOperation.Copy;
            nativeLibraryDrag = owner.BeginNativeLibraryDrag(e.Data, source.Source, source.Items, source.Anchor);
            await ApplySystemPreview(e.DragUI);
            await FillCopy(e.Data, source.Items);
        } catch (Exception error) {
            e.Cancel = true;
            await FinishNativeNavigation(DataPackageOperation.None);
            if (!closed) Notify(error.Message);
        }
        finally { deferral.Complete(); }
    }

    void WireDrag(GridViewItem card, ContentControl grip)
    {
        uint? pressed = null;
        bool collapseOnRelease = false, control = false;
        Point origin = default, grab = default;
        grip.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) => {
            var point = e.GetCurrentPoint(grip);
            if (point.PointerDeviceType != PointerDeviceType.Mouse) { grip.CanDrag = true; return; }
            if (!point.Properties.IsLeftButtonPressed || point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
                || busy || dragging || localDrag || owner.ModalActive) return;
            e.Handled = true;
            card.Focus(FocusState.Pointer);
            control = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
            bool shift = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
            collapseOnRelease = card.IsSelected && !shift && (control || files.SelectedItems.Count > 1);
            if (!collapseOnRelease) StationWindow.SelectGridCard(files, card, control, shift, ref selectionAnchor);
            grip.CanDrag = false;
            pressed = e.Pointer.PointerId;
            origin = e.GetCurrentPoint(this).Position;
            var thumbnail = (FrameworkElement)((StackPanel)grip.Content).Children[0];
            var relative = e.GetCurrentPoint(thumbnail).Position;
            double padding = StationWindow.DragPreviewFramePadding;
            grab = new(Math.Clamp((relative.X + padding) / (metrics.IconSize + 2 * padding), 0, 1),
                Math.Clamp((relative.Y + padding) / (metrics.IconSize + 2 * padding), 0, 1));
            var pointer = e.Pointer;
            cancelPress = () => { pressed = null; grip.ReleasePointerCapture(pointer); };
            if (!grip.CapturePointer(pointer)) { pressed = null; cancelPress = null; grip.CanDrag = true; }
        }), true);
        grip.AddHandler(PointerMovedEvent, new PointerEventHandler((_, e) => {
            if (grip.CanDrag || pressed != e.Pointer.PointerId) return;
            var position = e.GetCurrentPoint(this);
            if (!position.Properties.IsLeftButtonPressed) return;
            var mouse = position.Position;
            if (Math.Abs(mouse.X - origin.X) < 4 && Math.Abs(mouse.Y - origin.Y) < 4) return;
            e.Handled = true;
            pressed = null;
            cancelPress = null;
            grip.ReleasePointerCapture(e.Pointer);
            if (!CarryPointer(e.Pointer)) return;
            BeginLocalDrag(card, mouse, grab);
            if (!localDrag) { ReleaseCarriedPointer(); return; }
            originGrip = grip;
            bool copy = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
            copyFeedback.Watch(copy);
            TrackBackHover(mouse, localLibraryDrag!);
            TrackCarriedDrop(copy);
        }), true);
        grip.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, e) => {
            if (pressed != e.Pointer.PointerId) return;
            pressed = null;
            cancelPress = null;
            grip.ReleasePointerCapture(e.Pointer);
            if (collapseOnRelease)
                StationWindow.SelectGridCard(files, card, control, false, ref selectionAnchor);
        }), true);
        grip.PointerCaptureLost += Cancel;
        grip.PointerCanceled += Cancel;
        grip.DragStarting += async (_, e) => await StartDrag(card, e);
        grip.DropCompleted += async (_, e) => await FinishNativeNavigation(e.DropResult);

        void Cancel(object sender, PointerRoutedEventArgs e)
        {
            if (pressed != e.Pointer.PointerId) return;
            pressed = null;
            cancelPress = null;
            if (localDrag) FinishLocalDrag();
        }
    }

    void EndDrag()
    {
        dragging = false;
        owner.EndNativeLibraryDrag(nativeLibraryDrag);
        nativeLibraryDrag = null;
        StopBackHover();
        ClearLibraryDropTarget();
        dragLease?.Dispose();
        dragLease = null;
        systemPreview?.Dispose();
        systemPreview = null;
        ClearDragVisual();
        if (!closed) selectionDetails.UpdateSelectionDetails();
        if (refreshAfterNativeDrop) {
            refreshAfterNativeDrop = false;
            if (!ShowingDesktopDuringDrag) _ = RefreshAfterLibraryMove(preserveNotice: true);
        }
    }

    internal async Task FillCopy(DataPackage package, StationItem[] items)
    {
        if (items.Length == 0) throw new IOException("没有选中项目。");
        foreach (var item in items) store.ValidateFolderPath(folder, item.Value);
        var storage = await StationTransfer.PrepareDragFiles(items);
        package.SetStorageItems(storage, true);
        package.RequestedOperation = DataPackageOperation.Copy;
    }

    StationItem[] SelectedEntries() => files.SelectedItems.Cast<GridViewItem>().Select(card => (StationItem)card.Tag).DistinctBy(item => item.Id).ToArray();
    async Task Run(Func<Task> action)
    {
        using var lease = owner.Retain();
        try { await action(); }
        catch (Exception error) { if (!closed) Notify(error.Message); }
    }
    internal void ShowImportFailure(string text) => Notify(text);

    void Notify(string text, FolderUndo? undo = null)
    {
        message.Text = text;
        lastUndo = undo;
        undoButton.Content = undo?.Label;
        undoButton.Visibility = undo == null ? Visibility.Collapsed : Visibility.Visible;
        notice.IsHitTestVisible = undo != null;
        notice.Visibility = Visibility.Visible;
    }

    async Task UndoLastOrganization()
    {
        if (!CanWriteFolder) return;
        if (owner.LatestLibraryMove != null) { await owner.UndoLibraryMove(); return; }
        if (lastUndo is not { IsCurrent: null } undo) return;
        await UpdateDirectory(_ => undo.Apply());
        if (!closed) Notify("已撤销整理");
    }

    internal int LibraryColumns => VisibleColumns;
    internal void NotifyLibraryCut(string text) => Notify(text, lastUndo);
    internal void NotifyLibraryMove(string text, FolderUndo? undo) => Notify(text, undo);
    internal async Task RefreshAfterLibraryMove(bool preserveNotice = false)
    {
        if (directory is not { } path || closed) return;
        if (dragging) { refreshAfterNativeDrop = true; return; }
        if (!Directory.Exists(path) || !store.Items.Any(item => item.Id == folder.Id && item.Value == folder.Value)) Dismiss();
        else await Navigate(path, refresh: true, preserveNotice: preserveNotice);
    }
}

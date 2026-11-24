using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using AITaskbar;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.ViewManagement;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Halo;

internal sealed partial class StationWindow
{
    FlyoutWindowLayer? flyoutLayer;
    AIEnter.FlyoutFileDrop? shellFileDrop;
    FrameworkElement? flyoutAnchor;
    nint taskbarOwner;
    nint foregroundHook;
    WinEventCallback? foregroundChanged;
    bool shellDragOver;
    RectInt32 flyoutBounds;
    (ElementTheme Theme, Windows.UI.Color Color)? taskbarPalette;
    RectInt32? movingFlyoutFrom;
    Native.Point moveOrigin;
    Pointer? movePointer;
    IDisposable? moveLease;
    readonly UISettings flyoutVisuals = new();
    readonly AccessibilitySettings flyoutAccessibility = new();

    bool FlyoutTopmost => !detached || App.Settings.Data.Pinned;
    internal bool UsesTaskbarFlyout => TaskbarFlyout != null;
    double WindowLayoutScale => TaskbarFlyout == null
        ? Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d
        : flyoutAnchor?.XamlRoot?.RasterizationScale ?? 1;
    nint InteractionWindow => flyoutLayer is { Handle: not 0 } layer
        ? layer.Handle : WinRT.Interop.WindowNative.GetWindowHandle(this);

    void InitializeTaskbarFlyout(FrameworkElement anchor)
    {
        flyoutAnchor = anchor;
        var flyout = new TaskbarPanelFlyout(anchor, root, new Border(), movable: true);
        TaskbarFlyout = flyout;
        flyoutLayer = new(WinRT.Interop.WindowNative.GetWindowHandle(this));
        int generation = 0;
        flyout.Opened += async () =>
        {
            int opening = ++generation;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(25);
                if (opening != generation || destroyed || !IsOpen || flyout.IsClosing) return;
                nint window = FindHaloPopup();
                if (window == 0) continue;
                flyoutLayer.Attach(window, FlyoutTopmost);
                WatchHaloFlyout(window);
                WatchFlyoutForeground(window);
                RegisterShellFileDrop(window);
                return;
            }
        };
        void Detach()
        {
            generation++;
            if (foregroundHook != 0) UnhookWinEvent(foregroundHook);
            foregroundHook = 0;
            foregroundChanged = null;
            shellDragOver = false;
            shellFileDrop?.Dispose();
            shellFileDrop = null;
            flyoutLayer.Detach();
        }
        flyout.Dismissing += Detach;
        flyout.Closed += Detach;
        InitializeFlyoutMove();
    }

    void WatchHaloFlyout(nint window) => WatchOutsideClicks(window, () =>
    {
        if (IsOpen && !FlyoutTopmost && !ModalActive && !DragActive) flyoutLayer?.Raise();
    });

    void WatchFlyoutForeground(nint popup)
    {
        const uint foregroundEvent = 3; // EVENT_SYSTEM_FOREGROUND, delivered on the UI thread.
        foregroundChanged = (hook, _, foreground, _, _, _, _) =>
        {
            if (hook != foregroundHook || !CanDismiss() || foreground == 0 || OwnsForeground(foreground)) return;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (hook == foregroundHook && CanDismiss() && Native.GetForegroundWindow() == foreground)
                    Hide();
            });
        };
        foregroundHook = SetWinEventHook(foregroundEvent, foregroundEvent, 0, foregroundChanged, 0, 0, 0);

        bool CanDismiss() => IsOpen && detached && DismissOnOutside && !ModalActive && !DragActive
            && !shellDragOver && !TaskbarFlyout!.IsClosing;
        bool OwnsForeground(nint window)
        {
            // The taskbar receives focus during a ring click before its toggle handler runs.
            for (var owner = window; owner != 0; owner = FlyoutWindowLayer.GetLayerWindow(owner, FlyoutWindowLayer.OwnerWindow))
                if (owner == popup || owner == taskbarOwner) return true;
            return false;
        }
    }

    void RegisterShellFileDrop(nint popup)
    {
        var target = new AIEnter.FlyoutFileDrop(
            () => IsOpen && !TaskbarFlyout!.IsClosing && !destroyed && !ShowingSettings && !ModalActive && !DragActive
                && !App.Station.ReadOnly && (folderBrowser is null || folderBrowser.CanReceiveShellFiles),
            dragging => { shellDragOver = dragging; if (dragging) dragLeave.Stop(); else ScheduleDragDismiss(); },
            paths =>
            {
                var browser = folderBrowser;
                var directory = browser?.CurrentDirectory;
                return DispatcherQueue.TryEnqueue(async () => await ReceiveShellFiles(paths, browser, directory));
            },
            StationStore.MaximumCapacity, HandleExtendedDrop);
        if (target.Register(popup) >= 0) shellFileDrop = target;
    }

    bool HandleExtendedDrop(AIEnter.FlyoutDropStage stage, IDataObject? data, AIEnter.Native.Point cursor, uint keys)
    {
        try
        {
            // While this source is in the native drag loop, the re-entering OLE drag is its own operation.
            var own = outgoingHandoff && ActiveNativeLibraryDrag is { Source.RootId: null } drag ? drag : null;
            bool canReceive = IsOpen && !destroyed && !TaskbarFlyout!.IsClosing && !ShowingSettings
                && !ModalActive && !App.Station.ReadOnly && folderBrowser is null;
            if (stage == AIEnter.FlyoutDropStage.Enter)
                return own is not null || data is not null && BrowserDrop.Supports(data);
            if (!canReceive) return false;
            if (own is not null)
            {
                if (own.HandledInside || nativeReturnCancelled) return false;
                var point = FlyoutGridPoint(cursor.X, cursor.Y);
                bool copy = (keys & 8) != 0; // MK_CONTROL
                if (stage == AIEnter.FlyoutDropStage.Over) return UpdateDropTarget(point, copy);
                if (App.Verification) TraceInternalDrop("OLE", point);
                var commit = PrepareInternalDrop(own, point, copy);
                if (commit is null || !DispatcherQueue.TryEnqueue(async () => await Run(commit))) return false;
                own.HandledInside = true;
                return true;
            }
            if (DragActive) return false;
            if (stage == AIEnter.FlyoutDropStage.Over) return true;
            var payload = data is null ? null : BrowserDrop.Read(data);
            return payload is not null && DispatcherQueue.TryEnqueue(async () => await ReceiveBrowserDrop(payload));
        }
        catch (Exception error)
        {
            App.Log(error);
            return false;
        }
    }

    Windows.Foundation.Point FlyoutGridPoint(int x, int y)
    {
        var bounds = CurrentBounds();
        double scale = WindowLayoutScale;
        var grid = files.TransformToVisual(root).TransformPoint(default);
        return new((x - bounds.X) / scale - grid.X, (y - bounds.Y) / scale - grid.Y);
    }

    async Task ReceiveBrowserDrop(BrowserDrop payload)
    {
        using var lease = KeepOpenForDrag();
        using var bitmap = payload.Image is null ? null : new Windows.Storage.Streams.InMemoryRandomAccessStream();
        try
        {
            if (!await Receive(await payload.Package(bitmap))) return;
            openedForDrop = false;
            ConfirmIncomingDrop();
        }
        catch (Exception error) { ReportImportFailure(ImportFailure(error)); Notify(error.Message, true); }
    }

    async Task ReceiveShellFiles(string[] paths, FolderBrowser? browser, string? directory)
    {
        using var lease = KeepOpenForDrag();
        try
        {
            var items = new List<IStorageItem>(paths.Length);
            foreach (string path in paths)
                items.Add(Directory.Exists(path) ? await StorageFolder.GetFolderFromPathAsync(path) : await StorageFile.GetFileFromPathAsync(path));
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetStorageItems(items, true);
            if (browser != folderBrowser || browser?.CurrentDirectory != directory)
                throw new IOException("已切换拖入目标，请重新拖入。");
            if (browser is not null)
                await browser.ReceiveShellFiles(package.GetView());
            else if (await Receive(package.GetView()))
            {
                openedForDrop = false;
                ConfirmIncomingDrop();
            }
        }
        catch (Exception error) { ReportImportFailure(ImportFailure(error)); Notify(error.Message, true); }
    }

    nint FindHaloPopup()
    {
        var center = new Native.Point { X = flyoutBounds.X + flyoutBounds.Width / 2, Y = flyoutBounds.Y + flyoutBounds.Height / 2 };
        var window = Native.GetAncestor(FlyoutWindowAtPoint(center), 2);
        if (window == 0 || window == taskbarOwner || GetWindowThreadProcessId(window, out uint process) == 0
            || process != Environment.ProcessId || FlyoutWindowLayer.GetLayerWindow(window, FlyoutWindowLayer.OwnerWindow) == 0)
            return 0;
        return window;
    }

    void ApplyBounds(RectInt32 bounds)
    {
        if (TaskbarFlyout is not { } flyout) { AppWindow.MoveAndResize(bounds); return; }
        flyoutBounds = bounds;
        root.Width = bounds.Width / WindowLayoutScale;
        root.Height = bounds.Height / WindowLayoutScale;
        flyout.SetScreenPosition(bounds.X, bounds.Y);
        flyoutLayer?.Apply(FlyoutTopmost);
    }

    internal void SetTaskbarPalette(ElementTheme theme, Windows.UI.Color color)
    {
        if (taskbarPalette == (theme, color)) return;
        taskbarPalette = (theme, color);
        if (TaskbarFlyout != null) ApplyTaskbarFlyoutAppearance();
    }

    void ApplyTaskbarFlyoutAppearance()
    {
        var appearance = App.Appearance;
        var theme = taskbarPalette?.Theme ?? ElementTheme.Default;
        bool dark = (theme == ElementTheme.Default ? flyoutAnchor?.ActualTheme : theme) == ElementTheme.Dark;
        bool highContrast = flyoutAccessibility.HighContrast;
        var tint = taskbarPalette?.Color ?? PanelTheme.SolidContentSurface(dark);
        string custom = dark ? appearance.DarkBackplateColor : appearance.LightBackplateColor;
        if (custom.Length != 0) tint = PanelTheme.PaletteColor(UiAppearance.ColorValue(custom, 0));
        bool glass = !highContrast && flyoutVisuals.AdvancedEffectsEnabled;
        var color = highContrast ? flyoutVisuals.GetColorValue(UIColorType.Background) : ReadableTint();
        root.RequestedTheme = highContrast ? ElementTheme.Default : dark ? ElementTheme.Dark : ElementTheme.Light;
        root.CornerRadius = backplate.CornerRadius = new(appearance.BackplateRadius);
        backplate.UpdateAppearance(appearance with { BackplateBlur = 0 }, dark, color);
        TaskbarFlyout!.SetGlass(glass ? appearance.BackplateBlur : 0, appearance.BackplateRadius,
            double.IsFinite(root.Height) ? (float)root.Height : 0, squareBottom: false);
        PanelTheme.ClipDesktopBackdrop(root);
        flyoutLayer?.Apply(FlyoutTopmost);

        Windows.UI.Color ReadableTint()
        {
            var black = Microsoft.UI.Colors.Black;
            var white = Microsoft.UI.Colors.White;
            tint.A = 255;
            var foreground = dark ? white : black;
            if (AIEnter.PanelTheme.Contrast(foreground, tint) < 4.5)
            {
                dark = !dark;
                foreground = dark ? white : black;
            }
            if (!glass) return tint;
            tint.A = (byte)Math.Round(255 * (1 - appearance.BackplateTransparency / 100d));
            while (tint.A < 255
                && Math.Min(AIEnter.PanelTheme.Contrast(foreground, AIEnter.PanelTheme.Composite(tint, black)),
                    AIEnter.PanelTheme.Contrast(foreground, AIEnter.PanelTheme.Composite(tint, white))) < 4.5)
                tint.A++;
            return tint;
        }
    }

    void InitializeFlyoutMove()
    {
        header.IsTabStop = true;
        header.PointerPressed += (_, e) =>
        {
            if (!detached || ShowingSettings || resizing || changingMode || DragActive || ModalActive
                || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed || !GetCursorPos(out moveOrigin)) return;
            for (var visual = e.OriginalSource as DependencyObject; visual != header; visual = VisualTreeHelper.GetParent(visual))
                if (visual is null or ButtonBase or Thumb) return;
            if (!header.CapturePointer(e.Pointer)) return;
            movePointer = e.Pointer;
            movingFlyoutFrom = CurrentBounds();
            moveLease = KeepOpenForDrag();
            header.Focus(FocusState.Pointer);
            layoutTracking?.Start();
            e.Handled = true;
        };
        header.PointerReleased += (_, e) =>
        {
            if (movePointer?.PointerId != e.Pointer.PointerId) return;
            TrackWindowLayout();
            EndFlyoutMove(false);
            e.Handled = true;
        };
        header.PointerCaptureLost += (_, _) => EndFlyoutMove(true);
        header.PointerCanceled += (_, _) => EndFlyoutMove(true);
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (movingFlyoutFrom is null || e.Key != VirtualKey.Escape) return;
            EndFlyoutMove(true);
            e.Handled = true;
        }), true);
        Closed += (_, _) => EndFlyoutMove(true);
    }

    void MoveFlyout(Native.Point cursor)
    {
        if (movingFlyoutFrom is not { } start) return;
        var work = LayoutWorkArea(start);
        int x = Math.Clamp(start.X + cursor.X - moveOrigin.X, work.Left, Math.Max(work.Left, work.Right - start.Width));
        int y = Math.Clamp(start.Y + cursor.Y - moveOrigin.Y, work.Top, Math.Max(work.Top, work.Bottom - start.Height));
        ApplyBounds(new(x, y, start.Width, start.Height));
    }

    void EndFlyoutMove(bool cancel)
    {
        if (movingFlyoutFrom is not { } start) return;
        movingFlyoutFrom = null;
        layoutTracking?.Stop();
        if (movePointer is { } pointer) header.ReleasePointerCapture(pointer);
        movePointer = null;
        moveLease?.Dispose();
        moveLease = null;
        if (destroyed) return;
        if (cancel) ApplyBounds(start);
        else SaveSize();
    }

    Native.Rect LayoutWorkArea(RectInt32 bounds) => Native.MonitorInformation(TaskbarFlyout != null
        ? Native.MonitorForPosition(bounds.X, bounds.Y)
        : Native.MonitorForWindow(InteractionWindow)).Work;

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    static extern nint FlyoutWindowAtPoint(Native.Point point);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(nint window, out uint process);
    delegate void WinEventCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")]
    static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(nint hook);
}

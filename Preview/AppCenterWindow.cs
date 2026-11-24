using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization.Collation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace AITaskbar;

internal sealed class AppCenterWindow : Window
{
    private const int EnterMilliseconds = 180, ExitMilliseconds = 135;
    private const int PageSlideMilliseconds = 180, PagePreparationBatch = 12;
    private const double ContentTravel = 10;

    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
    private static readonly CharacterGroupings NameGroups = new("zh-CN");
    private static readonly SemaphoreSlim ArtworkWorker = new(1);
    private sealed record IconSource(string? Executable, string? AppId)
    {
        internal string? Key => TaskbarAppArtwork.Key(Executable, AppId);
    }
    private sealed record PendingArtwork(TaskbarIconPlate Plate, int Size, bool Original, IconSource[] Sources);
    private sealed class AppPage
    {
        internal Grid Grid { get; } = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        internal List<PendingArtwork>? Pending { get; set; } = [];
        internal CancellationTokenSource? Artwork;
    }
    private readonly App app;
    private readonly AppWindow taskbar;
    private readonly PinnedApps pins;
    private readonly Func<IReadOnlyList<AppGroup>> taskbarGroups;
    private readonly Action pinsChanged;
    private readonly AppCenterOrder order;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Grid root = new();
    private readonly TranslateTransform entrance = new();
    private readonly Grid pageArea = new();
    private readonly StackPanel fixedApps = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel fixedSection = new() { Spacing = 18 };
    private readonly StackPanel pager = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 28 };
    private readonly TextBox search = AppCenterSearch.Create();
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<int, AppPage> pages = [];
    private readonly Queue<Action> deferredArtwork = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer pageSlide;
    private AppPage? displayedPage;
    private int? requestedPage;
    private bool preparationQueued;
    private readonly SolidColorBrush foreground = new();
    private readonly TaskbarIconHoverLayer pinnedHover;
    private readonly TaskbarIconHoverLayer gridHover;
    private AIEnter.DesktopGlassBackdrop? backdrop;
    private AppCenterEntry[] installed = [], alphabetical = [], visible = [];
    private AppCenterPanelStyle style;
    private TaskbarAppFeedbackStyle feedback;
    private CancellationTokenSource? pinnedArtwork;
    private string? draggedKey;
    private int page, columns = 1, rows = 1, wheel;
    private bool loading = true, readingCatalog, renderQueued, closed, glassUnavailable, expanded, animating;
    private float opacity = 1, transitionFrom;
    private double transitionDuration;
    private long transitionStart;
    private bool CanAnimate => system.AnimationsEnabled && !accessibility.HighContrast && !glassUnavailable;
    private int IconSize => (int)Math.Round(app.IconSize * 1.55);
    private double TileWidth => IconSize + (style.Spacious ? 74 : 56);
    private double TileHeight => IconSize + (style.Spacious ? 52 : 36);
    private int PageSize => Math.Max(1, columns * rows);
    private int PageCount => Math.Max(1, (visible.Length + PageSize - 1) / PageSize);
    private bool CanReorder => style.ManualOrder && string.IsNullOrWhiteSpace(search.Text);

    internal event Action<bool>? ExpandedChanged;

    internal AppCenterWindow(App app, AppWindow taskbar, PinnedApps pins, string profile,
        Func<IReadOnlyList<AppGroup>> taskbarGroups, Action pinsChanged)
    {
        this.app = app;
        this.taskbar = taskbar;
        this.pins = pins;
        this.taskbarGroups = taskbarGroups;
        this.pinsChanged = pinsChanged;
        style = app.CurrentAppCenterAppearance.Panel;
        feedback = app.AppFeedback;
        bool animateHover = system.AnimationsEnabled && !accessibility.HighContrast;
        pinnedHover = new(animateHover);
        gridHover = new(animateHover);
        pageSlide = root.DispatcherQueue.CreateTimer();
        pageSlide.IsRepeating = false;
        pageSlide.Interval = TimeSpan.FromMilliseconds(PageSlideMilliseconds + 16);
        pageSlide.Tick += (_, _) =>
        {
            pageSlide.Stop();
            FlushArtwork();
            QueuePagePreparation();
        };
        order = new(profile);
        Title = "AITaskbar · 应用中心";
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        try { AppWindow.IsShownInSwitchers = false; }
        catch (NotImplementedException) { }
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AIEnter.Native.RemoveWindowFrame(handle);
        const int transitionsForcedDisabled = 3; // DWMWA_TRANSITIONS_FORCEDISABLED
        int disableNativeTransitions = 1;
        AIEnter.Native.DwmSetWindowAttribute(handle, transitionsForcedDisabled, ref disableNativeTransitions, sizeof(int));
        int squareCorners = 1;
        AIEnter.Native.DwmSetWindowAttribute(handle, 33, ref squareCorners, sizeof(int));

        var content = new Grid { Padding = new Thickness(38, 24, 38, 12), RenderTransform = entrance };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        search.Margin = new Thickness(0, 0, 0, 24);
        AutomationProperties.SetName(search, "搜索应用");
        search.TextChanged += (_, _) => { page = wheel = 0; RenderApps(); };
        content.Children.Add(search);

        var frequent = new Grid { ColumnSpacing = 12 };
        frequent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        frequent.ColumnDefinitions.Add(new());
        var pin = new SymbolIcon(Symbol.Pin) { Opacity = .65, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(pin, "已固定");
        AutomationProperties.SetName(pin, "已固定的应用");
        frequent.Children.Add(pin);
        var pinContent = new Grid();
        pinContent.Children.Add(pinnedHover.Surface);
        pinContent.Children.Add(fixedApps);
        var fixedScroller = new ScrollViewer { Content = pinContent, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        fixedScroller.ViewChanged += (_, _) => pinnedHover.Reposition();
        Grid.SetColumn(fixedScroller, 1);
        frequent.Children.Add(fixedScroller);
        fixedSection.Children.Add(frequent);
        fixedSection.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8), Opacity = .24,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
        Grid.SetRow(fixedSection, 1);
        content.Children.Add(fixedSection);
        pageArea.Children.Add(gridHover.Surface);
        pageArea.Children.Add(empty);
        Grid.SetRow(pageArea, 2);
        content.Children.Add(pageArea);
        Grid.SetRow(status, 3);
        content.Children.Add(status);
        Grid.SetRow(pager, 4);
        content.Children.Add(pager);
        root.Children.Add(content);
        Content = root;
        var backgroundMenu = new MenuFlyout();
        var browse = new MenuFlyoutItem { Text = "浏览本地程序", Icon = new SymbolIcon(Symbol.Folder) };
        browse.Click += async (_, _) => await BrowseAsync(handle);
        backgroundMenu.Items.Add(browse);
        var settings = new MenuFlyoutItem { Text = "应用中心设置", Icon = new SymbolIcon(Symbol.Setting) };
        settings.Click += (_, _) => app.ShowAppCenterInterfaceSettings();
        backgroundMenu.Items.Add(settings);
        root.ContextFlyout = backgroundMenu;

        root.Tapped += OnBackgroundTapped;
        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKey), true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        pageArea.SizeChanged += (_, _) =>
        {
            pageArea.Clip = new RectangleGeometry { Rect = new(0, 0, pageArea.ActualWidth, pageArea.ActualHeight) };
            if (Fit() || displayedPage is null) QueueRender();
        };
        root.SizeChanged += (_, _) => SizeBackdrop();
        root.ActualThemeChanged += (_, _) => ApplyPalette();
        system.ColorValuesChanged += SystemColorsChanged;
        taskbar.Changed += TaskbarChanged;
        pins.Changed += OnPinsChanged;
        Closed += (_, _) =>
        {
            closed = true;
            StopAnimation();
            taskbar.Changed -= TaskbarChanged;
            pins.Changed -= OnPinsChanged;
            system.ColorValuesChanged -= SystemColorsChanged;
            CancelArtwork(ref pinnedArtwork);
            pageSlide.Stop();
            deferredArtwork.Clear();
            ClearPages();
        };
        SetAnimationFrame(CanAnimate ? 0 : 1);
        Reposition();
        ApplyPalette();
        root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (closed) return;
            UpdateCatalog();
            _ = LoadAsync();
        });
    }

    private async Task LoadAsync()
    {
        if (readingCatalog || closed) return;
        readingCatalog = true;
        try
        {
            var next = await AppCenterCatalog.ReadAsync();
            if (closed || (!loading && installed.SequenceEqual(next))) return;
            installed = next;
            loading = false;
            status.Text = "";
            UpdateCatalog();
        }
        catch (Exception)
        {
            if (closed) return;
            loading = false;
            status.Text = "无法读取 Windows 应用列表。仍可打开已固定的应用。";
            QueueRender();
        }
        finally { readingCatalog = false; }
    }

    private void UpdateCatalog()
    {
        if (closed) return;
        var groups = AppGroups.Build(pins.Items, taskbarGroups().SelectMany(group => group.Windows).ToArray());
        var byExe = groups.Where(group => !string.IsNullOrEmpty(group.Executable))
            .GroupBy(group => group.Executable!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(items => items.Key, items => items.First(), StringComparer.OrdinalIgnoreCase);
        var byId = groups.Where(group => group.PinAppId is not null)
            .GroupBy(group => group.PinAppId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(items => items.Key, items => items.First(), StringComparer.OrdinalIgnoreCase);
        var apps = installed.Select(entry =>
        {
            var group = entry.ShellId is not null ? byId.GetValueOrDefault(entry.ShellId) : null;
            group ??= !entry.SeparateIcon && entry.Executable is not null ? byExe.GetValueOrDefault(entry.Executable) : null;
            return group is null ? entry : entry with
            {
                Executable = group.IconExecutable ?? entry.Executable,
                IconAppId = group.IconAppId ?? entry.IconAppId
            };
        }).ToList();
        CancelArtwork(ref pinnedArtwork);
        fixedApps.Children.Clear();
        var pending = new List<PendingArtwork>();
        foreach (var group in groups.Where(group => group.Pin is not null))
        {
            var pin = group.Pin!;
            var entry = apps.FirstOrDefault(item => MatchesPin(item, pin));
            if (entry is null)
            {
                if (pin.AppId is null && AppCenterCatalog.IsMissingLocalTarget(pin.Executable)) continue;
                entry = new(pin.Key, pin.Name, group.IconExecutable, group.IconAppId, ActivationId: pin.AppId,
                    LaunchTarget: pin.AppId is null ? pin.Executable : null);
                apps.Add(entry);
            }
            fixedApps.Children.Add(BuildTile(entry with
            {
                Name = pin.Name,
                Executable = group.IconExecutable ?? entry.Executable,
                IconAppId = group.IconAppId ?? (entry.SeparateIcon ? null : entry.IconAppId),
                SeparateIcon = false
            }, fixedTile: true, pending));
        }
        LoadArtwork(pending, ref pinnedArtwork);
        alphabetical = apps.OrderBy(entry => NameGroup(entry.Name))
            .ThenBy(entry => entry.Name, NameOrder).ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        QueueRender();
    }

    private void QueueRender()
    {
        if (renderQueued || closed || pageArea.ActualWidth <= 0 || pageArea.ActualHeight <= 0) return;
        renderQueued = root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            renderQueued = false;
            RenderApps();
        });
    }

    private static int NameGroup(string name)
    {
        string label = NameGroups.Lookup(name);
        char letter = char.ToUpperInvariant(label.Length == 0 ? name[0] : label[0]);
        return letter is >= 'A' and <= 'Z' ? letter - 'A' : 26;
    }

    private void OnPinsChanged() => root.DispatcherQueue.TryEnqueue(UpdateCatalog);

    internal void RefreshStyle()
    {
        style = app.CurrentAppCenterAppearance.Panel;
        ApplyPalette();
        Fit();
        UpdateCatalog();
    }

    internal void SetAppFeedback(TaskbarAppFeedbackStyle value)
    {
        feedback = value;
        ApplyHoverStyle(root.ActualTheme);
    }

    internal void ToggleVisibility() => AnimateVisibility(open: !expanded);
    private void Dismiss() => AnimateVisibility(open: false);

    private void AnimateVisibility(bool open)
    {
        if (closed || expanded == open) return;
        expanded = open;
        root.IsHitTestVisible = open;
        if (open)
        {
            if (!AppWindow.IsVisible)
            {
                SetAnimationFrame(CanAnimate ? 0 : 1);
                AppWindow.Show(activateWindow: false);
            }
            Activate();
            root.DispatcherQueue.TryEnqueue(() => { if (!closed && expanded) search.Focus(FocusState.Programmatic); });
            root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (expanded) _ = LoadAsync(); });
        }
        ExpandedChanged?.Invoke(open);
        float target = open ? 1 : 0;
        if (!CanAnimate || opacity == target) { FinishAnimation(target); return; }
        transitionFrom = opacity;
        transitionDuration = Math.Max(60, (open ? EnterMilliseconds : ExitMilliseconds) * Math.Abs(target - opacity));
        transitionStart = Stopwatch.GetTimestamp();
        if (animating) return;
        animating = true;
        CompositionTarget.Rendering += RenderAnimation;
    }

    private void RenderAnimation(object? sender, object args)
    {
        float target = expanded ? 1 : 0;
        double elapsed = Stopwatch.GetElapsedTime(transitionStart).TotalMilliseconds / transitionDuration;
        if (!CanAnimate || elapsed >= 1) { FinishAnimation(target); return; }
        var progress = (float)(1 - Math.Pow(1 - elapsed, expanded ? 3 : 2));
        SetAnimationFrame(transitionFrom + (target - transitionFrom) * progress);
    }

    private void SetAnimationFrame(float amount)
    {
        opacity = amount;
        root.Opacity = amount;
        entrance.Y = (1 - amount) * ContentTravel;
        backdrop?.SetMotion(amount, 0);
    }

    private void FinishAnimation(float target)
    {
        StopAnimation();
        if (!expanded)
        {
            AppWindow.Hide();
            SetAnimationFrame(0);
            // Prepare the home page while hidden. Leave an already complete first page intact.
            requestedPage = null;
            if (search.Text.Length > 0) search.Text = "";
            else if (page != 0) ShowPage(0);
            wheel = 0;
            return;
        }
        SetAnimationFrame(target);
    }

    internal void HideAndClose()
    {
        if (closed) return;
        // Hide before WinUI detaches the backdrop and restores the native frame.
        if (AppWindow.IsVisible) AppWindow.Hide();
        Close();
    }

    private void StopAnimation()
    {
        if (!animating) return;
        CompositionTarget.Rendering -= RenderAnimation;
        animating = false;
    }

    private void ApplyHoverStyle(ElementTheme theme)
    {
        bool light = theme != ElementTheme.Dark;
        bool highContrast = accessibility.HighContrast;
        pinnedHover.ApplyStyle(feedback, light, system, highContrast);
        gridHover.ApplyStyle(feedback, light, system, highContrast);
    }

    internal void ApplyPalette()
    {
        if (closed) return;
        var palette = AppCenterBackplate.Resolve(style.Backdrop, app.BarAppearance, system, accessibility.HighContrast);
        root.RequestedTheme = palette.Theme;
        AppCenterSearch.ApplyTheme(search, palette.Theme == ElementTheme.Dark, accessibility.HighContrast);
        ApplyHoverStyle(palette.Theme);
        foreground.Color = palette.Text;
        status.Foreground = empty.Foreground = foreground;
        root.Background = new SolidColorBrush(glassUnavailable ? palette.Solid : palette.Tint);
        if (!glassUnavailable && (palette.Tint.A < 255 || CanAnimate))
        {
            if (backdrop is null)
            {
                backdrop = new(transparentWindow: true);
                backdrop.GlassUnavailable += OnGlassUnavailable;
                backdrop.WindowSettingsChanged += QueuePaletteRefresh;
                backdrop.SetMotion(opacity, 0);
                backdrop.SetBlur(palette.Blur);
                SizeBackdrop();
                try { SystemBackdrop = backdrop; }
                catch (ExternalException)
                {
                    root.Background = new SolidColorBrush(palette.Solid);
                    OnGlassUnavailable();
                }
            }
            else backdrop.SetBlur(palette.Blur);
        }
        else if (backdrop is not null)
        {
            SystemBackdrop = null;
            backdrop = null;
        }
    }

    private void OnGlassUnavailable()
    {
        glassUnavailable = true;
        QueuePaletteRefresh();
    }

    private void SystemColorsChanged(UISettings sender, object args) => QueuePaletteRefresh();
    private void QueuePaletteRefresh() => root.DispatcherQueue.TryEnqueue(ApplyPalette);

    private void SizeBackdrop()
    {
        var size = new Vector2((float)root.ActualWidth, (float)root.ActualHeight);
        backdrop?.SetShape(size, size, 0);
    }

    private void TaskbarChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange || args.DidSizeChange) Reposition();
    }

    private void Reposition()
    {
        var area = DisplayArea.GetFromWindowId(taskbar.Id, DisplayAreaFallback.Primary).WorkArea;
        int bottom = Math.Min(area.Y + area.Height, taskbar.Position.Y);
        if (bottom > area.Y) AppWindow.MoveAndResize(new(area.X, area.Y, area.Width, bottom - area.Y));
    }

    private bool Fit()
    {
        double gap = app.IconLayout.Gap;
        fixedApps.Spacing = gap;
        int nextColumns = Math.Max(1, (int)((pageArea.ActualWidth + gap) / (TileWidth + gap)));
        int nextRows = Math.Max(1, (int)((pageArea.ActualHeight + gap) / (TileHeight + gap)));
        if (columns == nextColumns && rows == nextRows) return false;
        int anchor = page * PageSize;
        columns = nextColumns;
        rows = nextRows;
        page = anchor / PageSize;
        return true;
    }

    private void RenderApps()
    {
        if (closed) return;
        requestedPage = null;
        ClearPages();
        string query = search.Text.Trim();
        fixedSection.Visibility = query.Length == 0 && fixedApps.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sorted = style.ManualOrder ? order.Arrange(alphabetical) : alphabetical;
        visible = query.Length == 0 ? sorted : sorted.Where(entry =>
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(entry.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth) >= 0).ToArray();
        page = Math.Clamp(page, 0, PageCount - 1);
        empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = loading ? "正在读取应用…" : query.Length > 0 ? "没找到应用" : "没有可显示的应用";
        BuildPager();
        if (visible.Length == 0) return;
        var view = GetPage(page);
        PreparePage(page, view, PageSize);
        PresentPage(view, direction: 0);
        QueuePagePreparation();
    }

    private void ClearPages()
    {
        if (displayedPage is not null) pageArea.Children.Remove(displayedPage.Grid);
        displayedPage = null;
        foreach (var view in pages.Values) CancelArtwork(ref view.Artwork);
        pages.Clear();
    }

    private AppPage GetPage(int index)
    {
        if (pages.TryGetValue(index, out var existing)) return existing;
        var view = new AppPage();
        var grid = view.Grid;
        grid.ColumnSpacing = grid.RowSpacing = app.IconLayout.Gap;
        for (int column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(TileWidth) });
        for (int row = 0; row < rows; row++) grid.RowDefinitions.Add(new() { Height = new GridLength(TileHeight) });
        return pages[index] = view;
    }

    private bool PreparePage(int index, AppPage view, int budget)
    {
        if (view.Pending is not { } pending) return true;
        int start = index * PageSize;
        int count = Math.Min(PageSize, visible.Length - start);
        int stop = Math.Min(count, view.Grid.Children.Count + budget);
        for (int slot = view.Grid.Children.Count; slot < stop; slot++)
        {
            var tile = BuildTile(visible[start + slot], fixedTile: false, pending);
            Grid.SetColumn(tile, slot % columns);
            Grid.SetRow(tile, slot / columns);
            view.Grid.Children.Add(tile);
        }
        if (stop < count) return false;
        view.Pending = null;
        LoadArtwork(pending, ref view.Artwork);
        return true;
    }

    private bool NeedsPreparation(int index) => index >= 0 && index < PageCount &&
        (!pages.TryGetValue(index, out var view) || view.Pending is not null);

    private void QueuePagePreparation()
    {
        if (closed || preparationQueued || pageSlide.IsRunning) return;
        if (requestedPage is null && !NeedsPreparation(page + 1) && !NeedsPreparation(page - 1)) return;
        preparationQueued = root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            preparationQueued = false;
            if (closed || pageSlide.IsRunning) return;
            int index = requestedPage ?? (NeedsPreparation(page + 1) ? page + 1 : page - 1);
            if (index < 0 || index >= PageCount) return;
            var view = GetPage(index);
            if (PreparePage(index, view, PagePreparationBatch) && requestedPage == index) NavigateTo(index, view);
            QueuePagePreparation();
        });
    }

    private void PresentPage(AppPage view, int direction)
    {
        if (displayedPage is not null) pageArea.Children.Remove(displayedPage.Grid);
        displayedPage = view;
        pageArea.Children.Insert(1, view.Grid);
        ElementCompositionPreview.SetIsTranslationEnabled(view.Grid, true);
        var visual = ElementCompositionPreview.GetElementVisual(view.Grid);
        visual.StopAnimation("Translation");
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!expanded || direction == 0 || !system.AnimationsEnabled || accessibility.HighContrast) return;

        var start = new Vector3((float)(direction * Math.Min(pageArea.ActualWidth * .18, 140)), 0, 0);
        visual.Properties.InsertVector3("Translation", start);
        using var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        using var easing = visual.Compositor.CreateCubicBezierEasingFunction(new(.2f, .7f), new(.3f, 1f));
        animation.InsertKeyFrame(0, start);
        animation.InsertKeyFrame(1, Vector3.Zero, easing);
        animation.Duration = TimeSpan.FromMilliseconds(PageSlideMilliseconds);
        visual.StartAnimation("Translation", animation);
        pageSlide.Start();
    }

    private Button BuildTile(AppCenterEntry entry, bool fixedTile, List<PendingArtwork> pending)
    {
        int size = IconSize;
        IconSource[] sources = entry.IconAppId is null && entry.ShellId is { } shellId
            ? [new(entry.Executable, null), new(null, shellId)] : [new(entry.SeparateIcon ? null : entry.Executable, entry.IconAppId)];
        var missing = new List<IconSource>();
        AppIconArtwork? icon = null;
        foreach (var source in sources)
        {
            if (source.Key is not { Length: > 0 } key) continue;
            if (!app.AppArtwork.TryGet(key, out icon)) missing.Add(source);
            if (icon is not null) break;
        }
        FrameworkElement glyph = icon is null ? new FontIcon { Glyph = "\uE7C5", FontSize = size * .65 }
            : new Image { Stretch = Stretch.Uniform };
        var plate = new TaskbarIconPlate(size, glyph) { HorizontalAlignment = HorizontalAlignment.Center };
        bool original = !entry.SeparateIcon && entry.Executable is { } executable && app.IconPreferences.UsesOriginal(executable);
        TaskbarAppArtwork.Apply(plate, icon, size, app.IconLayout, original, accessibility.HighContrast);
        if (icon is null && missing.Count > 0)
            pending.Add(new(plate, size, original, missing.ToArray()));
        var label = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center, MaxWidth = TileWidth - 8, HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = foreground };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(plate);
        body.Children.Add(label);
        var button = new Button { Content = body, Width = TileWidth, Height = TileHeight,
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10) };
        (fixedTile ? pinnedHover : gridHover).TrackButton(button, plate);
        AutomationProperties.SetName(button, entry.Name);
        ToolTipService.SetToolTip(button, entry.Name);
        button.Click += (_, _) => Launch(entry);
        button.ContextRequested += (_, args) => ShowTileMenu(button, entry, args);
        if (!fixedTile && CanReorder)
        {
            button.CanDrag = button.AllowDrop = true;
            button.DragStarting += (_, args) =>
            {
                draggedKey = entry.Key;
                args.Data.RequestedOperation = DataPackageOperation.Move;
                args.Data.SetText(entry.Key);
            };
            button.DropCompleted += (_, _) => draggedKey = null;
            button.DragOver += (_, args) =>
            {
                if (draggedKey is null || draggedKey == entry.Key) return;
                args.AcceptedOperation = DataPackageOperation.Move;
                args.Handled = true;
            };
            button.Drop += (_, args) =>
            {
                if (draggedKey is not { } source) return;
                Move(source, entry.Key, args.GetPosition(button).X >= button.ActualWidth / 2);
                draggedKey = null;
                args.Handled = true;
            };
        }
        return button;
    }

    private void ShowTileMenu(Button button, AppCenterEntry entry, ContextRequestedEventArgs args)
    {
        var pin = pins.Items.FirstOrDefault(item => MatchesPin(entry, item));
        string? appId = entry.ShellId ?? entry.ActivationId;
        bool package = PackagedAppPin.Name(appId) is not null;
        if (pin is null && !package && !RunningWindows.CanPin(entry.Executable)) return;
        var menu = new MenuFlyout();
        var action = new MenuFlyoutItem { Text = pin is null ? "固定到任务栏" : "取消固定", Icon = new SymbolIcon(Symbol.Pin) };
        action.Click += (_, _) => ChangePins(() =>
        {
            if (pin is not null) pins.Remove(pin);
            else if (package) pins.Add(appId!, entry.Executable);
            else pins.Add(entry.Executable!);
        });
        menu.Items.Add(action);
        args.Handled = true;
        menu.ShowAt(button);
    }

    private static void CancelArtwork(ref CancellationTokenSource? load)
    {
        load?.Cancel();
        load?.Dispose();
        load = null;
    }

    private void LoadArtwork(List<PendingArtwork> pending, ref CancellationTokenSource? load)
    {
        if (pending.Count == 0) return;
        load = new();
        _ = LoadArtworkAsync(pending, load.Token);
    }

    private async Task LoadArtworkAsync(List<PendingArtwork> pending, CancellationToken token)
    {
        var dispatcher = root.DispatcherQueue;
        try
        {
            await ArtworkWorker.WaitAsync(token);
            try
            {
                await AppCenterCatalog.RunOnShellThread(() =>
                {
                    var prepared = new Dictionary<string, AppIconArtwork.Prepared?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tile in pending)
                    {
                        if (token.IsCancellationRequested) break;
                        var results = new List<(string Key, AppIconArtwork.Prepared? Artwork)>();
                        foreach (var source in tile.Sources)
                        {
                            if (token.IsCancellationRequested) break;
                            string key = source.Key!;
                            if (!prepared.TryGetValue(key, out var image))
                                prepared[key] = image = TaskbarAppArtwork.Prepare(source.Executable, source.AppId);
                            results.Add((key, image));
                            if (image is not null) break;
                        }
                        if (!dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => ApplyLoadedArtwork(tile, results, token)))
                            break;
                    }
                    return true;
                }, "AITaskbar application icons");
            }
            finally { ArtworkWorker.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void ApplyLoadedArtwork(PendingArtwork tile,
        List<(string Key, AppIconArtwork.Prepared? Artwork)> results, CancellationToken token)
    {
        if (closed || token.IsCancellationRequested) return;
        if (pageSlide.IsRunning)
        {
            deferredArtwork.Enqueue(() => ApplyLoadedArtwork(tile, results, token));
            return;
        }
        foreach (var (key, prepared) in results)
        {
            if (app.AppArtwork.Remember(key, prepared) is not { } icon) continue;
            tile.Plate.Artwork.Child = new Image { Stretch = Stretch.Uniform };
            TaskbarAppArtwork.Apply(tile.Plate, icon, tile.Size, app.IconLayout, tile.Original, accessibility.HighContrast);
            break;
        }
    }

    private void FlushArtwork()
    {
        while (deferredArtwork.TryDequeue(out var apply))
            root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => apply());
    }

    private static bool MatchesPin(AppCenterEntry entry, PinnedApp pin) => pin.AppId is not null
        ? PinnedApps.SameProgram(entry.ShellId ?? entry.ActivationId, pin.AppId)
        : PinnedApps.SameProgram(entry.Executable, pin.Executable);

    private void Move(string source, string target, bool after)
    {
        if (source == target) return;
        var apps = order.Arrange(alphabetical).ToList();
        var entry = apps.FirstOrDefault(item => item.Key == source);
        if (entry is null || !apps.Remove(entry)) return;
        int index = apps.FindIndex(item => item.Key == target);
        if (index < 0) return;
        apps.Insert(index + (after ? 1 : 0), entry);
        try { order.Save(apps); RenderApps(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { status.Text = "无法保存应用顺序。"; }
    }

    private void Launch(AppCenterEntry entry)
    {
        if (!expanded) return;
        try { entry.Launch(); Dismiss(); }
        catch (FileNotFoundException)
        {
            AppCenterCatalog.Invalidate();
            installed = installed.Where(candidate => candidate.Key != entry.Key).ToArray();
            UpdateCatalog();
            status.Text = "无法打开 " + entry.Name + "。启动目标已移动或删除。";
        }
        catch (Exception error) when (error is Win32Exception or COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { status.Text = "无法打开 " + entry.Name + "。"; }
    }

    private async Task BrowseAsync(nint handle)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, handle);
            if (await picker.PickSingleFileAsync() is { } file && !closed) ChangePins(() => pins.Add(file.Path));
        }
        catch (Exception) { if (!closed) status.Text = "无法浏览本地程序。"; }
    }

    private void ChangePins(Action change)
    {
        try { change(); pinsChanged(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { status.Text = error.Message; }
    }

    private void BuildPager()
    {
        pager.Children.Clear();
        if (PageCount == 1) return;
        for (int index = 0; index < PageCount; index++)
        {
            int target = index;
            var dot = new Button { Content = new TextBlock { Text = index == page ? "━" : "•" },
                Padding = new Thickness(3, 0, 3, 0), Width = 22, MinWidth = 0, Height = 28, MinHeight = 0,
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Opacity = index == page ? 1 : .58 };
            AutomationProperties.SetName(dot, $"第 {index + 1} 页，共 {PageCount} 页");
            if (index == page) AutomationProperties.SetItemStatus(dot, "当前页");
            dot.Click += (_, _) => ShowPage(target);
            if (CanReorder)
            {
                dot.AllowDrop = true;
                dot.DragOver += (_, args) =>
                {
                    if (draggedKey is null) return;
                    args.AcceptedOperation = DataPackageOperation.Move;
                    args.Handled = true;
                };
                dot.Drop += (_, args) =>
                {
                    if (draggedKey is not { } source) return;
                    var targetEntry = visible.Skip(target * PageSize).Take(PageSize).LastOrDefault();
                    if (targetEntry is not null) { page = target; Move(source, targetEntry.Key, after: true); }
                    draggedKey = null;
                    args.Handled = true;
                };
            }
            pager.Children.Add(dot);
        }
    }

    private void ShowPage(int target)
    {
        target = Math.Clamp(target, 0, PageCount - 1);
        if (draggedKey is not null) return;
        requestedPage = target == page ? null : target;
        TrimPages();
        if (requestedPage is null) return;
        if (pages.TryGetValue(target, out var view) && view.Pending is null)
            NavigateTo(target, view);
        else QueuePagePreparation();
    }

    private void NavigateTo(int target, AppPage view)
    {
        requestedPage = null;
        int direction = Math.Sign(target - page);
        page = target;
        PresentPage(view, direction);
        TrimPages();
        for (int index = 0; index < pager.Children.Count; index++)
        {
            var dot = (Button)pager.Children[index];
            ((TextBlock)dot.Content).Text = index == page ? "━" : "•";
            dot.Opacity = index == page ? 1 : .58;
            AutomationProperties.SetItemStatus(dot, index == page ? "当前页" : "");
        }
        QueuePagePreparation();
    }

    private void TrimPages()
    {
        foreach (int index in pages.Keys.Where(index => index != requestedPage && Math.Abs(index - page) > 1).ToArray())
        {
            CancelArtwork(ref pages[index].Artwork);
            pages.Remove(index);
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        if (!expanded || PageCount < 2 || draggedKey is not null) return;
        int delta = args.GetCurrentPoint(root).Properties.MouseWheelDelta;
        if (delta == 0) return;
        args.Handled = true;
        if (Math.Sign(delta) != Math.Sign(wheel)) wheel = 0;
        wheel += delta;
        if (Math.Abs(wheel) < 120) return;
        ShowPage((requestedPage ?? page) - Math.Sign(wheel));
        wheel = 0;
    }

    private void OnBackgroundTapped(object sender, TappedRoutedEventArgs args)
    {
        if (closed || draggedKey is not null) return;
        for (var source = args.OriginalSource as DependencyObject; source is not null && !ReferenceEquals(source, root);
            source = VisualTreeHelper.GetParent(source))
        {
            if (source is ButtonBase or TextBox or ScrollBar) return;
        }
        args.Handled = true;
        Dismiss();
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (!expanded) { args.Handled = true; return; }
        if (args.Key == VirtualKey.Escape) { args.Handled = true; Dismiss(); }
        else if (args.Key is VirtualKey.PageUp or VirtualKey.PageDown)
        {
            args.Handled = true;
            ShowPage((requestedPage ?? page) + (args.Key == VirtualKey.PageDown ? 1 : -1));
        }
        else if (args.Key == VirtualKey.Enter && search.FocusState != FocusState.Unfocused && visible.Length > 0)
        {
            args.Handled = true;
            Launch(visible[page * PageSize]);
        }
    }
}

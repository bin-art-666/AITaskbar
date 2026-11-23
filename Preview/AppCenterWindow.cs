using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Globalization.Collation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class AppCenterWindow : Window
{
    private static readonly StringComparer NameOrder = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
    private static readonly CharacterGroupings NameGroups = new("zh-CN");
    private readonly App app;
    private readonly AppWindow taskbar;
    private readonly PinnedApps pins;
    private readonly Action pinsChanged;
    private readonly AppCenterOrder order;
    private readonly UISettings system = new();
    private readonly AccessibilitySettings accessibility = new();
    private readonly Grid root = new();
    private readonly Grid applicationGrid = new();
    private readonly Grid pageArea = new();
    private readonly StackPanel fixedApps = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel fixedSection = new() { Spacing = 18 };
    private readonly StackPanel pager = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, MinHeight = 28 };
    private readonly TextBox search = new() { PlaceholderText = "搜索应用", MaxWidth = 550, MinHeight = 44 };
    private readonly TextBlock empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly List<Button> pageButtons = [];
    private AIEnter.DesktopGlassBackdrop? backdrop;
    private AppCenterEntry[] installed = [], alphabetical = [], visible = [];
    private AppCenterPanelStyle style;
    private string? draggedKey;
    private int page, columns = 1, rows = 1, wheel;
    private bool loading = true, closed;
    private double CellWidth => style.Spacious ? 134 : 116;
    private double CellHeight => style.Spacious ? 112 : 96;
    private int PageSize => Math.Max(1, columns * rows);
    private int PageCount => Math.Max(1, (visible.Length + PageSize - 1) / PageSize);
    private bool CanReorder => style.ManualOrder && string.IsNullOrWhiteSpace(search.Text);

    internal AppCenterWindow(App app, AppWindow taskbar, PinnedApps pins, string profile, Action pinsChanged)
    {
        this.app = app;
        this.taskbar = taskbar;
        this.pins = pins;
        this.pinsChanged = pinsChanged;
        style = app.CurrentAppCenterAppearance.Panel;
        order = new(profile);
        Title = "AITaskbar · 应用中心";
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = presenter.IsMinimizable = presenter.IsMaximizable = false;
        try { AppWindow.IsShownInSwitchers = false; }
        catch (NotImplementedException) { }
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AIEnter.Native.RemoveWindowFrame(handle);
        int squareCorners = 1;
        AIEnter.Native.DwmSetWindowAttribute(handle, 33, ref squareCorners, sizeof(int));

        var content = new Grid { Padding = new Thickness(38, 24, 38, 12) };
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        content.RowDefinitions.Add(new() { Height = GridLength.Auto });
        search.Margin = new Thickness(0, 0, 0, 24);
        search.CornerRadius = new CornerRadius(10);
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
        var fixedScroller = new ScrollViewer { Content = fixedApps, HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(fixedScroller, 1);
        frequent.Children.Add(fixedScroller);
        fixedSection.Children.Add(frequent);
        fixedSection.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 0, 0, 8), Opacity = .24,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Gray) });
        Grid.SetRow(fixedSection, 1);
        content.Children.Add(fixedSection);
        applicationGrid.HorizontalAlignment = HorizontalAlignment.Center;
        applicationGrid.VerticalAlignment = VerticalAlignment.Center;
        pageArea.Children.Add(applicationGrid);
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

        root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnKey), true);
        root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        pageArea.SizeChanged += (_, _) =>
        {
            pageArea.Clip = new RectangleGeometry { Rect = new(0, 0, pageArea.ActualWidth, pageArea.ActualHeight) };
            Fit();
        };
        root.SizeChanged += (_, _) => SizeBackdrop();
        root.ActualThemeChanged += (_, _) => ApplyPalette();
        taskbar.Changed += TaskbarChanged;
        pins.Changed += OnPinsChanged;
        Closed += (_, _) =>
        {
            closed = true;
            taskbar.Changed -= TaskbarChanged;
            pins.Changed -= OnPinsChanged;
            SystemBackdrop = null;
        };
        Reposition();
        ApplyPalette();
        Activate();
        root.DispatcherQueue.TryEnqueue(() => search.Focus(FocusState.Programmatic));
        UpdateCatalog();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { installed = await AppCenterCatalog.ReadAsync(); }
        catch (Exception) { if (!closed) status.Text = "无法读取 Windows 应用列表。仍可打开已固定的应用。"; }
        if (closed) return;
        loading = false;
        UpdateCatalog();
    }

    private void UpdateCatalog()
    {
        if (closed) return;
        var groups = AppGroups.Build(pins.Items, RunningWindows.List());
        var byExe = groups.Where(group => !string.IsNullOrEmpty(group.Executable))
            .GroupBy(group => group.Executable!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(items => items.Key, items => items.First(), StringComparer.OrdinalIgnoreCase);
        var byId = groups.Where(group => group.PinAppId is not null)
            .GroupBy(group => group.PinAppId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(items => items.Key, items => items.First(), StringComparer.OrdinalIgnoreCase);
        var apps = installed.Select(entry =>
        {
            var group = entry.ShellId is not null ? byId.GetValueOrDefault(entry.ShellId) : null;
            group ??= entry.Executable is not null ? byExe.GetValueOrDefault(entry.Executable) : null;
            return group is null ? entry : entry with
            {
                Executable = group.IconExecutable ?? entry.Executable,
                IconAppId = group.IconAppId ?? entry.IconAppId
            };
        }).ToList();
        fixedApps.Children.Clear();
        foreach (var group in groups.Where(group => group.Pin is not null))
        {
            var pin = group.Pin!;
            var entry = apps.FirstOrDefault(item => MatchesPin(item, pin));
            if (entry is null)
            {
                entry = new(pin.Key, pin.Name, group.IconExecutable, group.IconAppId, ActivationId: pin.AppId);
                apps.Add(entry);
            }
            fixedApps.Children.Add(BuildTile(entry with { Name = pin.Name }, fixedTile: true));
        }
        alphabetical = apps.OrderBy(entry => NameGroup(entry.Name))
            .ThenBy(entry => entry.Name, NameOrder).ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase).ToArray();
        RenderApps();
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
        var next = app.CurrentAppCenterAppearance.Panel;
        bool densityChanged = style.Spacious != next.Spacious;
        style = next;
        ApplyPalette();
        if (densityChanged) Fit();
        UpdateCatalog();
    }

    internal void ApplyPalette()
    {
        if (closed) return;
        var appearance = app.BarAppearance;
        var palette = TaskbarPanelFlyout.ResolvePalette(new(Backdrop: style.Backdrop),
            appearance is null ? root.ActualTheme != ElementTheme.Dark : appearance.Tone == LumaBar.Tone.Light,
            appearance, system, accessibility.HighContrast);
        root.RequestedTheme = palette.Theme;
        // A full page needs more contrast than the compact date flyout over light windows.
        var background = palette.Glass && palette.Background.A < 192
            ? TaskbarPanelFlyout.Alpha(palette.Solid, 192) : palette.Background;
        root.Background = new SolidColorBrush(background);
        status.Foreground = empty.Foreground = new SolidColorBrush(palette.Text);
        if (palette.Glass)
        {
            if (backdrop is null)
            {
                backdrop = new();
                backdrop.SetMotion(1, 0);
                backdrop.SetBlur(palette.Blur);
                SizeBackdrop();
                SystemBackdrop = backdrop;
            }
            else backdrop.SetBlur(palette.Blur);
        }
        else if (backdrop is not null)
        {
            SystemBackdrop = null;
            backdrop = null;
        }
    }

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

    private void Fit()
    {
        int nextColumns = Math.Max(1, (int)(pageArea.ActualWidth / CellWidth));
        int nextRows = Math.Max(1, (int)(pageArea.ActualHeight / CellHeight));
        if (columns == nextColumns && rows == nextRows) return;
        int anchor = page * PageSize;
        columns = nextColumns;
        rows = nextRows;
        page = anchor / PageSize;
        RenderApps();
    }

    private void RenderApps(int direction = 0)
    {
        string query = search.Text.Trim();
        fixedSection.Visibility = query.Length == 0 && fixedApps.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var sorted = style.ManualOrder ? order.Arrange(alphabetical) : alphabetical;
        visible = query.Length == 0 ? sorted : sorted.Where(entry =>
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(entry.Name, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth) >= 0).ToArray();
        page = Math.Clamp(page, 0, PageCount - 1);
        applicationGrid.Children.Clear();
        applicationGrid.ColumnDefinitions.Clear();
        applicationGrid.RowDefinitions.Clear();
        for (int index = 0; index < columns; index++)
            applicationGrid.ColumnDefinitions.Add(new() { Width = new GridLength(CellWidth) });
        for (int index = 0; index < rows; index++)
            applicationGrid.RowDefinitions.Add(new() { Height = new GridLength(CellHeight) });
        int slot = 0;
        pageButtons.Clear();
        foreach (var entry in visible.Skip(page * PageSize).Take(PageSize))
        {
            var tile = BuildTile(entry, fixedTile: false);
            Grid.SetColumn(tile, slot % columns);
            Grid.SetRow(tile, slot++ / columns);
            applicationGrid.Children.Add(tile);
            pageButtons.Add(tile);
        }
        empty.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        empty.Text = loading ? "正在读取应用…" : query.Length > 0 ? "没找到应用" : "没有可显示的应用";
        BuildPager();
        if (direction != 0 && system.AnimationsEnabled && !accessibility.HighContrast)
        {
            var offset = new TranslateTransform();
            applicationGrid.RenderTransform = offset;
            var animation = new Storyboard();
            TaskbarPanelFlyout.AddAnimation(animation, offset, "X", direction * Math.Min(pageArea.ActualWidth * .18, 140), 0, 180);
            animation.Begin();
        }
    }

    private Button BuildTile(AppCenterEntry entry, bool fixedTile)
    {
        int size = Math.Max(48, (int)Math.Round(app.IconSize * 1.55));
        var icon = app.AppArtwork.Get(entry.Executable, entry.IconAppId);
        if (icon is null && entry.ShellId is { } shellId && entry.IconAppId is null)
            icon = app.AppArtwork.Get(null, shellId);
        FrameworkElement glyph = icon is null ? new FontIcon { Glyph = "\uE7C5", FontSize = size * .65 }
            : new Image { Stretch = Stretch.Uniform };
        var plate = new TaskbarIconPlate(size, glyph) { HorizontalAlignment = HorizontalAlignment.Center };
        TaskbarAppArtwork.Apply(plate, icon, size, app.IconLayout,
            entry.Executable is { } executable && app.IconPreferences.UsesOriginal(executable), accessibility.HighContrast);
        var label = new TextBlock { Text = entry.Name, TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center, MaxWidth = CellWidth - 12, HorizontalAlignment = HorizontalAlignment.Center };
        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(plate);
        body.Children.Add(label);
        var button = new Button { Content = body, Width = CellWidth - 4, Height = CellHeight - 4,
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10) };
        AutomationProperties.SetName(button, entry.Name);
        ToolTipService.SetToolTip(button, entry.Name);
        button.Click += (_, _) => Launch(entry);
        var menu = new MenuFlyout();
        var pin = pins.Items.FirstOrDefault(item => MatchesPin(entry, item));
        bool package = PackagedAppPin.Name(entry.ShellId ?? entry.ActivationId) is not null;
        if (pin is not null || package || RunningWindows.CanPin(entry.Executable))
        {
            var action = new MenuFlyoutItem { Text = pin is null ? "固定到任务栏" : "取消固定", Icon = new SymbolIcon(Symbol.Pin) };
            action.Click += (_, _) => ChangePins(() =>
            {
                if (pin is not null) pins.Remove(pin);
                else if (package) pins.Add((entry.ShellId ?? entry.ActivationId)!, entry.Executable);
                else pins.Add(entry.Executable!);
            });
            menu.Items.Add(action);
        }
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
        if (menu.Items.Count > 0) button.ContextFlyout = menu;
        return button;
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
        try { entry.Launch(); Close(); }
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
        if (target == page || draggedKey is not null) return;
        int direction = Math.Sign(target - page);
        page = target;
        RenderApps(direction);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        if (PageCount < 2 || draggedKey is not null) return;
        int delta = args.GetCurrentPoint(root).Properties.MouseWheelDelta;
        if (delta == 0) return;
        args.Handled = true;
        if (Math.Sign(delta) != Math.Sign(wheel)) wheel = 0;
        wheel += delta;
        if (Math.Abs(wheel) < 120) return;
        ShowPage(page - Math.Sign(wheel));
        wheel = 0;
    }

    private void OnKey(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape) { args.Handled = true; Close(); }
        else if (args.Key is VirtualKey.PageUp or VirtualKey.PageDown)
        {
            args.Handled = true;
            ShowPage(page + (args.Key == VirtualKey.PageDown ? 1 : -1));
        }
        else if (args.Key == VirtualKey.Enter && search.FocusState != FocusState.Unfocused && pageButtons.Count > 0)
        {
            args.Handled = true;
            Launch(visible[page * PageSize]);
        }
    }
}

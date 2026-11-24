using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed class AppMenuBackplates : IDisposable
{
    private readonly UISettings system;
    private readonly AccessibilitySettings accessibility;
    private readonly Func<PopupBackplatePalette> resolvePalette;
    private readonly DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<MenuFlyoutPresenter, Backplate> open = [];
    private MenuFlyout? activeMenu;
    private PopupBackplatePalette activePalette;
    private AIEnter.DesktopGlassBackdrop? mainGlass;

    internal AppMenuBackplates(UISettings system, AccessibilitySettings accessibility,
        Func<PopupBackplatePalette> resolvePalette)
    {
        this.system = system;
        this.accessibility = accessibility;
        this.resolvePalette = resolvePalette;
        system.AdvancedEffectsEnabledChanged += OnAdvancedEffectsChanged;
    }

    internal void Begin(MenuFlyout menu)
    {
        activeMenu = menu;
        // The flyout can briefly take focus from the application used to choose the bar's appearance.
        // Keep the same material for this menu and all its submenus until the menu closes.
        activePalette = resolvePalette();
        bool effects = system.AdvancedEffectsEnabled && !accessibility.HighContrast;
        mainGlass = new(allowGlassFallback: true);
        mainGlass.SetBlur(effects ? activePalette.Blur : 0);
        menu.SystemBackdrop = effects && activePalette.Blur > 0 ? mainGlass : null;
        var style = new Style(typeof(MenuFlyoutPresenter));
        style.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, activePalette.Theme));
        style.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(effects ? activePalette.Tint : activePalette.Solid)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(PopupBackplatePalette.Radius)));
        menu.MenuFlyoutPresenterStyle = style;
    }

    internal void End(MenuFlyout menu)
    {
        if (!ReferenceEquals(activeMenu, menu)) return;
        activeMenu = null;
        foreach (var presenter in open.Keys.ToArray()) Detach(presenter);
        menu.SystemBackdrop = null;
        mainGlass = null;
    }

    internal void Watch(MenuFlyout menu, MenuFlyoutItemBase item, bool main = false)
    {
        item.Loaded += (_, _) => Attach(menu, item, main);
        if (item.IsLoaded) Attach(menu, item, main);
    }

    private void Attach(MenuFlyout menu, MenuFlyoutItemBase item, bool main)
    {
        if (!ReferenceEquals(activeMenu, menu)) return;
        // WinUI creates a native presenter for each submenu when the submenu opens.
        for (var parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not MenuFlyoutPresenter presenter) continue;
            if (open.ContainsKey(presenter)) return;
            var backplate = main
                ? new Backplate(presenter, menu, null, mainGlass!)
                : new Backplate(presenter, null, FindPopup(presenter), new(allowGlassFallback: true));
            open.Add(presenter, backplate);
            presenter.Unloaded += OnPresenterUnloaded;
            Apply(backplate, activePalette);
            return;
        }
    }

    private static Popup? FindPopup(MenuFlyoutPresenter presenter)
    {
        if (presenter.XamlRoot is not { } root) return null;
        var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root);
        for (DependencyObject? parent = presenter; parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is Popup popup) return popup;
            foreach (var candidate in popups)
                if (ReferenceEquals(candidate.Child, parent)) return candidate;
        }
        return null;
    }

    private void OnPresenterUnloaded(object sender, RoutedEventArgs args) => Detach((MenuFlyoutPresenter)sender);

    private void Detach(MenuFlyoutPresenter presenter)
    {
        if (!open.Remove(presenter, out var backplate)) return;
        presenter.Unloaded -= OnPresenterUnloaded;
        backplate.Dispose();
    }

    private void OnAdvancedEffectsChanged(UISettings sender, object args) => dispatcher.TryEnqueue(Refresh);

    private void Refresh()
    {
        if (activeMenu is null || open.Count == 0) return;
        var palette = accessibility.HighContrast ? resolvePalette() : activePalette;
        foreach (var backplate in open.Values) Apply(backplate, palette);
    }

    private void Apply(Backplate backplate, PopupBackplatePalette palette)
    {
        bool contrast = accessibility.HighContrast;
        var edge = contrast ? system.GetColorValue(UIColorType.Foreground)
            : palette.Theme == ElementTheme.Dark ? Color.FromArgb(82, 255, 255, 255) : Color.FromArgb(56, 35, 58, 84);
        backplate.Apply(palette, edge, !contrast && system.AdvancedEffectsEnabled);
    }

    public void Dispose()
    {
        system.AdvancedEffectsEnabledChanged -= OnAdvancedEffectsChanged;
        if (activeMenu is { } menu) End(menu);
    }

    private sealed class Backplate : IDisposable
    {
        private readonly MenuFlyoutPresenter presenter;
        private readonly MenuFlyout? menu;
        private readonly Popup? popup;
        private readonly SolidColorBrush tint = new();
        private readonly SolidColorBrush edge = new();
        private readonly AIEnter.DesktopGlassBackdrop glass;
        private PopupBackplatePalette palette;
        private bool effectsEnabled;

        internal Backplate(MenuFlyoutPresenter presenter, MenuFlyout? menu, Popup? popup, AIEnter.DesktopGlassBackdrop glass)
        {
            this.presenter = presenter;
            this.menu = menu;
            this.popup = popup;
            this.glass = glass;
            presenter.CornerRadius = new CornerRadius(PopupBackplatePalette.Radius);
            presenter.Background = tint;
            presenter.BorderBrush = edge;
            presenter.BorderThickness = new Thickness(1);
            presenter.SizeChanged += OnSizeChanged;
            glass.GlassUnavailable += RefreshTint;
            glass.SetMotion(1, 0);
            Resize(presenter.ActualWidth, presenter.ActualHeight);
        }

        internal void Apply(PopupBackplatePalette next, Color border, bool enableEffects)
        {
            palette = next;
            effectsEnabled = enableEffects;
            presenter.RequestedTheme = next.Theme;
            edge.Color = border;
            glass.SetBlur(enableEffects ? next.Blur : 0);
            SystemBackdrop = enableEffects && next.Blur > 0 && glass.CanUseGlass ? glass : null;
            RefreshTint();
        }

        private SystemBackdrop? SystemBackdrop
        {
            set
            {
                if (menu is not null)
                {
                    if (!ReferenceEquals(menu.SystemBackdrop, value)) menu.SystemBackdrop = value;
                }
                else if (popup is not null)
                {
                    if (!ReferenceEquals(popup.SystemBackdrop, value)) popup.SystemBackdrop = value;
                }
                else if (!ReferenceEquals(presenter.SystemBackdrop, value)) presenter.SystemBackdrop = value;
            }
        }

        private void RefreshTint() => tint.Color = effectsEnabled && glass.CanUseGlass ? palette.Tint : palette.Solid;

        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Resize(args.NewSize.Width, args.NewSize.Height);

        private void Resize(double width, double height)
        {
            var size = new Vector2((float)width, (float)height);
            glass.SetShape(size, size, PopupBackplatePalette.Radius);
        }

        public void Dispose()
        {
            presenter.SizeChanged -= OnSizeChanged;
            glass.GlassUnavailable -= RefreshTint;
            SystemBackdrop = null;
            presenter.ClearValue(Control.BackgroundProperty);
            presenter.ClearValue(Control.BorderBrushProperty);
            presenter.ClearValue(Control.BorderThicknessProperty);
            presenter.ClearValue(Control.CornerRadiusProperty);
            presenter.ClearValue(FrameworkElement.RequestedThemeProperty);
        }
    }
}

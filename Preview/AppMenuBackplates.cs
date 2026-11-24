using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    internal AppMenuBackplates(UISettings system, AccessibilitySettings accessibility,
        Func<PopupBackplatePalette> resolvePalette)
    {
        this.system = system;
        this.accessibility = accessibility;
        this.resolvePalette = resolvePalette;
        system.AdvancedEffectsEnabledChanged += OnAdvancedEffectsChanged;
    }

    internal void Watch(MenuFlyoutItemBase item)
    {
        item.Loaded += OnItemLoaded;
        if (item.IsLoaded) Attach(item);
    }

    private void OnItemLoaded(object sender, RoutedEventArgs args) => Attach((MenuFlyoutItemBase)sender);

    private void Attach(MenuFlyoutItemBase item)
    {
        // WinUI creates a native presenter for each submenu when the submenu opens.
        for (var parent = VisualTreeHelper.GetParent(item); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is not MenuFlyoutPresenter presenter) continue;
            if (open.ContainsKey(presenter)) return;
            var backplate = new Backplate(presenter);
            open.Add(presenter, backplate);
            presenter.Unloaded += OnPresenterUnloaded;
            Apply(backplate, resolvePalette());
            return;
        }
    }

    private void OnPresenterUnloaded(object sender, RoutedEventArgs args) => Detach((MenuFlyoutPresenter)sender);

    private void Detach(MenuFlyoutPresenter presenter)
    {
        if (!open.Remove(presenter, out var backplate)) return;
        presenter.Unloaded -= OnPresenterUnloaded;
        backplate.Dispose();
    }

    private void OnAdvancedEffectsChanged(UISettings sender, object args) => dispatcher.TryEnqueue(Refresh);

    internal void Refresh()
    {
        if (open.Count == 0) return;
        var palette = resolvePalette();
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
        foreach (var presenter in open.Keys.ToArray()) Detach(presenter);
    }

    private sealed class Backplate : IDisposable
    {
        private readonly MenuFlyoutPresenter presenter;
        private readonly SolidColorBrush tint = new();
        private readonly SolidColorBrush edge = new();
        private readonly AIEnter.DesktopGlassBackdrop glass = new(allowGlassFallback: true);
        private PopupBackplatePalette palette;
        private bool effectsEnabled;

        internal Backplate(MenuFlyoutPresenter presenter)
        {
            this.presenter = presenter;
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
            if (enableEffects && next.Blur > 0 && glass.CanUseGlass)
                presenter.SystemBackdrop ??= glass;
            RefreshTint();
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
            presenter.SystemBackdrop = null;
        }
    }
}

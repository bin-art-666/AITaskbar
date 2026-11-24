using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal sealed partial class WindowThumbnailPopup
{
    private const int ExitMs = 100, ReflowMs = 150, LastWindowMs = 120;
    private const int HoverOffset = 6;
    private readonly UISettings animationSettings = new();
    private readonly AccessibilitySettings animationAccessibility = new();
    private readonly Dictionary<(nint Handle, uint ProcessId), ImageSource> closingImages = [];
    private List<PreviewCard> cards = [];
    private CardAnimation? animation;
    private HoverAnimation? hoverAnimation;
    private double hoverOpacity = 1, hoverOffset;
    private int presentation;
    private int captures;
    private bool Animate => animationSettings.AnimationsEnabled && !animationAccessibility.HighContrast;
    internal int HoverDurationMs { get; set; } = PreviewIconLayout.DefaultThumbnailAnimationMs;

    private sealed class PreviewCard(Source source, Canvas visual, Grid image, TextBlock placeholder, Rect layout, Rect imageArea)
    {
        internal Source Source { get; set; } = source;
        internal Canvas Visual { get; } = visual;
        internal Grid Image { get; } = image;
        internal TextBlock Placeholder { get; } = placeholder;
        internal Rect Layout { get; } = layout;
        internal Rect ImageArea { get; } = imageArea;
        internal Rect Drawn { get; set; }
        internal double Alpha { get; set; } = 1;
        internal bool HasSnapshot { get; set; }
        internal CompositeTransform Transform { get; } = new();
    }

    private sealed record CardMotion(PreviewCard Card, Rect From, Rect To, double Alpha, bool Exiting);
    private sealed record CardAnimation(long Started, RectInt32 From, RectInt32 To, CardMotion[] Motions,
        Grid? Navigation, bool Last);
    private sealed record ExitImage(int Width, int Height, byte[] Pixels);
    private sealed record HoverAnimation(long Started, int DurationMs, double Opacity, double Offset, bool Hide);

    internal bool ResumeHover()
    {
        if (!visible || windows.Count == 0) return false;
        if (hoverAnimation is { Hide: true }) StartVisibilityAnimation(hide: false);
        return true;
    }

    internal void FadeOutOnLeave()
    {
        // The final card already has its own exit animation.
        if (!visible || windows.Count == 0 || hoverAnimation is { Hide: true }) return;
        if (!Animate) { Dismiss(); return; }
        StartVisibilityAnimation(hide: true);
    }

    private double OffsetTowardAnchor => above ? HoverOffset : -HoverOffset;

    private void PrepareReveal()
    {
        SetPopupOpacity(hoverOpacity = 0);
        hoverOffset = OffsetTowardAnchor;
    }

    private void StartVisibilityAnimation(bool hide)
    {
        if (hoverAnimation is null) CompositionTarget.Rendering += RenderVisibility;
        hoverAnimation = new(Stopwatch.GetTimestamp(), HoverDurationMs, hoverOpacity, hoverOffset, hide);
    }

    private void RenderVisibility(object? sender, object args)
    {
        if (hoverAnimation is not { } current) return;
        int duration = current.DurationMs;
        double elapsed = Animate ? Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds : duration;
        double progress = Progress(elapsed, duration);
        SetPopupOpacity(hoverOpacity = Mix(current.Opacity, current.Hide ? 0 : 1, progress));
        hoverOffset = Mix(current.Offset, current.Hide ? OffsetTowardAnchor : 0, progress);
        PositionPopup();
        foreach (var card in cards) DrawCard(card, card.Drawn, card.Alpha);
        if (elapsed < duration) return;
        StopVisibilityAnimation();
        if (current.Hide) Dismiss();
    }

    private void StopVisibilityAnimation()
    {
        if (hoverAnimation is null) return;
        CompositionTarget.Rendering -= RenderVisibility;
        hoverAnimation = null;
    }

    private async Task CloseWithSnapshotAsync(RunningWindow window)
    {
        var request = close;
        if (request is null) return;
        int opened = presentation;
        var identity = Identity(window);
        if (Animate && !closingImages.ContainsKey(identity) && Volatile.Read(ref captures) < 2)
        {
            Interlocked.Increment(ref captures);
            var capture = Task.Run(() =>
            {
                try { return CaptureExitImage(window); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Runtime.InteropServices.ExternalException)
                {
                    Debug.WriteLine(error);
                    return null;
                }
                finally { Interlocked.Decrement(ref captures); }
            });
            try
            {
                var image = await capture.WaitAsync(TimeSpan.FromMilliseconds(90));
                if (image is not null && opened == presentation)
                {
                    if (closingImages.Count >= 5) closingImages.Remove(closingImages.Keys.First());
                    var bitmap = new WriteableBitmap(image.Width, image.Height);
                    using var buffer = bitmap.PixelBuffer.AsStream();
                    buffer.Write(image.Pixels);
                    bitmap.Invalidate();
                    closingImages[identity] = bitmap;
                }
            }
            catch (TimeoutException) { }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or
                System.Runtime.InteropServices.ExternalException)
            {
                Debug.WriteLine(error);
            }
        }
        request(window);
    }

    private static ExitImage? CaptureExitImage(RunningWindow window)
    {
        if (WindowMotionNative.Capture(window) is not { } snapshot) return null;
        double scale = Math.Min(1, 480d / Math.Max(snapshot.Width, snapshot.Height));
        if (scale == 1) return new(snapshot.Width, snapshot.Height, snapshot.Pixels);
        int width = Math.Max(1, (int)Math.Round(snapshot.Width * scale));
        int height = Math.Max(1, (int)Math.Round(snapshot.Height * scale));
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * snapshot.Height / height * snapshot.Width + x * snapshot.Width / width) * 4;
            snapshot.Pixels.AsSpan(offset, 4).CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
        }
        return new(width, height, pixels);
    }

    private void ShowCards(List<PreviewCard> next, RectInt32 target, Grid? navigation, bool animate)
    {
        StopCardAnimation();
        var oldCards = cards;
        bool reflow = visible && animate && Animate && oldCards.Count > 0;
        panel.Children.Clear();
        if (!reflow)
            foreach (var source in windows) HideThumbnail(source);
        cards = next;
        foreach (var card in cards) AddVisual(card);
        if (!reflow)
        {
            SetBounds(target);
            foreach (var card in cards) DrawCard(card, AtScreen(card.Layout, target), 1);
            if (navigation is not null) panel.Children.Add(navigation);
            return;
        }

        var previous = oldCards.ToDictionary(card => Identity(card.Source.Window));
        var motions = new List<CardMotion>();
        foreach (var card in next.ToArray())
        {
            var end = AtScreen(card.Layout, target);
            bool existed = previous.Remove(Identity(card.Source.Window), out var old);
            motions.Add(new(card, existed ? old!.Drawn : ScaleCard(end, .96), end, existed ? old!.Alpha : 0, false));
        }
        foreach (var old in previous.Values)
        {
            var current = windows.Find(source => Identity(source.Window) == Identity(old.Source.Window));
            PrepareExit(old, current);
            cards.Add(old);
            AddVisual(old);
            var end = ScaleCard(old.Drawn, .88);
            // Move the departing card inside the shrinking window until its fade completes.
            end.X = Math.Max(target.X / scale, Math.Min(end.X, (target.X + target.Width) / scale - end.Width));
            end.Y = Math.Max(target.Y / scale, Math.Min(end.Y, (target.Y + target.Height) / scale - end.Height));
            motions.Add(new(old, old.Drawn, end, old.Alpha, true));
        }
        if (navigation is not null)
        {
            navigation.Opacity = 0;
            panel.Children.Add(navigation);
        }
        StartCardAnimation(new(Stopwatch.GetTimestamp(), bounds, target, motions.ToArray(), navigation, Last: false));
    }

    private void FadeLastCard()
    {
        if (!visible || !Animate || cards.Count == 0) { Dismiss(); return; }
        StopVisibilityAnimation();
        StopCardAnimation();
        var motions = new List<CardMotion>();
        foreach (var card in cards)
        {
            PrepareExit(card, null);
            motions.Add(new(card, card.Drawn, ScaleCard(card.Drawn, .88), card.Alpha, true));
        }
        StartCardAnimation(new(Stopwatch.GetTimestamp(), bounds, bounds, motions.ToArray(), null, Last: true));
    }

    private void PrepareExit(PreviewCard card, Source? current)
    {
        card.Visual.IsHitTestVisible = false;
        foreach (var button in card.Visual.Children.OfType<Button>()) button.IsTabStop = false;
        if (current is not null) return;
        card.Source = card.Source with { Thumbnail = 0 };
        card.Placeholder.Visibility = Visibility.Collapsed;
        if (closingImages.Remove(Identity(card.Source.Window), out var image))
        {
            card.Image.Children.Add(new Image { Source = image, Stretch = Stretch.Uniform });
            card.HasSnapshot = true;
        }
    }

    private void AddVisual(PreviewCard card)
    {
        card.Visual.RenderTransform = card.Transform;
        panel.Children.Add(card.Visual);
    }

    private void StartCardAnimation(CardAnimation next)
    {
        animation = next;
        DrawAnimation(next, 0);
        CompositionTarget.Rendering += RenderCards;
    }

    private void RenderCards(object? sender, object args)
    {
        if (animation is not { } current) return;
        double elapsed = Stopwatch.GetElapsedTime(current.Started).TotalMilliseconds;
        DrawAnimation(current, elapsed);
        if (elapsed < (current.Last ? LastWindowMs : ReflowMs)) return;
        StopCardAnimation();
        if (current.Last) { Dismiss(); return; }
        foreach (var motion in current.Motions.Where(motion => motion.Exiting))
        {
            HideThumbnail(motion.Card.Source);
            panel.Children.Remove(motion.Card.Visual);
            cards.Remove(motion.Card);
        }
    }

    private void DrawAnimation(CardAnimation current, double elapsed)
    {
        double slide = Progress(elapsed, current.Last ? LastWindowMs : ReflowMs);
        double exit = Progress(elapsed, ExitMs);
        SetBounds(new(
            (int)Math.Round(Mix(current.From.X, current.To.X, slide)),
            (int)Math.Round(Mix(current.From.Y, current.To.Y, slide)),
            (int)Math.Round(Mix(current.From.Width, current.To.Width, slide)),
            (int)Math.Round(Mix(current.From.Height, current.To.Height, slide))));
        if (current.Last) SetPopupOpacity(hoverOpacity * (1 - slide));
        foreach (var motion in current.Motions)
        {
            double progress = motion.Exiting ? exit : slide;
            double alpha = motion.Exiting ? motion.Alpha * (1 - exit) : Mix(motion.Alpha, 1, slide);
            DrawCard(motion.Card, Interpolate(motion.From, motion.To, progress), alpha);
        }
        if (current.Navigation is { } navigation) navigation.Opacity = slide;
    }

    private void DrawCard(PreviewCard card, Rect screen, double opacity)
    {
        card.Drawn = screen;
        card.Alpha = opacity;
        double x = screen.X - bounds.X / scale, y = screen.Y - bounds.Y / scale;
        double width = screen.Width / card.Layout.Width, height = screen.Height / card.Layout.Height;
        card.Transform.TranslateX = x;
        card.Transform.TranslateY = y;
        card.Transform.ScaleX = width;
        card.Transform.ScaleY = height;
        card.Visual.Opacity = opacity;
        if (card.Source.Thumbnail == 0 || card.HasSnapshot) return;
        var image = card.ImageArea;
        var area = new Rect(x + image.X * width, y + image.Y * height, image.Width * width, image.Height * height);
        // These native visuals do not inherit the opacity or transform of the XAML cards.
        double thumbnailOpacity = opacity * hoverOpacity;
        if (thumbnailOpacity <= .005) HideThumbnail(card.Source);
        else card.Placeholder.Visibility = ShowThumbnail(card.Source, area, thumbnailOpacity) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBounds(RectInt32 next)
    {
        bounds = next;
        var size = new System.Numerics.Vector2((float)(next.Width / scale), (float)(next.Height / scale));
        glass.SetShape(size, size, BackplateRadius);
        PositionPopup();
    }

    private void PositionPopup()
    {
        var next = bounds;
        next.Y = Math.Clamp(bounds.Y + Pixel(hoverOffset), workArea.Y, workArea.Y + workArea.Height - bounds.Height);
        if (positionedBounds.Equals(next)) return;
        positionedBounds = next;
        AppWindow.MoveAndResize(next);
    }

    private static void HideThumbnail(Source source)
    {
        if (source.Thumbnail == 0) return;
        var hide = new ThumbnailProperties { Flags = VisibilityFlag };
        DwmUpdateThumbnailProperties(source.Thumbnail, in hide);
    }

    private Rect AtScreen(Rect layout, RectInt32 window) =>
        new(layout.X + window.X / scale, layout.Y + window.Y / scale, layout.Width, layout.Height);

    private static Rect ScaleCard(Rect rect, double factor) =>
        new(rect.X + rect.Width * (1 - factor) / 2, rect.Y + rect.Height * (1 - factor) / 2, rect.Width * factor, rect.Height * factor);

    private static Rect Interpolate(Rect from, Rect to, double amount) =>
        new(Mix(from.X, to.X, amount), Mix(from.Y, to.Y, amount), Mix(from.Width, to.Width, amount), Mix(from.Height, to.Height, amount));

    private static double Mix(double from, double to, double amount) => from + (to - from) * amount;

    private static double Progress(double elapsed, int duration)
    {
        double remaining = 1 - Math.Clamp(elapsed / duration, 0, 1);
        return 1 - remaining * remaining * remaining;
    }

    private void StopCardAnimation()
    {
        if (animation is null) return;
        CompositionTarget.Rendering -= RenderCards;
        animation = null;
    }

    private void ClearCardAnimation()
    {
        StopCardAnimation();
        StopVisibilityAnimation();
        presentation++;
        cards.Clear();
        closingImages.Clear();
        panel.Children.Clear();
        SetPopupOpacity(hoverOpacity = 1);
        hoverOffset = 0;
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI;
using Windows.UI.ViewManagement;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace AITaskbar;

internal sealed class PreviewModuleIcons : IDisposable
{
    private const int Side = 64;
    private readonly UISettings system = new();
    private readonly DispatcherQueueTimer timer;
    private readonly Halo.RingAppearanceBridge? ringBridge = Halo.App.RingBridge;
    private readonly Image ai = new() { Stretch = Stretch.Uniform };
    private readonly Image halo = new() { Stretch = Stretch.Uniform };
    private readonly ImageSource staticAi = CreateStaticAiIcon();
    private readonly WriteableBitmap flow = new(Side, Side), ring = new(Side, Side);
    private readonly byte[] aiPixels = new byte[Side * Side * 4], ringPixels = new byte[Side * Side * 4];
    private readonly Grid glow = new() { IsHitTestVisible = false };
    private readonly Grid aiHost;
    private readonly Button aiButton, haloButton;
    private AIEnter.UiAppearance? lastAi;
    private Halo.UiAppearance? lastHalo;
    private bool hover, light, animations, highContrast;
    private double time, angle, hoverOpacity;
    private long lastTick;

    public FrameworkElement AiEntrance => aiHost;
    internal Image AiIcon => ai;

    internal static ImageSource CreateStaticAiIcon() =>
        new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AIEnter.png")));

    internal static ImageSource CreateStaticHaloIcon(Halo.UiAppearance appearance, bool light, Color? foreground)
    {
        var bitmap = new WriteableBitmap(Side, Side);
        var color = RingColor(appearance, light, foreground);
        DrawRing(bitmap, new byte[Side * Side * 4], appearance.RingThickness, _ => color);
        return bitmap;
    }

    public PreviewModuleIcons(Button haloButton, Button aiButton, DispatcherQueue dispatcher)
    {
        this.aiButton = aiButton;
        this.haloButton = haloButton;
        Prepare(haloButton, halo);
        Prepare(aiButton, ai);
        halo.Source = ring;
        ai.Source = staticAi;
        aiHost = new Grid { Height = 48, VerticalAlignment = VerticalAlignment.Center };
        aiHost.Children.Add(glow);
        aiHost.Children.Add(aiButton);
        haloButton.PointerEntered += (_, _) => { hover = true; Refresh(); };
        haloButton.PointerExited += (_, _) => { hover = false; Refresh(); };
        timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        timer.Tick += (_, _) => Paint();
        AIEnter.App.TaskbarAppearanceChanged += Refresh;
        if (ringBridge is not null) ringBridge.Changed += Refresh;
        RefreshSystem();
    }

    public void RefreshSystem(bool? lightTheme = null)
    {
        light = lightTheme ?? (system.GetColorValue(UIColorType.Background).R >= 128);
        animations = system.AnimationsEnabled;
        highContrast = new AccessibilitySettings().HighContrast;
        Refresh();
    }

    private void Refresh()
    {
        var nextAi = AIEnter.App.Appearance;
        if (lastAi != nextAi)
        {
            ai.Width = ai.Height = nextAi.TaskbarIconSize;
            aiButton.Width = aiButton.Height = Math.Max(32, nextAi.TaskbarIconSize + 4);
            aiButton.CornerRadius = new CornerRadius(aiButton.Width / 2);
            aiHost.Width = Math.Max(aiButton.Width, nextAi.TaskbarIconSize + 2 * nextAi.GlowRange);
            aiHost.Clip = new RectangleGeometry { Rect = new(0, 0, aiHost.Width, 48) };
            glow.Children.Clear();
            for (var layer = 0; layer < 16; layer++)
            {
                var diameter = nextAi.TaskbarIconSize + 2 * nextAi.GlowRange * (16 - layer) / 16.0;
                glow.Children.Add(new Ellipse
                {
                    Width = diameter, Height = diameter, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, Fill = new SolidColorBrush(Color.FromArgb((byte)(5 + layer / 3), 115, 145, 255))
                });
            }
        }
        lastAi = nextAi;
        glow.Opacity = !highContrast && nextAi.GlowEnabled && nextAi.GlowRange > 0 && AIEnter.App.Ai.IsOpen
            ? nextAi.GlowIntensity / 100.0 : 0;
        var nextHalo = Halo.App.Appearance;
        if (lastHalo != nextHalo)
        {
            halo.Width = halo.Height = nextHalo.TaskbarIconSize;
            haloButton.Width = haloButton.Height = Math.Max(36, nextHalo.TaskbarIconSize + 8);
            haloButton.CornerRadius = new CornerRadius(haloButton.Width / 2);
        }
        lastHalo = nextHalo;
        if (!animations || highContrast) ai.Source = staticAi;
        if (!timer.IsRunning) lastTick = Environment.TickCount64;
        Paint();
    }

    private void Paint()
    {
        if (lastAi is not { } appearance || lastHalo is not { } haloAppearance) return;
        var now = Environment.TickCount64;
        var dt = Math.Min((now - lastTick) / 1000.0, .1);
        lastTick = now;
        bool animateAi = animations && !highContrast && appearance.FlowSpeed > 0;
        if (animateAi)
        {
            time += dt * appearance.FlowSpeed / 100.0;
            PaintAi();
            if (!ReferenceEquals(ai.Source, flow)) ai.Source = flow;
        }

        var pulse = ringBridge?.ImportPulse ?? 0;
        uint age = (unchecked((uint)(now / 16)) - (pulse & 0x7FFFFFFFu)) & 0x7FFFFFFFu;
        bool failed = (pulse & 0x80000000u) != 0;
        var import = pulse != 0 && age < (failed ? 750u : 75u) ? failed ? 2 : 1 : 0;
        bool animateHalo = animations && !highContrast && haloAppearance.RingHoverFlow;
        hoverOpacity = animateHalo ? Math.Clamp(hoverOpacity + (hover ? 1 : -1) * dt / .15, 0, 1) : 0;
        angle = (angle + dt * Math.Tau / 2.4 * haloAppearance.RingFlowSpeed / 100.0) % Math.Tau;
        if (lastRing != (haloAppearance, light, highContrast, hover, hoverOpacity, import) || hoverOpacity > 0 || import == 1)
        {
            PaintRing(haloAppearance, animateHalo, import, age);
            lastRing = (haloAppearance, light, highContrast, hover, hoverOpacity, import);
        }
        bool running = animateAi || animateHalo && (hover || hoverOpacity > 0) || import != 0;
        if (running && !timer.IsRunning) timer.Start();
        else if (!running && timer.IsRunning) timer.Stop();
    }

    private (Halo.UiAppearance? Appearance, bool Light, bool HighContrast, bool Hover, double Opacity, int Import) lastRing;

    private void PaintRing(Halo.UiAppearance appearance, bool animate, int import, uint age)
    {
        uint baseColor = RingColor(appearance, light, highContrast ? system.GetColorValue(UIColorType.Foreground) : null);
        uint accent = highContrast ? baseColor : light ? Mix(baseColor, 0, .65) : 0xFFFFFF;
        if (!animate && hover) baseColor = accent;
        if (import == 1 && !highContrast) accent = light ? 0x006B61u : 0x84F1D3u;
        if (import == 2 && !highContrast) baseColor = light ? 0x943C00u : 0xFFB462u;
        var importAngle = age / 75.0 * Math.Tau + Math.Tau * .75;
        DrawRing(ring, ringPixels, appearance.RingThickness, phase =>
        {
            double weight = hoverOpacity * FlowWeight(angle, phase, appearance.RingFlowLength);
            if (import == 1) weight = Math.Max(weight, animations && !highContrast ? FlowWeight(importAngle, phase, 38) : 1);
            return Mix(baseColor, accent, weight);
        }, import == 2);
    }

    private static uint RingColor(Halo.UiAppearance appearance, bool light, Color? foreground) =>
        foreground is { } value ? (uint)(value.R << 16 | value.G << 8 | value.B) :
        Halo.UiAppearance.ColorValue(light ? appearance.RingLightColor : appearance.RingColor,
            light ? 0xFF505050u : 0xFFE6E6E6u);

    private static void DrawRing(WriteableBitmap bitmap, byte[] pixels, int thickness, Func<double, uint> colorAt, bool interrupted = false)
    {
        var inner = Side * (.5 - thickness / 100.0);
        for (var y = 0; y < Side; y++)
        for (var x = 0; x < Side; x++)
        {
            double dx = x + .5 - Side / 2.0, dy = y + .5 - Side / 2.0;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            double phase = Math.Atan2(dy, dx) + Math.Tau;
            double alpha = Math.Clamp(Math.Min(radius - inner + .5, Side / 2.0 - radius), 0, 1);
            if (interrupted) alpha *= Math.Clamp((Math.Abs(phase - Math.Tau * .75) - .21) / .07, 0, 1);
            WritePixel(pixels, (y * Side + x) * 4, alpha > 0 ? colorAt(phase) : 0, alpha);
        }
        Present(bitmap, pixels);
    }

    private static double FlowWeight(double head, double phase, int length)
    {
        var t = ((head - phase + 2 * Math.Tau) % Math.Tau) / (Math.Tau * length / 100.0);
        if (t <= 0 || t >= 1) return 0;
        double front = Math.Clamp(t / .07, 0, 1);
        return Math.Clamp(front * front * (3 - 2 * front) * (1 - t) * (1 - t) / .9, 0, 1);
    }

    private void PaintAi()
    {
        // The same moving color sources and circular mask as the Explorer button.
        Span<(double X, double Y, int R, int G, int B)> sources = stackalloc (double, double, int, int, int)[]
        {
            (-.48 + .25 * Math.Sin(time * .24), -.30 + .25 * Math.Cos(time * .19), 255, 159, 202),
            (.22 + .32 * Math.Cos(time * .21), -.50 + .23 * Math.Sin(time * .27), 255, 240, 143),
            (.48 + .30 * Math.Sin(time * .18 + 1), .26 + .33 * Math.Cos(time * .25), 143, 87, 249),
            (-.22 + .34 * Math.Cos(time * .23 + 2), .55 + .23 * Math.Sin(time * .20), 113, 230, 247),
            (-.05 + .35 * Math.Sin(time * .16 + 3), -.05 + .36 * Math.Cos(time * .22), 240, 167, 248)
        };
        for (var y = 0; y < Side; y++)
        for (var x = 0; x < Side; x++)
        {
            double nx = (x + .5) / (Side / 2.0) - 1, ny = (y + .5) / (Side / 2.0) - 1;
            var radius = nx * nx + ny * ny;
            var alpha = Math.Clamp((1 - Math.Sqrt(radius)) * Side / 2.0, 0, 1);
            int offset = (y * Side + x) * 4;
            if (alpha == 0) { WritePixel(aiPixels, offset, 0, 0); continue; }
            double sum = 0, r = 0, g = 0, b = 0;
            foreach (var source in sources)
            {
                var dx = nx - source.X; var dy = ny - source.Y;
                var weight = Math.Exp(-5.5 * (dx * dx + dy * dy));
                sum += weight; r += weight * source.R; g += weight * source.G; b += weight * source.B;
            }
            var lightness = .82 + .18 * Math.Sqrt(Math.Max(0, 1 - radius));
            var shine = .24 * Math.Exp(-7 * ((nx + .32) * (nx + .32) + (ny + .48) * (ny + .48)));
            uint Channel(double channel) => (uint)Math.Clamp(channel / sum * lightness * (1 - shine) + 255 * shine, 0, 255);
            WritePixel(aiPixels, offset, Channel(r) << 16 | Channel(g) << 8 | Channel(b), alpha);
        }
        Present(flow, aiPixels);
    }

    private static uint Mix(uint from, uint to, double weight)
    {
        uint Channel(int shift) => (uint)(((from >> shift) & 255) * (1 - weight) + ((to >> shift) & 255) * weight);
        return Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    private static void WritePixel(byte[] pixels, int offset, uint rgb, double alpha)
    {
        pixels[offset] = (byte)((rgb & 255) * alpha);
        pixels[offset + 1] = (byte)(((rgb >> 8) & 255) * alpha);
        pixels[offset + 2] = (byte)(((rgb >> 16) & 255) * alpha);
        pixels[offset + 3] = (byte)(255 * alpha);
    }

    private static void Present(WriteableBitmap bitmap, byte[] pixels)
    {
        using var buffer = bitmap.PixelBuffer.AsStream();
        buffer.Write(pixels);
        bitmap.Invalidate();
    }

    private static void Prepare(Button button, Image image)
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Background = button.BorderBrush = clear;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        button.VerticalAlignment = VerticalAlignment.Center;
        button.Padding = new Thickness(0);
        button.BorderThickness = new Thickness(0);
        foreach (var state in new[] { "PointerOver", "Pressed" })
        {
            button.Resources["ButtonBackground" + state] = clear;
            button.Resources["ButtonBorderBrush" + state] = clear;
        }
        button.Content = image;
    }

    public void Dispose()
    {
        timer.Stop();
        AIEnter.App.TaskbarAppearanceChanged -= Refresh;
        if (ringBridge is not null) ringBridge.Changed -= Refresh;
    }
}

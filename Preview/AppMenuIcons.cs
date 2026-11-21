using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AITaskbar;

internal enum AppMenuIcon { Open, NewWindow, PrivateWindow, RecentFile, Pin, Unpin, CloseWindow, CloseAllWindows, EndTask, Empty }

internal sealed class AppMenuFlyoutItem : MenuFlyoutItem
{
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        // Keep the stock menu behavior while allowing an 18-DIP icon.
        if (GetTemplateChild("IconRoot") is Viewbox slot)
            slot.Width = slot.Height = AppMenuIcons.Size;
    }
}

internal sealed class AppMenuIcons(UISettings system, AccessibilitySettings accessibility)
{
    internal const int Size = 18;
    private const float Center = Size / 2f, LineWidth = 1.15f;
    private readonly Dictionary<(AppMenuIcon, Palette), WriteableBitmap> bitmaps = [];

    private readonly record struct Palette(Color Ink, Color Ring);

    internal IconElement Create(AppMenuIcon icon, ElementTheme theme, bool enabled = true)
    {
        var element = new ImageIcon { Width = Size, Height = Size };
        if (icon == AppMenuIcon.Empty) return element;
        var key = (icon, Colors(theme, enabled));
        if (!bitmaps.TryGetValue(key, out var bitmap))
        {
            bitmap = Draw(icon, key.Item2);
            bitmaps.Add(key, bitmap);
        }
        element.Source = bitmap;
        return element;
    }

    private Palette Colors(ElementTheme theme, bool enabled)
    {
        if (accessibility.HighContrast)
        {
            var ink = enabled ? system.GetColorValue(UIColorType.Foreground) : system.UIElementColor(UIElementType.GrayText);
            return new(ink, ink);
        }
        bool dark = theme == ElementTheme.Dark;
        if (!enabled)
        {
            var gray = dark ? Rgb(157, 169, 183) : Rgb(107, 118, 132);
            return new(gray, gray);
        }
        return dark
            ? new(Rgb(231, 240, 251), Rgb(125, 143, 165))
            : new(Rgb(38, 59, 85), Rgb(157, 174, 194));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    private static WriteableBitmap Draw(AppMenuIcon icon, Palette palette)
    {
        // Render on a shared 18-DIP coordinate system with every mark inside the ring.
        using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), Size, Size, 288);
        using (var drawing = target.CreateDrawingSession())
        using (var caps = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round })
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            drawing.DrawCircle(Center, Center, 8.35f, palette.Ring, 1);
            drawing.Transform = Matrix3x2.CreateScale(.92f, new Vector2(Center, Center));

            void Line(float x1, float y1, float x2, float y2) =>
                drawing.DrawLine(x1, y1, x2, y2, palette.Ink, LineWidth, caps);

            void Cross(float x, float y, float radius)
            {
                Line(x - radius, y - radius, x + radius, y + radius);
                Line(x + radius, y - radius, x - radius, y + radius);
            }

            switch (icon)
            {
                case AppMenuIcon.Open:
                    Line(6.2f, 11.8f, 11.8f, 6.2f);
                    Line(6.6f, 6.2f, 11.8f, 6.2f);
                    Line(11.8f, 6.2f, 11.8f, 11.4f);
                    break;
                case AppMenuIcon.NewWindow:
                    Line(5.4f, Center, 12.6f, Center);
                    Line(Center, 5.4f, Center, 12.6f);
                    break;
                case AppMenuIcon.PrivateWindow:
                    drawing.DrawCircle(6.8f, Center, 1.55f, palette.Ink, LineWidth);
                    drawing.DrawCircle(11.2f, Center, 1.55f, palette.Ink, LineWidth);
                    Line(8.35f, Center, 9.65f, Center);
                    Line(4.25f, 7.75f, 5.35f, 8.65f);
                    Line(12.65f, 8.65f, 13.75f, 7.75f);
                    break;
                case AppMenuIcon.RecentFile:
                    drawing.DrawRoundedRectangle(5.9f, 4.9f, 6.2f, 8.2f, .75f, .75f, palette.Ink, 1);
                    Line(7.7f, 8.0f, 10.3f, 8.0f);
                    Line(7.7f, 10.1f, 10.3f, 10.1f);
                    break;
                case AppMenuIcon.Pin:
                case AppMenuIcon.Unpin:
                    DrawPin(drawing, caps, palette.Ink);
                    if (icon == AppMenuIcon.Unpin)
                    {
                        // Clear the space around the slash so the menu shows through.
                        drawing.Blend = CanvasBlend.Copy;
                        drawing.DrawLine(5.05f, 5.05f, 12.95f, 12.95f, Microsoft.UI.Colors.Transparent, 2.5f, caps);
                        drawing.Blend = CanvasBlend.SourceOver;
                        Line(5.05f, 5.05f, 12.95f, 12.95f);
                    }
                    break;
                case AppMenuIcon.CloseWindow:
                    Cross(Center, Center, 2.85f);
                    break;
                case AppMenuIcon.CloseAllWindows:
                    Line(7.35f, 4.9f, 13.05f, 4.9f);
                    Line(13.05f, 4.9f, 13.05f, 10.55f);
                    drawing.DrawRoundedRectangle(4.95f, 6.85f, 6.15f, 6.25f, .65f, .65f, palette.Ink, .95f);
                    Cross(8.025f, 9.975f, 1.3f);
                    break;
                case AppMenuIcon.EndTask:
                    drawing.FillRoundedRectangle(6.6f, 6.6f, 4.8f, 4.8f, .6f, .6f, palette.Ink);
                    break;
            }
        }

        var bitmap = new WriteableBitmap((int)target.SizeInPixels.Width, (int)target.SizeInPixels.Height);
        using var buffer = bitmap.PixelBuffer.AsStream();
        buffer.Write(target.GetPixelBytes());
        bitmap.Invalidate();
        return bitmap;
    }

    private static void DrawPin(CanvasDrawingSession drawing, CanvasStrokeStyle caps, Color ink)
    {
        using var path = new CanvasPathBuilder(drawing);
        path.BeginFigure(6.4f, 5.1f);
        path.AddLine(11.6f, 5.1f);
        path.AddLine(10.45f, 6.65f);
        path.AddLine(10.45f, 8.55f);
        path.AddLine(12.1f, 10.15f);
        path.AddLine(12.1f, 10.75f);
        path.AddLine(5.9f, 10.75f);
        path.AddLine(5.9f, 10.15f);
        path.AddLine(7.55f, 8.55f);
        path.AddLine(7.55f, 6.65f);
        path.EndFigure(CanvasFigureLoop.Closed);
        using var outline = CanvasGeometry.CreatePath(path);
        drawing.DrawGeometry(outline, ink, 1.05f, caps);
        drawing.DrawLine(Center, 10.75f, Center, 13.15f, ink, LineWidth, caps);
    }
}

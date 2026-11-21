using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;

namespace AITaskbar;

internal sealed class WindowMotionMesh : IDisposable
{
    private const int Columns = 10;
    private const int Rows = 16;
    private const int Stride = Columns + 1;
    private readonly CanvasImageBrush brush;
    private readonly CanvasGeometry?[] triangles = new CanvasGeometry[Columns * Rows * 2];
    private readonly Vector2[] vertices = new Vector2[Stride * (Rows + 1)];
    private readonly Rect window, icon, content;
    private readonly float cellWidth, cellHeight;

    internal WindowMotionMesh(CanvasDevice device, CanvasBitmap image, Rect window, Rect icon, Rect content)
    {
        this.window = window;
        this.icon = icon;
        this.content = content;
        cellWidth = image.SizeInPixels.Width / (float)Columns;
        cellHeight = image.SizeInPixels.Height / (float)Rows;
        brush = new CanvasImageBrush(device, image)
        {
            SourceRectangle = new Rect(0, 0, image.SizeInPixels.Width, image.SizeInPixels.Height),
            Interpolation = CanvasImageInterpolation.Anisotropic,
            ExtendX = CanvasEdgeBehavior.Clamp,
            ExtendY = CanvasEdgeBehavior.Clamp
        };
        try
        {
            for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
            {
                var a = new Vector2(column * cellWidth, row * cellHeight);
                var b = new Vector2((column + 1) * cellWidth, a.Y);
                var c = new Vector2(a.X, (row + 1) * cellHeight);
                var d = new Vector2(b.X, c.Y);
                int index = (row * Columns + column) * 2;
                triangles[index] = CanvasGeometry.CreatePolygon(device, [a, b, c]);
                triangles[index + 1] = CanvasGeometry.CreatePolygon(device, [d, c, b]);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void Draw(CanvasDrawingSession drawing, double extent, bool opening)
    {
        UpdateVertices(extent, opening);
        // Rasterize shared edges once so neighboring texture patches do not show transparent seams.
        drawing.Antialiasing = CanvasAntialiasing.Aliased;
        for (int row = 0; row < Rows; row++)
        for (int column = 0; column < Columns; column++)
        {
            int corner = row * Stride + column;
            var a = vertices[corner];
            var b = vertices[corner + 1];
            var c = vertices[corner + Stride];
            var d = vertices[corner + Stride + 1];
            int index = (row * Columns + column) * 2;
            DrawTriangle(drawing, index, a, b - a, c - a, column, row);
            DrawTriangle(drawing, index + 1, d, d - c, d - b, column + 1, row + 1);
        }
    }

    private void DrawTriangle(CanvasDrawingSession drawing, int index, Vector2 anchor, Vector2 across, Vector2 down,
        int column, int row)
    {
        var origin = anchor - across * column - down * row;
        across /= cellWidth;
        down /= cellHeight;
        drawing.Transform = new Matrix3x2(across.X, across.Y, down.X, down.Y, origin.X, origin.Y);
        drawing.FillGeometry(triangles[index]!, brush);
    }

    private void UpdateVertices(double extent, bool opening)
    {
        // Move the far edge out first while the near edge stays close to the icon.
        // Closing reaches the narrow silhouette sooner, then gathers it into the icon.
        double head = SmoothStep(0, opening ? .49 : .53, extent);
        double tail = SmoothStep(opening ? .20 : .09, opening ? .89 : .83, extent);
        double spread = SmoothStep(opening ? .25 : .38, 1, extent);
        double iconX = icon.X + icon.Width / 2;
        double windowX = window.X + window.Width / 2;
        bool iconAbove = icon.Y + icon.Height / 2 < window.Y + window.Height / 2;
        double top = Mix(icon.Top, window.Top, iconAbove ? tail : head);
        double bottom = Mix(icon.Bottom, window.Bottom, iconAbove ? head : tail);
        double headX = Mix(iconX, windowX, .25 * head + .75 * spread);
        double tailX = Mix(iconX, windowX, .35 * tail + .65 * spread * spread);
        double centerX = (headX + tailX) / 2;
        double hullLeft = Math.Min(icon.Left, window.Left);
        double hullRight = Math.Max(icon.Right, window.Right);
        double headWidth = Mix(icon.Width, window.Width, Math.Pow(spread, .85));
        double tailWidth = Mix(icon.Width, window.Width, spread * spread);
        double bend = Math.Min(MaximumBend(window), (bottom - top) * .23) * Math.Sin(Math.PI * spread);
        double bendSpan = (Math.Max(headWidth, tailWidth) + Math.Abs(headX - tailX)) / 2;
        double direction = iconX < windowX ? -1 : 1;
        double iconSideLimit = iconAbove ? Math.Min(icon.Top, window.Top) : Math.Max(icon.Bottom, window.Bottom);
        double bendLimit = BendMargin(window);

        for (int row = 0; row <= Rows; row++)
        {
            double v = content.Y + content.Height * row / Rows;
            double towardTail = iconAbove ? 1 - v : v;
            double edgeProgress = SmoothStep(0, 1, towardTail);
            double width = Mix(headWidth, tailWidth, edgeProgress);
            double center = Math.Clamp(Mix(headX, tailX, edgeProgress), hullLeft + width / 2, hullRight - width / 2);
            double baseY = Mix(top, bottom, v);
            double roomAtIcon = Math.Min(1, Math.Abs(iconSideLimit - baseY) / bendLimit);
            for (int column = 0; column <= Columns; column++)
            {
                double u = content.X + content.Width * column / Columns;
                double x = center + (u - .5) * width;
                double side = Math.Clamp((x - centerX) / bendSpan, -1, 1);
                // Bend the textured rows across their width, including the interior of the window.
                double offset = bend * (.55 * direction * side + .45 * (1 - side * side));
                // The tip can touch the icon without dipping past its far edge.
                if (iconAbove ? offset < 0 : offset > 0) offset *= roomAtIcon;
                double y = baseY + offset;
                vertices[row * Stride + column] = new Vector2((float)x, (float)y);
            }
        }
    }

    private static double MaximumBend(Rect window) => Math.Min(window.Height * .27, window.Width * .18);

    internal static int BendMargin(Rect window) => (int)Math.Ceiling(MaximumBend(window) * .62) + 1;

    internal static double SmoothStep(double start, double end, double value)
    {
        double t = Math.Clamp((value - start) / (end - start), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static double Mix(double from, double to, double amount) => from + (to - from) * amount;

    public void Dispose()
    {
        foreach (var triangle in triangles) triangle?.Dispose();
        brush.Dispose();
    }
}

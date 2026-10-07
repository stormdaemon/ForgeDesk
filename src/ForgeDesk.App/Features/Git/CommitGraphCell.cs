using System.Windows;
using System.Windows.Media;
using ForgeDesk.Presentation.Git;

namespace ForgeDesk.App.Features.Git;

/// <summary>
/// Draws the graph cell of one History row (<see cref="CommitGraphRow"/>): the lines crossing the
/// row and the commit's node. One lightweight element per row, rendered straight to the drawing
/// context with frozen pens, so scrolling thousands of commits stays smooth.
/// </summary>
public sealed class CommitGraphCell : FrameworkElement
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(
        nameof(Row), typeof(CommitGraphRow), typeof(CommitGraphCell), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsHeadProperty = DependencyProperty.Register(
        nameof(IsHead), typeof(bool), typeof(CommitGraphCell), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LaneWidthProperty = DependencyProperty.Register(
        nameof(LaneWidth), typeof(double), typeof(CommitGraphCell), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double LeftPadding = 2;
    private const double LineThickness = 1.6;

    // Mid-tone lane colors, readable on both light and dark surfaces (index = GraphEdge.Color).
    private static readonly Color[] LaneColors =
    [
        Color.FromRgb(0xF2, 0x76, 0x2E), // ember
        Color.FromRgb(0x3B, 0x82, 0xF6), // blue
        Color.FromRgb(0x22, 0xA0, 0x6B), // green
        Color.FromRgb(0xA8, 0x55, 0xF7), // purple
        Color.FromRgb(0xE5, 0x48, 0x4D), // red
        Color.FromRgb(0x0E, 0xA5, 0xE9), // sky
        Color.FromRgb(0xD2, 0x99, 0x22), // gold
        Color.FromRgb(0xEC, 0x48, 0x99), // pink
    ];

    private static readonly SolidColorBrush[] LaneBrushes = LaneColors.Select(Freeze).ToArray();
    private static readonly Pen[] LanePens = LaneBrushes.Select(b => Freeze(new Pen(b, LineThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round })).ToArray();

    public CommitGraphCell()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public CommitGraphRow? Row
    {
        get => (CommitGraphRow?)GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>The commit checked out: its node gets an accent ring.</summary>
    public bool IsHead
    {
        get => (bool)GetValue(IsHeadProperty);
        set => SetValue(IsHeadProperty, value);
    }

    public double LaneWidth
    {
        get => (double)GetValue(LaneWidthProperty);
        set => SetValue(LaneWidthProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        var row = Row;
        var height = ActualHeight;
        var width = ActualWidth;
        if (row is null || row.LaneCount == 0 || height <= 0 || width <= 0)
        {
            return;
        }

        var middle = height / 2;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        foreach (var edge in row.Edges)
        {
            var pen = LanePens[Math.Abs(edge.Color) % LanePens.Length];
            var (top, bottom) = edge.Part == GraphEdgePart.Upper ? (0.0, middle) : (middle, height);
            var from = new Point(X(edge.From), top);
            var to = new Point(X(edge.To), bottom);
            if (edge.From == edge.To)
            {
                drawingContext.DrawLine(pen, from, to);
                continue;
            }

            // A smooth S-curve between two lanes.
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(from, isFilled: false, isClosed: false);
                var bend = (bottom - top) * 0.6;
                context.BezierTo(new Point(from.X, top + bend), new Point(to.X, bottom - bend), to, isStroked: true, isSmoothJoin: true);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }

        var center = new Point(X(row.Lane), middle);
        var brush = LaneBrushes[Math.Abs(row.Color) % LaneBrushes.Length];
        if (IsHead && TryFindResource("ForgeAccentBrush") is Brush accent)
        {
            drawingContext.DrawEllipse(null, new Pen(accent, 1.5), center, 6.5, 6.5);
        }

        var radius = row.IsMerge ? 3.2 : 4.2;
        drawingContext.DrawEllipse(brush, null, center, radius, radius);
        drawingContext.Pop();
    }

    private double X(int lane) => LeftPadding + (lane * LaneWidth) + (LaneWidth / 2);

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}

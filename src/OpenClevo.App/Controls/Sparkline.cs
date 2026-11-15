using System.Windows;
using System.Windows.Media;

namespace OpenClevo.App.Controls;

/// <summary>
/// History chart: temperature as a thin line over a faint fill, fan duty as a hairline underneath, both 0–100.
/// No grid, no glow; one baseline and a dot on the latest value.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    IReadOnlyList<double> _a = [], _b = [];
    int _capacity = 90;

    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public void SetData(IReadOnlyList<double> primary, IReadOnlyList<double> secondary, int capacity)
    {
        _a = primary; _b = secondary; _capacity = capacity;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // spread the samples over the full width (fresh start: few samples still fill the card); 20–100 range keeps small changes visible
        double X(int i, int n) => 4 + i * ((w - 8) / Math.Max(n - 1, 1));
        double Y(double v) => h - 4 - (Math.Clamp(v, 20, 100) - 20) / 80 * (h - 10);

        if (_b.Count > 1)
        {
            var pts = Enumerable.Range(0, _b.Count).Select(i => new Point(X(i, _b.Count), Y(_b[i]))).ToList();
            dc.DrawGeometry(null, Theme.Pen("Faint", 1.25, Colors.Gray), Smooth(pts, false, h));
        }
        if (_a.Count > 1)
        {
            var pts = Enumerable.Range(0, _a.Count).Select(i => new Point(X(i, _a.Count), Y(_a[i]))).ToList();
            var fill = new LinearGradientBrush(Theme.WithAlpha(Accent, 46), Theme.WithAlpha(Accent, 0), 90);
            fill.Freeze();
            dc.DrawGeometry(fill, null, Smooth(pts, true, h));
            var pen = new Pen(new SolidColorBrush(Accent), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, pen, Smooth(pts, false, h));
            dc.DrawEllipse(new SolidColorBrush(Accent), null, pts[^1], 3, 3);
        }
    }

    /// <summary>Catmull-Rom-ish smoothing (cubic Béziers through the samples) so 1 Hz data reads as a curve, not a staircase.</summary>
    static Geometry Smooth(List<Point> p, bool closed, double h)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(closed ? new Point(p[0].X, h) : p[0], closed, closed);
            if (closed) ctx.LineTo(p[0], false, false);
            for (int i = 0; i < p.Count - 1; i++)
            {
                var p0 = p[Math.Max(i - 1, 0)]; var p1 = p[i]; var p2 = p[i + 1]; var p3 = p[Math.Min(i + 2, p.Count - 1)];
                var c1 = new Point(p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6);
                ctx.BezierTo(c1, c2, p2, true, true);
            }
            if (closed) ctx.LineTo(new Point(p[^1].X, h), false, false);
        }
        g.Freeze();
        return g;
    }
}

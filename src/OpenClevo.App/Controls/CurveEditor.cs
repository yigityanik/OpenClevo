using OpenClevo.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenClevo.Core;

namespace OpenClevo.App.Controls;

/// <summary>
/// Fan curve chart (x: 30–100 °C, y: 0–100 % duty in steps of ten). Points 2 and 3 can be dragged; point 1 is firmware-fixed and
/// point 4 is always (100 °C, 100 %). Shows the live temperature as a vertical marker. Colors come from the active theme.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    const double TMin = 30, TMax = 100;
    const double PadL = 42, PadR = 10, PadT = 8, PadB = 24;

    FanCurve? _curve;
    int _drag = -1, _hover = -1;
    double? _liveTemp;

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Color), typeof(CurveEditor), new FrameworkPropertyMetadata(Colors.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Accent { get => (Color)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public event Action<FanCurve>? CurveEdited;

    public FanCurve? Curve
    {
        get => _curve;
        set { _curve = value; InvalidateVisual(); }
    }

    public double? LiveTemperature
    {
        get => _liveTemp;
        set { _liveTemp = value; InvalidateVisual(); }
    }

    public CurveEditor()
    {
        Cursor = Cursors.Arrow;
        Focusable = false;
    }

    Rect Plot => new(PadL, PadT, Math.Max(1, ActualWidth - PadL - PadR), Math.Max(1, ActualHeight - PadT - PadB));
    Point ToScreen(double t, double d) { var p = Plot; return new(p.Left + (t - TMin) / (TMax - TMin) * p.Width, p.Bottom - d / 100 * p.Height); }
    (double T, double D) FromScreen(Point s) { var p = Plot; return (TMin + (s.X - p.Left) / p.Width * (TMax - TMin), (p.Bottom - s.Y) / p.Height * 100); }

    (int T, int D)[] Points(FanCurve c) => [(c.T1, c.D1), (c.T2, c.D2), (c.T3, c.D3), (FanCurve.T4, FanCurve.D4)];

    static readonly Typeface Face = new("Segoe UI Variable Text");

    void Label(DrawingContext dc, string s, Point at, Brush b, double size = 11, bool center = false, bool right = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, center ? new Point(at.X - ft.Width / 2, at.Y) : right ? new Point(at.X - ft.Width, at.Y) : at);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var p = Plot;
        var muted = Theme.Brush("Muted", Colors.Gray);
        var text = Theme.Brush("Text", Colors.White);
        var faint = Theme.Brush("Faint", Colors.Gray);
        var gridPen = Theme.Pen("Line", 1, Colors.Gray);
        var surface = Theme.Brush("Bg", Colors.Black);

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        // duty axis in steps of ten; hairlines only, temperature labels underneath
        for (int d = 0; d <= 100; d += 10)
        {
            var y = ToScreen(TMin, d).Y;
            dc.DrawLine(gridPen, new Point(p.Left, y), new Point(p.Right, y));
            Label(dc, d + "%", new Point(p.Left - 8, y - 6.5), faint, 10, right: true);
        }
        // temperature axis in steps of ten with a faint vertical hairline, and a small tick every five degrees between
        var vb = Theme.Brush("Line", Colors.Gray).Clone(); vb.Opacity = 0.6;
        var vPen = new Pen(vb, 1);
        for (int t = (int)TMin; t <= (int)TMax; t += 5)
        {
            double x = ToScreen(t, 0).X;
            if (t % 10 == 0)
            {
                dc.DrawLine(vPen, new Point(x, p.Top), new Point(x, p.Bottom));
                Label(dc, t + "°", new Point(x, p.Bottom + 6), faint, 10, center: true);
            }
            else dc.DrawLine(gridPen, new Point(x, p.Bottom), new Point(x, p.Bottom + 4));
        }

        if (_curve == null) { Label(dc, Loc.T("fan.curve_unreadable"), new Point(p.Left + p.Width / 2, p.Top + p.Height / 2 - 8), muted, 13, center: true); return; }

        var pts = Points(_curve);
        var accent = new SolidColorBrush(Accent);

        // the curve: one line, flat from TMin to the first point; no fill
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(ToScreen(TMin, pts[0].D), false, false);
            foreach (var (t, d) in pts) ctx.LineTo(ToScreen(t, d), true, true);
        }
        dc.DrawGeometry(null, new Pen(accent, 1.75) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);

        // live temperature: a dashed hairline and a small dot on the curve
        if (_liveTemp is { } lt && lt >= TMin)
        {
            var top = ToScreen(lt, _curve.DutyAt(lt));
            dc.DrawLine(new Pen(faint, 1) { DashStyle = new DashStyle([2, 3], 0) }, new Point(top.X, p.Bottom), top);
            dc.DrawEllipse(text, null, top, 3, 3);
        }

        // handles: small rings; their value shows only while hovered or dragged
        for (int i = 0; i < pts.Length; i++)
        {
            var s = ToScreen(pts[i].T, pts[i].D);
            if (i is 1 or 2)
            {
                bool active = i == _drag || i == _hover;
                dc.DrawEllipse(surface, new Pen(accent, 2), s, active ? 6.5 : 5, active ? 6.5 : 5);
                if (active) Label(dc, $"{pts[i].T}° · %{pts[i].D}", new Point(s.X - 12, s.Y - 22), text, 11, right: true);
            }
            else dc.DrawEllipse(accent, null, s, 2.5, 2.5);
        }
    }
    int HitTest(Point m)
    {
        if (_curve == null) return -1;
        var pts = Points(_curve);
        for (int i = 1; i <= 2; i++)
            if ((ToScreen(pts[i].T, pts[i].D) - m).Length <= 14) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (!IsEnabled) return;
        _drag = HitTest(e.GetPosition(this));
        if (_drag >= 0) { CaptureMouse(); e.Handled = true; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var m = e.GetPosition(this);
        if (_drag >= 0 && _curve != null)
        {
            var (t, d) = FromScreen(m);
            int ti = (int)Math.Round(t), di = (int)Math.Round(d);
            var c = _drag == 1 ? _curve with { T2 = ti, D2 = di } : _curve with { T3 = ti, D3 = di };
            // keep the point being dragged between its neighbours
            c = _drag == 1
                ? c with { T2 = Math.Clamp(ti, c.T1 + 1, c.T3 - 1), D2 = Math.Clamp(di, c.D1 + 1, c.D3 - 1) }
                : c with { T3 = Math.Clamp(ti, c.T2 + 1, 99), D3 = Math.Clamp(di, c.D2 + 1, 99) };
            if (c != _curve) { _curve = c; InvalidateVisual(); CurveEdited?.Invoke(c); }
            return;
        }
        int h = IsEnabled ? HitTest(m) : -1;
        Cursor = h >= 0 ? Cursors.Hand : Cursors.Arrow;
        if (h != _hover) { _hover = h; InvalidateVisual(); }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_drag >= 0) { _drag = -1; ReleaseMouseCapture(); InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != -1) { _hover = -1; InvalidateVisual(); }
    }
}

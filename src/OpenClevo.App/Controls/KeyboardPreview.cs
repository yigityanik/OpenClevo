using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenClevo.App.Controls;

/// <summary>
/// A laptop keyboard drawn from plain rounded rectangles (our own generic layout, six rows plus a four-column numpad). Keys take the colour of
/// their backlight zone; the window feeds them the live (or previewed) effect colours. Multi-zone keyboards can be
/// clicked to pick a zone.
/// </summary>
public sealed class KeyboardPreview : FrameworkElement
{
    // key widths in units, rows sum to MainW. Row 0 (function row) is drawn lower than the rest.
    static readonly double[][] Rows =
    [
        [1.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.75],
        [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2],
        [1.5, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1.5],
        [1.75, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.25],
        [2.25, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2.75],
        [1.25, 1.25, 1.25, 1.25, 6.25, 1, 1, 0.5833, 0.5833, 0.5834],
    ];
    // numpad keys as (column, row, width, height) in units; rows count like Rows. Row 0 aligns with the function row (height RowH0).
    static readonly (double C, int R, double W, double H)[] Numpad =
    [
        (0, 0, 1, 1), (1, 0, 1, 1), (2, 0, 1, 1), (3, 0, 1, 1),
        (0, 1, 1, 1), (1, 1, 1, 1), (2, 1, 1, 1), (3, 1, 1, 1),
        (0, 2, 1, 1), (1, 2, 1, 1), (2, 2, 1, 1), (3, 2, 1, 2),
        (0, 3, 1, 1), (1, 3, 1, 1), (2, 3, 1, 1),
        (0, 4, 1, 1), (1, 4, 1, 1), (2, 4, 1, 1), (3, 4, 1, 2),
        (0, 5, 2, 1), (2, 5, 1, 1),
    ];
    const double RowH0 = 0.62, Gap = 0.14, TotalH = RowH0 + 5;
    const double MainW = 15, NumpadGap = 0.5, NumpadW = 4, TotalW = MainW + NumpadGap + NumpadW;

    public static readonly DependencyProperty ColorsProperty = DependencyProperty.Register(nameof(Colors), typeof(Color[]), typeof(KeyboardPreview),
        new FrameworkPropertyMetadata(new[] { Color.FromRgb(0, 0, 255), Color.FromRgb(0, 0, 255), Color.FromRgb(0, 0, 255) }, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty BrightnessProperty = DependencyProperty.Register(nameof(Brightness), typeof(int), typeof(KeyboardPreview),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedZoneProperty = DependencyProperty.Register(nameof(SelectedZone), typeof(int), typeof(KeyboardPreview),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SingleZoneProperty = DependencyProperty.Register(nameof(SingleZone), typeof(bool), typeof(KeyboardPreview),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color[] Colors { get => (Color[])GetValue(ColorsProperty); set => SetValue(ColorsProperty, value); }
    /// <summary>0 = off … 4 = full.</summary>
    public int Brightness { get => (int)GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }
    public int SelectedZone { get => (int)GetValue(SelectedZoneProperty); set => SetValue(SelectedZoneProperty, value); }
    public bool SingleZone { get => (bool)GetValue(SingleZoneProperty); set => SetValue(SingleZoneProperty, value); }
    public event Action<int>? ZoneClicked;

    protected override Size MeasureOverride(Size a)
    {
        double w = double.IsInfinity(a.Width) ? 520 : a.Width;
        return new Size(w, w * TotalH / TotalW);
    }

    (Rect Rect, int Zone)[] Layout()
    {
        double u = ActualWidth / TotalW, y = 0;
        var keys = new List<(Rect, int)>();
        for (int r = 0; r < Rows.Length; r++)
        {
            double h = (r == 0 ? RowH0 : 1) * u, x = 0;
            foreach (var wk in Rows[r])
            {
                var rect = new Rect(x + Gap * u / 2, y + Gap * u / 2, wk * u - Gap * u, h - Gap * u);
                double cx = (x + wk * u / 2) / ActualWidth;
                keys.Add((rect, cx < 1 / 3.0 ? 0 : cx < 2 / 3.0 ? 1 : 2));
                x += wk * u;
            }
            y += h;
        }
        foreach (var (c, r, wk, hk) in Numpad)
        {
            double x = (MainW + NumpadGap + c) * u, ky = r == 0 ? 0 : (RowH0 + r - 1) * u, kh = r == 0 ? RowH0 * u : hk * u;
            var rect = new Rect(x + Gap * u / 2, ky + Gap * u / 2, wk * u - Gap * u, kh - Gap * u);
            double cx = (x + wk * u / 2) / ActualWidth;
            keys.Add((rect, cx < 1 / 3.0 ? 0 : cx < 2 / 3.0 ? 1 : 2));
        }
        return keys.ToArray();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 40) return;
        double u = ActualWidth / TotalW, radius = Math.Max(2.5, u * 0.16);
        double bright = Brightness <= 0 ? 0 : 0.30 + 0.50 * Brightness / 4.0;
        var off = Theme.Color("CardHi", System.Windows.Media.Colors.DimGray);
        var colors = Colors;

        foreach (var (rect, zone) in Layout())
        {
            var c = colors[SingleZone ? 0 : Math.Min(zone, colors.Length - 1)];

            var fill = Mix(off, c, bright);
            dc.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, radius, radius);
            if (!SingleZone && SelectedZone == zone)
                dc.DrawRoundedRectangle(null, new Pen(Brushes.White, 1.5), Rect.Inflate(rect, -0.75, -0.75), radius, radius);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SingleZone) return;
        var p = e.GetPosition(this);
        foreach (var (rect, zone) in Layout())
            if (rect.Contains(p)) { ZoneClicked?.Invoke(zone); e.Handled = true; return; }
        // clicks in the gaps still pick the nearest third
        ZoneClicked?.Invoke(Math.Clamp((int)(p.X / ActualWidth * 3), 0, 2));
    }

    static Color Mix(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}

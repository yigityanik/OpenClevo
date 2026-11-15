using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace OpenClevo.App.Controls;

public sealed class ChargeRange : FrameworkElement
{
    public static readonly DependencyProperty StartProperty = DependencyProperty.Register(
        nameof(Start), typeof(double), typeof(ChargeRange), new FrameworkPropertyMetadata(40d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StopProperty = DependencyProperty.Register(
        nameof(Stop), typeof(double), typeof(ChargeRange), new FrameworkPropertyMetadata(80d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(ChargeRange), new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(ChargeRange), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Start { get => (double)GetValue(StartProperty); set => SetValue(StartProperty, value); }
    public double Stop { get => (double)GetValue(StopProperty); set => SetValue(StopProperty, value); }
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        if (w < 10 || ActualHeight < 20) return;
        const double left = 4, right = 4, y = 7, barHeight = 3;
        double x0 = left, x100 = w - right;
        double X(double value) => x0 + Math.Clamp(value, 0, 100) / 100 * (x100 - x0);
        var track = Theme.Brush("TrackOff", Colors.Gray);
        dc.DrawRoundedRectangle(track, null, new Rect(x0, y, x100 - x0, barHeight), barHeight / 2, barHeight / 2);
        var range = Theme.Brush(Active ? "Accent" : "Faint", Colors.Gray);
        dc.DrawRoundedRectangle(range, null, new Rect(X(Start), y, Math.Max(0, X(Stop) - X(Start)), barHeight), barHeight / 2, barHeight / 2);
        if (Level >= 0)
            dc.DrawLine(Theme.Pen("Text", 1, Colors.Black), new Point(X(Level), y - 3), new Point(X(Level), y + barHeight + 3));

        var labelBrush = Theme.Brush("Faint", Colors.Gray);
        var typeface = new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        void Label(string text, double x, TextAlignment alignment)
        {
            var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, labelBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double textX = alignment == TextAlignment.Center ? x - formatted.Width / 2
                : alignment == TextAlignment.Right ? x - formatted.Width : x;
            dc.DrawText(formatted, new Point(textX, 13));
        }
        Label("0", x0, TextAlignment.Left);
        Label("50", (x0 + x100) / 2, TextAlignment.Center);
        Label("100", x100, TextAlignment.Right);
    }
}

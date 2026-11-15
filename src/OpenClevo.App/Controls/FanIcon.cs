using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenClevo.App.Controls;

/// <summary>
/// A small seven-blade fan. The spin is a WPF animation on a RenderTransform, so it runs on the compositor thread and
/// stays smooth even when the UI thread is busy. The speed follows the RPM (scaled down so it stays readable) and is
/// only restarted when it changes noticeably, continuing from the current angle.
/// </summary>
public sealed class FanIcon : FrameworkElement
{
    public static readonly DependencyProperty RpmProperty = DependencyProperty.Register(nameof(Rpm), typeof(int), typeof(FanIcon),
        new PropertyMetadata(0, (d, _) => ((FanIcon)d).UpdateSpin()));
    public int Rpm { get => (int)GetValue(RpmProperty); set => SetValue(RpmProperty, value); }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(FanIcon),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Blade colour; the theme's muted text colour when not set.</summary>
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    readonly RotateTransform _rot = new();
    double _dps;   // degrees per second currently animated
    static readonly Geometry Blade = MakeBlade();

    public FanIcon()
    {
        RenderTransform = _rot;
        RenderTransformOrigin = new Point(0.5, 0.5);
    }

    void UpdateSpin()
    {
        double target = Math.Min(Rpm * 0.12, 900);   // 3500 rpm ≈ 420 °/s
        if (Math.Abs(target - _dps) < Math.Max(10, _dps * 0.05)) return;
        _dps = target;
        double from = _rot.Angle % 360;
        if (target < 1) { _rot.BeginAnimation(RotateTransform.AngleProperty, null); _rot.Angle = from; return; }
        var spin = new DoubleAnimation(from, from + 360, new Duration(TimeSpan.FromSeconds(360 / target))) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(spin, 60);
        _rot.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    static Geometry MakeBlade()
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0.02, -0.16), true, true);
            c.BezierTo(new Point(0.42, -0.30), new Point(0.52, -0.72), new Point(0.30, -0.98), true, true);
            c.BezierTo(new Point(0.12, -0.80), new Point(0.04, -0.50), new Point(-0.05, -0.16), true, true);
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double r = Math.Min(ActualWidth, ActualHeight) / 2;
        if (r < 3) return;
        var fill = Fill ?? Theme.Brush("Muted", Colors.Gray);
        var hub = Theme.Brush("Card", Colors.Black);
        dc.PushTransform(new TranslateTransform(ActualWidth / 2, ActualHeight / 2));
        dc.PushTransform(new ScaleTransform(r, r));
        for (int i = 0; i < 7; i++)
        {
            dc.PushTransform(new RotateTransform(i * (360.0 / 7)));
            dc.DrawGeometry(fill, null, Blade);
            dc.Pop();
        }
        dc.Pop();
        dc.DrawEllipse(hub, null, new Point(0, 0), r * 0.16, r * 0.16);
        dc.Pop();
    }
}

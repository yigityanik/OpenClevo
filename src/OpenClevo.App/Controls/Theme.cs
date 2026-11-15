using System.Windows;
using System.Windows.Media;

namespace OpenClevo.App.Controls;

/// <summary>Reads theme colors at render time so the custom-drawn controls follow Dark/Light without extra plumbing.</summary>
static class Theme
{
    public static Color Color(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    public static Brush Brush(string key, Color fallback)
    {
        var br = new SolidColorBrush(Color(key, fallback));
        br.Freeze();
        return br;
    }

    public static Pen Pen(string key, double width, Color fallback, DashStyle? dash = null)
    {
        var p = new Pen(Brush(key, fallback), width) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dash != null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    public static Color WithAlpha(Color c, byte a) => System.Windows.Media.Color.FromArgb(a, c.R, c.G, c.B);
}

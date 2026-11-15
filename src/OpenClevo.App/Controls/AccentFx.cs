using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace OpenClevo.App.Controls;

/// <summary>
/// Drives the "ModeAccent" brush: the colour of the selected power-mode tile only. Modes without their own colour
/// use the theme accent. Everything else in the app keeps the theme's "Accent". Fades in ~180 ms.
/// </summary>
static class AccentFx
{
    static Color _current;
    static Color _from, _to;
    static int _step;
    const int Steps = 11;
    static readonly DispatcherTimer Timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };

    static AccentFx() => Timer.Tick += (_, _) =>
    {
        _step++;
        double k = 1 - Math.Pow(1 - Math.Min(_step / (double)Steps, 1), 3);   // ease-out
        Apply(Lerp(_from, _to, k));
        if (_step >= Steps) Timer.Stop();
    };

    static Color ThemeAccent() =>
        Application.Current.Resources.MergedDictionaries[0]["AccentColor"] is Color c ? c : Colors.DodgerBlue;

    /// <summary>null = the theme accent.</summary>
    public static void To(Color? c)
    {
        var target = c ?? ThemeAccent();
        if (_current == default) { _to = target; Apply(target); return; }
        if (target == _to) return;
        _from = _current; _to = target; _step = 0;
        Timer.Start();
    }

    static void Apply(Color c)
    {
        _current = c;
        var res = Application.Current.Resources;
        res["ModeAccent"] = new SolidColorBrush(c);
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        res["ModeOnAccent"] = new SolidColorBrush(lum > 0.52 ? Color.FromRgb(0x12, 0x12, 0x14) : Colors.White);
    }

    static Color Lerp(Color a, Color b, double k) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}

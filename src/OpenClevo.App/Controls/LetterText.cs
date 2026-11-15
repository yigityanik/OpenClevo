using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenClevo.App.Controls;

/// <summary>
/// A thin, large word used as a selector option. Selecting it grows it from 30 to 44 and fills its letters with the
/// mode colour one after another, left to right; deselecting reverses that. Everything is driven by one animated
/// <see cref="Progress"/> value, so the word keeps its baseline and neighbours move with it.
/// </summary>
public sealed class LetterText : FrameworkElement
{
    public const double IdleSize = 30, SelectedSize = 44;
    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Light, FontStretches.Normal);

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(LetterText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(nameof(Progress), typeof(double), typeof(LetterText),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(nameof(Selected), typeof(bool), typeof(LetterText),
        new PropertyMetadata(false, (d, e) => ((LetterText)d).OnSelected((bool)e.NewValue)));
    public static readonly DependencyProperty SelectedColorProperty = DependencyProperty.Register(nameof(SelectedColor), typeof(Color), typeof(LetterText),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HoverProperty = DependencyProperty.Register(nameof(Hover), typeof(bool), typeof(LetterText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    /// <summary>0 = idle (small, dim) … 1 = selected (large, coloured).</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool Selected { get => (bool)GetValue(SelectedProperty); set => SetValue(SelectedProperty, value); }
    public Color SelectedColor { get => (Color)GetValue(SelectedColorProperty); set => SetValue(SelectedColorProperty, value); }
    public bool Hover { get => (bool)GetValue(HoverProperty); set => SetValue(HoverProperty, value); }

    void OnSelected(bool on)
    {
        if (!IsLoaded) { BeginAnimation(ProgressProperty, null); Progress = on ? 1 : 0; return; }   // first layout: no animation
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(ProgressProperty, new DoubleAnimation(on ? 1 : 0, TimeSpan.FromMilliseconds(on ? 750 : 450)) { EasingFunction = ease });
    }

    FormattedText Make(string s, double size, Brush b) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    double CurrentSize => IdleSize + (SelectedSize - IdleSize) * Math.Clamp(Progress, 0, 1);

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Text ?? "";
        double w = text.Length == 0 ? 0 : Make(text, CurrentSize, Brushes.White).WidthIncludingTrailingWhitespace;
        double h = Make("Xg", SelectedSize, Brushes.White).Height;
        return new Size(Math.Ceiling(w) + 2, Math.Ceiling(h));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Text ?? "";
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));   // whole word is clickable
        if (text.Length == 0) return;

        double p = Math.Clamp(Progress, 0, 1), size = CurrentSize;
        Color idle = Theme.Color(Hover ? "Muted" : "Faint", Colors.Gray), sel = SelectedColor;
        int n = text.Length;
        double stagger = Math.Min(0.06, 0.5 / Math.Max(n - 1, 1)), window = 1 - stagger * (n - 1);
        double baseline = Make("Xg", SelectedSize, Brushes.White).Baseline;   // all words share the baseline of the large size

        for (int i = 0; i < n; i++)
        {
            if (text[i] == ' ') continue;
            double f = Math.Clamp((p - i * stagger) / window, 0, 1);
            var c = Color.FromRgb((byte)(idle.R + (sel.R - idle.R) * f), (byte)(idle.G + (sel.G - idle.G) * f), (byte)(idle.B + (sel.B - idle.B) * f));
            double x = i == 0 ? 0 : Make(text[..i], size, Brushes.White).WidthIncludingTrailingWhitespace;
            var ft = Make(text[i].ToString(), size, new SolidColorBrush(c));
            dc.DrawText(ft, new Point(x, baseline - ft.Baseline));
        }
    }
}

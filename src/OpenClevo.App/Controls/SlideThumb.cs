using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OpenClevo.App.Controls;

/// <summary>
/// Segmented-control selection that slides: a Border sitting behind a panel of RadioButtons glides to the checked
/// item (220 ms, ease-out) instead of the background jumping.
/// </summary>
static class SlideThumb
{
    /// <param name="overshoot">Slightly overshoots and settles, like a spring-loaded slider.</param>
    public static void Attach(Panel host, Border thumb, bool overshoot = false)
    {
        var move = new TranslateTransform();
        thumb.RenderTransform = move;
        thumb.HorizontalAlignment = HorizontalAlignment.Left;
        thumb.VerticalAlignment = VerticalAlignment.Top;
        thumb.IsHitTestVisible = false;
        thumb.Opacity = 0;
        IEasingFunction soft = new CubicEase { EasingMode = EasingMode.EaseOut };
        IEasingFunction spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        void Sync(bool animate)
        {
            var rb = host.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
            if (rb == null || rb.ActualWidth < 1 || thumb.Parent is not Visual parent) { thumb.Opacity = 0; return; }
            var pt = rb.TransformToAncestor(parent).Transform(new Point(0, 0));
            bool wasHidden = thumb.Opacity == 0;
            thumb.Opacity = 1;
            thumb.Height = rb.ActualHeight;
            move.Y = pt.Y;
            if (animate && !wasHidden)
            {
                int idx = host.Children.IndexOf(rb);
                bool edge = idx == 0 || idx == host.Children.Count - 1;   // overshooting an end item would leave the bar
                var ease = overshoot && !edge ? spring : soft;
                var dur = TimeSpan.FromMilliseconds(overshoot && !edge ? 340 : 240);
                move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(pt.X, dur) { EasingFunction = ease });
                thumb.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(rb.ActualWidth, dur) { EasingFunction = ease });
            }
            else
            {
                move.BeginAnimation(TranslateTransform.XProperty, null);
                thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
                move.X = pt.X;
                thumb.Width = rb.ActualWidth;
            }
        }

        host.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => Sync(true)));
        host.SizeChanged += (_, _) => Sync(false);
        host.Loaded += (_, _) => Sync(false);
    }
}

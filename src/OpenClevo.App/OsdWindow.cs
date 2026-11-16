using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenClevo.Localization;

namespace OpenClevo.App;

/// <summary>
/// Small on-screen notice for Fn keys ("Camera off"): bottom centre of the screen, never takes focus or clicks, and fades
/// out by itself. Always uses the dark surface of the app so it reads the same over any window.
/// </summary>
sealed class OsdWindow : Window
{
    static readonly Brush Card = Frozen("#1E1E21"), Line = Frozen("#2A2A2D"), Text = Frozen("#F5F5F7"), Accent = Frozen("#4CC2FF"), Muted = Frozen("#8E8E93");

    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets"), TextFont = new("Segoe UI Variable Text, Segoe UI");

    readonly TextBlock _icon = new()
    {
        FontFamily = IconFont,
        FontSize = 22,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 14, 0),
    };
    readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 15,
        Foreground = Text,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    public OsdWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        FlowDirection = Loc.Flow;
        Content = new Border
        {
            Margin = new Thickness(14),   // room for the shadow
            Padding = new Thickness(20, 13, 24, 13),
            Background = Card,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black },
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _icon, _text } },
        };
        SourceInitialized += (_, _) =>
        {
            // tool window, never activated, clicks pass through
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x80 | 0x08000000 | 0x20);
        };
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) =>
            {
                if (Opacity > 0.01) return;   // a new notice took over
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Hide();
            };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    /// <summary>Shows (or updates) the notice. <paramref name="on"/> colours the icon: accent when on, grey when off.</summary>
    public void Notify(string glyph, string text, bool on)
    {
        _icon.Text = glyph;
        // plain text "icons" (keycaps like "Aa") use the text font; private-use glyphs use the icon font
        bool keycap = glyph.Length > 0 && glyph[0] < 0xE000;
        _icon.FontFamily = keycap ? TextFont : IconFont;
        _icon.FontSize = keycap ? 17 : 22;
        _icon.FontWeight = keycap ? FontWeights.SemiBold : FontWeights.Normal;
        _icon.Foreground = on ? Accent : Muted;
        _text.Text = text;
        // keep the current (possibly half-faded) opacity as the base, so a repeated key continues from there instead of dipping to 0
        double now = IsVisible ? Opacity : 0;
        BeginAnimation(OpacityProperty, null);
        Opacity = now;
        if (!IsVisible) Show();
        UpdateLayout();
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Bottom - ActualHeight - 70;
        BeginAnimation(OpacityProperty, new DoubleAnimation(now, 1, TimeSpan.FromMilliseconds(120 * (1 - now) + 1)));
        _hide.Stop();
        _hide.Start();
    }

    static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}

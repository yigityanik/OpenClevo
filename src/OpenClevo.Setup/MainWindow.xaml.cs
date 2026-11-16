using OpenClevo.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OpenClevo.Setup;

public partial class MainWindow : Window
{
    // The language picker is a vertical column, measured from the approved mock: a 120 px view with 44 px rows,
    // the selected language 40 px, its neighbours 22 px at 60 % opacity, anything further away hidden. The dots sit on
    // the right and the scroll icon under the footer buttons.
    const double RowH = 44, ViewH = 120, VBig = 40, VNear = 22, VFar = 16;
    const double DotSize = 5, DotBigScale = 1.2;
    static readonly double NearScale = VNear / VBig, FarScale = VFar / VBig;
    const double NearOpacity = 0.6;

    static readonly Color TextColor = Color.FromRgb(0xEC, 0xEC, 0xEE), FaintColor = Color.FromRgb(0x5B, 0x5B, 0x60);
    static readonly Color DotOff = Color.FromRgb(0x2A, 0x2A, 0x2D);

    // the mock's CSS curves, as WPF key splines: transform "cubic-bezier(.22,.9,.25,1)" and the default "ease"
    static readonly KeySpline Slide = new(new Point(0.22, 0.9), new Point(0.25, 1));
    static readonly KeySpline Ease = new(new Point(0.25, 0.1), new Point(0.25, 1));

    /// <summary>Where the column must sit so that language <paramref name="i"/> is centred in the picker.</summary>
    static double StripPos(int i) => ViewH / 2 - RowH / 2 - i * RowH;

    int _page;                        // 0 welcome · 1 options · 2 install · 3 done
    readonly UIElement[] _pages;
    readonly List<TextBlock> _words = new();
    readonly List<ScaleTransform> _wordScales = new();
    readonly List<SolidColorBrush> _wordBrushes = new();
    readonly List<ScaleTransform> _dotScales = new();
    readonly List<SolidColorBrush> _dotBrushes = new();
    int _sel;
    bool _picked;                     // the user has chosen a language (click, arrow or wheel): Next becomes available
    int _wheelAccum;                  // mouse-wheel units not yet turned into a step

    public MainWindow() : this(null, 0) { }

    /// <summary>
    /// <paramref name="lang"/> is the language picked on the welcome page (null: none picked yet). The window is built
    /// again after the welcome page, so every text on the other pages is loaded in the chosen language.
    /// </summary>
    public MainWindow(string? lang, int page)
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
        _pages = [PageWelcome, PageOptions, PageInstall, PageDone];

        var face = (FontFamily)FindResource("Display");
        foreach (var code in Loc.Languages)
        {
            var brush = new SolidColorBrush(FaintColor);
            var scale = new ScaleTransform(FarScale, FarScale);
            var word = new TextBlock
            {
                Text = Loc.NativeName(code), TextAlignment = TextAlignment.Center, FontFamily = face,
                FontWeight = FontWeights.Light, FontSize = VBig, Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,   // no fixed height: a TextBlock clips its text to it
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
            };
            _words.Add(word); _wordBrushes.Add(brush); _wordScales.Add(scale);

            // WPF clips a child that is taller than the slot it is given, which cut the descenders of the
            // words: the row is taller than the 44 px step and the negative margins make it take only 44 px
            var row = new Grid { Height = RowH + 20, Margin = new Thickness(0, -10, 0, -10) };
            row.Children.Add(word);
            VStrip.Children.Add(row);

            var dbrush = new SolidColorBrush(DotOff);
            var dscale = new ScaleTransform(1, 1);
            var dot = new Ellipse
            {
                Width = DotSize, Height = DotSize, Fill = dbrush,
                Margin = new Thickness(0, 4, 0, 4),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = dscale,
            };
            _dotScales.Add(dscale); _dotBrushes.Add(dbrush);
            VDots.Children.Add(dot);
        }

        // English is selected from the start (App sets it), so Next is available right away
        _sel = Math.Max(0, Array.IndexOf(Loc.Languages, lang ?? Loc.Current));
        _picked = true;
        Refresh(animate: false);
        Loaded += (_, _) => Refresh(animate: false);   // positioned again once the window has its real size
        KeyDown += Window_KeyDown;
        StartWheelHint();
        Show(page);
    }

    /// <summary>Selects language <paramref name="i"/> (clamped): the column glides there and the welcome texts change.</summary>
    void Select(int i)
    {
        int n = Loc.Languages.Length;
        _sel = Math.Clamp(i, 0, n - 1);
        _picked = true;
        Loc.Set(Loc.Languages[_sel]);
        Refresh(animate: true);
        Show(_page);
    }

    /// <summary>Lays the column out for the selected language; animated after a choice, set directly on first layout.</summary>
    void Refresh(bool animate)
    {
        for (int i = 0; i < _words.Count; i++)
        {
            int d = Math.Abs(i - _sel);
            bool on = d == 0;
            double scale = on ? 1 : d == 1 ? NearScale : FarScale;
            double opacity = on ? 1 : d == 1 ? NearOpacity : 0;
            double dot = on ? DotBigScale : 1;
            Color colour = on ? TextColor : FaintColor;
            Color dotColour = on ? TextColor : DotOff;

            if (animate)
            {
                Animate(_wordScales[i], ScaleTransform.ScaleXProperty, scale, 600, Slide);
                Animate(_wordScales[i], ScaleTransform.ScaleYProperty, scale, 600, Slide);
                Animate(_words[i], UIElement.OpacityProperty, opacity, 500, Ease);
                Animate(_wordBrushes[i], SolidColorBrush.ColorProperty, colour, 500, Ease);
                Animate(_dotScales[i], ScaleTransform.ScaleXProperty, dot, 450, Ease);
                Animate(_dotScales[i], ScaleTransform.ScaleYProperty, dot, 450, Ease);
                Animate(_dotBrushes[i], SolidColorBrush.ColorProperty, dotColour, 450, Ease);
            }
            else
            {
                _wordScales[i].ScaleX = _wordScales[i].ScaleY = scale;
                _words[i].Opacity = opacity;
                _wordBrushes[i].Color = colour;
                _dotScales[i].ScaleX = _dotScales[i].ScaleY = dot;
                _dotBrushes[i].Color = dotColour;
            }
        }

        if (animate) Animate(VStripMove, TranslateTransform.YProperty, StripPos(_sel), 600, Slide);
        else VStripMove.Y = StripPos(_sel);

        string heading = Loc.T("setup.heading"), body = Loc.T("setup.welcome_body");
        if (animate)
        {
            if (HeadingText.Text != heading) CrossFade(HeadingText, heading);
            if (DescText.Text != body) CrossFade(DescText, body);
        }
        else
        {
            HeadingText.Text = heading;
            DescText.Text = body;
        }
        BtnBack.Content = Loc.T("common.back");
        BtnCancel.Content = Loc.T("common.abort");
    }

    /// <summary>Animates one property to <paramref name="to"/> along a CSS-like curve over <paramref name="ms"/>.</summary>
    static void Animate(IAnimatable target, DependencyProperty property, double to, int ms, KeySpline curve)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), curve));
        target.BeginAnimation(property, anim);
    }

    static void Animate(IAnimatable target, DependencyProperty property, Color to, int ms, KeySpline curve)
    {
        var anim = new ColorAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new SplineColorKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), curve));
        target.BeginAnimation(property, anim);
    }

    /// <summary>The text swap: fade out, change the text at 180 ms, fade back in.</summary>
    static void CrossFade(TextBlock tb, string text)
    {
        Animate(tb, UIElement.OpacityProperty, 0, 300, Ease);
        var swap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        swap.Tick += (_, _) =>
        {
            swap.Stop();
            tb.Text = text;
            Animate(tb, UIElement.OpacityProperty, 1, 300, Ease);
        };
        swap.Start();
    }

    /// <summary>The scroll icon under the footer: a dot that moves down and fades, 1.2 s per cycle.</summary>
    void StartWheelHint()
    {
        var move = new TranslateTransform();
        Wheel.RenderTransform = move;
        var sine = new SineEase { EasingMode = EasingMode.EaseInOut };
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 4, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine,
        });
        Wheel.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(600))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = sine,
        });
    }

    // ---- language picker: arrows, direct clicks on the neighbours, keys and the mouse wheel all make one step each ----

    void Up_Click(object s, RoutedEventArgs e) => Step(-1);
    void Down_Click(object s, RoutedEventArgs e) => Step(1);

    void Step(int direction)
    {
        if (_page != 0) return;
        Select(_sel + direction);
    }

    void Window_KeyDown(object s, KeyEventArgs e)
    {
        if (_page != 0) return;
        if (e.Key == Key.Up) { Step(-1); e.Handled = true; }
        else if (e.Key == Key.Down) { Step(1); e.Handled = true; }
    }

    /// <summary>A click on the neighbour above moves to the previous language, below to the next; on the selected one, nothing.</summary>
    void Strip_Click(object s, MouseButtonEventArgs e)
    {
        double y = e.GetPosition(VStripHost).Y, mid = VStripHost.ActualHeight / 2, half = RowH / 2;
        if (y < mid - half) Step(-1);
        else if (y > mid + half) Step(1);
    }

    /// <summary>One notch of the mouse wheel (120 units) is one language. Touchpad scrolls add up until they make a notch.</summary>
    void Welcome_Wheel(object s, MouseWheelEventArgs e)
    {
        if (_page != 0) return;
        e.Handled = true;
        if (Math.Sign(e.Delta) != Math.Sign(_wheelAccum)) _wheelAccum = 0;
        _wheelAccum += e.Delta;
        if (Math.Abs(_wheelAccum) < 120) return;
        int direction = _wheelAccum < 0 ? 1 : -1;   // wheel down (negative) moves towards the next language
        _wheelAccum = 0;
        Step(direction);
    }

    void Show(int page)
    {
        _page = page;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility = page is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Content = page switch { 0 => Loc.T("common.next"), 1 => Loc.T("setup.install_btn"), 3 => Loc.T("setup.finish_btn"), _ => "" };
        WheelFrame.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.IsEnabled = page != 2 && (page != 0 || _picked);
        // the accent button looks the same when disabled, so it is faded too: "İleri" must not look ready before a pick
        BtnNext.Opacity = BtnNext.IsEnabled ? 1 : 0.35;
    }

    void Next_Click(object s, RoutedEventArgs e)
    {
        switch (_page)
        {
            case 0: Reopen(1); break;
            case 1: StartInstall(); break;
            case 3: Finish(); break;
        }
    }

    /// <summary>Replaces this window with one built in the chosen language, on <paramref name="page"/>.</summary>
    void Reopen(int page)
    {
        var next = new MainWindow(Loc.Current, page);
        Application.Current.MainWindow = next;
        next.Show();
        Close();
    }

    void Back_Click(object s, RoutedEventArgs e) { if (_page == 1) Show(0); }

    async void StartInstall()
    {
        Show(2);
        bool startup = OptStartup.IsChecked == true, tray = OptTray.IsChecked == true, desktop = OptDesktop.IsChecked == true;
        string? error = null;
        try
        {
            await Task.Run(() => Installer.Run(startup, tray, desktop, (text, frac) =>
                Dispatcher.BeginInvoke(() =>
                {
                    StepText.Text = text;
                    Bar.Width = (PageInstall.ActualWidth > 0 ? PageInstall.ActualWidth : 456) * Math.Clamp(frac, 0, 1);
                })));
        }
        catch (Exception ex) { error = ex.Message; }

        if (error != null)
        {
            StepText.Text = error;
            BtnCancel.Visibility = Visibility.Visible;
            BtnCancel.Content = Loc.T("common.close");
            return;
        }
        PathText.Text = Installer.TargetDir;
        Show(3);
    }

    void Finish()
    {
        if (OptLaunch.IsChecked == true) Installer.LaunchAsUser();
        Close();
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();

    // drag the window from empty areas only: a press on a button or on the language picker must not move the window
    void Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase || d == VerticalPicker) return;
        }
        DragMove();
    }
}

using OpenClevo.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OpenClevo.Core;

namespace OpenClevo.App;

public partial class MainWindow : Window
{
    const string AppKey = @"Software\OpenClevo";

    // order shown in the mode selector
    static readonly (PowerMode Mode, string Desc)[] ModeInfo =
    [
        (PowerMode.Performance, Loc.T("power.desc.performance")),
        (PowerMode.Entertainment, Loc.T("power.desc.entertainment")),
        (PowerMode.Quiet, Loc.T("power.desc.quiet")),
        (PowerMode.PowerSaving, Loc.T("power.desc.saving")),
    ];

    // one strong colour per mode: the sliding tile in the mode bar takes it
    static readonly Dictionary<PowerMode, Color> ModeAccent = new()
    {
        [PowerMode.Quiet] = Color.FromRgb(0x00, 0xC2, 0xE0),          // cyan
        [PowerMode.PowerSaving] = Color.FromRgb(0x22, 0xC5, 0x5E),    // green
        [PowerMode.Entertainment] = Color.FromRgb(0xE0, 0xA1, 0x00),  // dark yellow
        [PowerMode.Performance] = Color.FromRgb(0xF0, 0x2D, 0x2D),    // red
    };
    readonly HardwareService _hw;
    readonly Dictionary<PowerMode, RadioButton> _modeButtons = new();
    FanCurve? _cpuEdit, _gpuEdit;
    bool _busy;

    readonly KeyboardEffectEngine _kb;

    public MainWindow(HardwareService hw, KeyboardEffectEngine kb)
    {
        InitializeComponent();
        FlowDirection = OpenClevo.Localization.Loc.Flow;   // Arabic mirrors the window
        _hw = hw;
        UpdateThemeIcon(App.IsLightTheme, animate: false);
        BuildLangList();
        _kb = kb;
        FitToScreen();
        CpuName.Text = HardwareNames.Cpu();
        GpuName.Text = HardwareNames.Gpu();
        BuildModeTiles();
        BuildKeyboardPanel();
        Controls.SlideThumb.Attach(FanHost, FanThumb);
        KbPanel.IsVisibleChanged += (_, _) => { if (KbPanel.IsVisible) _kbTimer.Start(); else _kbTimer.Stop(); };
        _kbTimer.Tick += (_, _) => TickKeyboardPreview();
        _offsetTimer.Tick += (_, _) => ApplyOffset();
        SysPanel.IsVisibleChanged += (_, _) => { if (SysPanel.IsVisible) { RefreshSystemSwitches(); _sysTimer.Start(); } else _sysTimer.Stop(); };
        _sysTimer.Tick += (_, _) => RefreshSystemSwitches();

        CpuCurve.CurveEdited += c => { _cpuEdit = c; UpdateCurveButtons(); };
        GpuCurve.CurveEdited += c => { _gpuEdit = c; UpdateCurveButtons(); };

        _hw.Updated += OnSensors;
        _hw.SettingsChanged += OnSettings;
        OnSettings();
        OnSensors();
    }

    /// <summary>Fixed 16:9 window, ~78 % of the work area width (max 1280 wide), never taller than 90 % of it.</summary>
    void FitToScreen()
    {
        var wa = SystemParameters.WorkArea;
        double w = Math.Min(wa.Width * 0.78, 1280), h = w * 9 / 16;
        if (h > wa.Height * 0.9) { h = wa.Height * 0.9; w = h * 16 / 9; }
        Width = Math.Round(w);
        Height = Math.Round(h);
    }

    internal static bool CloseToTray
    {
        get => (Registry.CurrentUser.OpenSubKey(AppKey)?.GetValue("CloseToTray") as int? ?? 1) == 1;
        set { using var k = Registry.CurrentUser.CreateSubKey(AppKey); k.SetValue("CloseToTray", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    /// <summary>Power modes as one segmented control; the one-line description lives in the tooltip.</summary>
    void BuildModeTiles()
    {
        foreach (var (mode, desc) in ModeInfo)
        {
            var rb = new RadioButton { Style = (Style)FindResource("ModeWord"), GroupName = "power", Content = Ui.ModeName(mode), ToolTip = desc, Tag = ModeAccent[mode] };
            rb.Click += async (_, _) => await OnModeClicked(mode);
            _modeButtons[mode] = rb;
            ModeGrid.Children.Add(rb);
        }
    }

    /// <summary>
    /// Power saving can also turn the NVIDIA GPU off (as Clevo's app does, after asking); leaving power saving turns it
    /// back on if it was power saving that turned it off.
    /// </summary>
    async Task OnModeClicked(PowerMode mode)
    {
        if (_busy) return;
        // busy from the click on: when the question closes the window is activated again, and that would re-read the
        // firmware (still the old mode) and flash the old mode in the indicator before the new one is applied
        _busy = true;
        var prev = _hw.PowerMode;
        bool gpuOff = false;
        if (mode == PowerMode.PowerSaving && prev != PowerMode.PowerSaving && NvidiaPower.IsOn == true)
        {
            int r = Dialog.Ask(this, Loc.T("power.saving"), Loc.T("power.gpu_question"), Loc.T("power.gpu_off"), Loc.T("power.mode_only"), Loc.T("common.cancel"));
            if (r is 2 or -1) { _busy = false; OnSettings(); return; }
            gpuOff = r == 0;
        }
        // The firmware calls (~0.3 s) run on a worker thread so the letter animation of the mode words keeps playing;
        // the settings reload (UI thread) waits until the animation is over.
        try { await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetPowerMode(mode); }); }
        catch (Exception ex) { Dialog.Info(this, Loc.F("power.apply_failed", ex.Message)); }
        await Task.Delay(800);
        _busy = false;
        _hw.ReloadSettings();
        if (gpuOff)
        {
            if (await SetNvidia(false)) NvidiaPower.OffByPowerSaving = true;
        }
        else if (mode != PowerMode.PowerSaving && NvidiaPower.OffByPowerSaving && NvidiaPower.IsOn == false)
        {
            if (await SetNvidia(true)) NvidiaPower.OffByPowerSaving = false;
        }
        if (SysPanel.IsVisible) RefreshSystemSwitches();
    }

    void OnSensors()
    {
        RefreshBattery();
        bool ok = _hw.Error == null;
        StatusPill.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;   // silent when all is well
        StatusPill.ToolTip = _hw.Error;

        Show(1, CpuTemp, CpuRpm, CpuDuty, CpuSpark, CpuCurve, CpuFan);
        Show(2, GpuTemp, GpuRpm, GpuDuty, GpuSpark, GpuCurve, GpuFan);
    }

    void Show(int idx, TextBlock temp, TextBlock rpm, TextBlock duty, Controls.Sparkline spark, Controls.CurveEditor curve, Controls.FanIcon fan)
    {
        var f = _hw.Fans.FirstOrDefault(x => x.Index == idx);
        if (f.Index == 0) { temp.Text = rpm.Text = duty.Text = "--"; return; }
        // a switched-off GPU has no temperature: the fan controller's floor value (20°) would be misleading
        int shownTemp = idx == 2 && NvidiaPower.IsOn == false ? 0 : f.TemperatureC;
        temp.Text = $"{shownTemp}°";
        // the number is neutral until it gets warm: color means "look at this"
        // bound to the theme resource, so the colour follows a theme change at once (not on the next sensor tick)
        temp.SetResourceReference(TextBlock.ForegroundProperty, shownTemp >= 90 ? "Hot" : shownTemp >= 78 ? "Warn" : "Text");
        rpm.Text = $"{f.Rpm:N0} rpm";
        duty.Text = $"fan %{f.DutyPercent}";
        curve.LiveTemperature = f.TemperatureC;
        (idx == 1 ? CpuMhz : GpuMhz).Text = idx == 2 && NvidiaPower.IsOn == false ? Loc.T("gpu.off")
            : (idx == 1 ? _hw.CpuMhz : _hw.GpuMhz) is > 0 and var mhz ? $"{mhz:N0} MHz" : "";
        fan.Rpm = f.Rpm;
        if (_hw.History.TryGetValue(idx, out var h))
            spark.SetData(h.Select(x => (double)x.TemperatureC).ToList(), h.Select(x => (double)x.DutyPercent).ToList(), HardwareService.HistoryLength);
    }

    void OnSettings()
    {
        foreach (var (m, rb) in _modeButtons) rb.IsChecked = m == _hw.PowerMode;
        Controls.AccentFx.To(_hw.PowerMode is { } pm && ModeAccent.TryGetValue(pm, out var accent) ? accent : null);

        FanAuto.IsChecked = _hw.FanMode == FanMode.Auto;
        _loadingOffset = true;
        FanOffsetSlider.Value = _hw.FanOffset;
        FanOffsetText.Text = $"%{_hw.FanOffset}";
        _loadingOffset = false;
        OffsetBar.Visibility = _hw.FanMode == FanMode.Auto ? Visibility.Visible : Visibility.Collapsed;
        FanCustom.IsChecked = _hw.FanMode == FanMode.Custom;
        _cpuEdit = _gpuEdit = null;
        CpuCurve.Curve = _hw.Curves.FirstOrDefault(c => c.Index == 1);
        GpuCurve.Curve = _hw.Curves.FirstOrDefault(c => c.Index == 2);
        UpdateCurveButtons();

        LoadCharger();
        LoadKeyboard();
    }

    // ---- system switches (state is always read back from Windows, so the keyboard's own Num/Scroll Lock stay in sync) ----
    readonly System.Windows.Threading.DispatcherTimer _sysTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    void RefreshSystemSwitches()
    {
        SwWinLock.IsChecked = SystemToggles.WinKeyLock;
        SwCamera.IsChecked = _hw.CameraOn ?? SystemToggles.Camera;   // firmware state (follows the Fn key); the Windows switch only if the driver is missing
        SwTouchpad.IsEnabled = SystemToggles.TouchpadSupported;
        SwTouchpad.IsChecked = SystemToggles.Touchpad;
        SwNum.IsChecked = SystemToggles.NumLock;
        SwScroll.IsChecked = SystemToggles.ScrollLock;
        var nv = NvidiaPower.IsOn;
        SwNvidia.IsEnabled = nv != null && !_nvBusy;
        SwNvidia.IsChecked = nv == true;
    }

    /// <summary>Re-reads the system switches now (a Fn key just changed one of them).</summary>
    public void SyncSystemSwitches() => RefreshSystemSwitches();

    bool _nvBusy;

    /// <summary>Switches the NVIDIA GPU; the first time it explains and sets up the privileged helper (one Windows prompt).</summary>
    async Task<bool> SetNvidia(bool on)
    {
        if (!NvidiaPower.HelperReady)
        {
            int c = Dialog.Ask(this, Loc.T("nvidia.setup_title"),
                Loc.T("nvidia.setup_body"),
                Loc.T("common.continue"), Loc.T("common.cancel"));
            if (c != 0) return false;
            if (!await NvidiaPower.InstallHelperAsync()) { Dialog.Info(this, Loc.T("nvidia.setup_failed")); return false; }
        }
        bool ok = await NvidiaPower.SetAsync(on);
        if (!ok) Dialog.Info(this, on ? Loc.T("nvidia.on_failed") : Loc.T("nvidia.off_failed"));
        return ok;
    }

    async Task ToggleNvidia(bool on)
    {
        _nvBusy = true;
        RefreshSystemSwitches();
        bool ok = await SetNvidia(on);
        if (ok) NvidiaPower.OffByPowerSaving = false;   // the user's own choice from now on
        _nvBusy = false;
        RefreshSystemSwitches();
    }

    void SysToggle_Click(object s, RoutedEventArgs e)
    {
        bool on = ((CheckBox)s).IsChecked == true;
        try
        {
            if (s == SwWinLock) SystemToggles.WinKeyLock = on;
            else if (s == SwCamera)
            {
                try { _hw.SetCameraPower(on); }
                catch { SystemToggles.Camera = on; }   // no driver: fall back to Windows' own camera privacy switch
            }
            else if (s == SwTouchpad) SystemToggles.Touchpad = on;
            else if (s == SwNum) SystemToggles.NumLock = on;
            else if (s == SwScroll) SystemToggles.ScrollLock = on;
            else if (s == SwNvidia) { _ = ToggleNvidia(on); return; }
        }
        catch (Exception ex) { Dialog.Info(this, ex.Message); }
        RefreshSystemSwitches();
    }

    // Windows 11 app-open motion: the new page grows slightly from its centre and fades in, the old one fades out quickly
    const double EntranceScale = 0.97;  // size the new page starts from
    const int EntranceMs = 350;         // grow + fade-in
    const int ExitMs = 120;             // old page fade-out
    const int EntranceDelayMs = 60;     // short pause so the exit reads first
    FrameworkElement _activePanel;

    bool _suppressTab;                 // setting IsChecked fires Checked, which must not re-enter ShowPage
    FrameworkElement _lastPage;        // the tab page to return to after the settings page

    void Tab_Click(object s, RoutedEventArgs e)
    {
        if (!IsInitialized || _suppressTab) return;   // Checked fires for the default tab while XAML is loading
        var shown = TabFans.IsChecked == true ? FanPanel : TabKb.IsChecked == true ? KbPanel : TabSys.IsChecked == true ? SysPanel : BatPanel;
        ShowPage(shown);
    }

    /// <summary>One place for every page change: the old page leaves, the new one comes in with the same motion.</summary>
    void ShowPage(FrameworkElement target)
    {
        _suppressTab = true;
        TabFans.IsChecked = target == FanPanel;
        TabKb.IsChecked = target == KbPanel;
        TabBat.IsChecked = target == BatPanel;
        TabSys.IsChecked = target == SysPanel;
        _suppressTab = false;

        _activePanel = target;
        foreach (var p in new FrameworkElement[] { FanPanel, KbPanel, BatPanel, SysPanel, SettingsPanel })
            if (p != target && p.Visibility == Visibility.Visible) Exit(p);
        if (target != SettingsPanel) _lastPage = target;
        Reset(target);
        target.Visibility = Visibility.Visible;
        Entrance(target);
    }

    static void Reset(FrameworkElement p)
    {
        p.BeginAnimation(UIElement.OpacityProperty, null);
        p.Opacity = 1;
        p.RenderTransform = Transform.Identity;
    }

    // decelerating curve, the same feel as the Windows 11 entrance motion
    static CubicEase Decelerate() => new() { EasingMode = EasingMode.EaseOut };

    void Entrance(FrameworkElement p)
    {
        p.RenderTransformOrigin = new Point(0.5, 0.5);
        var sc = new ScaleTransform(EntranceScale, EntranceScale);
        p.RenderTransform = sc;
        p.Opacity = 0;
        var span = TimeSpan.FromMilliseconds(EntranceMs);
        var begin = TimeSpan.FromMilliseconds(EntranceDelayMs);
        sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(EntranceScale, 1, span) { BeginTime = begin, EasingFunction = Decelerate() });
        sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(EntranceScale, 1, span) { BeginTime = begin, EasingFunction = Decelerate() });
        p.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, span) { BeginTime = begin, EasingFunction = Decelerate() });
    }

    void Exit(FrameworkElement p)
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(ExitMs));
        fade.Completed += (_, _) =>
        {
            if (_activePanel == p) return;   // the user switched back to this page during the fade
            p.Visibility = Visibility.Collapsed;
            Reset(p);
        };
        p.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // ---------------- keyboard backlight ----------------

    static readonly (string Name, Rgb Color)[] Palette =
    [
        (Loc.T("color.white"), new(255, 255, 255)), (Loc.T("color.red"), new(255, 0, 0)), (Loc.T("color.orange"), new(255, 90, 0)), (Loc.T("color.yellow"), new(255, 210, 0)),
        (Loc.T("color.green"), new(0, 255, 0)), (Loc.T("color.turquoise"), new(0, 255, 200)), (Loc.T("color.blue"), new(0, 0, 255)), (Loc.T("color.purple"), new(140, 0, 255)), (Loc.T("color.pink"), new(255, 0, 150)),
    ];

    static readonly (KbEffect Effect, string Name)[] HwEffectNames =
    [
        (KbEffect.Static, Loc.T("kb.fx.static")), (KbEffect.Breathe, Loc.T("kb.fx.breathe")), (KbEffect.Cycle, Loc.T("kb.fx.cycle")), (KbEffect.Wave, Loc.T("kb.fx.wave")),
        (KbEffect.Dance, Loc.T("kb.fx.dance")), (KbEffect.Tempo, Loc.T("kb.fx.tempo")), (KbEffect.Flash, Loc.T("kb.fx.flash")), (KbEffect.Random, Loc.T("kb.fx.random")),
    ];

    // Single-zone keyboards (type 6, verified on this G5 KF): firmware effects do nothing, so every effect is ours.
    static readonly (SoftEffect Effect, string Name, string Tip)[] SwEffectsSingle =
    [
        (SoftEffect.Breathe, Loc.T("kb.fx.breathe"), Loc.T("kb.tip.breathe")),
        (SoftEffect.Cycle, Loc.T("kb.fx.cycle"), Loc.T("kb.tip.cycle")),
        (SoftEffect.RandomFade, Loc.T("kb.fx.random_fade"), Loc.T("kb.tip.random")),
        (SoftEffect.Flash, Loc.T("kb.fx.flash"), Loc.T("kb.tip.flash")),
        (SoftEffect.Temperature, Loc.T("kb.fx.temperature"), Loc.T("kb.tip.temperature")),
        (SoftEffect.Audio, Loc.T("kb.fx.audio"), Loc.T("kb.tip.audio")),
        (SoftEffect.AudioCycle, Loc.T("kb.fx.audio_cycle"), Loc.T("kb.tip.audio_cycle")),
    ];

    static readonly (SoftEffect Effect, string Name, string Tip)[] SwEffectsMulti =
    [
        (SoftEffect.Temperature, Loc.T("kb.fx.temperature"), Loc.T("kb.tip.temperature_multi")),
        (SoftEffect.Cycle, Loc.T("kb.fx.rainbow"), Loc.T("kb.tip.rainbow_multi")),
        (SoftEffect.Breathe, Loc.T("kb.fx.soft_breathe"), Loc.T("kb.tip.breathe_multi")),
        (SoftEffect.Audio, Loc.T("kb.fx.audio"), Loc.T("kb.tip.audio_multi")),
        (SoftEffect.AudioCycle, Loc.T("kb.fx.audio_cycle"), Loc.T("kb.tip.audio_cycle_multi")),
    ];

    (SoftEffect Effect, string Name, string Tip)[] SwEffectNames => _singleZone ? SwEffectsSingle : SwEffectsMulti;
    bool _singleZone = true;
    bool _effectsBuilt;

    readonly Rgb[] _zones = [new(0, 0, 255), new(0, 0, 255), new(0, 0, 255)];

    readonly Dictionary<KbEffect, RadioButton> _hwButtons = new();
    readonly Dictionary<SoftEffect, RadioButton> _swButtons = new();
    readonly List<RadioButton> _brightButtons = new();
    KbEffect _kbEffect = KbEffect.Static;
    int _selectedZone = -1;
    bool _kbLoaded;

    RadioButton Chip(string text, string group, string? tip = null) => new()
    {
        Style = (Style)FindResource("Chip"), GroupName = group, Margin = new Thickness(0, 0, 8, 8), ToolTip = tip, Content = text,
    };

    // ---- live keyboard preview: mirrors the running effect, or simulates the hovered one ----
    readonly System.Windows.Threading.DispatcherTimer _kbTimer = new(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
    readonly System.Diagnostics.Stopwatch _simClock = System.Diagnostics.Stopwatch.StartNew();
    SoftEffect _hoverSim = SoftEffect.None;

    static SoftEffect SimFor(KbEffect e) => e switch
    {
        KbEffect.Breathe => SoftEffect.Breathe,
        KbEffect.Flash => SoftEffect.Flash,
        KbEffect.Random => SoftEffect.RandomFade,
        KbEffect.Static => SoftEffect.None,
        _ => SoftEffect.Cycle,   // cycle / wave / dance / tempo: moving colours
    };

    void Hover(RadioButton rb, SoftEffect sim, bool forceStatic = false)
    {
        rb.MouseEnter += (_, _) => _hoverSim = forceStatic ? SoftEffect.None : sim;
        rb.MouseLeave += (_, _) => _hoverSim = SoftEffect.None;
    }

    void TickKeyboardPreview()
    {
        var baseC = _zones.ToArray();
        Rgb[] frame = baseC;
        double t = _simClock.Elapsed.TotalSeconds;
        int cpu = _hw.Fans.FirstOrDefault(x => x.Index == 1).TemperatureC, gpu = _hw.Fans.FirstOrDefault(x => x.Index == 2).TemperatureC;

        if (_hoverSim != SoftEffect.None)
            frame = KeyboardEffectEngine.Simulate(_hoverSim, t, baseC, _singleZone, cpu, gpu);          // hovering an option
        else if (!_kbDirty && _kb.Current != SoftEffect.None)
            frame = _kb.LastFrame ?? baseC;                                                             // nothing pending: what the keyboard really shows
        else if (_pSw != SoftEffect.None)
            frame = KeyboardEffectEngine.Simulate(_pSw, t, baseC, _singleZone, cpu, gpu);               // pending, not yet applied
        else if (!_singleZone && SimFor(_kbEffect) is var s && s != SoftEffect.None)
            frame = KeyboardEffectEngine.Simulate(s, t, baseC, false, cpu, gpu);

        KbPreview.Colors = frame.Select(c => Color.FromRgb(c.R, c.G, c.B)).ToArray();
    }
    void BuildKeyboardPanel()
    {
        KbPreview.ZoneClicked += z =>
        {
            if (_singleZone) return;
            _selectedZone = z;
            if (AllZonesToggle.IsChecked == true) AllZonesToggle.IsChecked = false;
            RefreshKeyboardUi();
        };

        foreach (var (name, c) in Palette)
        {
            var b = new Button { Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0), ToolTip = name, Cursor = System.Windows.Input.Cursors.Hand };
            b.Template = SwatchTemplate(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)));
            b.Click += (_, _) => PickColor(c);
            Swatches.Children.Add(b);
        }
        var custom = new Button { Style = (Style)FindResource("Btn"), Content = Loc.T("kb.custom_color"), Height = 30, FontSize = 12 };
        custom.Click += (_, _) => PickCustomColor();
        Swatches.Children.Add(custom);

        string[] levels = [Loc.T("common.off"), "1", "2", "3", "4"];
        for (int i = 0; i < levels.Length; i++)
        {
            int level = i;
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "kbbright", Content = levels[i] };
            rb.Click += (_, _) => { KbPreview.Brightness = level; KeyboardIdle.SavedBrightness = level; KbAct(() => _hw.SetKeyboardBrightness(level), Loc.T("kb.brightness")); };
            _brightButtons.Add(rb);
            BrightnessBar.Children.Add(rb);
        }
        KbPreview.Brightness = KeyboardIdle.SavedBrightness;

        foreach (var (label, sec) in new[] { (Loc.T("kb.never"), 0), (Loc.T("kb.t10"), 10), (Loc.T("kb.t30"), 30), (Loc.T("kb.t60"), 60), (Loc.T("kb.t300"), 300) })
        {
            var rb = new RadioButton { Style = (Style)FindResource("Segment"), GroupName = "kbtimeout", Content = label };
            rb.Click += (_, _) => { _pTimeout = sec; RefreshKeyboardUi(); };
            _timeoutButtons[sec] = rb;
            TimeoutBar.Children.Add(rb);
        }
    }

    static ControlTemplate SwatchTemplate(Brush fill)
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, fill);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetValue(Border.BorderBrushProperty, Application.Current.FindResource("Line"));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        t.VisualTree = border;
        return t;
    }

    void LoadKeyboard()
    {
        var k = _hw.Keyboard;
        bool ok = k != null && KeyboardExtensions.IsSupportedType(k.Type);
        KbPanel.IsEnabled = ok;
        if (!ok) return;

        if (!_kbLoaded)   // take colors/effect from Clevo's store once; afterwards the UI is the source of truth
        {
            _singleZone = k!.Type == 6;
            _kb.SingleZone = _singleZone;
            _zones[0] = k.Left; _zones[1] = k.Middle; _zones[2] = k.Right;
            if (_singleZone) _zones[1] = _zones[2] = _zones[0];
            _kbEffect = _singleZone ? KbEffect.Static : k.Effect;
            _kbLoaded = true;
            _pSw = _appliedSw = _kb.Current;
            _appliedHw = _kbEffect;
            _appliedZones = _zones.ToArray();
            _pTimeout = _appliedTimeout = KeyboardIdle.TimeoutSeconds;
            BuildEffectChips();
        }
        RefreshKeyboardUi();
    }

    /// <summary>Builds effect chips once the keyboard type is known (single-zone gets a software-only list).</summary>
    void BuildEffectChips()
    {
        if (_effectsBuilt) return;
        _effectsBuilt = true;

        if (_singleZone)
        {
            // one zone: hide middle/right, the "all zones" toggle and the firmware effect row
            KbPreview.SingleZone = true;


            AllZonesToggle.Visibility = Visibility.Collapsed;
            AllZonesToggle.IsChecked = true;
            HwHeader.Visibility = HwEffects.Visibility = Visibility.Collapsed;
            SwHeader.Margin = new Thickness(0);
            SwHeaderText.Text = Loc.T("kb.effects");
            var stat = Chip(Loc.T("kb.fx.static"), "kbfx");
            Hover(stat, SoftEffect.None);
            stat.Click += (_, _) => SelectHwEffect(KbEffect.Static);
            _hwButtons[KbEffect.Static] = stat;
            SwEffects.Children.Add(stat);
        }
        else
        {
            foreach (var (eff, name) in HwEffectNames)
            {
                var rb = Chip(name, "kbfx");
                Hover(rb, SimFor(eff));
                rb.Click += (_, _) => SelectHwEffect(eff);
                _hwButtons[eff] = rb;
                HwEffects.Children.Add(rb);
            }
        }
        foreach (var (eff, name, tip) in SwEffectNames)
        {
            var rb = Chip(name, "kbfx");
            Hover(rb, eff);
            rb.Click += (_, _) => SelectSwEffect(eff);
            _swButtons[eff] = rb;
            SwEffects.Children.Add(rb);
        }
    }

    // Everything on this page except brightness is staged: selections only change the preview until Loc.T("common.apply").
    SoftEffect _pSw = SoftEffect.None, _appliedSw = SoftEffect.None;
    KbEffect _appliedHw = KbEffect.Static;
    Rgb[] _appliedZones = [new(0, 0, 255), new(0, 0, 255), new(0, 0, 255)];
    int _pTimeout, _appliedTimeout;
    bool _kbDirty;
    readonly Dictionary<int, RadioButton> _timeoutButtons = new();

    void RefreshKeyboardUi()
    {
        KbPreview.SingleZone = _singleZone;
        KbPreview.SelectedZone = AllZonesToggle.IsChecked != true ? _selectedZone : -1;
        foreach (var (e, rb) in _hwButtons) rb.IsChecked = _pSw == SoftEffect.None && e == _kbEffect;
        foreach (var (e, rb) in _swButtons) rb.IsChecked = e == _pSw;
        foreach (var (sec, rb) in _timeoutButtons) rb.IsChecked = sec == _pTimeout;

        _kbDirty = !_zones.SequenceEqual(_appliedZones) || _pSw != _appliedSw ||
                   (_pSw == SoftEffect.None && _kbEffect != _appliedHw) || _pTimeout != _appliedTimeout;
        KbApplyBtn.IsEnabled = _kbDirty;
    }

    void AllZones_Click(object s, RoutedEventArgs e) => RefreshKeyboardUi();

    void PickColor(Rgb c)
    {
        if (AllZonesToggle.IsChecked == true) { _zones[0] = _zones[1] = _zones[2] = c; }
        else if (_selectedZone >= 0) _zones[_selectedZone] = c;
        else return;
        RefreshKeyboardUi();
    }

    void PickCustomColor()
    {
        var start = AllZonesToggle.IsChecked == true || _selectedZone < 0 ? _zones[0] : _zones[_selectedZone];
        using var dlg = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(start.R, start.G, start.B) };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) PickColor(new Rgb(dlg.Color.R, dlg.Color.G, dlg.Color.B));
    }

    void SelectHwEffect(KbEffect e) { _kbEffect = e; _pSw = SoftEffect.None; RefreshKeyboardUi(); }

    void SelectSwEffect(SoftEffect e) { _pSw = e; RefreshKeyboardUi(); }

    void KbApply_Click(object s, RoutedEventArgs e) => KbAct(() =>
    {
        if (_pSw != SoftEffect.None)
        {
            // put the firmware in static mode once (and store it), then the engine animates
            if (_kb.Current == SoftEffect.None) _hw.ApplyKeyboard(KbEffect.Static, _zones[0], _zones[1], _zones[2]);
            _kb.Start(_pSw, _zones.ToArray());
        }
        else
        {
            _kb.Stop();
            _hw.ApplyKeyboard(_kbEffect, _zones[0], _zones[1], _zones[2]);
        }
        KeyboardIdle.TimeoutSeconds = _pTimeout;
        _appliedSw = _pSw; _appliedHw = _kbEffect; _appliedZones = _zones.ToArray(); _appliedTimeout = _pTimeout;
    }, Loc.T("tab.keyboard"));

    void KbAct(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) { Dialog.Info(this, Loc.F("common.apply_failed", what, ex.Message)); }
        RefreshKeyboardUi();
    }
    bool _loadingCharger;

    void LoadCharger()
    {
        _loadingCharger = true;
        var c = _hw.Charger ?? new FlexiCharger(false, 95, 100);
        ChargeToggle.IsChecked = c.Enabled;
        StartSlider.Value = Math.Clamp(c.StartPercent, 40, 95);
        StopSlider.Value = Math.Clamp(c.StopPercent, 50, 100);
        _loadingCharger = false;
        UpdateChargeUi(dirty: false);
    }

    void UpdateChargeUi(bool dirty)
    {
        StartText.Text = $"%{StartSlider.Value:0}";
        StopText.Text = $"%{StopSlider.Value:0}";
        bool on = ChargeToggle.IsChecked == true;
        StartSlider.IsEnabled = StopSlider.IsEnabled = on;
        ChargeApplyBtn.IsEnabled = dirty && _hw.Charger != null && !_busy;
        ChargeRangeText.Text = on ? $"%{StartSlider.Value:0} – %{StopSlider.Value:0}" : Loc.T("common.off");
        ChargeRangeBar.Start = StartSlider.Value;
        ChargeRangeBar.Stop = StopSlider.Value;
        ChargeRangeBar.Active = on;
    }

    void RefreshBattery()
    {
        try
        {
            var status = System.Windows.Forms.SystemInformation.PowerStatus;
            var charge = status.BatteryChargeStatus;
            if (charge == System.Windows.Forms.BatteryChargeStatus.Unknown || charge.HasFlag(System.Windows.Forms.BatteryChargeStatus.NoSystemBattery)
                || status.BatteryLifePercent is < 0 or > 1)
            {
                BatteryPercent.Text = "--";
                ChargeRangeBar.Level = -1;
                return;
            }

            int percent = (int)Math.Round(status.BatteryLifePercent * 100);
            BatteryPercent.Text = $"%{percent}";
            ChargeRangeBar.Level = percent;
        }
        catch
        {
            BatteryPercent.Text = "--";
            ChargeRangeBar.Level = -1;
        }
    }

    void ChargeEdited(object s, RoutedEventArgs e) { if (!_loadingCharger) UpdateChargeUi(dirty: true); }

    void ChargeSlider_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingCharger || !IsInitialized) return;   // sliders raise ValueChanged while XAML is still loading
        if (StartSlider.Value > StopSlider.Value - 5)
        {
            if (ReferenceEquals(s, StartSlider)) StopSlider.Value = Math.Min(100, StartSlider.Value + 5);
            else StartSlider.Value = Math.Max(40, StopSlider.Value - 5);
        }
        UpdateChargeUi(dirty: true);
    }

    void ChargeApply_Click(object s, RoutedEventArgs e)
    {
        var f = new FlexiCharger(ChargeToggle.IsChecked == true, (int)StartSlider.Value, (int)StopSlider.Value);
        var text = f.Enabled ? Loc.F("charge.apply_on", f.StartPercent, f.StopPercent) : Loc.T("charge.apply_off");
        if (Dialog.Ask(this, Loc.T("charge.limit"), text, Loc.T("common.apply"), Loc.T("common.cancel")) != 0) return;
        Act(() => _hw.SetCharger(f), Loc.T("charge.limit"));
    }

    void UpdateCurveButtons()
    {
        bool dirty = _cpuEdit != null || _gpuEdit != null;
        ApplyBtn.IsEnabled = dirty && !_busy;
        RevertBtn.IsEnabled = dirty && !_busy;
    }

    void Act(Action a, string what)
    {
        if (_busy) return;
        _busy = true;
        try { a(); }
        catch (Exception ex) { Dialog.Info(this, Loc.F("common.apply_failed", what, ex.Message)); }
        finally { _busy = false; OnSettings(); }
    }

    void FanSel_Changed(object s, RoutedEventArgs e)
    {
        if (!IsInitialized || CpuCurve == null || GpuCurve == null) return;
        bool cpu = TabCpuFan.IsChecked == true;
        CpuCurve.Visibility = cpu ? Visibility.Visible : Visibility.Collapsed;
        GpuCurve.Visibility = cpu ? Visibility.Collapsed : Visibility.Visible;
    }

    // Clevo's auto-mode "Increment": applied shortly after the slider stops moving
    bool _loadingOffset;
    readonly System.Windows.Threading.DispatcherTimer _offsetTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    void FanOffset_Changed(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingOffset || !IsInitialized) return;
        FanOffsetText.Text = $"%{FanOffsetSlider.Value:0}";
        _offsetTimer.Stop();
        _offsetTimer.Start();
    }

    void ApplyOffset()
    {
        _offsetTimer.Stop();
        int pct = (int)FanOffsetSlider.Value;
        if (pct != _hw.FanOffset) Act(() => _hw.SetFanOffset(pct), Loc.T("fan.offset_label"));
    }

    void FanAuto_Click(object s, RoutedEventArgs e) => Act(() => _hw.SetFanMode(FanMode.Auto), Loc.T("fan.auto_mode"));
    void FanCustom_Click(object s, RoutedEventArgs e) => Act(() => _hw.SetFanMode(FanMode.Custom), Loc.T("fan.custom_mode"));

    void ApplyBtn_Click(object s, RoutedEventArgs e)
    {
        var list = new List<FanCurve>();
        if (_cpuEdit != null) list.Add(_cpuEdit);
        if (_gpuEdit != null) list.Add(_gpuEdit);
        if (list.Count == 0) return;
        var summary = string.Join("\n", list.Select(c => $"{Ui.FanName(c.Index)}: {c.T2}° → %{c.D2},  {c.T3}° → %{c.D3}"));
        if (Dialog.Ask(this, Loc.T("fan.apply_title"), Loc.F("fan.apply_body", summary), Loc.T("common.apply"), Loc.T("common.cancel")) != 0) return;
        Act(() => _hw.ApplyCurves(list), Loc.T("fan.curve_label"));
    }

    void RevertBtn_Click(object s, RoutedEventArgs e) => OnSettings();

    // ---- settings page: inside the window, opened and closed by the gear ----
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    void SettingsBtn_Click(object s, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible) { ShowPage(_lastPage ?? FanPanel); return; }   // second press: back to the tab it came from
        RefreshSettings();
        ShowPage(SettingsPanel);
    }

    void RefreshSettings()
    {
        UpdateLangList();
        StartupToggle.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("OpenClevo") != null;
        TrayToggle.IsChecked = CloseToTray;
        OsdToggle.IsChecked = OsdHost.Enabled;
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";
        VersionText.Text = string.Format(Loc.T("settings.version"), version);
    }

    /// <summary>Builds the language list once: one small clickable line per language.</summary>
    void BuildLangList()
    {
        foreach (var code in Loc.Languages)
        {
            var line = new TextBlock
            {
                Text = Loc.NativeName(code),
                Tag = code,
                TextAlignment = TextAlignment.Right,
                HorizontalAlignment = HorizontalAlignment.Right,
                FontFamily = (FontFamily)FindResource("Display"),
                FontWeight = FontWeights.Light,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 2, 0, 2),   // ~37 px per line, as in the mockup
            };
            line.MouseLeftButtonUp += LangItem_Click;
            LangList.Children.Add(line);
        }
    }

    /// <summary>The language the window is running in is shown large; the others stay small and muted.</summary>
    void UpdateLangList()
    {
        string running = Loc.Current;
        foreach (TextBlock line in LangList.Children)
        {
            bool sel = (string)line.Tag == running;
            line.FontSize = sel ? 62 : 24;
            line.SetResourceReference(TextBlock.ForegroundProperty, sel ? "Text" : "Muted");
        }
    }

    /// <summary>A click asks first; the choice is saved and applied only when the user confirms the restart.</summary>
    void LangItem_Click(object s, MouseButtonEventArgs e)
    {
        string next = (string)((TextBlock)s).Tag;
        if (next == Loc.Current) return;
        if (Dialog.Ask(this, Loc.T("settings.lang_restart_title"), Loc.T("settings.lang_restart_body"),
                Loc.T("settings.lang_restart_btn"), Loc.T("common.cancel")) != 0) return;
        Loc.SavedLanguage = next;
        Restart();
    }

    /// <summary>Starts a new copy after this one has exited (this copy still holds the single-instance lock for a moment).</summary>
    static void Restart()
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 2 127.0.0.1 >nul & start \"\" \"{Environment.ProcessPath}\"")
        { UseShellExecute = false, CreateNoWindow = true });
        Application.Current.Shutdown();
    }

    void StartupToggle_Click(object s, RoutedEventArgs e)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartupToggle.IsChecked == true) k.SetValue("OpenClevo", $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue("OpenClevo", false);
    }

    void TrayToggle_Click(object s, RoutedEventArgs e) => CloseToTray = TrayToggle.IsChecked == true;

    void OsdToggle_Click(object s, RoutedEventArgs e) => OsdHost.Enabled = OsdToggle.IsChecked == true;

    // ---- theme toggle: the sun shows in the light theme, the moon in the dark one ----
    void ThemeBtn_Click(object s, RoutedEventArgs e)
    {
        bool toLight = !App.IsLightTheme;
        CrossfadeTheme(() => App.SetTheme(toLight));
        UpdateThemeIcon(toLight, animate: true);
    }

    /// <summary>
    /// Changes the theme without a hard cut: a snapshot of the window in its old colours is laid over it, the new theme is
    /// applied underneath at once, and the snapshot fades away. Works for every colour, however it is drawn.
    /// </summary>
    void CrossfadeTheme(Action applyTheme)
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (w < 1 || h < 1) { applyTheme(); return; }
        var dpi = VisualTreeHelper.GetDpi(Root);
        var snap = new RenderTargetBitmap((int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var background = new DrawingVisual();   // the window's own background is not part of Root
        using (var dc = background.RenderOpen()) dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, w, h));
        snap.Render(background);
        snap.Render(Root);

        var overlay = new System.Windows.Controls.Image { Source = snap, Stretch = Stretch.Fill, IsHitTestVisible = false };
        Grid.SetColumnSpan(overlay, 2);
        Panel.SetZIndex(overlay, 1000);
        Root.Children.Add(overlay);

        applyTheme();   // the new colours appear underneath, behind the snapshot

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(500)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        fade.Completed += (_, _) => Root.Children.Remove(overlay);
        overlay.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Shows the glyph for the current theme; the other one turns away and the new one turns in.</summary>
    void UpdateThemeIcon(bool light, bool animate)
    {
        Turn(SunIcon, SunRot, SunScale, light, +90, animate);
        Turn(MoonIcon, MoonRot, MoonScale, !light, -90, animate);
    }

    static void Turn(TextBlock tb, RotateTransform rot, ScaleTransform scale, bool shown, double hiddenAngle, bool animate)
    {
        double angle = shown ? 0 : hiddenAngle, size = shown ? 1 : 0.4, opacity = shown ? 1 : 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var d = TimeSpan.FromMilliseconds(450);
        if (!animate)
        {
            rot.Angle = angle; scale.ScaleX = scale.ScaleY = size; tb.Opacity = opacity;
            return;
        }
        rot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(size, d) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(size, d) { EasingFunction = ease });
        tb.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(opacity, d) { EasingFunction = ease });
    }

    // Fixed-size window: never allow maximize (e.g. via Win+Up or caption double-click).
    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
    }

    void MinBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void CloseBtn_Click(object s, RoutedEventArgs e) => Close();

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (!_busy && _cpuEdit == null && _gpuEdit == null) _hw.ReloadSettings();   // pick up Fn-key changes
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (CloseToTray) { e.Cancel = true; Hide(); ((App)Application.Current).HintTray(); App.TrimMemory(); }
        else Application.Current.Shutdown();
        base.OnClosing(e);
    }
}

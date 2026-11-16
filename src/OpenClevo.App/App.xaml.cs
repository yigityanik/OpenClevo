using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenClevo.Localization;
using OpenClevo.Localization;

namespace OpenClevo.App;

public partial class App : Application
{
    Mutex? _mutex;
    HardwareService? _hw;
    KeyboardEffectEngine? _kb;
    KeyboardIdle? _idle;
    TrayIcon? _tray;
    MainWindow? _window;
    string? _forcedTheme;   // "--theme=light|dark" (handy for testing); otherwise the Windows app theme

    protected override void OnStartup(StartupEventArgs e)
    {
        Loc.Init();   // before any window or tray menu is built
        // elevated helper started by the window to switch the NVIDIA GPU (no UI, no single-instance check)
        if (e.Args.FirstOrDefault(a => a.StartsWith("--nvidia=")) is { } nv) { Shutdown(NvidiaPower.ApplyHere(nv.EndsWith("on"))); return; }
        if (e.Args.FirstOrDefault(a => a.StartsWith("--install-helper=")) is { } ih) { Shutdown(NvidiaPower.InstallHelperHere(ih["--install-helper=".Length..])); return; }

        _mutex = new Mutex(true, @"Local\OpenClevo.App", out bool first);
        if (!first)
        {
            // another copy is already running (maybe in the tray): ask it to show its window, then leave
            if (EventWaitHandle.TryOpenExisting(ShowEventName, out var show)) { show.Set(); show.Dispose(); }
            Shutdown();
            return;
        }
        StartShowListener();
        base.OnStartup(e);

        _forcedTheme = e.Args.FirstOrDefault(a => a.StartsWith("--theme="))?["--theme=".Length..];
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == UserPreferenceCategory.General) Dispatcher.BeginInvoke(ApplyTheme);   // live follow of Windows light/dark
        };

        _hw = new HardwareService();
        _kb = new KeyboardEffectEngine(_hw);
        _idle = new KeyboardIdle(_kb);
        SystemToggles.Restore();
        _tray = new TrayIcon(_hw, ShowWindow, () => Shutdown());
        _hw.Start();

        // resume a software keyboard effect chosen in an earlier session
        if (KeyboardEffectEngine.Saved is var saved and not SoftEffect.None && _hw.Keyboard is { } k && Core.KeyboardExtensions.IsSupportedType(k.Type))
        {
            _kb.SingleZone = k.Type == 6;
            _kb.Start(saved, [k.Left, k.Middle, k.Right]);
        }
        if (!e.Args.Contains("--tray")) ShowWindow();
    }

    /// <summary>Swaps merged dictionary 0 between Themes/Dark.xaml and Themes/Light.xaml; everything binds with DynamicResource.</summary>
    /// <summary>True while the light theme is applied (the sidebar toggle reads this).</summary>
    public static bool IsLightTheme { get; private set; }

    /// <summary>The theme the user chose with the sidebar toggle: "light", "dark", or null to follow Windows.</summary>
    public static string? SavedTheme
    {
        get
        {
            try
            {
                var v = Registry.CurrentUser.OpenSubKey(@"Software\OpenClevo")?.GetValue("Theme") as string;
                return v is "light" or "dark" ? v : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Switches the theme now and remembers it, so it is kept the next time the app starts.</summary>
    public static void SetTheme(bool light)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(@"Software\OpenClevo")) k.SetValue("Theme", light ? "light" : "dark");
        ((App)Current).ApplyTheme();   // instant; the window fades a snapshot of the old look over it (MainWindow.CrossfadeTheme)
    }

    void ApplyTheme()
    {
        bool light = _forcedTheme is { } t ? t == "light" : SavedTheme is { } s ? s == "light" : SystemUsesLightTheme();
        IsLightTheme = light;
        var source = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = source };
    }

    static bool SystemUsesLightTheme()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") as int? == 1; }
        catch { return false; }
    }

    const string ShowEventName = @"Local\OpenClevo.Show";

    /// <summary>Waits for a second copy to ask for the window (the shortcut while the app sits in the tray).</summary>
    void StartShowListener()
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(() =>
        {
            while (show.WaitOne())
                Dispatcher.BeginInvoke(ShowWindow);
        }) { IsBackground = true, Name = "OpenClevo show listener" };
        listener.Start();
    }

    /// <summary>Tells the user the app is still running in the tray (after the window is closed to the tray).</summary>
    public void HintTray() => _tray?.ShowHint(Loc.T("tray.running"));

    void ShowWindow()
    {
        _window ??= new MainWindow(_hw!, _kb!);
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        SystemToggles.Shutdown();
        _idle?.Dispose();
        _kb?.Dispose();
        _hw?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

using System.Windows;
using Microsoft.Win32;

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
        // elevated helper started by the window to switch the NVIDIA GPU (no UI, no single-instance check)
        if (e.Args.FirstOrDefault(a => a.StartsWith("--nvidia=")) is { } nv) { Shutdown(NvidiaPower.ApplyHere(nv.EndsWith("on"))); return; }
        if (e.Args.FirstOrDefault(a => a.StartsWith("--install-helper=")) is { } ih) { Shutdown(NvidiaPower.InstallHelperHere(ih["--install-helper=".Length..])); return; }

        _mutex = new Mutex(true, @"Local\OpenClevo.App", out bool first);
        if (!first) { Shutdown(); return; }
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
    void ApplyTheme()
    {
        bool light = _forcedTheme is { } t ? t == "light" : SystemUsesLightTheme();
        var source = new Uri(light ? "Themes/Light.xaml" : "Themes/Dark.xaml", UriKind.Relative);
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = source };
    }

    static bool SystemUsesLightTheme()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") as int? == 1; }
        catch { return false; }
    }

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

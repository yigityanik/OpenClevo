using System.Windows.Threading;
using OpenClevo.Localization;

namespace OpenClevo.App;

/// <summary>
/// Runs the on-screen notice and the Caps/Num Lock hook on their own thread, so a busy main window (sensor polling,
/// animations) never delays a notice or the keyboard hook.
/// </summary>
sealed class OsdHost : IDisposable
{
    readonly Thread _thread;
    readonly ManualResetEventSlim _ready = new();
    Dispatcher? _dispatcher;
    OsdWindow? _osd;
    LockKeys? _locks;

    static volatile bool _enabled = (Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\OpenClevo")?.GetValue("KeyNotices") as int? ?? 1) == 1;

    /// <summary>The settings switch: show the key notices at all (on by default).</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\OpenClevo");
            k.SetValue("KeyNotices", value ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
        }
    }

    public OsdHost()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "OpenClevo OSD" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(3000);
    }

    void Run()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _osd = new OsdWindow();
        _locks = new LockKeys();
        _locks.Changed += (vk, on) =>
            Show(vk == LockKeys.Caps ? "Aa" : "123", $"{(vk == LockKeys.Caps ? "Caps Lock" : "Num Lock")} {Loc.T(on ? "osd.on" : "osd.off")}", on);
        _ready.Set();
        Dispatcher.Run();
        _locks.Dispose();
    }

    /// <summary>Shows the notice from any thread.</summary>
    public void Notify(string glyph, string text, bool on)
    {
        if (Enabled) _dispatcher?.BeginInvoke(() => _osd?.Notify(glyph, text, on));
    }

    void Show(string glyph, string text, bool on)
    {
        if (Enabled) _osd?.Notify(glyph, text, on);
    }

    public void Dispose() => _dispatcher?.InvokeShutdown();
}

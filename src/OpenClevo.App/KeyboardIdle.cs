using System.Runtime.InteropServices;
using Microsoft.Win32;
using OpenClevo.Core;

namespace OpenClevo.App;

/// <summary>
/// Keyboard backlight timeout: after N seconds without keyboard/mouse input the backlight goes off, the first input
/// brings it back. Done in software (idle time from Windows, brightness through the same EC call the brightness
/// buttons use), on its own thread and driver handle. Also pauses the software effect engine while the light is off.
/// </summary>
public sealed class KeyboardIdle : IDisposable
{
    const string Key = @"Software\OpenClevo";

    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);

    readonly KeyboardEffectEngine _kb;
    readonly Thread _thread;
    volatile bool _run = true;
    AcpiBridge? _br;

    public KeyboardIdle(KeyboardEffectEngine kb)
    {
        _kb = kb;
        _thread = new Thread(Loop) { IsBackground = true, Name = "OpenClevo keyboard timeout" };
        _thread.Start();
    }

    /// <summary>0 = never.</summary>
    public static int TimeoutSeconds
    {
        get => Registry.CurrentUser.OpenSubKey(Key)?.GetValue("KbTimeoutSec") as int? ?? 0;
        set { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue("KbTimeoutSec", value, RegistryValueKind.DWord); }
    }

    /// <summary>The brightness level the user last chose here (levels 0–4); restored when the light comes back.</summary>
    public static int SavedBrightness
    {
        get => Math.Clamp(Registry.CurrentUser.OpenSubKey(Key)?.GetValue("KbBrightness") as int? ?? 4, 0, 4);
        set { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue("KbBrightness", value, RegistryValueKind.DWord); }
    }

    static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? (uint)Environment.TickCount - info.dwTime : 0;
    }

    void Loop()
    {
        bool off = false;
        while (_run)
        {
            Thread.Sleep(250);
            int timeout = TimeoutSeconds;
            double idle = IdleSeconds() / 1000.0;
            try
            {
                if (!off && timeout > 0 && idle >= timeout) { _kb.Paused = true; Bridge().SetKeyboardBrightness(0); off = true; }
                else if (off && (timeout <= 0 || idle < timeout)) { Bridge().SetKeyboardBrightness(SavedBrightness); _kb.Paused = false; off = false; }
            }
            catch { _br?.Dispose(); _br = null; Thread.Sleep(1000); }
        }
        if (off) { try { Bridge().SetKeyboardBrightness(SavedBrightness); } catch { } _kb.Paused = false; }
        _br?.Dispose();
    }

    AcpiBridge Bridge() => _br ??= AcpiBridge.Open();

    public void Dispose() { _run = false; _thread.Join(1500); }
}

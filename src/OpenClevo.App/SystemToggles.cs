using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenClevo.App;

/// <summary>
/// The small on/off switches of the system page. All of them are Windows-level (nothing here talks to the firmware):
/// Num Lock / Scroll Lock (key state), the Windows-key lock (a low-level keyboard hook that swallows the key while
/// OpenClevo runs), touchpad (Windows' precision-touchpad switch) and camera access (the per-user camera privacy switch).
/// </summary>
static class SystemToggles
{
    const string AppKey = @"Software\OpenClevo";
    const string TouchpadKey = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status";
    const string CameraKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    // ---- lock keys ----
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    const int VkNumLock = 0x90, VkScroll = 0x91;

    public static bool NumLock { get => (GetKeyState(VkNumLock) & 1) != 0; set => SetLock(VkNumLock, value); }
    public static bool ScrollLock { get => (GetKeyState(VkScroll) & 1) != 0; set => SetLock(VkScroll, value); }

    static void SetLock(int vk, bool on)
    {
        if (((GetKeyState(vk) & 1) != 0) == on) return;
        keybd_event((byte)vk, 0x45, 1, UIntPtr.Zero);        // extended-key press
        keybd_event((byte)vk, 0x45, 1 | 2, UIntPtr.Zero);    // and release
    }

    // ---- Windows key lock ----
    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    static IntPtr _hook;
    static readonly HookProc Proc = (code, w, l) =>
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(l);   // KBDLLHOOKSTRUCT.vkCode
            if (vk is 0x5B or 0x5C) return (IntPtr)1;   // left / right Windows key
        }
        return CallNextHookEx(_hook, code, w, l);
    };

    public static bool WinKeyLock
    {
        get => _hook != IntPtr.Zero;
        set
        {
            if (value && _hook == IntPtr.Zero)
                _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, Proc, GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName), 0);
            else if (!value && _hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            using var k = Registry.CurrentUser.CreateSubKey(AppKey);
            k.SetValue("WinKeyLock", value ? 1 : 0, RegistryValueKind.DWord);
        }
    }

    /// <summary>Re-installs the Windows-key lock if it was on when the app last ran.</summary>
    public static void Restore()
    {
        if (Registry.CurrentUser.OpenSubKey(AppKey)?.GetValue("WinKeyLock") as int? == 1) WinKeyLock = true;
    }

    public static void Shutdown() { if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; } }

    // ---- touchpad ----
    public static bool TouchpadSupported => Registry.CurrentUser.OpenSubKey(TouchpadKey) != null;

    public static bool Touchpad
    {
        get => Registry.CurrentUser.OpenSubKey(TouchpadKey)?.GetValue("Enabled") as int? == 1;
        set { using var k = Registry.CurrentUser.CreateSubKey(TouchpadKey); k.SetValue("Enabled", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    // ---- camera ----
    public static bool Camera
    {
        get => (Registry.CurrentUser.OpenSubKey(CameraKey)?.GetValue("Value") as string) != "Deny";
        set { using var k = Registry.CurrentUser.CreateSubKey(CameraKey); k.SetValue("Value", value ? "Allow" : "Deny", RegistryValueKind.String); }
    }
}

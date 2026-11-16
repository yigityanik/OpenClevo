using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenClevo.App;

/// <summary>Watches Caps Lock and Num Lock (a low-level keyboard hook that never swallows a key) and reports the new state.</summary>
sealed class LockKeys : IDisposable
{
    public const int Caps = 0x14, Num = 0x90;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    readonly HookProc _proc;
    readonly IntPtr _hook;

    /// <summary>Raised on the UI thread: key (Caps / Num) and whether it is now on.</summary>
    public event Action<int, bool>? Changed;

    public LockKeys()
    {
        var ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _proc = (code, w, l) =>
        {
            if (code >= 0 && (int)w is 0x100 or 0x101)   // key down / up (the lock flips on the down)
            {
                int vk = Marshal.ReadInt32(l);
                if (vk is Caps or Num && (int)w == 0x100)
                    ui.BeginInvoke(async () =>
                    {
                        await Task.Delay(20);   // let Windows flip the state first
                        Changed?.Invoke(vk, (GetKeyState(vk) & 1) != 0);
                    });
            }
            return CallNextHookEx(_hook, code, w, l);
        };
        _hook = SetWindowsHookEx(13, _proc, GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName), 0);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}

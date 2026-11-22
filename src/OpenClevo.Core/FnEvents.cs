using System.Runtime.InteropServices;

namespace OpenClevo.Core;

/// <summary>
/// Listens for the Fn-key events of the Acpi Bridge driver the way Clevo's DCHUService does: IOCTL 0x322404 registers a
/// Win32 event, the driver signals it when a key is pressed, and IOCTL 0x322408 returns the 64-bit event code.
/// The firmware (or Windows) already does the work for these keys; the events only report what happened, so the app can
/// show a notice and keep its switches in step. Several programs can listen at the same time (Clevo's service too).
/// Codes seen on a Gigabyte G5 KF, matching Clevo's FnKey: 0xF6 / 0xF7 camera off / on, 0x5D touchpad changed,
/// 0xFA / 0xFB keyboard backlight keys.
/// </summary>
public sealed class FnEvents : IDisposable
{
    public const long CameraOff = 0xF6, CameraOn = 0xF7, Touchpad = 0x5D;

    const uint IoctlRegister = 0x322404, IoctlRead = 0x322408;

    readonly Thread _thread;
    volatile bool _stop;

    /// <summary>Raised on the listener thread with the event code.</summary>
    public event Action<long>? Received;

    /// <summary>True once the driver accepted the event registration (the listener is live).</summary>
    public volatile bool Registered;

    /// <summary>Why the channel could not be opened (null while fine or not yet tried).</summary>
    public volatile string? Error;

    public FnEvents()
    {
        _thread = new Thread(Listen) { IsBackground = true, Name = "OpenClevo Fn events" };
        _thread.Start();
    }

    void Listen()
    {
        try
        {
            using var h = CreateFileW(AcpiBridge.FindDevicePath(), 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
            if (h.IsInvalid) { Error = "driver handle could not be opened (error " + Marshal.GetLastWin32Error() + ")"; return; }
            IntPtr evt = CreateEvent(IntPtr.Zero, true, false, null);
            IntPtr inBuf = Marshal.AllocHGlobal(8), outBuf = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteIntPtr(inBuf, evt);
                if (!DeviceIoControl(h, IoctlRegister, inBuf, 8, IntPtr.Zero, 0, out _, IntPtr.Zero)) { Error = "event registration refused (error " + Marshal.GetLastWin32Error() + ")"; return; }
                Registered = true;
                while (!_stop)
                {
                    if (WaitForSingleObject(evt, 500) != 0) continue;   // timeout: look at _stop again
                    ResetEvent(evt);
                    Marshal.WriteInt64(outBuf, 0);
                    if (!DeviceIoControl(h, IoctlRead, IntPtr.Zero, 0, outBuf, 8, out _, IntPtr.Zero)) continue;
                    try { Received?.Invoke(Marshal.ReadInt64(outBuf)); } catch { }   // a failing handler must not end the listener
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inBuf);
                Marshal.FreeHGlobal(outBuf);
                CloseHandle(evt);
            }
        }
        catch (Exception ex) { Error ??= ex.Message; }   // no driver: nothing to listen to (the main window already says so)
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1500);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, IntPtr inBuf, int inLen, IntPtr outBuf, int outLen, out int ret, IntPtr ov);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateEvent(IntPtr sa, bool manual, bool initial, string? name);

    [DllImport("kernel32.dll")]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);

    [DllImport("kernel32.dll")]
    static extern bool ResetEvent(IntPtr h);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
}

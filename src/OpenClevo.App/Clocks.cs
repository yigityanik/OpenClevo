using System.Runtime.InteropServices;

namespace OpenClevo.App;

/// <summary>
/// Current CPU clock: base frequency × "% Processor Performance" (the same counters Task Manager uses), read through PDH.
/// Call Read() from one thread, about once a second; the first call returns 0 (rate counters need two samples).
/// </summary>
sealed class CpuClock : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct Value { public uint Status; public double Double; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQuery(string? source, IntPtr user, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type, out Value value);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);

    const uint Double = 0x200;
    IntPtr _query, _freq, _perf;
    bool _ok;

    public CpuClock()
    {
        try
        {
            _ok = PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
               && PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\Processor Frequency", IntPtr.Zero, out _freq) == 0
               && PdhAddEnglishCounterW(_query, @"\Processor Information(_Total)\% Processor Performance", IntPtr.Zero, out _perf) == 0;
            if (_ok) PdhCollectQueryData(_query);
        }
        catch { _ok = false; }
    }

    public int Read()
    {
        if (!_ok) return 0;
        try
        {
            if (PdhCollectQueryData(_query) != 0) return 0;
            if (PdhGetFormattedCounterValue(_freq, Double, IntPtr.Zero, out var f) != 0) return 0;
            if (PdhGetFormattedCounterValue(_perf, Double, IntPtr.Zero, out var p) != 0) return 0;
            return (int)Math.Round(f.Double * p.Double / 100.0);
        }
        catch { return 0; }
    }

    public void Dispose() { if (_ok) PdhCloseQuery(_query); _ok = false; }
}

/// <summary>
/// Current graphics clock of the first NVIDIA GPU, through the driver's own nvml.dll. 0 when there is none.
/// A device handle is only valid while the GPU stays as it was, so: it is fetched fresh for every read, and the whole
/// NVML session is shut down whenever the GPU is switched off, then started again when it is back. (Calling NVML with a
/// handle from before an off/on cycle crashes the process, and that cannot be caught.)
/// </summary>
sealed class GpuClock : IDisposable
{
    delegate int InitFn();
    delegate int ShutdownFn();
    delegate int HandleFn(uint index, out IntPtr device);
    delegate int ClockFn(IntPtr device, uint type, out uint mhz);

    InitFn? _init;
    ShutdownFn? _shutdown;
    HandleFn? _handle;
    ClockFn? _clock;
    bool _started, _missing;

    bool Load()
    {
        if (_clock != null) return true;
        if (_missing) return false;
        try
        {
            if (!NativeLibrary.TryLoad("nvml.dll", out var lib) &&
                !NativeLibrary.TryLoad(@"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll", out lib)) { _missing = true; return false; }
            _init = Marshal.GetDelegateForFunctionPointer<InitFn>(NativeLibrary.GetExport(lib, "nvmlInit_v2"));
            _shutdown = Marshal.GetDelegateForFunctionPointer<ShutdownFn>(NativeLibrary.GetExport(lib, "nvmlShutdown"));
            _handle = Marshal.GetDelegateForFunctionPointer<HandleFn>(NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2"));
            _clock = Marshal.GetDelegateForFunctionPointer<ClockFn>(NativeLibrary.GetExport(lib, "nvmlDeviceGetClockInfo"));
            return true;
        }
        catch { _missing = true; return false; }
    }

    void Stop()
    {
        if (!_started) return;
        _started = false;
        try { _shutdown!(); } catch { }
    }

    /// <param name="gpuOn">false while the GPU is switched off: nothing is called and the session is dropped.</param>
    public int Read(bool gpuOn)
    {
        if (!gpuOn) { Stop(); return 0; }
        if (!Load()) return 0;
        try
        {
            if (!_started) { if (_init!() != 0) return 0; _started = true; }
            if (_handle!(0, out var device) != 0) { Stop(); return 0; }
            if (_clock!(device, 0 /* graphics */, out uint mhz) == 0) return (int)mhz;
            Stop();
            return 0;
        }
        catch { Stop(); return 0; }
    }

    public void Dispose() => Stop();
}
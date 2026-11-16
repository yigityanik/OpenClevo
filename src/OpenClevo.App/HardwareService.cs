using System.Windows.Threading;
using OpenClevo.Core;

namespace OpenClevo.App;

/// <summary>Owns the driver connection, polls sensors every second and keeps a short history. UI thread only.</summary>
public sealed class HardwareService : IDisposable
{
    public const int HistoryLength = 90;

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    AcpiBridge? _bridge;

    public FanReading[] Fans { get; private set; } = [];
    public Dictionary<int, List<FanReading>> History { get; } = new();
    public FanCurve[] Curves { get; private set; } = [];
    public PowerMode? PowerMode { get; private set; }
    public FanMode? FanMode { get; private set; }
    public int FanOffset { get; private set; }
    public FlexiCharger? Charger { get; private set; }
    public GpuMode? Gpu { get; private set; }
    public KeyboardState? Keyboard { get; private set; }
    public string? Error { get; private set; }

    public event Action? Updated;
    public event Action? SettingsChanged;

    public void Start()
    {
        _timer.Tick += (_, _) => Poll();
        ReloadSettings();
        Poll();
        _timer.Start();
    }

    AcpiBridge Bridge => _bridge ??= AcpiBridge.Open();

    // The ~45 ms firmware calls run on a worker thread with their own driver handle, so the UI never stalls
    // (and animations keep their rhythm) when the sensors are read.
    AcpiBridge? _pollBridge;
    readonly CpuClock _cpuClock = new();
    readonly GpuClock _gpuClock = new();
    bool _polling;

    /// <summary>Camera power from the firmware (kept fresh by the poll; the Fn key changes it too). Null until the first read.</summary>
    public bool? CameraOn { get; private set; }

    /// <summary>The Fn camera key changed the camera power: take the new state now instead of waiting for the next poll.</summary>
    public void NoteCameraPower(bool on) => CameraOn = on;

    public int CpuMhz { get; private set; }
    public int GpuMhz { get; private set; }

    async void Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            var (fans, cpu, gpu, cam) = await Task.Run(() =>
            {
                _pollBridge ??= AcpiBridge.Open();
                return (_pollBridge.ReadFans(), _cpuClock.Read(), _gpuClock.Read(NvidiaPower.IsOn != false),   // never touch a switched-off GPU
                        _pollBridge.ReadCameraPower());
            });
            Fans = fans; CpuMhz = cpu; GpuMhz = gpu; CameraOn = cam;
            foreach (var f in Fans)
            {
                if (!History.TryGetValue(f.Index, out var h)) History[f.Index] = h = new();
                h.Add(f);
                if (h.Count > HistoryLength) h.RemoveAt(0);
            }
            Error = null;
        }
        catch (Exception ex) { Error = ex.Message; _pollBridge?.Dispose(); _pollBridge = null; }
        finally { _polling = false; }
        Updated?.Invoke();
    }
    /// <summary>Re-reads mode and curves (they can also be changed by Fn keys / Clevo's app).</summary>
    public void ReloadSettings()
    {
        try
        {
            PowerMode = Bridge.GetSavedPowerMode();
            int fm = Bridge.GetSavedFanMode();
            FanMode = Enum.IsDefined(typeof(FanMode), (byte)fm) ? (FanMode)fm : null;
            FanOffset = Bridge.GetSavedFanOffset();
            Curves = Bridge.ReadFanCurves();
            Charger = Bridge.ReadFlexiCharger();
            Gpu = Bridge.ReadGpuMode().Mode;
            Keyboard = Bridge.ReadKeyboard();
            Error = null;
        }
        catch (Exception ex) { Fail(ex); }
        SettingsChanged?.Invoke();
    }

    public void SetPowerMode(PowerMode mode) => Run(() => Bridge.SetPowerMode(mode));
    public void SetFanMode(FanMode mode) => Run(() => Bridge.SetFanMode(mode));
    public void SetFanOffset(int percent) => Run(() => Bridge.SetFanOffset(percent));
    public void ApplyKeyboard(KbEffect e, Rgb l, Rgb m, Rgb r) => Run(() => Bridge.ApplyKeyboard(e, l, m, r));
    public void SetKeyboardBrightness(int level) => Bridge.SetKeyboardBrightness(level);

    /// <summary>Fast path for software effects: colors only, no settings reload. Errors are swallowed (next frame retries).</summary>
    public void SetKeyboardColors(Rgb? l, Rgb? m, Rgb? r)
    {
        try { Bridge.SetKeyboardColors(Keyboard?.Type ?? 6, l, m, r); }
        catch (Exception ex) { Fail(ex); }
    }
    public void SetCharger(FlexiCharger f) => Run(() => Bridge.SetFlexiCharger(f));

    /// <summary>Cuts or restores the camera's power in firmware (what the Fn camera key does).</summary>
    public void SetCameraPower(bool on)
    {
        try { Bridge.SetCameraPower(on); CameraOn = on; }
        catch (Exception ex) { Fail(ex); throw; }
    }
    public void ApplyCurves(IReadOnlyList<FanCurve> curves) => Run(() => Bridge.ApplyFanCurves(curves));

    void Run(Action a)
    {
        try { a(); }
        finally { ReloadSettings(); }
    }

    void Fail(Exception ex)
    {
        Error = ex.Message;
        _bridge?.Dispose();
        _bridge = null;
    }

    public void Dispose()
    {
        _timer.Stop();
        _bridge?.Dispose();
        _pollBridge?.Dispose();
        _cpuClock.Dispose();
        _gpuClock.Dispose();
    }
}

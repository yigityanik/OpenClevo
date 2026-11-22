using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace OpenClevo.App;

/// <summary>
/// Measures the bass energy of what the PC is playing. Uses WASAPI loopback on the default playback device,
/// mixes to mono and runs a 24 dB/oct low-pass at 150 Hz. Samples are processed in memory and discarded;
/// nothing is stored. <see cref="Read"/> returns the loudest ~10 ms bass window since the previous call,
/// so short kicks are never averaged away. The master volume is read separately because it is not reflected in loopback.
/// </summary>
public sealed class BassMeter : IDisposable
{
    const double Cutoff = 150;

    readonly object _gate = new();
    WasapiLoopbackCapture? _capture;
    MMDevice? _device;
    double _volume = 1; DateTime _volumeAt = DateTime.MinValue;
    DateTime _deviceCheckAt = DateTime.MinValue;
    Biquad _lp1, _lp2;
    double _windowSum; int _windowCount, _windowSize;
    double _maxSinceRead;
    DateTime _lastData = DateTime.MinValue, _retryAt = DateTime.MinValue;
    bool _disposed;

    public bool IsCapturing { get { lock (_gate) return _capture != null; } }
    public string? Format { get; private set; }

    /// <summary>Bass RMS (0..~1) of the strongest short window since the last call; 0 when nothing plays.</summary>
    public double Read()
    {
        EnsureCapture();
        lock (_gate)
        {
            double v = _maxSinceRead;
            _maxSinceRead = 0;
            return DateTime.UtcNow - _lastData > TimeSpan.FromMilliseconds(300) ? 0 : v;
        }
    }

    void EnsureCapture()
    {
        lock (_gate)
        {
            if (_disposed || _capture != null || DateTime.UtcNow < _retryAt) return;
            MMDevice? dev = null;
            WasapiLoopbackCapture? cap = null;
            try
            {
                using var en = new MMDeviceEnumerator();
                dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                cap = new WasapiLoopbackCapture(dev);
                var wf = cap.WaveFormat;
                Format = $"{wf.SampleRate} Hz, {wf.Channels} ch, {wf.BitsPerSample} bit {wf.Encoding}";
                _lp1 = Biquad.LowPass(wf.SampleRate, Cutoff, 0.7071);
                _lp2 = Biquad.LowPass(wf.SampleRate, Cutoff, 0.7071);
                _windowSize = Math.Max(1, wf.SampleRate / 100);   // 10 ms
                _windowSum = 0; _windowCount = 0;
                cap.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, wf);
                // device removed/changed: drop it and retry. Dispose off the capture thread (Dispose joins that thread).
                cap.RecordingStopped += (_, _) =>
                {
                    MMDevice? stoppedDevice = null;
                    lock (_gate)
                    {
                        if (_capture == cap) { _capture = null; stoppedDevice = _device; _device = null; }
                        _retryAt = DateTime.UtcNow.AddSeconds(2);
                    }
                    // Dispose() below may dispose the same capture (it stops it, which raises this event): a second dispose
                    // throws inside NAudio, and on a pool thread that would take the whole app down
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { cap.Dispose(); } catch { }
                        try { stoppedDevice?.Dispose(); } catch { }
                    });
                };
                cap.StartRecording();
                _capture = cap;
                _device = dev;
                _volumeAt = DateTime.MinValue;
            }
            catch
            {
                try { cap?.Dispose(); } catch { }
                dev?.Dispose();
                _device = null;
                _capture = null;
                _retryAt = DateTime.UtcNow.AddSeconds(3);
            }
        }
    }

    void OnData(byte[] buf, int bytes, WaveFormat wf)
    {
        int ch = wf.Channels, bps = wf.BitsPerSample / 8, frame = ch * bps;
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf.Encoding == WaveFormatEncoding.Extensible && bps == 4);
        lock (_gate)
        {
            for (int o = 0; o + frame <= bytes; o += frame)
            {
                double mono = 0;
                for (int c = 0; c < ch; c++)
                {
                    int p = o + c * bps;
                    mono += isFloat ? BitConverter.ToSingle(buf, p)
                          : bps == 2 ? BitConverter.ToInt16(buf, p) / 32768.0
                          : bps == 4 ? BitConverter.ToInt32(buf, p) / 2147483648.0 : 0;
                }
                mono /= ch;
                double y = _lp2.Process(_lp1.Process(mono));
                _windowSum += y * y;
                if (++_windowCount >= _windowSize)
                {
                    double rms = Math.Sqrt(_windowSum / _windowCount);
                    if (rms > _maxSinceRead) _maxSinceRead = rms;
                    _windowSum = 0; _windowCount = 0;
                }
            }
            _lastData = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        WasapiLoopbackCapture? cap;
        MMDevice? device;
        lock (_gate) { _disposed = true; cap = _capture; _capture = null; device = _device; _device = null; }
        try { cap?.StopRecording(); } catch { }
        try { cap?.Dispose(); } catch { }
        try { device?.Dispose(); } catch { }
    }

    public double ReadVolume()
    {
        WasapiLoopbackCapture? captureToStop = null;
        lock (_gate)
        {
            if (_device == null) return 1;
            var now = DateTime.UtcNow;
            if (now - _deviceCheckAt >= TimeSpan.FromSeconds(1))
            {
                _deviceCheckAt = now;
                try
                {
                    using var en = new MMDeviceEnumerator();
                    using var defaultDevice = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (defaultDevice.ID != _device.ID) captureToStop = _capture;
                }
                catch { }
            }
        }

        try { captureToStop?.StopRecording(); } catch { }

        lock (_gate)
        {
            if (_device == null) return 1;
            if (DateTime.UtcNow - _volumeAt < TimeSpan.FromMilliseconds(200)) return _volume;
            try
            {
                _volume = _device.AudioEndpointVolume.Mute
                    ? 0
                    : Math.Clamp(_device.AudioEndpointVolume.MasterVolumeLevelScalar, 0, 1);
                _volumeAt = DateTime.UtcNow;
                return _volume;
            }
            catch
            {
                _volume = 1;
                _volumeAt = DateTime.UtcNow;
                return 1;
            }
        }
    }

    /// <summary>Offline check of the filter chain: RMS left after filtering a 0.5-amplitude sine (input RMS ≈ 0.354).</summary>
    internal static double MeasureSine(double freqHz, int fs = 48000)
    {
        var a = Biquad.LowPass(fs, Cutoff, 0.7071); var b = Biquad.LowPass(fs, Cutoff, 0.7071);
        double sum = 0; int n = fs, skip = fs / 10;
        for (int i = 0; i < n; i++)
        {
            double y = b.Process(a.Process(0.5 * Math.Sin(2 * Math.PI * freqHz * i / fs)));
            if (i >= skip) sum += y * y;
        }
        return Math.Sqrt(sum / (n - skip));
    }

    /// <summary>RBJ cookbook biquad (direct form I).</summary>
    struct Biquad
    {
        double b0, b1, b2, a1, a2, x1, x2, y1, y2;

        public static Biquad LowPass(double fs, double f0, double q)
        {
            double w = 2 * Math.PI * f0 / fs, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q), a0 = 1 + alpha;
            return new Biquad
            {
                b0 = (1 - cos) / 2 / a0, b1 = (1 - cos) / a0, b2 = (1 - cos) / 2 / a0,
                a1 = -2 * cos / a0, a2 = (1 - alpha) / a0,
            };
        }

        public double Process(double x)
        {
            double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            return y;
        }
    }
}

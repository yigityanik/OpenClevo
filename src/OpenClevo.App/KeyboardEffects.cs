using System.Diagnostics;
using Microsoft.Win32;
using OpenClevo.Core;

namespace OpenClevo.App;

/// <summary>Effects computed by OpenClevo itself.</summary>
public enum SoftEffect { None, Breathe, Cycle, Flash, RandomFade, Temperature, Audio, AudioCycle }

/// <summary>
/// Software keyboard effects. Runs on its own thread with its own driver handle: every _DSM call takes ~45 ms
/// (ACPI → EC), so frames are sent as fast as the hardware allows (min 50 ms apart) without blocking the UI.
/// Colors are continuous (no stepping) and brightness follows a perceptual curve so fades look smooth.
/// </summary>
public sealed class KeyboardEffectEngine : IDisposable
{
    const string Key = @"Software\OpenClevo";
    static readonly TimeSpan MinFrame = TimeSpan.FromMilliseconds(50);

    readonly HardwareService _hw;
    readonly object _gate = new();
    Thread? _thread;
    volatile bool _run;
    SoftEffect _effect;
    Rgb[] _base = [new(0, 0, 255), new(0, 0, 255), new(0, 0, 255)];

    public KeyboardEffectEngine(HardwareService hw) => _hw = hw;

    public SoftEffect Current { get { lock (_gate) return _effect; } }

    /// <summary>The colours the running effect is sending right now (null when nothing runs). The UI mirrors this.</summary>
    public volatile Rgb[]? LastFrame;

    /// <summary>Set by the keyboard timeout while the light is off: no frames are sent.</summary>
    public volatile bool Paused;

    /// <summary>Single-zone keyboards (type 6 on this machine) only honour the first zone command.</summary>
    public bool SingleZone { get; set; } = true;

    public static SoftEffect Saved
    {
        get
        {
            var s = Registry.CurrentUser.OpenSubKey(Key)?.GetValue("SoftEffect") as string;
            return s switch { "Rainbow" => SoftEffect.Cycle, "SoftBreathe" => SoftEffect.Breathe, _ => Enum.TryParse(s, out SoftEffect e) ? e : SoftEffect.None };
        }
        set { using var k = Registry.CurrentUser.CreateSubKey(Key); k.SetValue("SoftEffect", value.ToString()); }
    }

    public void Start(SoftEffect effect, Rgb[] baseColors)
    {
        lock (_gate) { _effect = effect; _base = baseColors.ToArray(); }
        Saved = effect;
        if (effect == SoftEffect.None) return;
        if (_thread is { IsAlive: true }) return;   // running loop picks up the new effect
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "OpenClevo keyboard effects", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop(bool remember = true)
    {
        lock (_gate) _effect = SoftEffect.None;
        LastFrame = null;
        _run = false;
        _thread?.Join(500);
        _thread = null;
        if (remember) Saved = SoftEffect.None;
    }

    public void UpdateBase(Rgb[] colors) { lock (_gate) _base = colors.ToArray(); }

    void Loop()
    {
        AcpiBridge? br = null;
        var clock = Stopwatch.StartNew();
        var last = new Rgb?[3];
        var smooth = new double[3][];      // eased colour per zone (temperature / random fade)
        int type = _hw.Keyboard?.Type ?? 6;
        var caps = _hw.Keyboard?.Caps;   // logo / lightbar follow the colour on laptops that have them
        var rnd = new Random();
        (Rgb From, Rgb To, double Start) fade = (new(255, 0, 0), new(0, 0, 255), 0);
        SoftEffect running = SoftEffect.None;
        BassMeter? bass = null;
        double envelope = 0, peakHold = 0.02, avg = 0, lastKick = -1;   // audio: light level, auto gain, bass average, last kick time
        double hueOffset = 0;                                            // audio+cycle: extra hue added on each kick

        // Bass-driven brightness (shared by Audio and AudioCycle). Returns 0.03..1 and whether this frame is a kick.
        (double K, bool Kick) AudioLevel(double t)
        {
            bass ??= new BassMeter();
            double b = bass.Read();                                     // strongest 10 ms bass window since last frame
            peakHold = Math.Max(b, peakHold * 0.997);                   // auto gain (~10 s memory)
            double norm = peakHold > 0.004 ? Math.Clamp(b / peakHold, 0, 1) : 0;
            // kick: bass jumps well above its (slow) average, at most once per 150 ms → full brightness instantly.
            // The refractory time stops one sustained kick from re-triggering every frame (that was the flicker).
            bool kick = norm > 0.5 && norm > avg * 1.4 && t - lastKick > 0.15;
            avg = avg * 0.95 + norm * 0.05;
            if (kick) lastKick = t;
            double target = kick ? 1.0 : 0.85 * Math.Pow(norm, 1.5);   // sustained bass holds a steady glow
            envelope = target > envelope
                ? envelope + (target - envelope) * (kick ? 1.0 : 0.6)  // instant on kicks, quick otherwise
                : envelope * 0.80;                                      // ~250 ms decay, no hard drop
            double vol = bass.ReadVolume();
            return (0.03 + 0.97 * envelope * vol, kick && vol > 0);
        }

        while (_run)
        {
            if (Paused) { Array.Clear(last); Thread.Sleep(200); continue; }   // light is off (timeout)
            var frameStart = clock.Elapsed;
            SoftEffect effect; Rgb[] baseC;
            lock (_gate) { effect = _effect; baseC = _base; }
            if (effect == SoftEffect.None) break;
            if (effect != running) { running = effect; Array.Clear(last); smooth = new double[3][]; clock.Restart(); frameStart = TimeSpan.Zero; }
            double t = frameStart.TotalSeconds;

            Rgb[] frame;
            switch (effect)
            {
                case SoftEffect.Breathe:
                {
                    // perceptual breathing: 5 s period, eased, gamma 2.2, never fully dark
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 5.0);
                    double k = 0.06 + 0.94 * Math.Pow(w, 2.2);
                    frame = baseC.Select(c => Scale(c, k)).ToArray();
                    break;
                }
                case SoftEffect.Cycle:
                {
                    double h = t * 30;   // full cycle in 12 s
                    frame = SingleZone ? [Hue(h), Hue(h), Hue(h)] : [Hue(h), Hue(h + 50), Hue(h + 100)];
                    break;
                }
                case SoftEffect.Flash:
                {
                    double p = t % 1.0;   // 1 Hz: quick fade-in, hold, fade-out
                    double k = p < 0.08 ? p / 0.08 : p < 0.35 ? 1 : p < 0.5 ? 1 - (p - 0.35) / 0.15 : 0;
                    frame = baseC.Select(c => Scale(c, k)).ToArray();
                    break;
                }
                case SoftEffect.RandomFade:
                {
                    const double hold = 1.5, trans = 2.0;
                    if (t - fade.Start > hold + trans) fade = (fade.To, Hue(rnd.Next(0, 360)), t);
                    double f = Math.Clamp((t - fade.Start) / trans, 0, 1);
                    f = f * f * (3 - 2 * f);   // smoothstep
                    var c = Lerp(fade.From, fade.To, f);
                    frame = [c, c, c];
                    break;
                }
                case SoftEffect.Temperature:
                {
                    int cpu = _hw.Fans.FirstOrDefault(x => x.Index == 1).TemperatureC;
                    int gpu = _hw.Fans.FirstOrDefault(x => x.Index == 2).TemperatureC;
                    var hot = TempColor(Math.Max(cpu, gpu));
                    Rgb[] target = SingleZone || gpu == 0 ? [hot, hot, hot] : [TempColor(cpu), TempColor(cpu), TempColor(gpu)];
                    frame = new Rgb[3];
                    for (int i = 0; i < 3; i++)   // glide toward the target instead of jumping
                    {
                        smooth[i] ??= [target[i].R, target[i].G, target[i].B];
                        smooth[i][0] += (target[i].R - smooth[i][0]) * 0.15;
                        smooth[i][1] += (target[i].G - smooth[i][1]) * 0.15;
                        smooth[i][2] += (target[i].B - smooth[i][2]) * 0.15;
                        frame[i] = new((byte)Math.Round(smooth[i][0]), (byte)Math.Round(smooth[i][1]), (byte)Math.Round(smooth[i][2]));
                    }
                    break;
                }
                case SoftEffect.Audio:
                {
                    var (k, _) = AudioLevel(t);
                    frame = baseC.Select(c => Scale(c, k)).ToArray();
                    break;
                }
                case SoftEffect.AudioCycle:
                {
                    // colour drifts round the wheel (20 s per turn) and jumps 40° on every kick; brightness follows the bass
                    var (k, kick) = AudioLevel(t);
                    if (kick) hueOffset += 40;
                    double h = t * 18 + hueOffset;
                    Rgb[] hues = SingleZone ? [Hue(h), Hue(h), Hue(h)] : [Hue(h), Hue(h + 50), Hue(h + 100)];
                    frame = hues.Select(c => Scale(c, k)).ToArray();
                    break;
                }
                default: frame = baseC; break;
            }

            LastFrame = frame;
            try
            {
                br ??= AcpiBridge.Open();
                int zones = SingleZone ? 1 : 3;
                Rgb?[] send = new Rgb?[3];
                bool any = false;
                for (int i = 0; i < zones; i++)
                    if (last[i] is not { } l || !SameOnWire(l, frame[i], type)) { send[i] = frame[i]; any = true; }
                if (any)
                {
                    br.SetKeyboardColors(type, send[0], send[1], send[2], caps);
                    for (int i = 0; i < zones; i++) if (send[i] != null) last[i] = send[i];
                }
            }
            catch
            {
                br?.Dispose(); br = null;
                Thread.Sleep(1000);
            }

            // audio runs flat out (the ~45 ms driver call is the pacing); other effects keep a 50 ms minimum frame
            bool audio = effect is SoftEffect.Audio or SoftEffect.AudioCycle;
            var wait = (audio ? TimeSpan.FromMilliseconds(5) : MinFrame) - (clock.Elapsed - frameStart);
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
            if (!audio && bass != null) { bass.Dispose(); bass = null; }   // stop loopback when not needed
        }
        br?.Dispose();
        bass?.Dispose();
    }


    /// <summary>
    /// Stateless look-alike of each effect, used by the on-screen keyboard to preview an effect that is not running
    /// (hovering a chip). Mirrors the timing of the real ones; the audio effects use a steady 120 bpm pulse instead of
    /// the real bass.
    /// </summary>
    public static Rgb[] Simulate(SoftEffect effect, double t, Rgb[] baseC, bool single, int cpuTemp, int gpuTemp)
    {
        switch (effect)
        {
            case SoftEffect.Breathe:
            {
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / 5.0);
                return baseC.Select(c => Scale(c, 0.06 + 0.94 * Math.Pow(w, 2.2))).ToArray();
            }
            case SoftEffect.Cycle:
            {
                double h = t * 30;
                return single ? [Hue(h), Hue(h), Hue(h)] : [Hue(h), Hue(h + 50), Hue(h + 100)];
            }
            case SoftEffect.Flash:
            {
                double p = t % 1.0;
                double k = p < 0.08 ? p / 0.08 : p < 0.35 ? 1 : p < 0.5 ? 1 - (p - 0.35) / 0.15 : 0;
                return baseC.Select(c => Scale(c, k)).ToArray();
            }
            case SoftEffect.RandomFade:
            {
                const double period = 3.5;
                static Rgb Pick(long n) => Hue(((n * 2654435761L) % 360 + 360) % 360);
                long i = (long)Math.Floor(t / period);
                double f = Math.Clamp((t / period - i) * period / 2.0, 0, 1);
                f = f * f * (3 - 2 * f);
                var c = Lerp(Pick(i), Pick(i + 1), f);
                return [c, c, c];
            }
            case SoftEffect.Temperature:
            {
                var hot = TempColor(Math.Max(cpuTemp, gpuTemp));
                return single || gpuTemp == 0 ? [hot, hot, hot] : [TempColor(cpuTemp), TempColor(cpuTemp), TempColor(gpuTemp)];
            }
            case SoftEffect.Audio:
            case SoftEffect.AudioCycle:
            {
                double beat = t % 0.5;                                       // 120 bpm
                double k = 0.25 + 0.75 * Math.Exp(-beat * 6);
                if (effect == SoftEffect.Audio) return baseC.Select(c => Scale(c, k)).ToArray();
                double h = t * 18 + Math.Floor(t / 0.5) * 40;
                Rgb[] hues = single ? [Hue(h), Hue(h), Hue(h)] : [Hue(h), Hue(h + 50), Hue(h + 100)];
                return hues.Select(c => Scale(c, k)).ToArray();
            }
            default: return baseC;
        }
    }
    /// <summary>Type 6/22 halve each component on the wire, so only send when the halved value changes.</summary>
    static bool SameOnWire(Rgb a, Rgb b, int type) =>
        type is 6 or 22 ? a.R / 2 == b.R / 2 && a.G / 2 == b.G / 2 && a.B / 2 == b.B / 2 : a == b;

    /// <summary>Blue ≤45 °C → green 60 → orange 75 → red ≥88.</summary>
    public static Rgb TempColor(double t)
    {
        (double T, Rgb C)[] stops = [(45, new(0, 90, 255)), (60, new(0, 230, 120)), (75, new(255, 150, 0)), (88, new(255, 0, 0))];
        if (t <= stops[0].T) return stops[0].C;
        for (int i = 1; i < stops.Length; i++)
            if (t <= stops[i].T) return Lerp(stops[i - 1].C, stops[i].C, (t - stops[i - 1].T) / (stops[i].T - stops[i - 1].T));
        return stops[^1].C;
    }

    static Rgb Scale(Rgb c, double k) => new((byte)Math.Round(c.R * k), (byte)Math.Round(c.G * k), (byte)Math.Round(c.B * k));

    static Rgb Lerp(Rgb a, Rgb b, double f) =>
        new((byte)Math.Round(a.R + (b.R - a.R) * f), (byte)Math.Round(a.G + (b.G - a.G) * f), (byte)Math.Round(a.B + (b.B - a.B) * f));

    /// <summary>Continuous, fully saturated hue.</summary>
    static Rgb Hue(double deg)
    {
        double h = (deg % 360 + 360) % 360 / 60.0;
        double x = 1 - Math.Abs(h % 2 - 1);
        (double r, double g, double b) = (int)h switch
        {
            0 => (1.0, x, 0.0), 1 => (x, 1.0, 0.0), 2 => (0.0, 1.0, x),
            3 => (0.0, x, 1.0), 4 => (x, 0.0, 1.0), _ => (1.0, 0.0, x),
        };
        return new((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    public void Dispose() => Stop(remember: false);
}

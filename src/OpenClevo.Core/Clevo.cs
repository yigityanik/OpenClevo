namespace OpenClevo.Core;

/// <summary>Firmware _DSM function numbers (reverse engineered from Clevo FnKey/CC40).</summary>
public static class DsmFn
{
    public const int FanTable = 4;          // RPM fan table (buffer[0]: 40/42 read, 41/43 write) – empty on G5 KF
    public const int HardwareMonitor = 12;  // fan raw speed, duty, temperatures (read-only, polled by CC40)
    public const int FanCurveRead = 13;     // current 4-point duty curves (read-only)
    public const int FanCurveWrite = 14;    // apply 4-point duty curves
    public const int ProductInfo = 17;
    public const int SetFeature = 121;      // 0x79: data[0..2] = value, data[3] = sub-command
}

/// <summary>Sub-commands of _DSM 0x79 (SetFeature).</summary>
public static class FeatureSub
{
    public const byte FanMode = 1;          // 0 = auto (firmware), 6 = custom curve
    public const byte FanOffset = 14;
    public const byte PowerMode = 25;       // 0 quiet, 1 power saving, 2 performance (+0x80/+0x40 flags), 3 entertainment
    public const byte Airplane = 26;
}

/// <summary>Byte addresses in the 4 KiB AppSettings store that Clevo's own app reads.</summary>
public static class AppAddr
{
    public const int PowerMode = 0x101;
    public const int FanMode = 0x405;
    public const int FanOffset = 0x407;
    public const int CurveBase = 0x410;     // per fan 18 bytes: D1 D2 D3 100 D2def D3def T1 T2 T3 100 T2def T3def slope1..3 (u16 LE)
    public const int CurveStride = 18;
}

public enum PowerMode : byte { Quiet = 0, PowerSaving = 1, Performance = 2, Entertainment = 3, QuietPlus = 4 }

public enum FanMode : byte { Auto = 0, Custom = 6 }

public enum GpuMode : byte { Unknown = 0, IntegratedOnly = 1, DiscreteOnly = 2, MSHybrid = 3, Dynamic = 4 }

public readonly record struct FlexiCharger(bool Enabled, int StartPercent, int StopPercent);

public readonly record struct FanReading(int Index, int Rpm, int DutyPercent, int TemperatureC);

/// <summary>
/// Clevo 4-point fan curve: (T1,D1) (T2,D2) (T3,D3) (100 °C, 100 %). Duties are percent.
/// Only points 2 and 3 are sent to the firmware (_DSM 14); point 1 is fixed by firmware.
/// </summary>
public sealed record FanCurve(int Index, int T1, int D1, int T2, int D2, int T3, int D3)
{
    public const int T4 = 100, D4 = 100;

    /// <summary>Same ordering rules Clevo FnKey enforces before writing.</summary>
    public FanCurve Normalized()
    {
        int t2 = Math.Clamp(T2, T1 + 1, 98), t3 = Math.Clamp(T3, t2 + 1, 99);
        int d2 = Math.Clamp(D2, D1 + 1, 98), d3 = Math.Clamp(D3, d2 + 1, 99);
        return this with { T2 = t2, D2 = d2, T3 = t3, D3 = d3 };
    }

    /// <summary>Duty (percent) the curve gives at a temperature (linear interpolation).</summary>
    public double DutyAt(double t)
    {
        (int T, int D)[] p = [(T1, D1), (T2, D2), (T3, D3), (T4, D4)];
        if (t <= p[0].T) return p[0].D;
        for (int i = 1; i < p.Length; i++)
            if (t <= p[i].T) return p[i - 1].D + (p[i].D - p[i - 1].D) * (t - p[i - 1].T) / (double)(p[i].T - p[i - 1].T);
        return D4;
    }

    internal int Slope(int ta, int da, int tb, int db) => (int)Math.Round((double)(db - da) / (tb - ta) * 2.55 * 16.0);
}

public static class ClevoExtensions
{
    /// <summary>RPM conversion used by Clevo CC40: 60 / (5.565217e-5 * raw) * 2.</summary>
    public static int RawToRpm(int raw) => raw == 0 ? 0 : (int)Math.Round(120.0 / (5.565217391304348E-05 * raw));

    static int Pct(byte b) => (int)Math.Round(b / 255.0 * 100.0);
    static byte Raw(int pct) => (byte)Math.Round(pct / 100.0 * 255.0);

    /// <summary>Decodes _DSM 12. Layout from CC40: fan n speed big-endian at [2+2n], duty/temp triplets from 16.</summary>
    public static FanReading[] ReadFans(this AcpiBridge br)
    {
        var b = br.GetBuffer(DsmFn.HardwareMonitor);
        var fans = new List<FanReading>();
        for (int i = 0; i < 3; i++)
        {
            int raw = (b[2 + i * 2] << 8) | b[3 + i * 2];
            int duty = b[16 + i * 3];
            int temp = b[18 + i * 3];
            if (raw == 0 && duty == 0 && temp == 0) continue;
            fans.Add(new FanReading(i + 1, RawToRpm(raw), (int)Math.Round(duty / 2.55), temp));
        }
        return fans.ToArray();
    }

    /// <summary>Reads the active curves from _DSM 13 (fan count at [12], 8 bytes per fan from [16]).</summary>
    public static FanCurve[] ReadFanCurves(this AcpiBridge br)
    {
        var b = br.GetBuffer(DsmFn.FanCurveRead);
        int count = Math.Clamp((int)b[12], 0, 3);
        var list = new List<FanCurve>();
        for (int i = 0; i < count; i++)
        {
            int o = 16 + i * 8;
            if (b[o] == 0) continue;
            list.Add(new FanCurve(i + 1, b[o], Pct(b[o + 1]), b[o + 2], Pct(b[o + 3]), b[o + 4], Pct(b[o + 5])));
        }
        return list.ToArray();
    }

    /// <summary>
    /// Applies curves the way FnKey does: persist in AppSettings page 4, send _DSM 14, then switch fan mode to Custom.
    /// </summary>
    public static void ApplyFanCurves(this AcpiBridge br, IReadOnlyList<FanCurve> curves)
    {
        var current = br.ReadFanCurves();
        var store = br.ReadAppSettingsStore();
        var all = new FanCurve[3];
        foreach (var c in current) all[c.Index - 1] = c;
        foreach (var c in curves) all[c.Index - 1] = c.Normalized();

        var dsm = new byte[256];
        for (int i = 0; i < 3; i++)
        {
            var c = all[i];
            if (c == null) continue;
            int s1 = c.Slope(c.T1, c.D1, c.T2, c.D2), s2 = c.Slope(c.T2, c.D2, c.T3, c.D3), s3 = c.Slope(c.T3, c.D3, FanCurve.T4, FanCurve.D4);

            int o = 2 + i * 4;
            dsm[o] = (byte)c.T2; dsm[o + 1] = Raw(c.D2); dsm[o + 2] = (byte)c.T3; dsm[o + 3] = Raw(c.D3);
            int so = 14 + i * 6;
            foreach (var s in new[] { s1, s2, s3 }) { dsm[so++] = (byte)(s >> 8); dsm[so++] = (byte)s; }

            int a = AppAddr.CurveBase + i * AppAddr.CurveStride;
            var rec = new byte[AppAddr.CurveStride];
            store.AsSpan(a, rec.Length).CopyTo(rec);          // keeps the *_Default bytes as they are
            rec[0] = (byte)c.D1; rec[1] = (byte)c.D2; rec[2] = (byte)c.D3; rec[3] = 100;
            rec[6] = (byte)c.T1; rec[7] = (byte)c.T2; rec[8] = (byte)c.T3; rec[9] = 100;
            rec[12] = (byte)s1; rec[13] = (byte)(s1 >> 8); rec[14] = (byte)s2; rec[15] = (byte)(s2 >> 8); rec[16] = (byte)s3; rec[17] = (byte)(s3 >> 8);
            br.WriteAppSettings(a >> 8, a & 0xFF, rec);
        }
        br.Call(DsmFn.FanCurveWrite, dsm);
        br.SetFanMode(FanMode.Custom);
    }

    /// <summary>Equivalent of Clevo's SetWMI(0x79, sub, value): value in bytes 0..2, sub-command in byte 3. Returns the firmware's integer result.</summary>
    public static uint SetFeature(this AcpiBridge br, byte sub, uint value)
    {
        if (value > 0xFFFFFF) throw new ArgumentOutOfRangeException(nameof(value));
        var data = BitConverter.GetBytes(value);
        data[3] = sub;
        return br.Call(DsmFn.SetFeature, data).Integer ?? 0;
    }

    /// <summary>Flags FnKey adds in Performance mode, read from the same AppSettings bytes FnKey checks (0x120 → 0x80, 0x408 → 0x40).</summary>
    public static uint PerformanceFlags(this AcpiBridge br)
    {
        var s = br.ReadAppSettingsStore();
        uint v = 0;
        if (s[0x120] == 1) v |= 0x80;
        if (s[0x408] == 1) v |= 0x40;
        return v;
    }

    /// <summary>
    /// Sets the power mode the way FnKey does. Verified on hardware: the power-mode call alone resets the
    /// firmware fan control (fans went to 100% with a custom curve active), so the saved fan mode is re-sent after it.
    /// The new mode is also stored at AppSettings 0x101 so Clevo's own app stays in sync.
    /// </summary>
    public static void SetPowerMode(this AcpiBridge br, PowerMode mode, int? fanMode = null)
    {
        uint value = mode switch
        {
            PowerMode.Performance => 2u | br.PerformanceFlags(),
            PowerMode.QuietPlus => 0u,
            _ => (uint)mode,
        };
        br.SetFeature(FeatureSub.PowerMode, value);
        br.SetFeature(FeatureSub.FanOffset, OffsetRaw(br.GetSavedFanOffset()));   // FnKey re-sends the auto-mode increment on every mode change
        br.SetFeature(FeatureSub.FanMode, (uint)(fanMode ?? br.GetSavedFanMode()));
        br.WriteAppSettings(AppAddr.PowerMode >> 8, AppAddr.PowerMode & 0xFF, [(byte)mode]);
    }

    /// <summary>Switches firmware fan control (auto or the custom curve) and stores it like FnKey (AppSettings 0x405).</summary>
    public static void SetFanMode(this AcpiBridge br, FanMode mode)
    {
        br.SetFeature(FeatureSub.FanMode, (uint)mode);
        br.WriteAppSettings(AppAddr.FanMode >> 8, AppAddr.FanMode & 0xFF, [(byte)mode]);
    }

    /// <summary>Battery charge limiter (FlexiCharger), _DSM 4 sub 30: [0] enabled, [1] start %, [2] stop %.</summary>
    public static FlexiCharger ReadFlexiCharger(this AcpiBridge br)
    {
        var b = br.Call(DsmFn.FanTable, [30]).Buffer ?? new byte[3];
        return new FlexiCharger(b[0] == 1, b[1], b[2]);
    }

    /// <summary>_DSM 4 sub 31 {enabled, start, stop}: same call FnKey/CC40 make (they use 70/100 on, 95/100 off).</summary>
    public static void SetFlexiCharger(this AcpiBridge br, FlexiCharger f)
    {
        int stop = Math.Clamp(f.StopPercent, 50, 100);
        int start = Math.Clamp(f.StartPercent, 40, stop - 5);
        br.Call(DsmFn.FanTable, [31, (byte)(f.Enabled ? 1 : 0), (byte)start, (byte)stop]);
    }

    /// <summary>GPU switch state, _DSM 4 sub 21: [0] mode, [1] support bits (read-only here; switching needs a reboot).</summary>
    public static (GpuMode Mode, byte SupportBits) ReadGpuMode(this AcpiBridge br)
    {
        var b = br.Call(DsmFn.FanTable, [21]).Buffer ?? new byte[2];
        return ((GpuMode)b[0], b[1]);
    }

    public static PowerMode GetSavedPowerMode(this AcpiBridge br) => (PowerMode)br.ReadAppSettings(1, 1, 1)[0];
    public static int GetSavedFanMode(this AcpiBridge br) => br.ReadAppSettings(4, 5, 1)[0];
    public static int GetSavedFanOffset(this AcpiBridge br) => Math.Clamp((int)br.ReadAppSettings(4, 7, 1)[0], 0, 100);

    static uint OffsetRaw(int percent) => (uint)Math.Round(255.0 * Math.Clamp(percent, 0, 100) / 100.0);

    /// <summary>
    /// The "Increment" slider Clevo shows next to Auto fan mode (0–100 % in steps of 10): extra fan duty added on top of
    /// what the firmware's automatic control chooses. _DSM 0x79 sub 14 = round(255 × %), and the percent is stored at
    /// AppSettings 0x407 (CC40 and FnKey both do exactly this).
    /// </summary>
    public static void SetFanOffset(this AcpiBridge br, int percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        br.SetFeature(FeatureSub.FanOffset, OffsetRaw(percent));
        br.WriteAppSettings(AppAddr.FanOffset >> 8, AppAddr.FanOffset & 0xFF, [(byte)percent]);
    }
}

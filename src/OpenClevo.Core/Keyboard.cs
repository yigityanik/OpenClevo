namespace OpenClevo.Core;

/// <summary>Effects of Clevo's EC keyboard backlight. Values match FnKey and the Linux clevo-xsm-wmi driver.</summary>
public enum KbEffect
{
    Static,      // per-zone colors (Clevo mode 1)
    Breathe,     // 0x1002A000 (mode 2)
    Cycle,       // 0x33010000 (mode 3)
    Wave,        // 0xB0000000 (mode 4)
    Dance,       // 0x80000000 (mode 5)
    Tempo,       // 0x90000000 (mode 6)
    Flash,       // 0xA0000000 (mode 7)
    Random,      // 0x70000000 (mode 0)
}

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
}

/// <summary>How Clevo's own app treats a keyboard type: White = brightness only, Zoned = 3-zone RGB with firmware effects,
/// AllColour = one colour for the whole keyboard (no firmware effects), PerKey = USB per-key controller (not supported yet).</summary>
public enum KeyboardKind { None, White, Zoned, AllColour, PerKey, Other }

/// <summary>Extra lighting the firmware reports (Clevo's fn 122 bits): logo, lightbar, and boot-effect override (fn 13 byte 43).</summary>
public readonly record struct KeyboardCaps(bool Logo, bool Lightbar, bool BootEffect);

/// <summary>
/// Keyboard backlight state. The type and capabilities come from the firmware (so they are right even if Clevo's app never ran);
/// colours and mode come from Clevo's store (AppSettings page 2), or defaults when that store was never filled (<see cref="Fresh"/>).
/// </summary>
public sealed record KeyboardState(int Type, int ClevoMode, Rgb Left, Rgb Middle, Rgb Right, KeyboardCaps? Caps = null, bool Fresh = false)
{
    public KeyboardKind Kind => KeyboardExtensions.KindOf(Type);

    public KbEffect Effect => ClevoMode switch
    {
        2 => KbEffect.Breathe, 3 => KbEffect.Cycle, 4 => KbEffect.Wave, 5 => KbEffect.Dance,
        6 => KbEffect.Tempo, 7 => KbEffect.Flash, 0 => KbEffect.Random, _ => KbEffect.Static,
    };
}

/// <summary>
/// EC keyboard backlight (Clevo KB types 1/2/6/22/38) via _DSM 103 (0x67):
///   sub 0xF0/0xF1/0xF2 = left/middle/right zone color, value bytes G,R,B (type 6/22: each component halved);
///   sub 0xF4 = brightness value (levels 1–4 → 47/95/143/191, 0 = off);
///   no sub, full 32-bit value = effect (breathe, cycle, wave, …).
/// AppSettings: 0x204 type, 0x220 mode, 0x250 + 4*i = {on, R, G, B} for all/left/mid/right/touchpad/logo/lightbar.
/// </summary>
public static class KeyboardExtensions
{
    public const int KbFn = 103;
    const int TypeAddr = 0x204, ModeAddr = 0x220, ColorBase = 0x250;
    static readonly byte[] ZoneSub = [0xF0, 0xF1, 0xF2];
    static readonly int[] BrightnessValue = [0, 47, 95, 143, 191];

    /// <summary>Clevo's CC 4.0 keyboard module: type 1 white, 2 (and 38 in FnKey) zoned RGB, 6 and 22 one-colour RGB, 3 / 19 / 35 / 51 / 0xF3 per-key USB.</summary>
    public static KeyboardKind KindOf(int type) => type switch
    {
        0 => KeyboardKind.None,
        1 => KeyboardKind.White,
        2 or 38 => KeyboardKind.Zoned,
        6 or 22 => KeyboardKind.AllColour,
        3 => KeyboardKind.PerKey,
        _ => KeyboardKind.Other,
    };

    /// <summary>Types we can drive through the EC path (others use a USB HID controller).</summary>
    public static bool IsSupportedType(int type) => KindOf(type) is KeyboardKind.White or KeyboardKind.Zoned or KeyboardKind.AllColour;

    /// <summary>
    /// Reads the keyboard type and capabilities from the firmware the way Clevo's CC 4.0 does: fn 13 byte 15 is the type
    /// (3 / 19 / 35 / 51 / 0xF3 all mean per-key), byte 43 bit 0 boot-effect override; fn 122 bit 18 logo, bit 12 lightbar.
    /// </summary>
    static (int Type, KeyboardCaps Caps) ReadCapabilities(AcpiBridge br)
    {
        int type = 0; bool boot = false;
        try
        {
            var b = br.Call(13).Buffer;
            if (b is not { Length: > 43 } || b[15] == 0)
            {
                br.Call(70, BitConverter.GetBytes(90u));   // CC selects the page first on some models
                b = br.Call(13).Buffer;
            }
            if (b is { Length: > 43 }) { type = b[15]; boot = (b[43] & 1) == 1; }
        }
        catch { }
        if (type is 19 or 35 or 51 or 0xF3) type = 3;
        uint f122 = 0;
        try { f122 = br.Call(122).Integer ?? 0; } catch { }
        return (type, new KeyboardCaps(((f122 >> 18) & 1) == 1, ((f122 >> 12) & 1) == 1, boot));
    }

    public static KeyboardState ReadKeyboard(this AcpiBridge br)
    {
        var s = br.ReadAppSettingsStore();
        var (fwType, caps) = ReadCapabilities(br);
        int type = fwType > 0 ? fwType : s[TypeAddr];
        // testing aid: OPENCLEVO_KBTYPE=1|3|22|99 shows how the page looks for another keyboard type (nothing is sent differently)
        if (int.TryParse(Environment.GetEnvironmentVariable("OPENCLEVO_KBTYPE"), out int forced)) type = forced is 19 or 35 or 51 or 0xF3 ? 3 : forced;
        // Clevo's store only has keyboard settings after Clevo's own app ran; on a clean system start from white, static
        if (s[TypeAddr] == 0) return new KeyboardState(type, 1, new Rgb(255, 255, 255), new Rgb(255, 255, 255), new Rgb(255, 255, 255), caps, Fresh: true);
        Rgb C(int i) { int o = ColorBase + i * 4; return new Rgb(s[o + 1], s[o + 2], s[o + 3]); }
        return new KeyboardState(type, s[ModeAddr], C(1), C(2), C(3), caps);
    }

    /// <summary>Logo (0xF6) and lightbar (0xF3) follow the main colour on laptops that have them, as Clevo's "all colour" does.</summary>
    static void SendExtraZones(AcpiBridge br, int type, KeyboardCaps? caps, Rgb c)
    {
        if (caps is not { } k) return;
        var v = Encode(c, type);
        if (k.Logo) Send(br, 0xF6, v);
        if (k.Lightbar) Send(br, 0xF3, v);
    }

    static void Send(AcpiBridge br, byte sub, uint value)
    {
        var d = BitConverter.GetBytes(value & 0xFFFFFF);
        d[3] = sub;
        br.Call(KbFn, d);
    }

    static void SendRaw(AcpiBridge br, uint value) => br.Call(KbFn, BitConverter.GetBytes(value));

    static uint Encode(Rgb c, int type)
    {
        if (type is 6 or 22)
        {
            int b = c.B;
            if (c.R == 0 && ((c.G == 254 && c.B == 129) || (c.G == 255 && c.B == 127))) b = 70;   // Clevo's own calibration case
            return (uint)(((b / 2) << 16) | ((c.R / 2) << 8) | (c.G / 2));
        }
        return (uint)(((byte)Math.Round(200.0 / 255.0 * c.B) << 16) | (c.R << 8) | c.G);
    }

    /// <summary>Sends zone colors only (no store write). Used by software effects; null zones are skipped.</summary>
    public static void SetKeyboardColors(this AcpiBridge br, int type, Rgb? left, Rgb? middle, Rgb? right, KeyboardCaps? caps = null)
    {
        Rgb?[] z = [left, middle, right];
        for (int i = 0; i < 3; i++)
            if (z[i] is { } c) Send(br, ZoneSub[i], Encode(c, type));
        if ((left ?? middle ?? right) is { } main) SendExtraZones(br, type, caps, main);
    }

    /// <summary>Brightness level 0 (off) to 4, same mapping as FnKey.</summary>
    public static void SetKeyboardBrightness(this AcpiBridge br, int level) =>
        Send(br, 0xF4, (uint)BrightnessValue[Math.Clamp(level, 0, 4)]);

    /// <summary>Sets the three zone colors and the effect, then stores them where Clevo's app reads them.</summary>
    public static void ApplyKeyboard(this AcpiBridge br, KbEffect effect, Rgb left, Rgb middle, Rgb right)
    {
        var state = br.ReadKeyboard();
        Rgb[] zones = [left, middle, right];
        bool needsColors = effect is KbEffect.Static or KbEffect.Breathe;
        if (needsColors)
        {
            for (int i = 0; i < 3; i++) Send(br, ZoneSub[i], Encode(zones[i], state.Type));
            SendExtraZones(br, state.Type, state.Caps, zones[0]);
        }

        int mode;
        switch (effect)
        {
            case KbEffect.Static: mode = 1; break;
            case KbEffect.Breathe: mode = 2; SendRaw(br, 0x1002A000); break;
            case KbEffect.Cycle: mode = 3; SendRaw(br, 0x33010000); break;
            case KbEffect.Wave: mode = 4; SendRaw(br, 0xB0000000); break;
            case KbEffect.Dance: mode = 5; SendRaw(br, 0x80000000); break;
            case KbEffect.Tempo: mode = 6; SendRaw(br, 0x90000000); break;
            case KbEffect.Flash: mode = 7; SendRaw(br, 0xA0000000); break;
            default: mode = 0; SendRaw(br, 0x70000000); break;
        }

        // keep Clevo's store in sync: mode + left/middle/right color records (touchpad/logo/lightbar untouched)
        br.WriteAppSettings(ModeAddr >> 8, ModeAddr & 0xFF, [(byte)mode]);
        var rec = new byte[12];
        for (int i = 0; i < 3; i++) { rec[i * 4] = 1; rec[i * 4 + 1] = zones[i].R; rec[i * 4 + 2] = zones[i].G; rec[i * 4 + 3] = zones[i].B; }
        int a = ColorBase + 4;
        br.WriteAppSettings(a >> 8, a & 0xFF, rec);
    }
}

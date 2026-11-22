using System.Diagnostics;
using Microsoft.Win32;
using OpenClevo.Core;
using OpenClevo.Localization;

namespace OpenClevo.App;

/// <summary>What a step did: whether the command was accepted, what was read back, and whether the readback matched.</summary>
sealed record StepOutcome(bool CommandOk, string Detail, string? Error = null, string? Readback = null, bool? ReadbackMatches = null);

/// <summary>Shared by every step of one wizard run.</summary>
sealed class StepContext
{
    public required HardwareService Hw { get; init; }
    public required KeyboardEffectEngine Kb { get; init; }
    public Action<string> Status { get; set; } = _ => { };
    /// <summary>Cancelled when the wizard window is closed while a step runs, so long waits end at once and the step can be undone.</summary>
    public CancellationToken Ct { get; set; }
    public Dictionary<string, object?> Bag { get; } = new();
}

/// <summary>
/// One test the wizard can run. <see cref="Run"/> does the action, <see cref="Undo"/> puts everything back (always called after
/// the answer), <see cref="Question"/> is what the user is asked while the effect is visible (null = nothing to ask).
/// <see cref="ExpectYes"/> lets the app say what the honest answer should be when it can check that itself.
/// </summary>
sealed class DiagStep
{
    public required string Id { get; init; }
    public required string Text { get; init; }          // what the app is about to do (shown before running)
    public string? Question { get; init; }
    public required Func<StepContext, Task<StepOutcome>> Run { get; init; }
    public Func<StepContext, Task>? Undo { get; init; }
    public Func<StepOutcome, bool?>? ExpectYes { get; init; }
    public string? Warning { get; init; }               // shown before running (write tests that may be noticed)
}

sealed class DiagScenario
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Hint { get; init; }
    public Func<StepContext, Task>? Setup { get; init; }
    public Func<StepContext, Task>? Teardown { get; init; }
    public required Func<StepContext, List<DiagStep>> Steps { get; init; }
}

/// <summary>What the report records for one step.</summary>
sealed record StepRecord(string Scenario, string Step, string Action, string? Question, bool CommandOk, string Detail, string? Error,
    string? Readback, bool? ReadbackMatches, string Answer, string Consistency, bool? RestoredOk, long DurationMs);

static class DiagCatalog
{
    /// <summary>Text in the user's language when the language file has it, otherwise the English default written here.</summary>
    public static string T(string key, string english) { var s = Loc.T(key); return s == key ? english : s; }

    static readonly Rgb Red = new(255, 0, 0), White = new(255, 255, 255);

    public static string Consistency(StepOutcome o, string answer, bool hasQuestion, bool? expectYes)
    {
        if (answer == "skipped") return "skipped";
        bool yes = answer == "yes";
        if (!o.CommandOk) return hasQuestion && yes ? "inconsistent: the command failed but the user saw the effect" : "failed: the command was refused or errored";
        if (o.ReadbackMatches == false) return hasQuestion && yes ? "inconsistent: the user saw the effect but the readback differs" : "failed: the readback does not match what was set";
        if (expectYes is bool e && hasQuestion) return yes == e ? "confirmed: the answer agrees with the app's own check" : "inconsistent: the answer contradicts the app's own check";
        if (!hasQuestion) return o.ReadbackMatches == true ? "confirmed: the readback matches" : "observed: the step ran and was recorded (nothing to confirm)";
        if (yes) return o.ReadbackMatches == true ? "confirmed: the user saw the effect and the readback matches" : "user-observed: the user confirmed it (no automatic check is possible)";
        return o.ReadbackMatches == true ? "inconsistent: the readback matches but the user saw no effect" : "no effect: the command was accepted but the user saw nothing";
    }

    public static List<DiagScenario> All() =>
    [
        Keyboard(), Fan(), Power(), Charger(), Camera(), Touchpad(), Nvidia(), FnKeys(), Startup(),
    ];

    // ------------------------------------------------------------------ keyboard

    static DiagScenario Keyboard() => new()
    {
        Id = "keyboard",
        Title = T("diag.sc.keyboard", "Keyboard backlight"),
        Hint = T("diag.sc.keyboard_hint", "colours, brightness, effects"),
        Setup = ctx => { ctx.Bag["kbPrev"] = ctx.Kb.Current; ctx.Kb.Stop(false); return Task.CompletedTask; },
        Teardown = ctx =>
        {
            ctx.Kb.Stop(false);
            var prev = ctx.Bag.GetValueOrDefault("kbPrev") as SoftEffect? ?? SoftEffect.None;
            var k = ctx.Hw.Keyboard;
            if (prev != SoftEffect.None && k != null) ctx.Kb.Start(prev, [k.Left, k.Middle, k.Right]);
            else KeyboardEffectEngine.Saved = SoftEffect.None;
            return Task.CompletedTask;
        },
        Steps = ctx =>
        {
            var steps = new List<DiagStep>();
            var k = ctx.Hw.Keyboard;
            if (k == null || !KeyboardExtensions.IsSupportedType(k.Type))
            {
                steps.Add(new DiagStep
                {
                    Id = "keyboard-not-testable",
                    Text = T("diag.kb.none", $"This keyboard cannot be tested by OpenClevo (type {k?.Type.ToString() ?? "unknown"}, {k?.Kind.ToString() ?? "no data"}). The type is recorded in the report."),
                    Run = _ => Task.FromResult(new StepOutcome(true, $"keyboard type {k?.Type}, kind {k?.Kind}: not testable")),
                });
                return steps;
            }
            Rgb[] original = [k.Left, k.Middle, k.Right];
            async Task RestoreColours(StepContext c) { await Task.Run(() => { using var br = AcpiBridge.Open(); br.ApplyKeyboard(KbEffect.Static, original[0], original[1], original[2]); }); }

            steps.Add(new DiagStep
            {
                Id = "kb-brightness",
                Text = T("diag.kb.brightness", "I will set the keyboard brightness to level 3."),
                Warning = T("diag.kb.brightness_warn", "Afterwards the brightness goes back to the level OpenClevo remembers. If it differs from yours, set it again."),
                Question = T("diag.kb.brightness_q", "Is the keyboard backlight on now?"),
                Run = async c => await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetKeyboardBrightness(3); return new StepOutcome(true, "brightness level 3 sent"); }),
                Undo = async c => { await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetKeyboardBrightness(KeyboardIdle.SavedBrightness); }); },
            });
            if (k.Kind != KeyboardKind.White)
            {
                steps.Add(new DiagStep
                {
                    Id = "kb-red",
                    Text = T("diag.kb.red", "I will turn the whole keyboard solid red."),
                    Question = T("diag.kb.red_q", "Is the keyboard red now?"),
                    Run = async c => await Task.Run(() => { using var br = AcpiBridge.Open(); br.ApplyKeyboard(KbEffect.Static, Red, Red, Red); return new StepOutcome(true, "static red sent to all zones"); }),
                    Undo = RestoreColours,
                });
                if (k.Kind == KeyboardKind.Zoned)
                    steps.Add(new DiagStep
                    {
                        Id = "kb-firmware-breathe",
                        Text = T("diag.kb.fw", "I will start the keyboard's own breathing effect (built into the firmware)."),
                        Question = T("diag.kb.fw_q", "Is the keyboard fading in and out slowly?"),
                        Run = async c => await Task.Run(() => { using var br = AcpiBridge.Open(); br.ApplyKeyboard(KbEffect.Breathe, Red, Red, Red); return new StepOutcome(true, "firmware breathe sent"); }),
                        Undo = RestoreColours,
                    });
                steps.Add(new DiagStep
                {
                    Id = "kb-software-breathe",
                    Text = T("diag.kb.sw", "I will run OpenClevo's own breathing effect for a few seconds."),
                    Question = T("diag.kb.sw_q", "Is the keyboard fading in and out?"),
                    Run = c => { c.Kb.Start(SoftEffect.Breathe, [Red, Red, Red]); return Task.FromResult(new StepOutcome(true, "software breathe started")); },
                    Undo = async c => { c.Kb.Stop(false); await RestoreColours(c); },
                });
                steps.Add(new DiagStep
                {
                    Id = "kb-sound",
                    Text = T("diag.kb.sound", "Start some music first. I will run the sound-reactive effect for a few seconds."),
                    Question = T("diag.kb.sound_q", "Does the keyboard light follow the music?"),
                    Run = c => { c.Kb.Start(SoftEffect.Audio, [Red, Red, Red]); return Task.FromResult(new StepOutcome(true, "sound-reactive effect started")); },
                    Undo = async c => { c.Kb.Stop(false); await RestoreColours(c); },
                });
            }
            return steps;
        },
    };

    // ------------------------------------------------------------------ fan, power, charger

    static DiagScenario Fan() => new()
    {
        Id = "fan", Title = T("diag.sc.fan", "Fans and temperature"), Hint = T("diag.sc.fan_hint", "readings, auto and custom mode"),
        Steps = ctx =>
        [
            new DiagStep
            {
                Id = "fan-readings",
                Text = T("diag.fan.read", "I will read the fan speeds and temperatures."),
                Question = T("diag.fan.read_q", "Are the fans spinning and do these numbers look believable?"),
                Run = async c => await Task.Run(() =>
                {
                    using var br = AcpiBridge.Open();
                    var f = br.ReadFans();
                    return f.Length == 0 ? new StepOutcome(false, "the firmware reports no fans")
                        : new StepOutcome(true, string.Join("; ", f.Select(x => $"fan {x.Index}: {x.Rpm} rpm, {x.DutyPercent}%, {x.TemperatureC} C")));
                }),
                ExpectYes = o => o.CommandOk ? null : false,
            },
            new DiagStep
            {
                Id = "fan-mode",
                Text = T("diag.fan.mode", "I will switch the fans to automatic control and then back to how they were."),
                Run = async c => await Task.Run(() =>
                {
                    using var br = AcpiBridge.Open();
                    c.Bag["fanPrev"] = br.GetSavedFanMode();
                    br.SetFanMode(FanMode.Auto);
                    int now = br.GetSavedFanMode();
                    return new StepOutcome(true, "fan mode set to automatic", Readback: "saved mode " + now, ReadbackMatches: now == 0);
                }),
                Undo = async c => { await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetFanMode((FanMode)(c.Bag["fanPrev"] as int? ?? 0)); }); },
            },
        ],
    };

    static DiagScenario Power() => new()
    {
        Id = "power", Title = T("diag.sc.power", "Power mode"), Hint = T("diag.sc.power_hint", "quiet, entertainment, performance"),
        Steps = ctx =>
        {
            DiagStep Mode(PowerMode m) => new()
            {
                Id = "power-" + m,
                Text = T("diag.power.set", "I will switch the power mode to ") + m + T("diag.power.set2", " and then back to how it was."),
                Warning = T("diag.power.warn", "The fans may change speed for a moment."),
                Run = async c => await Task.Run(() =>
                {
                    using var br = AcpiBridge.Open();
                    c.Bag["powerPrev"] = br.GetSavedPowerMode();
                    br.SetPowerMode(m);
                    var now = br.GetSavedPowerMode();
                    return new StepOutcome(true, $"power mode {m} sent", Readback: "saved mode " + now, ReadbackMatches: now == m);
                }),
                Undo = async c => { await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetPowerMode((PowerMode)(c.Bag["powerPrev"] as PowerMode? ?? PowerMode.Performance)); }); },
            };
            return [Mode(PowerMode.Quiet), Mode(PowerMode.Entertainment), Mode(PowerMode.PowerSaving)];
        },
    };

    static DiagScenario Charger() => new()
    {
        Id = "charger", Title = T("diag.sc.charger", "Battery charge limit"), Hint = T("diag.sc.charger_hint", "FlexiCharger"),
        Steps = ctx =>
        [
            new DiagStep
            {
                Id = "charger-limit",
                Text = T("diag.charger.set", "I will set the charge limit to 60 to 80 percent, read it back, and then restore your setting."),
                Run = async c => await Task.Run(() =>
                {
                    using var br = AcpiBridge.Open();
                    c.Bag["chargerPrev"] = br.ReadFlexiCharger();
                    br.SetFlexiCharger(new FlexiCharger(true, 60, 80));
                    var now = br.ReadFlexiCharger();
                    return new StepOutcome(true, "charge limit 60-80 sent", Readback: $"enabled={now.Enabled}, {now.StartPercent}-{now.StopPercent}", ReadbackMatches: now.Enabled && now.StartPercent == 60 && now.StopPercent == 80);
                }),
                Undo = async c => { await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetFlexiCharger((FlexiCharger)(c.Bag["chargerPrev"] ?? new FlexiCharger(false, 95, 100))); }); },
            },
        ],
    };

    // ------------------------------------------------------------------ camera, touchpad, GPU

    static DiagScenario Camera() => new()
    {
        Id = "camera", Title = T("diag.sc.camera", "Camera"), Hint = T("diag.sc.camera_hint", "firmware power switch"),
        Steps = ctx =>
        [
            new DiagStep
            {
                Id = "camera-off",
                Text = T("diag.camera.off", "I will switch the camera off in the firmware (like the Fn camera key) and then back on."),
                Question = T("diag.camera.off_q", "Is the camera gone? (Open the Windows Camera app: it should show no camera.)"),
                Run = async c => await Task.Run(() =>
                {
                    using var br = AcpiBridge.Open();
                    var prev = br.ReadCameraPower();
                    if (prev == null) return new StepOutcome(false, "the firmware does not report a camera switch");
                    c.Bag["cameraPrev"] = prev;
                    br.SetCameraPower(false);
                    var now = br.ReadCameraPower();
                    return new StepOutcome(true, "camera power off sent", Readback: "camera power " + now, ReadbackMatches: now == false);
                }),
                Undo = async c => { bool prev = c.Bag["cameraPrev"] as bool? ?? true; await Task.Run(() => { using var br = AcpiBridge.Open(); br.SetCameraPower(prev); }); c.Hw.NoteCameraPower(prev); },
            },
        ],
    };

    static DiagScenario Touchpad() => new()
    {
        Id = "touchpad", Title = T("diag.sc.touchpad", "Touchpad"), Hint = T("diag.sc.touchpad_hint", "Windows touchpad switch"),
        Steps = ctx =>
        [
            new DiagStep
            {
                Id = "touchpad-toggle",
                Text = T("diag.touchpad.toggle", "I will switch the touchpad to the opposite state and then back."),
                Warning = T("diag.touchpad.warn", "If the touchpad turns off, answer with the keyboard (Tab and Enter) or a mouse. It is switched back automatically."),
                Question = T("diag.touchpad.q", "Did the touchpad change (stop or start responding)?"),
                Run = c =>
                {
                    if (!SystemToggles.TouchpadSupported) return Task.FromResult(new StepOutcome(false, "no precision-touchpad setting in Windows"));
                    bool prev = SystemToggles.Touchpad;
                    c.Bag["touchpadPrev"] = prev;
                    SystemToggles.Touchpad = !prev;
                    bool now = SystemToggles.Touchpad;
                    return Task.FromResult(new StepOutcome(true, $"touchpad switched to {(!prev ? "on" : "off")}", Readback: "touchpad " + (now ? "on" : "off"), ReadbackMatches: now == !prev));
                },
                Undo = c => { SystemToggles.Touchpad = c.Bag["touchpadPrev"] as bool? ?? true; return Task.CompletedTask; },
            },
        ],
    };

    static DiagScenario Nvidia() => new()
    {
        Id = "nvidia", Title = T("diag.sc.nvidia", "NVIDIA GPU"), Hint = T("diag.sc.nvidia_hint", "status (nothing is switched)"),
        Steps = ctx =>
        [
            new DiagStep
            {
                Id = "nvidia-status",
                Text = T("diag.nvidia.read", "I will read whether the NVIDIA GPU is on. Nothing is switched."),
                Question = T("diag.nvidia.q", "Is the NVIDIA GPU listed in Task Manager under Performance?"),
                Run = c =>
                {
                    bool? on = NvidiaPower.IsOn;
                    return Task.FromResult(on == null ? new StepOutcome(false, "no NVIDIA platform controller on this laptop") : new StepOutcome(true, "the app sees the GPU as " + (on.Value ? "on" : "off")));
                },
                ExpectYes = o => o.CommandOk && o.Detail.EndsWith("on") ? true : o.CommandOk ? false : null,
            },
        ],
    };

    // ------------------------------------------------------------------ Fn keys and lock keys (you press, the app listens)

    static DiagScenario FnKeys() => new()
    {
        Id = "fn", Title = T("diag.sc.fn", "Fn keys and notices"), Hint = T("diag.sc.fn_hint", "you press a key, I listen"),
        Steps = ctx =>
        [
            // the key really toggles the camera / touchpad, so the state before is remembered and put back afterwards
            WaitStep("fn-camera", T("diag.fn.camera", "Press the camera Fn key now (you have 20 seconds)."), [FnEvents.CameraOff, FnEvents.CameraOn],
                snapshot: async c => { c.Bag["fnCamPrev"] = await Task.Run(() => { using var br = AcpiBridge.Open(); return br.ReadCameraPower(); }); },
                restore: async c =>
                {
                    if (c.Bag["fnCamPrev"] is not bool prev) return;
                    await Task.Run(() => { using var br = AcpiBridge.Open(); if (br.ReadCameraPower() != prev) br.SetCameraPower(prev); });
                    c.Hw.NoteCameraPower(prev);
                }),
            WaitStep("fn-touchpad", T("diag.fn.touchpad", "Press the touchpad Fn key now (you have 20 seconds)."), [FnEvents.Touchpad],
                snapshot: c => { c.Bag["fnTpPrev"] = SystemToggles.TouchpadSupported ? SystemToggles.Touchpad : null; return Task.CompletedTask; },
                restore: async c =>
                {
                    await Task.Delay(400);   // Windows applies the key a moment later
                    if (c.Bag["fnTpPrev"] is bool prev && SystemToggles.Touchpad != prev) SystemToggles.Touchpad = prev;
                }),
            // codes seen on a G5 KF for the keyboard-light keys: 0x81, 0x9f, 0x83, 0xfa, 0xfb (which key sends which is not settled yet)
            WaitStep("fn-kblight", T("diag.fn.kblight", "Press a keyboard-light Fn key now (you have 20 seconds)."), [0x81, 0x82, 0x83, 0x9F, 0xFA, 0xFB]),
            new DiagStep
            {
                Id = "fn-numlock",
                Text = T("diag.fn.numlock", "Press Num Lock now (you have 20 seconds). The on-screen notice should appear."),
                Question = T("diag.fn.numlock_q", "Did an on-screen notice appear?"),
                Run = async c =>
                {
                    bool start = SystemToggles.NumLock;
                    for (int i = 20; i > 0 && !c.Ct.IsCancellationRequested; i--) { c.Status($"{i} s"); if (SystemToggles.NumLock != start) break; try { await Task.Delay(1000, c.Ct); } catch (OperationCanceledException) { break; } }
                    bool changed = SystemToggles.NumLock != start;
                    if (changed) SystemToggles.NumLock = start;   // put the key back
                    return new StepOutcome(changed, changed ? "Num Lock changed state (restored)" : "no Num Lock change within 20 s");
                },
                ExpectYes = o => o.CommandOk ? (OsdHost.Enabled ? true : false) : null,
            },
        ],
    };

    /// <summary>
    /// Waits up to 20 s for the user to press a key and records every Fn event code that arrives. Any event proves the channel works;
    /// whether the code is the one the app expects is noted separately (a new code is data, not a failure).
    /// </summary>
    static DiagStep WaitStep(string id, string text, long[] wanted, Func<StepContext, Task>? snapshot = null, Func<StepContext, Task>? restore = null) => new()
    {
        Id = id,
        Text = text,
        Undo = restore,
        Run = async c =>
        {
            if (snapshot != null) { try { await snapshot(c); } catch { } }
            var got = new List<long>();
            using var fn = new FnEvents();
            fn.Received += code => { lock (got) got.Add(code); };
            await Task.Delay(300);
            if (!fn.Registered) return new StepOutcome(false, "the Fn event channel is not available", Error: fn.Error);
            for (int i = 20; i > 0; i--)
            {
                c.Status($"{i} s");
                bool any; lock (got) any = got.Count > 0;
                if (any) { await Task.Delay(1200); break; }   // let a repeated press finish
                if (c.Ct.IsCancellationRequested) break;
                try { await Task.Delay(1000, c.Ct); } catch (OperationCanceledException) { break; }
            }
            long[] seen; lock (got) seen = got.ToArray();
            if (seen.Length == 0) return new StepOutcome(false, "no event within 20 s", Readback: "no events");
            bool known = seen.Any(g => wanted.Contains(g));
            return new StepOutcome(true, known ? "event received (a code the app knows)" : "event received, but with a code the app does not list for this key: new information for the report",
                Readback: "events: " + string.Join(", ", seen.Select(s => "0x" + s.ToString("x2"))));
        },
    };

    // ------------------------------------------------------------------ start with Windows, tray

    static DiagScenario Startup() => new()
    {
        Id = "startup", Title = T("diag.sc.startup", "Startup and tray"), Hint = T("diag.sc.startup_hint", "start with Windows, tray icon"),
        Steps = ctx =>
        [
            new DiagStep
            {
                Id = "startup-entry",
                Text = T("diag.startup.read", "I will check whether OpenClevo is set to start with Windows, and whether Windows lets it."),
                Question = T("diag.startup.q", "After you restart Windows, does the OpenClevo tray icon appear by itself? (It starts hidden in the tray.)"),
                Run = c =>
                {
                    bool entry = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("OpenClevo") != null;
                    // Task Manager > Startup apps keeps its own on/off switch; first byte odd (03, 07) = switched off there
                    var approved = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")?.GetValue("OpenClevo") as byte[];
                    bool off = approved is { Length: > 0 } && (approved[0] & 1) == 1;
                    return Task.FromResult(new StepOutcome(true, $"start-with-Windows entry {(entry ? "present" : "not present")}; Windows startup switch {(approved == null ? "not set" : off ? "SWITCHED OFF in Task Manager > Startup apps" : "on")}"));
                },
                ExpectYes = o => o.Detail.Contains("entry present") && !o.Detail.Contains("SWITCHED OFF"),
            },
        ],
    };
}

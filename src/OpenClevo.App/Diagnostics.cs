using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenClevo.Core;
using Node = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace OpenClevo.App;

/// <summary>
/// The compatibility report: one read-only pass over everything OpenClevo depends on (the laptop, Windows, the Acpi Bridge
/// driver, the firmware words behind every feature, the other Clevo software on the machine) with a verdict per feature.
/// It never writes to the hardware: only reads, and a passive listener registration for the Fn-key channel.
/// The result is written as a readable text file and a JSON file (same data), zipped into Documents. No user name,
/// computer name or serial number is included.
/// </summary>
static partial class Diagnostics
{
    const string Schema = "openclevo-diagnostics/1";

    static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // ------------------------------------------------------------------ entry point

    /// <summary>Runs every check and saves the zip in Documents. Returns the path of the zip.</summary>
    public static string CreateReport(string appVersion, WizardResult? wiz = null)
    {
        var files = new List<(string Name, byte[] Data)>();
        var root = Collect(appVersion, files, wiz);
        string json = Redact(JsonSerializer.Serialize(root, JsonOpts));
        string text = Redact(RenderText(root));

        string dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string name = $"OpenClevo-Diagnostics-{DateTime.Now:yyyy-MM-dd}";
        string path = Path.Combine(dir, name + ".zip");
        if (File.Exists(path)) path = Path.Combine(dir, $"{name}-{DateTime.Now:HHmmss}.zip");

        using (var fs = new FileStream(path, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddEntry(zip, "OpenClevo-Diagnostics.txt", text);
            AddEntry(zip, "OpenClevo-Diagnostics.json", json);
            foreach (var (fname, data) in files)
            {
                var e = zip.CreateEntry(fname, CompressionLevel.Optimal);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return path;
    }

    static void AddEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    // ------------------------------------------------------------------ collecting

    static bool NeedsALook(string consistency) => consistency.StartsWith("failed") || consistency.StartsWith("inconsistent") || consistency.StartsWith("no effect");

    static Node ScenarioNode(WizardResult w)
    {
        var tests = w.Steps.Select(r => (object?)new Node
        {
            ["scenario"] = r.Scenario, ["step"] = r.Step, ["action"] = r.Action, ["question"] = r.Question,
            ["commandOk"] = r.CommandOk, ["detail"] = r.Detail, ["error"] = r.Error, ["readback"] = r.Readback, ["readbackMatches"] = r.ReadbackMatches,
            ["userAnswer"] = r.Answer, ["consistency"] = r.Consistency, ["restoredOk"] = r.RestoredOk, ["durationMs"] = r.DurationMs,
        }).ToList();
        return new Node
        {
            ["selectedProblems"] = w.Selected.Cast<object?>().ToList(),
            ["userNote"] = w.Note,
            ["testsRun"] = w.Steps.Count(r => r.Answer != "skipped"),
            ["needALook"] = w.Steps.Count(r => NeedsALook(r.Consistency)),
            ["tests"] = tests,
        };
    }

    static Node Collect(string appVersion, List<(string Name, byte[] Data)> files, WizardResult? wiz = null)
    {
        var total = Stopwatch.StartNew();
        var features = new List<object?>();
        var findings = new List<object?>();
        void Feature(string id, string name, string status, string detail) =>
            features.Add(new Node { ["id"] = id, ["name"] = name, ["status"] = status, ["detail"] = detail });
        void Finding(string severity, string code, string message) =>
            findings.Add(new Node { ["severity"] = severity, ["code"] = code, ["message"] = message });

        var root = new Node
        {
            ["schema"] = Schema,
            ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
            ["generatedLocal"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
        };
        var summary = new Node(); root["summary"] = summary;
        root["app"] = AppInfo(appVersion);
        root["system"] = SystemInfo();

        // ---- driver and the other Clevo software
        var driver = DriverInfo(out bool opened, out string? openError, out bool fnRegistered, out string? fnError, out bool ifaceFound, out int? openWin32);
        root["driver"] = driver;
        var deep = DriverDeep();
        root["driverDeep"] = deep.Node;
        root["otherClevoSoftware"] = OtherSoftware(out bool ccService, out bool fnKeyRunning, out bool dchuRunning);

        // ---- firmware
        var firmware = new Node(); root["firmware"] = firmware;
        FanReading[] fans = []; FanCurve[] curves = []; KeyboardState? kb = null; FlexiCharger? charger = null;
        (GpuMode Mode, byte Bits)? gpu = null; bool? camera = null; byte[]? store = null;
        PowerMode? power = null; int? fanMode = null, fanOffset = null;

        if (opened)
        {
            using var br = AcpiBridge.Open();
            var probes = new List<object?>(); firmware["probes"] = probes;
            probes.Add(Dsm(br, "camera power", DsmFn.CameraState));
            probes.Add(Dsm(br, "hardware monitor (fans, temperatures)", DsmFn.HardwareMonitor));
            probes.Add(Dsm(br, "capability and fan-curve page", 13));
            probes.Add(Dsm(br, "product info", DsmFn.ProductInfo));
            probes.Add(Dsm(br, "feature flags (fn 96)", 96));
            probes.Add(Dsm(br, "feature flags (fn 122: logo, lightbar)", 122));
            probes.Add(Dsm(br, "GPU mode (fn 4 sub 21)", DsmFn.FanTable, [21]));
            probes.Add(Dsm(br, "battery charge limit (fn 4 sub 30)", DsmFn.FanTable, [30]));

            var decoded = new Node(); firmware["decoded"] = decoded;
            decoded["fans"] = Probe(() => { fans = br.ReadFans(); return fans.Select(f => new Node { ["index"] = f.Index, ["rpm"] = f.Rpm, ["dutyPercent"] = f.DutyPercent, ["temperatureC"] = f.TemperatureC }).ToList<object?>(); });
            decoded["fanCurves"] = Probe(() => { curves = br.ReadFanCurves(); return curves.Select(c => new Node { ["fan"] = c.Index, ["points"] = $"({c.T1}C,{c.D1}%) ({c.T2}C,{c.D2}%) ({c.T3}C,{c.D3}%) (100C,100%)" }).ToList<object?>(); });
            decoded["capabilityPage_fn13"] = Probe(() =>
            {
                var b = br.Call(13).Buffer ?? [];
                return b.Length > 43 ? new Node { ["fanCount_b12"] = b[12], ["keyboardTypeRaw_b15"] = b[15], ["bootEffectOverride_b43bit0"] = (b[43] & 1) == 1 } : "buffer too short";
            });
            decoded["cameraPower"] = Probe(() => { camera = br.ReadCameraPower(); return camera; });
            decoded["gpuMode"] = Probe(() => { gpu = br.ReadGpuMode(); return new Node { ["mode"] = gpu.Value.Mode.ToString(), ["modeRaw"] = (int)gpu.Value.Mode, ["supportBits"] = "0x" + gpu.Value.Bits.ToString("x2") }; });
            decoded["chargeLimit"] = Probe(() => { charger = br.ReadFlexiCharger(); return new Node { ["enabled"] = charger.Value.Enabled, ["startPercent"] = charger.Value.StartPercent, ["stopPercent"] = charger.Value.StopPercent }; });
            decoded["savedPowerMode"] = Probe(() => { power = br.GetSavedPowerMode(); return power.ToString(); });
            decoded["savedFanMode"] = Probe(() => { fanMode = br.GetSavedFanMode(); return fanMode; });
            decoded["savedFanOffset"] = Probe(() => { fanOffset = br.GetSavedFanOffset(); return fanOffset; });
            decoded["keyboard"] = Probe(() =>
            {
                kb = br.ReadKeyboard();
                return new Node
                {
                    ["type"] = kb.Type, ["kind"] = kb.Kind.ToString(), ["supportedByOpenClevo"] = KeyboardExtensions.IsSupportedType(kb.Type),
                    ["storeWasEmpty"] = kb.Fresh, ["clevoMode"] = kb.ClevoMode, ["colourLeft"] = kb.Left.ToString(),
                    ["logo"] = kb.Caps?.Logo, ["lightbar"] = kb.Caps?.Lightbar, ["bootEffectOverride"] = kb.Caps?.BootEffect,
                };
            });

            var st = Probe(() =>
            {
                store = br.ReadAppSettingsStore();
                int nz = store.Count(b => b != 0);
                return new Node
                {
                    ["bytes"] = store.Length, ["nonZeroBytes"] = nz,
                    ["powerMode_0x101"] = store[0x101], ["cameraPref_0x106"] = store[0x106], ["keyboardType_0x204"] = store[0x204],
                    ["keyboardMode_0x220"] = store[0x220], ["fanMode_0x405"] = store[0x405], ["fanOffset_0x407"] = store[0x407],
                    ["rowsWithData"] = Enumerable.Range(0, store.Length / 16).Where(r => store.AsSpan(r * 16, 16).IndexOfAnyExcept((byte)0) >= 0)
                        .Select(r => (object?)$"0x{r * 16:x4}: {Convert.ToHexString(store.AsSpan(r * 16, 16))}").ToList(),
                    ["raw"] = Convert.ToHexString(store),
                };
            });
            firmware["appSettingsStore"] = st;
        }
        else firmware["skipped"] = "The driver could not be opened, so no firmware word could be read: " + openError;

        // ---- Windows-side features
        var windows = new Node(); root["windowsFeatures"] = windows;
        windows["touchpad"] = Probe(() => new Node { ["supported"] = SystemToggles.TouchpadSupported, ["enabled"] = SystemToggles.Touchpad });
        windows["cameraAccessSwitch"] = Probe(() => SystemToggles.Camera ? "Allow" : "Deny");
        windows["numLock"] = Probe(() => SystemToggles.NumLock);
        windows["scrollLock"] = Probe(() => SystemToggles.ScrollLock);
        windows["winKeyLockActive"] = Probe(() => SystemToggles.WinKeyLock);
        bool? nvidiaOn = null; bool helperReady = false;
        windows["nvidiaPlatformController"] = Probe(() => { nvidiaOn = NvidiaPower.IsOn; return nvidiaOn is null ? "not present" : nvidiaOn.Value ? "on" : "off (disabled by OpenClevo / Clevo)"; });
        windows["nvidiaHelper"] = Probe(() => { helperReady = NvidiaPower.HelperReady; return helperReady ? "installed and current" : "not installed (installed on first GPU switch)"; });
        windows["nvidiaOffByPowerSaving"] = Probe(() => NvidiaPower.OffByPowerSaving);
        windows["battery"] = Probe(() =>
        {
            var p = System.Windows.Forms.SystemInformation.PowerStatus;
            return new Node { ["powerLine"] = p.PowerLineStatus.ToString(), ["chargeStatus"] = p.BatteryChargeStatus.ToString(), ["lifePercent"] = (int)(p.BatteryLifePercent * 100) };
        });
        root["pathChecks"] = PathChecks();
        root["openClevoSettings"] = RegDump(Registry.CurrentUser, @"Software\OpenClevo");
        root["installation"] = Installation();
        root["logs"] = Logs();

        // ---- verdicts
        Feature("driver", "Driver access (Acpi Bridge)", opened ? "ok" : "error", opened ? "driver opened" : openError ?? "driver not available");
        if (!opened) Finding("critical", "driver-unavailable", "OpenClevo cannot reach the Acpi Bridge driver: " + openError + " Nothing else can work until the driver (ACPI\\CLV0001 / AcpiBridge.sys) is installed and started.");

        if (opened)
        {
            Feature("fans", "Fan monitoring", fans.Length > 0 ? "ok" : "unsupported", fans.Length > 0 ? string.Join("; ", fans.Select(f => $"fan {f.Index}: {f.Rpm} rpm, {f.DutyPercent}%, {f.TemperatureC} C")) : "the firmware reports no fans");
            if (fans.Length == 0) Finding("warning", "no-fans", "No fan data came back from the firmware (fn 12). Fan pages will be empty on this laptop.");
            else if (fans.All(f => f.Rpm == 0 && f.TemperatureC <= 20)) Finding("warning", "fan-readings-flat", "All fan readings are zero or at the controller floor value; the readings may not be valid on this model.");
            Feature("fan-curves", "Fan curves", curves.Length > 0 ? "ok" : "unsupported", curves.Length > 0 ? $"{curves.Length} curve(s) readable" : "no fan curve data (fn 13 reports 0 fans)");
            Feature("power-mode", "Power mode", power != null ? "ok" : "error", power != null ? $"saved mode {power} (read from Clevo's settings store)" : "could not read");
            Feature("fan-mode", "Fan mode and offset", fanMode != null ? "ok" : "error", fanMode != null ? $"fan mode {fanMode}, auto offset {fanOffset}%" : "could not read");
            if (kb != null)
            {
                bool sup = KeyboardExtensions.IsSupportedType(kb.Type);
                string how = kb.Kind switch
                {
                    KeyboardKind.White => "white backlight: brightness only",
                    KeyboardKind.Zoned => "zoned RGB with firmware effects",
                    KeyboardKind.AllColour => "one-colour RGB (firmware effects do not exist on this type; OpenClevo's software effects work)",
                    KeyboardKind.PerKey => "per-key RGB over USB: not supported yet",
                    KeyboardKind.None => "the firmware reports no keyboard backlight",
                    _ => "unknown type",
                };
                Feature("keyboard", "Keyboard backlight", sup ? "ok" : kb.Kind == KeyboardKind.None ? "unsupported" : "unsupported", $"type {kb.Type} ({kb.Kind}): {how}");
                if (kb.Kind == KeyboardKind.PerKey) Finding("warning", "keyboard-perkey", $"Keyboard type {kb.Type} is a per-key RGB keyboard (USB controller). OpenClevo cannot drive it yet.");
                if (kb.Kind == KeyboardKind.Other) Finding("warning", "keyboard-unknown", $"Keyboard type {kb.Type} is not known to OpenClevo. Please send this report so the type can be added.");
                if (kb.Fresh) Finding("info", "settings-store-empty", "Clevo's settings store has no keyboard data (Clevo's own app never ran here). Colours start from white, static.");
            }
            else Feature("keyboard", "Keyboard backlight", "error", "could not read the keyboard state");
            Feature("charge-limit", "Battery charge limit", charger != null ? "ok" : "unsupported", charger != null ? $"readable: {(charger.Value.Enabled ? "on" : "off")} {charger.Value.StartPercent}-{charger.Value.StopPercent}%" : "the firmware did not answer");
            Feature("gpu-mode", "GPU mode (firmware)", gpu != null ? "ok" : "unsupported", gpu != null ? $"{gpu.Value.Mode} (support bits 0x{gpu.Value.Bits:x2}); changing it needs a reboot and OpenClevo only reads it" : "not reported");
            Feature("camera-firmware", "Camera power (firmware)", camera != null ? "ok" : "unsupported", camera != null ? (camera.Value ? "camera powered" : "camera powered off") : "not reported");
        }
        Feature("nvidia-switch", "NVIDIA GPU switch", nvidiaOn != null ? "ok" : "unsupported", nvidiaOn != null ? $"platform controller present, GPU {(nvidiaOn.Value ? "on" : "off")}; helper {(helperReady ? "ready" : "not installed yet")}" : "no NVIDIA platform controller (ACPI\\NVDA0820) on this machine");
        Feature("fn-events", "Fn key events (camera, touchpad)", fnRegistered ? "ok" : opened ? "error" : "unsupported", fnRegistered ? "event channel registered" : fnError ?? "channel not available");
        Feature("touchpad", "Touchpad switch", SafeBool(() => SystemToggles.TouchpadSupported) ? "ok" : "unsupported", SafeBool(() => SystemToggles.TouchpadSupported) ? "Windows precision-touchpad setting present" : "no precision-touchpad setting in Windows");
        Feature("lock-keys", "Num Lock / Scroll Lock / Caps notices", "ok", "key state readable");

        // the installer logs what pnputil answered when it put the driver in: 259 = nothing newer to install (the driver was already there)
        try
        {
            string setupLog = Path.Combine(Path.GetTempPath(), "OpenClevo-setup.log");
            if (File.Exists(setupLog))
            {
                var codes = Regex.Matches(File.ReadAllText(setupLog), @"pnputil exit code (\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToList();
                var bad = codes.Where(c => c is not (0 or 259 or 3010)).Distinct().ToList();
                if (bad.Count > 0) Finding("warning", "driver-install-failed", $"The installer log shows pnputil failing with exit code(s) {string.Join(", ", bad)} while installing the Acpi Bridge driver.");
                else if (codes.Contains(259)) Finding("info", "pnputil-259", "The installer log shows pnputil exit code 259: the Acpi Bridge driver was already installed (for example by Clevo's own package), so nothing newer was added. This is not an error.");
            }
        }
        catch { }

        if (!SafeBool(() => SystemToggles.Camera))
            Finding("warning", "camera-privacy-deny", "Windows' camera privacy switch for desktop apps is set to Deny for this user: desktop apps (browsers, Zoom, Teams) cannot use the camera even when it is powered on. Settings > Privacy > Camera.");
        if (ccService) Finding("warning", "clevo-service-running", "Clevo's Control Center Hotkey Service (CCDCHUService) is running. It can reset fan and power settings in the background and may handle the same Fn keys.");
        if (fnKeyRunning) Finding("info", "fnkey-running", "Clevo's FnKey.exe (Fn hotkeys and OSD) is running next to OpenClevo. Both react to the Fn keys; this is normal but can double notices.");
        if (dchuRunning && !ccService) Finding("info", "dchu-running", "DCHUService is running.");
        if (!features.Cast<Node>().Any(f => (string?)f["status"] == "error") && !findings.Cast<Node>().Any(f => (string?)f["severity"] is "critical" or "warning"))
            Finding("info", "all-clear", "Every check passed and no warnings were found.");

        // ---- the root-cause chain and the parts that only matter when something is wrong
        bool dsmAnswered = firmware.TryGetValue("probes", out var pr) && pr is List<object?> pl && pl.OfType<Node>().Any(p => p["ok"] is true);
        bool sane = fans.Length > 0 || (kb != null && kb.Type != 0);
        var chain = RootCause(deep, ifaceFound, opened, openWin32, openError, dsmAnswered, sane);
        summary["rootCause"] = chain;
        if (chain["firstBrokenLink"] != null)
            Finding("critical", "root-cause", $"{chain["verdict"]}. {chain["suggestedFix"]}");
        root["acpiTables"] = AcpiTables(files);
        root["appParts"] = AppParts();
        if (wiz != null)
        {
            root["scenarios"] = ScenarioNode(wiz);
            foreach (var r in wiz.Steps.Where(r => NeedsALook(r.Consistency)))
                Finding("warning", "scenario-" + r.Step, $"Guided test '{r.Step}': {r.Consistency}. {r.Detail}{(r.Readback != null ? " [" + r.Readback + "]" : "")}");
            if (!string.IsNullOrWhiteSpace(wiz.Note)) Finding("info", "user-note", "The user wrote: " + wiz.Note);
        }

        // ---- summary on top
        summary["features"] = features;
        summary["findings"] = findings;
        summary["counts"] = new Node
        {
            ["ok"] = features.Cast<Node>().Count(f => (string?)f["status"] == "ok"),
            ["unsupported"] = features.Cast<Node>().Count(f => (string?)f["status"] == "unsupported"),
            ["error"] = features.Cast<Node>().Count(f => (string?)f["status"] == "error"),
        };
        // one paste-ready line: who, what keyboard, how many features work, where the chain breaks
        var counts = (Node)summary["counts"]!;
        string laptop = ((root["system"] as Node)?["bios"] is Node b ? $"{b.GetValueOrDefault("SystemManufacturer")} {b.GetValueOrDefault("SystemProductName")} (SKU {b.GetValueOrDefault("SystemSKU")})" : "unknown laptop");
        summary["headline"] = $"{laptop} | OpenClevo {appVersion} | keyboard type {(kb != null ? kb.Type.ToString() : "?")} | {counts["ok"]} ok, {counts["unsupported"]} not offered, {counts["error"]} failed | {chain["verdict"]}";
        root["durationMs"] = total.ElapsedMilliseconds;
        root["privacy"] = "No user name, computer name or serial number is included; the user profile path is replaced by %USERPROFILE%. " +
            (wiz is { Steps.Count: > 0 } ? "The data collection only reads; the guided tests changed settings for a moment (each one was undone, see 'restored' per test)." : "Nothing was written to the hardware.");
        return root;
    }

    static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }

    /// <summary>Every file, device, registry key and task a feature relies on, and whether it exists on this machine.</summary>
    static List<object?> PathChecks()
    {
        var l = new List<object?>();
        void File_(string feature, string path) => l.Add(new Node { ["feature"] = feature, ["kind"] = "file", ["path"] = path, ["exists"] = File.Exists(path) || Directory.Exists(path) });
        void Reg_(string feature, RegistryKey hive, string path) => l.Add(new Node { ["feature"] = feature, ["kind"] = "registry", ["path"] = (hive == Registry.LocalMachine ? "HKLM\\" : "HKCU\\") + path, ["exists"] = SafeBool(() => { using var k = hive.OpenSubKey(path); return k != null; }) });
        void Task_(string feature, string name) => l.Add(new Node { ["feature"] = feature, ["kind"] = "scheduled task", ["path"] = name, ["exists"] = SafeBool(() => { using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/query /tn {name}") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }); return p != null && p.WaitForExit(8000) && p.ExitCode == 0; }) });

        string drivers = Path.Combine(Environment.SystemDirectory, "drivers");
        File_("driver", Path.Combine(drivers, "AcpiBridge.sys"));
        Reg_("driver", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\ACPI\CLV0001");
        Reg_("driver", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\AcpiBridge");
        Reg_("fan, power, keyboard, charger (Clevo's other ACPI device)", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\ACPI\CLV0002");
        Reg_("NVIDIA GPU switch", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Enum\ACPI\NVDA0820");
        Task_("NVIDIA GPU switch (off)", "OpenClevoNvidiaOff");
        Task_("NVIDIA GPU switch (on)", "OpenClevoNvidiaOn");
        Reg_("touchpad switch", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status");
        Reg_("camera privacy switch (apps)", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam");
        Reg_("camera privacy switch (desktop apps)", Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged");
        File_("installed copy", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenClevo", "OpenClevo.exe"));
        File_("Clevo's InsydeDCHU.dll (needed only by other tools)", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ControlCenter", "InsydeDCHU.dll"));
        Reg_("Clevo WMI mapper (needed only by other tools)", Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\WMI\WmiAcpi");
        return l;
    }

    // ------------------------------------------------------------------ sections

    static Node AppInfo(string version)
    {
        var n = new Node
        {
            ["version"] = version,
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["elevated"] = SafeBool(() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)),
            ["language"] = OpenClevo.Localization.Loc.Current,
            ["theme"] = App.IsLightTheme ? "light" : "dark",
            ["keyNoticesEnabled"] = OsdHost.Enabled,
            ["closeToTray"] = SafeBool(() => MainWindow.CloseToTray),
            ["testOverrides"] = new[] { "OPENCLEVO_KBTYPE", "OPENCLEVO_HWRENDER" }.Where(v => Environment.GetEnvironmentVariable(v) != null).Select(v => (object?)(v + "=" + Environment.GetEnvironmentVariable(v))).ToList(),
        };
        try { n["exe"] = FileNode(Environment.ProcessPath); } catch { }
        return n;
    }

    static Node SystemInfo()
    {
        var n = new Node
        {
            ["windows"] = RuntimeInformation.OSDescription,
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["uptimeHours"] = Math.Round(Environment.TickCount64 / 3600000.0, 1),
            ["culture"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
            ["logicalProcessors"] = Environment.ProcessorCount,
        };
        n["windowsName"] = Probe(() =>
        {
            int build = Environment.OSVersion.Version.Build;
            var ubr = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")?.GetValue("UBR");
            return $"{(build >= 22000 ? "Windows 11" : "Windows 10")} build {build}.{ubr}";   // the registry still says "Windows 10" on Windows 11
        });
        n["windowsVersion"] = RegDump(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName", "DisplayVersion", "CurrentBuild", "UBR", "EditionID");
        n["bios"] = RegDump(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\BIOS");
        n["cpu"] = RegDump(Registry.LocalMachine, @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "Identifier", "~MHz", "VendorIdentifier");
        n["memory"] = Probe(() => { var m = new MemStatus { dwLength = (uint)Marshal.SizeOf<MemStatus>() }; return GlobalMemoryStatusEx(ref m) ? $"{m.ullTotalPhys / 1048576} MB" : null; });
        var gpus = new List<object?>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames().Where(s => s.All(char.IsDigit)) ?? [])
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") == null) continue;
                gpus.Add(new Node { ["name"] = k.GetValue("DriverDesc"), ["driverVersion"] = k.GetValue("DriverVersion"), ["provider"] = k.GetValue("ProviderName"), ["hardwareId"] = k.GetValue("MatchingDeviceId") });
            }
        }
        catch (Exception ex) { gpus.Add("error: " + ex.Message); }
        n["displayAdapters"] = gpus;
        return n;
    }

    static Node DriverInfo(out bool opened, out string? openError, out bool fnRegistered, out string? fnError, out bool ifaceFound, out int? openWin32)
    {
        var n = new Node();
        string? error = null; int? win32 = null;
        n["interfacePath"] = Probe(() => AcpiBridge.FindDevicePath());
        ifaceFound = SafeBool(() => { AcpiBridge.FindDevicePath(); return true; });
        try
        {
            // testing aid: OPENCLEVO_DIAG_SIMULATE=nodriver shows how the report reads when the driver cannot be opened
            if (Environment.GetEnvironmentVariable("OPENCLEVO_DIAG_SIMULATE") == "nodriver") throw new Win32Exception(5, "simulated: access denied");
            using var br = AcpiBridge.Open(); opened = true; n["open"] = "ok";
        }
        catch (Exception ex) { opened = false; error = ex.Message; if (ex is Win32Exception w) win32 = w.NativeErrorCode; n["open"] = "failed: " + ex.Message + (win32 != null ? $" (Win32 error {win32})" : ""); }
        openError = error; openWin32 = win32;
        n["service"] = RegDump(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\AcpiBridge", "DisplayName", "Start", "Type", "ErrorControl", "ImagePath");
        n["service2"] = RegDump(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Services\AcpiBridge1", "DisplayName", "Start", "Type", "ImagePath");
        string drivers = Path.Combine(Environment.SystemDirectory, "drivers");
        n["driverFile"] = FileNode(Path.Combine(drivers, "AcpiBridge.sys"));
        n["driverFile2"] = FileNode(Path.Combine(drivers, "AcpiBridge1.sys"));
        n["clevoAcpiDevice"] = Probe(() =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ACPI\CLV0001");
            if (k == null) return "ACPI\\CLV0001 not present";
            return k.GetSubKeyNames().Select(s =>
            {
                using var d = k.OpenSubKey(s);
                return (object?)new Node { ["instance"] = s, ["service"] = d?.GetValue("Service"), ["class"] = d?.GetValue("Class"), ["configFlags"] = d?.GetValue("ConfigFlags"), ["driver"] = d?.GetValue("Driver") };
            }).ToList<object?>();
        });
        n["acpiDevices"] = Probe(() =>
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ACPI");
            return k?.GetSubKeyNames().Order().ToList<object?>();
        });
        n["wmiMapper"] = RegDump(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\WMI\WmiAcpi");

        // passive: register for Fn events like Clevo's service does, then let go (nothing is written to the hardware)
        bool reg = false; string? ferr = null;
        try
        {
            using var fn = new FnEvents();
            for (int i = 0; i < 12 && !fn.Registered && fn.Error == null; i++) Thread.Sleep(50);
            reg = fn.Registered; ferr = fn.Error;
        }
        catch (Exception ex) { ferr = ex.Message; }
        fnRegistered = reg; fnError = ferr;
        n["fnEventChannel"] = reg ? "registered" : "failed: " + (ferr ?? "no answer");
        return n;
    }

    static readonly Regex ClevoNames = new(@"clevo|control ?center|dchu|fnkey|cc30|cc40|hotkey|hkey|insyde|gigabyte|aorus|flexi|ledkeyboard|clevo|monster|nvidia|powerlife", RegexOptions.IgnoreCase);

    static Node OtherSoftware(out bool ccService, out bool fnKeyRunning, out bool dchuRunning)
    {
        var n = new Node();
        var procs = new List<object?>();
        bool cc = false, fnk = false, dchu = false;
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    string name = p.ProcessName;
                    if (!ClevoNames.IsMatch(name)) continue;
                    if (name.Equals("FnKey", StringComparison.OrdinalIgnoreCase)) fnk = true;
                    if (name.Equals("DCHUService", StringComparison.OrdinalIgnoreCase)) dchu = true;
                    string? path = null; string? start = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    try { start = p.StartTime.ToString("yyyy-MM-dd HH:mm"); } catch { }
                    procs.Add(new Node { ["name"] = name, ["pid"] = p.Id, ["path"] = path, ["started"] = start });
                }
            }
        }
        catch (Exception ex) { procs.Add("error: " + ex.Message); }
        n["processes"] = procs;

        var services = new List<object?>();
        try
        {
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var s in svc?.GetSubKeyNames() ?? [])
            {
                if (!Regex.IsMatch(s, @"clevo|dchu|insyde|acpibridge|fnkey|hotkey", RegexOptions.IgnoreCase)) continue;
                using var k = svc!.OpenSubKey(s);
                services.Add(new Node { ["name"] = s, ["displayName"] = k?.GetValue("DisplayName"), ["start"] = StartType(k?.GetValue("Start")), ["imagePath"] = k?.GetValue("ImagePath") });
                if (s.Equals("CCDCHUService", StringComparison.OrdinalIgnoreCase) && procs.OfType<Node>().Any(p => string.Equals((string?)p["name"], "DCHUService", StringComparison.OrdinalIgnoreCase))) cc = true;
            }
        }
        catch (Exception ex) { services.Add("error: " + ex.Message); }
        n["services"] = services;

        n["appxPackages"] = Probe(() =>
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            return k?.GetSubKeyNames().Where(s => Regex.IsMatch(s, "clevo|controlcenter|fnkey", RegexOptions.IgnoreCase)).Order().ToList<object?>();
        });
        n["controlCenterFolder"] = Probe(() =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ControlCenter");
            return Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order().ToList<object?>() : "not installed";
        });
        ccService = cc; fnKeyRunning = fnk; dchuRunning = dchu;
        return n;
    }

    static object? StartType(object? v) => v switch { 0 => "boot", 1 => "system", 2 => "automatic", 3 => "manual", 4 => "disabled", _ => v };

    static Node Installation()
    {
        var n = new Node();
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenClevo");
        n["installFolder"] = Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order().ToList<object?>() : "not found";
        n["installedExe"] = FileNode(Path.Combine(dir, "OpenClevo.exe"));
        n["startWithWindows"] = Probe(() => Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("OpenClevo") != null);
        n["uninstallEntry"] = Probe(() =>
        {
            using var u = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            foreach (var s in u?.GetSubKeyNames() ?? [])
            {
                using var k = u!.OpenSubKey(s);
                if ((k?.GetValue("DisplayName") as string) == "OpenClevo")
                    return new Node { ["displayVersion"] = k.GetValue("DisplayVersion"), ["installDate"] = k.GetValue("InstallDate"), ["publisher"] = k.GetValue("Publisher") };
            }
            return null;
        });
        return n;
    }

    static Node Logs()
    {
        var n = new Node();
        // crashes of OpenClevo itself: its own crash log, the Application event log (error and warning entries naming OpenClevo), dump files
        n["crashLog"] = Probe(() => { string p = Path.Combine(Path.GetTempPath(), "OpenClevo-crash.log"); return File.Exists(p) ? File.ReadAllLines(p).TakeLast(80).ToList<object?>() : "no crash log"; });
        n["applicationEventLog"] = EventExcerpt("Application");
        n["crashDumps"] = Probe(() =>
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "OpenClevo*.dmp").Select(f => (object?)$"{Path.GetFileName(f)}  {File.GetLastWriteTime(f):yyyy-MM-dd HH:mm}  {new FileInfo(f).Length / 1024} KB").ToList() : "no dump folder";
        });
        foreach (var name in new[] { "OpenClevo-setup.log", "OpenClevo-uninstall.log", "OpenClevo-wizard.log" })
        {
            string p = Path.Combine(Path.GetTempPath(), name);
            n[name] = Probe(() => File.Exists(p) ? File.ReadAllLines(p).TakeLast(60).ToList<object?>() : "no log");
        }
        return n;
    }

    // ------------------------------------------------------------------ helpers

    static Node Probe(Func<object?> f)
    {
        var sw = Stopwatch.StartNew();
        var n = new Node();
        try
        {
            var v = f();
            n["ok"] = true;
            if (v != null) n["value"] = v;
        }
        catch (Exception ex)
        {
            n["ok"] = false;
            n["error"] = ex.GetType().Name + ": " + ex.Message;
            if (ex is Win32Exception w) n["win32Error"] = w.NativeErrorCode;
        }
        n["ms"] = sw.ElapsedMilliseconds;
        return n;
    }

    /// <summary>One read-only firmware call, kept raw: input, integer result, buffer bytes.</summary>
    static Node Dsm(AcpiBridge br, string what, int function, byte[]? data = null)
    {
        var n = new Node { ["what"] = what, ["function"] = function };
        if (data != null) n["input"] = Convert.ToHexString(data);
        var sw = Stopwatch.StartNew();
        try
        {
            var r = br.Call(function, data ?? default);
            n["ok"] = true;
            if (r.Integer is uint i) { n["integer"] = i; n["integerHex"] = "0x" + i.ToString("x8"); }
            if (r.Buffer is { } b) { n["bufferLength"] = b.Length; n["bufferHex"] = Convert.ToHexString(b.AsSpan(0, Math.Min(256, b.Length))); }
        }
        catch (Exception ex) { n["ok"] = false; n["error"] = ex.Message; }
        n["ms"] = sw.ElapsedMilliseconds;
        return n;
    }

    static Node FileNode(string? path)
    {
        var n = new Node { ["path"] = path };
        try
        {
            if (path == null || !File.Exists(path)) { n["exists"] = false; return n; }
            var fi = new FileInfo(path); var v = FileVersionInfo.GetVersionInfo(path);
            n["exists"] = true; n["sizeBytes"] = fi.Length; n["modified"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
            n["fileVersion"] = v.FileVersion; n["productVersion"] = v.ProductVersion; n["company"] = v.CompanyName;
        }
        catch (Exception ex) { n["error"] = ex.Message; }
        return n;
    }

    static object? RegDump(RegistryKey hive, string path, params string[] names)
    {
        try
        {
            using var k = hive.OpenSubKey(path);
            if (k == null) return "key not present";
            var n = new Node();
            foreach (var name in names.Length > 0 ? names : k.GetValueNames())
            {
                var v = k.GetValue(name);
                if (v == null) continue;
                n[string.IsNullOrEmpty(name) ? "(default)" : name] = v switch
                {
                    byte[] b => Convert.ToHexString(b.AsSpan(0, Math.Min(64, b.Length))),
                    string[] s => s.ToList<object?>(),
                    _ => v,
                };
            }
            return n;
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    /// <summary>Removes the user and computer names (and the profile path) from the finished text.</summary>
    static string Redact(string s)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase).Replace(profile.Replace("\\", "\\\\"), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        s = Regex.Replace(s, @"S-1-5-21-\d+-\d+-\d+-\d+", "S-1-5-21-<redacted>");   // the user's account id in event logs
        foreach (var (word, repl) in new[] { (Environment.UserName, "<user>"), (Environment.MachineName, "<computer>") })
            if (word.Length > 2) s = Regex.Replace(s, @"(?<![A-Za-z0-9])" + Regex.Escape(word) + @"(?![A-Za-z0-9])", repl, RegexOptions.IgnoreCase);
        return s;
    }

    // ------------------------------------------------------------------ text rendering

    static string RenderText(Node root)
    {
        var sb = new StringBuilder();
        sb.AppendLine("OPENCLEVO DIAGNOSTIC REPORT");
        sb.AppendLine(new string('=', 27));
        sb.AppendLine($"Generated : {root["generatedLocal"]}   (took {root["durationMs"]} ms)");
        sb.AppendLine($"Schema    : {Schema}");
        sb.AppendLine($"Privacy   : {root["privacy"]}");
        sb.AppendLine();
        sb.AppendLine("SUMMARY : " + ((Node)root["summary"]!)["headline"]);
        sb.AppendLine();

        var summary = (Node)root["summary"]!;
        var sys = (Node)root["system"]!;
        if (sys["bios"] is Node bios)
            sb.AppendLine($"Laptop    : {bios.GetValueOrDefault("SystemManufacturer")} {bios.GetValueOrDefault("SystemProductName")}  (SKU {bios.GetValueOrDefault("SystemSKU")}, board {bios.GetValueOrDefault("BaseBoardProduct")}, BIOS {bios.GetValueOrDefault("BIOSVersion")} {bios.GetValueOrDefault("BIOSReleaseDate")})");
        sb.AppendLine($"OpenClevo : {((Node)root["app"]!)["version"]}");
        sb.AppendLine();

        var chainNode = (Node)summary["rootCause"]!;
        sb.AppendLine("ROOT CAUSE");
        sb.AppendLine("----------");
        sb.AppendLine($"Verdict : {chainNode["verdict"]}");
        if (chainNode["suggestedFix"] is string fixText) sb.AppendLine($"Fix     : {fixText}");
        foreach (Node st in (List<object?>)chainNode["steps"]!)
        {
            string tag = (string)st["status"]! switch { "ok" => "[ OK ]", "BROKEN" => "[ !! ]", _ => "[ .. ]" };
            sb.AppendLine($"  {tag} {st["step"]}. {st["name"]}: {st["detail"]}");
        }
        sb.AppendLine();

        sb.AppendLine("RESULT PER FEATURE");
        sb.AppendLine("------------------");
        foreach (Node f in (List<object?>)summary["features"]!)
        {
            string tag = (string)f["status"]! switch { "ok" => "[ OK ]", "unsupported" => "[ -- ]", _ => "[FAIL]" };
            sb.AppendLine($"{tag} {f["name"],-38} {f["detail"]}");
        }
        sb.AppendLine("  ([ -- ] = this laptop does not offer it, [FAIL] = something that should work did not)");
        sb.AppendLine();

        sb.AppendLine("FINDINGS");
        sb.AppendLine("--------");
        foreach (Node f in (List<object?>)summary["findings"]!)
            sb.AppendLine($"* {((string)f["severity"]!).ToUpperInvariant(),-8} {f["message"]}");
        sb.AppendLine();

        if (root.TryGetValue("scenarios", out var scObj) && scObj is Node sc)
        {
            sb.AppendLine("GUIDED TESTS (the user was asked what they see; every change was undone)");
            sb.AppendLine("-----------------------------------------------------------------------");
            sb.AppendLine($"Problems ticked: {string.Join(", ", (List<object?>)sc["selectedProblems"]!)}   tests run: {sc["testsRun"]}   need a look: {sc["needALook"]}");
            if (sc["userNote"] is string note) sb.AppendLine($"User note: {note}");
            foreach (Node t in (List<object?>)sc["tests"]!)
            {
                string c = (string)t["consistency"]!;
                string tag = c.StartsWith("confirmed") ? "[ OK ]" : c == "skipped" ? "[skip]" : NeedsALook(c) ? "[ !! ]" : "[ ~~ ]";
                sb.AppendLine($"{tag} {t["scenario"]} / {t["step"]}: {c}");
                sb.AppendLine($"        did: {t["detail"]}{(t["readback"] != null ? "   readback: " + t["readback"] : "")}{(t["error"] != null ? "   error: " + t["error"] : "")}");
                if (t["question"] != null) sb.AppendLine($"        asked: \"{t["question"]}\"  answer: {t["userAnswer"]}   restored: {t["restoredOk"]}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("DETAILS");
        sb.AppendLine("=======");
        foreach (var kv in root)
        {
            if (kv.Key is "summary" or "schema" or "generatedUtc" or "generatedLocal" or "privacy" or "durationMs") continue;
            sb.AppendLine();
            sb.AppendLine($"[{kv.Key}]");
            if (kv.Value is Node n) foreach (var c in n) W(sb, c.Key, c.Value, 1);
            else W(sb, "items", kv.Value, 1);
        }
        return sb.ToString();
    }

    static void W(StringBuilder sb, string key, object? v, int level)
    {
        string pad = new(' ', level * 2);
        switch (v)
        {
            case null: sb.AppendLine($"{pad}{key}: -"); break;
            case Node n:
                sb.AppendLine($"{pad}{key}:");
                foreach (var kv in n) W(sb, kv.Key, kv.Value, level + 1);
                break;
            case List<object?> l when l.Count > 0 && l[0] is Node:
                sb.AppendLine($"{pad}{key}:");
                int i = 1;
                foreach (var item in l)
                {
                    sb.AppendLine($"{pad}  #{i++}");
                    if (item is Node ni) foreach (var kv in ni) W(sb, kv.Key, kv.Value, level + 2);
                    else sb.AppendLine($"{pad}    {item}");
                }
                break;
            case List<object?> l when l.Count > 4 || l.Any(x => x is string s && (s.Length > 70 || s.Contains('\n'))):
                sb.AppendLine($"{pad}{key}:");
                foreach (var item in l)
                    foreach (var line in (item?.ToString() ?? "-").Split('\n')) sb.AppendLine($"{pad}  {line.TrimEnd()}");
                break;
            case List<object?> l: sb.AppendLine($"{pad}{key}: [{string.Join(", ", l)}]"); break;
            case string when key == "raw": sb.AppendLine($"{pad}raw: (all 4096 bytes are in the JSON file; the rows with data are listed above)"); break;
            case string s when key == "bufferHex":
                {
                    string t = s.TrimEnd('0');
                    if (t.Length % 2 == 1) t += "0";
                    int zeros = (s.Length - t.Length) / 2;
                    sb.AppendLine($"{pad}{key}: {t}{(zeros > 8 ? $"  ... +{zeros} zero bytes" : new string('0', zeros * 2))}");
                    break;
                }
            case string s when s.Length > 160:
                sb.AppendLine($"{pad}{key}:");
                for (int p = 0; p < s.Length; p += 96) sb.AppendLine($"{pad}  {s.Substring(p, Math.Min(96, s.Length - p))}");
                break;
            default: sb.AppendLine($"{pad}{key}: {v}"); break;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus s);
}

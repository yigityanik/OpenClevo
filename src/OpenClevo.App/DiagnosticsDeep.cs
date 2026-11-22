using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using OpenClevo.Core;
using Node = System.Collections.Generic.OrderedDictionary<string, object?>;

namespace OpenClevo.App;

/// <summary>
/// The deeper half of the report: why the driver may not work (security settings, driver store, device problem code, logs),
/// the root-cause chain that names the first broken link, the ACPI tables of the laptop, and the checks for the app's own parts.
/// Everything here only reads.
/// </summary>
static partial class Diagnostics
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("kernel32.dll")] static extern uint EnumSystemFirmwareTables(uint signature, byte[]? buffer, uint size);
    [DllImport("kernel32.dll")] static extern uint GetSystemFirmwareTable(uint signature, uint id, byte[]? buffer, uint size);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();

    /// <summary>What the chain needs to know about the driver, gathered once.</summary>
    sealed class Deep
    {
        public bool AcpiDevicePresent, InDriverStore, DeviceFound, ServiceRunning, SecurityBlocks;
        public uint? Problem, Status;
        public string? ServiceState;
        public Node Node = new();
    }

    static readonly Dictionary<uint, string> ProblemText = new()
    {
        [0] = "no problem",
        [1] = "not configured", [3] = "driver corrupt or low memory",
        [10] = "the device cannot start", [12] = "resource conflict", [14] = "restart needed", [18] = "reinstall the drivers",
        [19] = "registry problem", [21] = "Windows is removing the device", [22] = "the device is disabled",
        [24] = "device not present or not working", [28] = "no driver installed for this device", [29] = "disabled in the firmware",
        [31] = "the device is not working properly (driver could not load)", [32] = "driver service disabled", [33] = "cannot determine resources",
        [35] = "firmware table problem", [37] = "driver initialization failed", [38] = "previous driver instance still in memory",
        [39] = "driver corrupt or missing", [40] = "service key missing", [41] = "driver loaded but device not found",
        [43] = "the device reported a problem", [48] = "driver blocked (known incompatibility)", [52] = "Windows cannot verify the driver's digital signature",
    };

    // ------------------------------------------------------------------ driver, deeper

    static Deep DriverDeep()
    {
        var d = new Deep();
        var n = d.Node;

        // ACPI id present?
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ACPI\CLV0001"); d.AcpiDevicePresent = k != null; }
        catch { }
        n["acpiClv0001Present"] = d.AcpiDevicePresent;

        // security settings that stop kernel drivers from loading
        var sec = new Node();
        sec["secureBoot"] = RegVal(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
        sec["memoryIntegrityHVCI"] = RegVal(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
        sec["vulnerableDriverBlocklist"] = RegVal(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable");
        sec["virtualizationBasedSecurity"] = RegVal(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity");
        n["security"] = sec;

        // driver store (pnputil only reads)
        var store = new Node();
        string? pnp = Run("pnputil.exe", "/enum-drivers", 20000);
        if (pnp == null) store["error"] = "pnputil did not answer";
        else
        {
            var blocks = Regex.Split(pnp, @"\r?\n\s*\r?\n").Where(b => Regex.IsMatch(b, "acpibridge|clv0001|clevo", RegexOptions.IgnoreCase)).Select(b => (object?)b.Trim()).ToList();
            d.InDriverStore = blocks.Count > 0;
            store["matchingPackages"] = blocks;
        }
        n["driverStore"] = store;

        // device node status (Windows' own verdict on the device)
        var dev = new Node();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ACPI\CLV0001");
            string? inst = k?.GetSubKeyNames().FirstOrDefault();
            if (inst == null) dev["state"] = "device node not present";
            else if (CM_Locate_DevNodeW(out uint di, $@"ACPI\CLV0001\{inst}", 0) != 0) dev["state"] = "device node could not be located";
            else if (CM_Get_DevNode_Status(out uint st, out uint pr, di, 0) != 0) dev["state"] = "status could not be read";
            else
            {
                d.DeviceFound = true; d.Status = st; d.Problem = pr;
                dev["statusFlags"] = "0x" + st.ToString("x");
                dev["started"] = (st & 0x8) != 0;
                dev["driverLoaded"] = (st & 0x2) != 0;
                dev["hasProblem"] = (st & 0x400) != 0;
                dev["problemCode"] = pr;
                dev["problemMeaning"] = ProblemText.TryGetValue(pr, out var t) ? t : "see Microsoft's device manager error codes";
            }
        }
        catch (Exception ex) { dev["error"] = ex.Message; }
        n["deviceStatus"] = dev;

        // service state
        var svc = new Node();
        string? sc = Run("sc.exe", "query AcpiBridge", 8000);
        if (sc != null)
        {
            var m = Regex.Match(sc, @"STATE\s*:\s*(\d+)\s+(\w+)");
            if (m.Success) { d.ServiceState = m.Groups[2].Value; d.ServiceRunning = d.ServiceState == "RUNNING"; }
            svc["scQuery"] = sc.Trim();
        }
        else svc["scQuery"] = "sc.exe did not answer";
        n["serviceState"] = svc;

        // fingerprint of the driver and the app, so the exact versions are known
        n["fingerprints"] = new Node
        {
            ["AcpiBridge.sys"] = Sha256(Path.Combine(Environment.SystemDirectory, "drivers", "AcpiBridge.sys")),
            ["OpenClevo.exe (this run)"] = Sha256(Environment.ProcessPath),
        };

        // logs that explain a failed install or a blocked driver
        n["setupapiLog"] = SetupApiExcerpt();
        n["eventLogSystem"] = EventExcerpt("System");
        n["eventLogCodeIntegrity"] = EventExcerpt("Microsoft-Windows-CodeIntegrity/Operational");

        d.SecurityBlocks = false;
        return d;
    }

    static object? RegVal(RegistryKey hive, string path, string name)
    {
        try { using var k = hive.OpenSubKey(path); return k?.GetValue(name) ?? "not set"; }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    static string? Run(string file, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return null; }
            return output.Wait(3000) ? output.Result : null;
        }
        catch { return null; }
    }

    static string? Sha256(string? path)
    {
        try { return path != null && File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : "file not found"; }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    /// <summary>Lines of Windows' device-install log that mention the Acpi Bridge driver, with a little context.</summary>
    static object? SetupApiExcerpt()
    {
        try
        {
            string path = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "INF", "setupapi.dev.log");
            if (!File.Exists(path)) return "setupapi.dev.log not found";
            string[] lines;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs, Encoding.Default)) lines = r.ReadToEnd().Split('\n');
            var hits = Enumerable.Range(0, lines.Length).Where(i => Regex.IsMatch(lines[i], "acpibridge|clv0001", RegexOptions.IgnoreCase)).ToList();
            if (hits.Count == 0) return "no mention of AcpiBridge or CLV0001 in the log";
            var picked = new SortedSet<int>();
            foreach (int i in hits.TakeLast(12)) for (int j = Math.Max(0, i - 3); j <= Math.Min(lines.Length - 1, i + 6); j++) picked.Add(j);
            return picked.TakeLast(160).Select(i => (object?)lines[i].TrimEnd()).ToList();
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }

    /// <summary>Recent warnings and errors of one Windows event log that mention the driver, as plain text.</summary>
    static object? EventExcerpt(string log)
    {
        string? text = Run("wevtutil.exe", $"qe \"{log}\" /q:\"*[System[(Level=1 or Level=2 or Level=3)]]\" /c:400 /rd:true /f:text", 25000);
        if (text == null) return "wevtutil did not answer (log missing or access denied)";
        var blocks = Regex.Split(text, @"(?m)(?=^Event\[\d+\])").Where(b => Regex.IsMatch(b, "acpibridge|clv0001|clevo|dchu", RegexOptions.IgnoreCase)).Take(12).Select(b => (object?)Regex.Replace(b.Trim(), @"\s+\r?\n", "\n")).ToList();
        return blocks.Count > 0 ? blocks : "no matching events in the latest 400";
    }

    // ------------------------------------------------------------------ root cause

    /// <summary>Walks the chain from "this is a Clevo" to "the firmware answers sensibly" and names the first broken link.</summary>
    static Node RootCause(Deep d, bool interfaceFound, bool opened, int? openWin32, string? openError, bool dsmAnswered, bool sane)
    {
        var steps = new List<object?>();
        string? broken = null, fix = null;
        void Step(string name, bool ok, string detail, string? fixText)
        {
            steps.Add(new Node { ["step"] = steps.Count + 1, ["name"] = name, ["status"] = broken != null && !ok ? "not reached" : ok ? "ok" : "BROKEN", ["detail"] = detail });
            if (!ok && broken == null) { broken = name; fix = fixText; }
        }

        Step("The laptop has Clevo's ACPI device (ACPI\\CLV0001)", d.AcpiDevicePresent,
            d.AcpiDevicePresent ? "present" : "not present in the device list",
            "This laptop's firmware does not expose Clevo's ACPI interface, so OpenClevo cannot control it. Check the ACPI device list in this report: a Clevo laptop should list CLV0001.");
        Step("The Acpi Bridge driver package is in Windows' driver store", d.InDriverStore || opened,
            d.InDriverStore ? "found by pnputil" : opened ? "driver works (store listing not needed)" : "no AcpiBridge package listed",
            "The driver was never installed. Run OpenClevo's setup as administrator again; check the installer log and setupapi excerpt below for the reason.");
        Step("Windows has bound the driver to the device without a problem", d.DeviceFound && d.Problem == 0 || opened,
            !d.DeviceFound ? "device status unavailable" : d.Problem == 0 ? "no problem" : $"problem code {d.Problem}: {(d.Problem is uint pc && ProblemText.TryGetValue(pc, out var t) ? t : "unknown")}",
            d.Problem == 52 ? "Windows rejected the driver's signature. Secure Boot, memory integrity and the driver blocklist settings in this report show why; the driver may need a newer signed build."
            : d.Problem == 28 ? "No driver is attached to CLV0001. Reinstall the driver (OpenClevo setup as administrator)."
            : d.Problem is 10 or 31 or 37 ? "The driver is installed but cannot start. See the System event log excerpt and the setupapi excerpt for the exact error."
            : "See the problem code above and the logs in this report.");
        Step("The driver service is running", d.ServiceRunning || opened,
            d.ServiceState ?? "state unknown", "The service AcpiBridge is not running. Restart the PC, or start it from an administrator prompt: sc start AcpiBridge.");
        Step("The driver's device interface is found", interfaceFound || opened, interfaceFound ? "found" : "not found",
            "The driver is installed but exposes no interface. This usually means the device is disabled or failed to start; see the previous steps.");
        Step("OpenClevo can open the driver", opened,
            opened ? "opened" : $"{openError} (Win32 error {openWin32}: {Win32Meaning(openWin32)})",
            openWin32 == 5 ? "Access denied: start OpenClevo as administrator once, or check whether another program holds the driver exclusively."
            : openWin32 == 1275 || openWin32 == 577 ? "Windows blocks this driver (code integrity). See the security settings and the Code Integrity log excerpt."
            : "See the Win32 error above.");
        Step("The firmware answers a _DSM call", dsmAnswered, dsmAnswered ? "answered" : "no answer",
            "The driver opened but the firmware did not answer. The firmware's _DSM may differ on this model; send this report together with the ACPI tables.");
        Step("The answers make sense (fans or a keyboard type)", sane, sane ? "plausible data" : "all zero or empty",
            "The firmware answers but the data are empty or unfamiliar. This model may use different function numbers; the ACPI tables and raw words in this report show what it supports.");

        return new Node
        {
            ["verdict"] = broken == null ? "no broken link" : "first broken link: " + broken,
            ["firstBrokenLink"] = broken,
            ["suggestedFix"] = fix,
            ["steps"] = steps,
        };
    }

    static string Win32Meaning(int? code) => code switch
    {
        null => "n/a", 2 => "file not found", 5 => "access denied", 32 => "sharing violation (another program holds it)",
        87 => "invalid parameter", 1275 => "driver blocked from loading", 577 => "Windows cannot verify the digital signature", _ => new Win32Exception(code.Value).Message,
    };

    // ------------------------------------------------------------------ ACPI tables

    /// <summary>All ACPI tables of the laptop (DSDT and SSDTs hold Clevo's _DSM methods): bytes for the zip and a listing for the report.</summary>
    // Only the tables that hold Clevo's _DSM methods and the platform flags are collected. Everything else stays out: MSDM carries the
    // Windows product key of the machine, SLIC/WPBT/BGRT and friends carry licence data, binaries or images.
    static readonly HashSet<string> AcpiAllowList = new(["DSDT", "SSDT", "PSDT", "FACP", "ECDT"]);

    static List<object?> AcpiTables(List<(string Name, byte[] Data)> files, List<object?> skipped)
    {
        var list = new List<object?>();
        try
        {
            const uint Acpi = 0x41435049;   // 'ACPI'
            uint size = EnumSystemFirmwareTables(Acpi, null, 0);
            var ids = new List<string> { "DSDT" };
            if (size > 0)
            {
                var buf = new byte[size];
                EnumSystemFirmwareTables(Acpi, buf, size);
                for (int i = 0; i + 4 <= size; i += 4) ids.Add(Encoding.ASCII.GetString(buf, i, 4));
            }
            var seen = new Dictionary<string, int>();
            foreach (var id in ids)
            {
                if (!AcpiAllowList.Contains(id)) { if (!skipped.Contains(id)) skipped.Add(id); continue; }
                uint key = BitConverter.ToUInt32(Encoding.ASCII.GetBytes(id), 0);
                uint len = GetSystemFirmwareTable(Acpi, key, null, 0);
                if (len == 0) continue;
                var data = new byte[len];
                if (GetSystemFirmwareTable(Acpi, key, data, len) == 0) continue;
                // several SSDTs share one signature: the call returns the first, so only list distinct contents
                string hash = Convert.ToHexString(SHA256.HashData(data));
                if (!seen.TryAdd(hash, 1)) continue;
                int n = files.Count(f => f.Name.StartsWith("acpi/" + id));
                string name = n == 0 ? $"acpi/{id}.bin" : $"acpi/{id}-{n + 1}.bin";
                files.Add((name, data));
                list.Add(new Node
                {
                    ["file"] = name, ["bytes"] = len,
                    ["oemId"] = data.Length > 15 ? Encoding.ASCII.GetString(data, 10, 6).Trim('\0', ' ') : null,
                    ["oemTableId"] = data.Length > 23 ? Encoding.ASCII.GetString(data, 16, 8).Trim('\0', ' ') : null,
                    ["sha256"] = hash,
                });
            }
        }
        catch (Exception ex) { list.Add("error: " + ex.Message); }
        return list;
    }

    // ------------------------------------------------------------------ the app's own parts

    static Node AppParts()
    {
        var n = new Node();
        n["cpuClock"] = Probe(() => { using var c = new CpuClock(); c.Read(); Thread.Sleep(250); return c.Read() + " MHz"; });
        n["gpuClock"] = Probe(() => { using var g = new GpuClock(); bool on = NvidiaPower.IsOn != false; return on ? g.Read(true) + " MHz" : "GPU is off, not read"; });
        n["audioOutputDevices"] = Probe(() =>
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var devs = en.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).Select(x => (object?)x.FriendlyName).ToList();
            string def; try { using var d = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia); def = d.FriendlyName; } catch { def = "none"; }
            return new Node { ["defaultDevice"] = def, ["active"] = devs, ["note"] = "the sound-reactive keyboard effects listen to the default device" };
        });
        n["renderTier"] = Probe(() => System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Media.RenderCapability.Tier >> 16));
        n["fonts"] = Probe(() =>
        {
            var names = System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new Node { ["Segoe UI Variable"] = names.Contains("Segoe UI Variable Text") || names.Contains("Segoe UI Variable"), ["Segoe Fluent Icons"] = names.Contains("Segoe Fluent Icons"), ["Segoe MDL2 Assets"] = names.Contains("Segoe MDL2 Assets") };
        });
        n["displayScaling"] = Probe(() => new Node { ["systemDpi"] = GetDpiForSystem(), ["primaryScreen"] = System.Windows.Forms.Screen.PrimaryScreen is { } s ? $"{s.Bounds.Width}x{s.Bounds.Height}" : "unknown" });
        n["activePowerPlan"] = Probe(() => Run("powercfg.exe", "/getactivescheme", 8000)?.Trim());
        n["tempFolderWritable"] = Probe(() => { string f = Path.Combine(Path.GetTempPath(), "openclevo-write-test.tmp"); File.WriteAllText(f, "x"); File.Delete(f); return true; });
        n["singleFileExtractDir"] = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR") ?? "default";
        return n;
    }
}

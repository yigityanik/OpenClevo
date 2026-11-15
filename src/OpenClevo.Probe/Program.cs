using OpenClevo.Core;

// Default: READ-ONLY probe. "power-rewrite" re-sends the currently saved power mode (no change of mode).
static void PrintState(AcpiBridge br, string label)
{
    Console.WriteLine($"--- {label} ---");
    foreach (var f in br.ReadFans()) Console.WriteLine($"Fan {f.Index}: {f.Rpm} RPM, duty {f.DutyPercent}%, temp {f.TemperatureC} °C");
    var s = br.ReadAppSettingsStore();
    Console.WriteLine($"Saved: power mode={(PowerMode)s[0x101]}, fan mode={s[0x405]}, fan offset={s[0x407]}, [0x120]={s[0x120]}, [0x408]={s[0x408]}");
}

Console.WriteLine("Device: " + AcpiBridge.FindDevicePath());
using var br = AcpiBridge.Open();
PrintState(br, "before");

if (args.Length > 0 && args[0] == "wmi")
{
    // READ-ONLY: the integer reads FnKey performs at start-up (WMI 70/82/122/96/60/16) + the stored BIOS feature list.
    foreach (var cmd in new[] { 70, 82, 122, 96, 60, 16 })
        Console.WriteLine($"WMI {cmd,3} = 0x{br.GetInteger(cmd):X8}");
    var st = br.ReadAppSettingsStore();
    Console.WriteLine("feature list @0x700: " + Convert.ToHexString(st.AsSpan(0x700, 32)));
}

if (args.Length > 0 && args[0] == "audio")
{
    foreach (var f in new[] { 40.0, 60, 100, 150, 300, 1000, 4000 })
        Console.WriteLine($"filter {f,5} Hz: rms {OpenClevo.App.BassMeter.MeasureSine(f):0.0000} ({20 * Math.Log10(OpenClevo.App.BassMeter.MeasureSine(f) / 0.35355):+0.0;-0.0} dB)");
    // Bass meter only (no keyboard writes): reads loopback for 4 s and prints a coarse bass level timeline.
    using var m = new OpenClevo.App.BassMeter();
    var vals = new List<double>();
    var dev = new NAudio.CoreAudioApi.MMDeviceEnumerator().GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
    var total = new List<float>();
    for (int i = 0; i < 80; i++) { vals.Add(m.Read()); total.Add(dev.AudioMeterInformation.MasterPeakValue); Thread.Sleep(50); }
    Console.WriteLine($"device: {dev.FriendlyName}, total peak max {total.Max():0.000} avg {total.Average():0.000}");
    Console.WriteLine($"capturing: {m.IsCapturing}, format: {m.Format}");
    Console.WriteLine($"bass over 4 s: min {vals.Min():0.0000} max {vals.Max():0.0000} avg {vals.Average():0.0000}");
    double mx = Math.Max(vals.Max(), 1e-9);
    Console.WriteLine("timeline: " + new string(vals.Select(v => " .:-=+*#%@"[(int)Math.Round(v / mx * 9)]).ToArray()));
}

if (args.Length > 0 && args[0] == "latency")
{
    // Read-only timing of the _DSM round trip (same IOCTL path the keyboard color writes use).
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 100; i++) br.Call(DsmFn.HardwareMonitor);
    Console.WriteLine($"_DSM 12 x100: {sw.Elapsed.TotalMilliseconds / 100:0.00} ms per call");
}

if (args.Length > 0 && args[0] == "kb")
{
    var st = br.ReadAppSettingsStore();
    Console.WriteLine("AppSettings page 2 [0x200..0x300]:");
    for (int o = 0x200; o < 0x300; o += 16) Console.WriteLine($"  {o:X3}: {Convert.ToHexString(st.AsSpan(o, 16))}");
    var p13 = br.GetBuffer(DsmFn.FanCurveRead);
    Console.WriteLine($"pkg13 [0..16]: {Convert.ToHexString(p13.AsSpan(0, 16))}  [44..54]: {Convert.ToHexString(p13.AsSpan(44, 10))}");
}

if (args.Length > 0 && args[0] == "features")
{
    // Read-only sub-operations of _DSM 4 that FnKey/CC40 use to detect features.
    foreach (var (sub, name, extra) in new (byte, string, byte[])[] {
        (21, "GPU mode (1 iGPU, 2 dGPU, 3 MSHybrid, 4 Dynamic) + support bits", []),
        (30, "FlexiCharger status [0]=on [1]=start% [2]=stop%", []),
        (13, "Battery saver status", [0, 0]),
    })
    {
        var r = br.Call(DsmFn.FanTable, [sub, .. extra]);
        Console.WriteLine($"sub {sub,2} ({name}): count={r.Count} int={r.Integer} buf={(r.Buffer == null ? "-" : Convert.ToHexString(r.Buffer.AsSpan(0, 8)))}");
    }
    var st = br.ReadAppSettingsStore();
    Console.WriteLine("AppSettings 0x700 (capability bitmap saved by FnKey from _DSM 4 sub 8):");
    for (int o = 0x700; o < 0x720; o += 16) Console.WriteLine($"  {o:X3}: {Convert.ToHexString(st.AsSpan(o, 16))}");
    var p17 = br.GetBuffer(DsmFn.ProductInfo);
    Console.WriteLine($"pkg 17 [208]=StopChargeLimit {p17[208]}, [209]=StopDischargeLimit {p17[209]}");
    Console.WriteLine("pkg 17 non-zero bytes: " + string.Join(" ", Enumerable.Range(0, 256).Where(i => p17[i] != 0).Select(i => $"[{i}]={p17[i]}")));
}

if (args.Length > 0 && args[0] == "fan-table")
{
    // Same read CC40 performs at startup: _DSM 4 with data[0] = 40 (fan 1+2 table).
    var p13 = br.GetBuffer(DsmFn.FanCurveRead);
    Console.WriteLine("pkg 13:");
    for (int o = 0; o < 64; o += 16) Console.WriteLine($"  {o:X3}: {Convert.ToHexString(p13.AsSpan(o, 16))}");
    var st = br.ReadAppSettingsStore();
    Console.WriteLine("AppSettings page 4 [0..0x50]:");
    for (int o = 0; o < 0x50; o += 16) Console.WriteLine($"  {o:X3}: {Convert.ToHexString(st.AsSpan(0x400 + o, 16))}");
    var r = br.Call(DsmFn.FanTable, [40]);
    Console.WriteLine($"cmd 4/40: count={r.Count} int={r.Integer} buflen={r.Buffer?.Length}");
    if (r.Buffer != null)
        for (int o = 0; o < r.Buffer.Length; o += 16)
            Console.WriteLine($"  {o:X3}: {Convert.ToHexString(r.Buffer.AsSpan(o, Math.Min(16, r.Buffer.Length - o)))}");
    PrintState(br, "after");
}

if (args.Length > 0 && args[0] == "fan-restore")
{
    int fanMode = br.GetSavedFanMode();
    if (fanMode != 6) { Console.WriteLine($"Saved fan mode is {fanMode}, not 6; aborting."); return; }
    Console.WriteLine($"Sending _DSM 0x79 sub 0x{FeatureSub.FanMode:X2} value {fanMode} ...");
    Console.WriteLine($"Result int={br.SetFeature(FeatureSub.FanMode, (uint)fanMode)}");
    for (int i = 1; i <= 5; i++) { Thread.Sleep(3000); PrintState(br, $"after +{i * 3}s"); }
}

if (args.Length > 0 && args[0] == "power-rewrite")
{
    var mode = br.GetSavedPowerMode();
    if (mode != PowerMode.Performance) { Console.WriteLine($"Saved mode is {mode}, not Performance; aborting (test only covers re-writing Performance)."); return; }
    uint value = (uint)mode | br.PerformanceFlags();
    Console.WriteLine($"Sending _DSM 0x79 sub 0x{FeatureSub.PowerMode:X2} value 0x{value:X2} ...");
    var r = br.Call(DsmFn.SetFeature, [(byte)value, (byte)(value >> 8), (byte)(value >> 16), FeatureSub.PowerMode]);
    Console.WriteLine($"Result: sig=0x{r.Signature:X8} count={r.Count} int={r.Integer} buf={(r.Buffer == null ? "-" : Convert.ToHexString(r.Buffer.AsSpan(0, Math.Min(8, r.Buffer.Length))))}");
    for (int i = 1; i <= 3; i++) { Thread.Sleep(2000); PrintState(br, $"after +{i * 2}s"); }
}

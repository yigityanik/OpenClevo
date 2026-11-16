using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using OpenClevo.Core;

namespace OpenClevo.App;

/// <summary>
/// Turns the NVIDIA GPU off / on the way Clevo's Control Center does it in power saving:
///   off: disable the "NVIDIA Platform Controllers and Framework" device (ACPI\NVDA0820), then _DSM 4 [34, 0];
///   on:  _DSM 4 [34, 1], enable that device again, then clear "Capabilities" on the NVIDIA PCI devices.
/// Changing a device needs administrator rights. Instead of asking every time, OpenClevo does a one-time setup
/// (one Windows permission prompt): it copies itself to Program Files (writable only by administrators) and registers
/// two on-demand tasks, "OpenClevoNvidiaOff" and "OpenClevoNvidiaOn", that run that copy with the single argument
/// --nvidia=off / --nvidia=on at highest privileges. Afterwards the app just starts the task: no prompt. The tasks can
/// do nothing except switch the NVIDIA GPU. Reading the state never needs rights.
/// </summary>
static class NvidiaPower
{
    const string AppKey = @"Software\OpenClevo";
    const string NpcfEnum = @"SYSTEM\CurrentControlSet\Enum\ACPI\NVDA0820";
    const int CmProbDisabled = 22;
    const uint DnHasProblem = 0x400;
    const string TaskOff = "OpenClevoNvidiaOff", TaskOn = "OpenClevoNvidiaOn";

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Disable_DevNode(uint devInst, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Enable_DevNode(uint devInst, uint flags);

    static string? _id;

    /// <summary>Device instance id of the NVIDIA platform controller on this machine (null when there is none).</summary>
    static string? InstanceId()
    {
        if (_id != null) return _id;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(NpcfEnum);
            var sub = k?.GetSubKeyNames().FirstOrDefault();
            return _id = sub == null ? null : @"ACPI\NVDA0820\" + sub;
        }
        catch { return null; }
    }

    static bool Locate(out uint inst)
    {
        inst = 0;
        return InstanceId() is { } id && CM_Locate_DevNodeW(out inst, id, 0) == 0;
    }

    /// <summary>true = on, false = turned off, null = not supported here.</summary>
    public static bool? IsOn
    {
        get
        {
            if (!Locate(out uint inst)) return null;
            if (CM_Get_DevNode_Status(out uint status, out uint problem, inst, 0) != 0) return null;
            return !((status & DnHasProblem) != 0 && problem == CmProbDisabled);
        }
    }

    /// <summary>Remembers that power saving (not the user's own switch) turned the GPU off, so leaving it turns it back on.</summary>
    public static bool OffByPowerSaving
    {
        get => Registry.CurrentUser.OpenSubKey(AppKey)?.GetValue("NvidiaOffByPowerSaving") as int? == 1;
        set { using var k = Registry.CurrentUser.CreateSubKey(AppKey); k.SetValue("NvidiaOffByPowerSaving", value ? 1 : 0, RegistryValueKind.DWord); }
    }

    // ------------------------------------------------------------------ one-time helper setup

    static string HelperDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenClevo");
    static string ExeName => Path.GetFileName(Environment.ProcessPath!);
    static string DllName => Path.ChangeExtension(ExeName, ".dll");
    /// <summary>The file that tells whether the installed copy is current: the dll, or the exe itself in the single-file build (no dll beside it).</summary>
    static string VersionFile => File.Exists(Path.Combine(AppContext.BaseDirectory, DllName)) ? DllName : ExeName;

    static int Run(string file, string args, int timeoutMs = 15000)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true });
        return p != null && p.WaitForExit(timeoutMs) ? p.ExitCode : -1;
    }

    static bool TaskExists(string name) => Run("schtasks.exe", $"/query /tn {name}") == 0;

    /// <summary>The installed copy is current and both tasks exist.</summary>
    public static bool HelperReady
    {
        get
        {
            try
            {
                var installed = new FileInfo(Path.Combine(HelperDir, VersionFile));
                var mine = new FileInfo(Path.Combine(AppContext.BaseDirectory, VersionFile));
                if (!installed.Exists || !mine.Exists) return false;
                if (installed.Length != mine.Length || installed.LastWriteTimeUtc != mine.LastWriteTimeUtc) return false;
                return TaskExists(TaskOff) && TaskExists(TaskOn);
            }
            catch { return false; }
        }
    }

    /// <summary>Starts an elevated copy of this exe to do the setup. One Windows permission prompt; false if declined.</summary>
    public static Task<bool> InstallHelperAsync() => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--install-helper={WindowsIdentity.GetCurrent().User!.Value}") { UseShellExecute = true, Verb = "runas" };
            using var p = Process.Start(psi);
            return p != null && p.WaitForExit(60000) && p.ExitCode == 0 && HelperReady;
        }
        catch (Win32Exception) { return false; }   // permission prompt declined
    });

    /// <summary>Runs inside the elevated setup process: copy the app, register the two tasks. Returns the exit code.</summary>
    public static int InstallHelperHere(string userSid)
    {
        try
        {
            var src = AppContext.BaseDirectory.TrimEnd('\\');
            Directory.CreateDirectory(HelperDir);
            bool same = string.Equals(Path.GetFullPath(src), Path.GetFullPath(HelperDir), StringComparison.OrdinalIgnoreCase);
            // already running from the install folder (the setup program copied it): only register the tasks
            foreach (var f in same ? [] : Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.Combine(HelperDir, Path.GetRelativePath(src, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, true);
            }
            foreach (var (name, arg) in new[] { (TaskOff, "--nvidia=off"), (TaskOn, "--nvidia=on") })
            {
                var xml = $"""
                    <?xml version="1.0" encoding="UTF-16"?>
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <RegistrationInfo><Description>OpenClevo: switch the NVIDIA GPU ({arg})</Description></RegistrationInfo>
                      <Principals><Principal id="Author"><UserId>{userSid}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
                      <Settings>
                        <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                        <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                        <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                        <AllowStartOnDemand>true</AllowStartOnDemand>
                        <Enabled>true</Enabled>
                        <Hidden>true</Hidden>
                        <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
                      </Settings>
                      <Actions Context="Author"><Exec><Command>{Path.Combine(HelperDir, ExeName)}</Command><Arguments>{arg}</Arguments></Exec></Actions>
                    </Task>
                    """;
                var tmp = Path.Combine(Path.GetTempPath(), $"openclevo-{Guid.NewGuid():N}.xml");
                File.WriteAllText(tmp, xml, System.Text.Encoding.Unicode);
                try { if (Run("schtasks.exe", $"/create /tn {name} /xml \"{tmp}\" /f") != 0) return 3; }
                finally { File.Delete(tmp); }
            }
            return 0;
        }
        catch { return 4; }
    }

    // ------------------------------------------------------------------ switching

    /// <summary>Starts the matching task (no prompt) and waits until the device reaches the wanted state.</summary>
    public static Task<bool> SetAsync(bool on) => Task.Run(() =>
    {
        try
        {
            if (Run("schtasks.exe", $"/run /tn {(on ? TaskOn : TaskOff)}") != 0) return false;
            for (int i = 0; i < 80; i++)   // up to 20 s
            {
                if (IsOn == on) return true;
                Thread.Sleep(250);
            }
            return false;
        }
        catch { return false; }
    });

    /// <summary>The work itself; runs inside the privileged task. Returns the process exit code.</summary>
    public static int ApplyHere(bool on)
    {
        if (!Locate(out uint inst)) return 2;
        try
        {
            using var br = AcpiBridge.Open();
            if (on)
            {
                br.Call(DsmFn.FanTable, [34, 1]);
                Thread.Sleep(100);
                int r = CM_Enable_DevNode(inst, 0);
                Thread.Sleep(100);
                ClearNvidiaCapabilities();
                return r == 0 ? 0 : 3;
            }
            int rc = -1;
            for (int i = 0; i < 3 && rc != 0; i++) { rc = CM_Disable_DevNode(inst, 0); Thread.Sleep(100); }
            br.Call(DsmFn.FanTable, [34, 0]);
            return rc == 0 ? 0 : 3;
        }
        catch { return 4; }
    }

    static void ClearNvidiaCapabilities()
    {
        try
        {
            using var pci = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pci == null) return;
            foreach (var dev in pci.GetSubKeyNames().Where(n => n.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)))
            {
                using var d = pci.OpenSubKey(dev);
                foreach (var inst in d?.GetSubKeyNames() ?? [])
                {
                    try { using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\PCI\{dev}\{inst}", true); k?.SetValue("Capabilities", 0, RegistryValueKind.DWord); }
                    catch { }   // best effort, as in Clevo's tool
                }
            }
        }
        catch { }
    }
}

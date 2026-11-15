using OpenClevo.Localization;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;

namespace OpenClevo.Setup;

/// <summary>
/// What the setup does, in order: stop a running copy, unpack the app into Program Files, register the NVIDIA helper
/// tasks (the app's own <c>--install-helper</c> mode, so the task definitions live in one place), then the optional
/// startup entry, tray setting, shortcuts and launch.
/// </summary>
static class Installer
{
    public const string AppExe = "OpenClevo.exe";
    const string AppKey = @"Software\OpenClevo";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string TargetDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenClevo");
    public static string TargetExe => Path.Combine(TargetDir, AppExe);

    public static void Run(bool startup, bool tray, bool desktop, Action<string, double> progress)
    {
        progress(Loc.T("setup.progress_stop"), 0.05);
        foreach (var p in Process.GetProcessesByName("OpenClevo").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }

        progress(Loc.T("setup.progress_copy"), 0.15);
        Directory.CreateDirectory(TargetDir);
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.zip")
               ?? throw new InvalidOperationException(Loc.T("setup.err_payload")))
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
        {
            int n = 0, total = zip.Entries.Count;
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;   // directory entry
                var dest = Path.Combine(TargetDir, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                progress(Loc.T("setup.progress_copy"), 0.15 + 0.5 * ++n / total);
            }
        }

        // this program doubles as the uninstaller (started with --uninstall from Apps & features)
        // the uninstaller is this program under another name: it needs its own dll and config next to it
        var self = Environment.ProcessPath!;
        var selfDir = Path.GetDirectoryName(self)!;
        var selfName = Path.GetFileNameWithoutExtension(self);
        File.Copy(self, UninstallerPath, overwrite: true);
        foreach (var ext in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        {
            var from = Path.Combine(selfDir, selfName + ext);
            if (File.Exists(from)) File.Copy(from, Path.Combine(TargetDir, selfName + ext), overwrite: true);
        }

        progress(Loc.T("setup.progress_tasks"), 0.7);
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException(Loc.T("setup.err_sid"));
        using (var helper = Process.Start(new ProcessStartInfo(TargetExe, "--install-helper=" + sid) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (helper == null || !helper.WaitForExit(60000) || helper.ExitCode != 0)
                throw new InvalidOperationException(Loc.F("setup.err_tasks", helper?.ExitCode.ToString() ?? Loc.T("common.none")));
        }

        progress(Loc.T("setup.progress_settings"), 0.8);
        using (var k = Registry.CurrentUser.CreateSubKey(AppKey))
            k.SetValue("CloseToTray", tray ? 1 : 0, RegistryValueKind.DWord);
        Loc.SavedLanguage = Loc.Current;   // the language picked on the welcome page
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (startup) run.SetValue("OpenClevo", $"\"{TargetExe}\" --tray");
            else run.DeleteValue("OpenClevo", throwOnMissingValue: false);
        }

        progress(Loc.T("setup.progress_shortcuts"), 0.9);
        CreateShortcut(StartMenuShortcut);
        if (desktop) CreateShortcut(DesktopShortcut);

        progress(Loc.T("setup.progress_register"), 0.95);
        RegisterUninstall();
        progress(Loc.T("common.ok"), 1.0);
    }

    public static string UninstallerPath => Path.Combine(TargetDir, "Uninstall.exe");
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "OpenClevo.lnk");
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenClevo.lnk");
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OpenClevo";

    static void RegisterUninstall()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "OpenClevo");
        k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        k.SetValue("Publisher", "OpenClevo");
        k.SetValue("InstallLocation", TargetDir);
        k.SetValue("DisplayIcon", TargetExe + ",0");
        k.SetValue("UninstallString", $"\"{UninstallerPath}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Removes everything the setup added: stops the app, turns the NVIDIA GPU back on if it is off, deletes the helper
    /// tasks, startup entry, shortcuts and the Apps &amp; features entry, optionally the settings, then the folder itself
    /// (after this program has exited: a detached cmd waits and deletes it).
    /// </summary>
    public static void Uninstall(bool removeSettings, Action<string, double> progress)
    {
        progress(Loc.T("setup.uninst_stop"), 0.05);
        foreach (var p in Process.GetProcessesByName("OpenClevo").Where(p => p.Id != Environment.ProcessId))
        {
            try { p.Kill(true); p.WaitForExit(5000); } catch { }
        }

        progress(Loc.T("setup.uninst_gpu"), 0.25);
        Schtasks("/run /tn OpenClevoNvidiaOn");   // no-op when the GPU is already on
        Thread.Sleep(3000);

        progress(Loc.T("setup.uninst_tasks"), 0.45);
        Schtasks("/delete /tn OpenClevoNvidiaOff /f");
        Schtasks("/delete /tn OpenClevoNvidiaOn /f");

        progress(Loc.T("setup.uninst_shortcuts"), 0.6);
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            Log("startup key opened: " + (run != null));
            if (run?.GetValue("OpenClevo") != null) { run.DeleteValue("OpenClevo"); Log("startup value removed"); }
            else Log("startup value not present");
        }
        TryDelete(StartMenuShortcut);
        TryDelete(DesktopShortcut);

        progress(Loc.T("setup.uninst_registry"), 0.75);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        if (removeSettings) Registry.CurrentUser.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false);

        progress(Loc.T("setup.uninst_files"), 0.9);
        // cmd outlives this process, waits a moment (this program is still running), then removes the folder
        var bat = Path.Combine(Path.GetTempPath(), "openclevo-cleanup.cmd");
        File.WriteAllText(bat, "@echo off\r\nset /a n=0\r\n:loop\r\nrmdir /s /q \"" + TargetDir + "\" 2>nul\r\nif not exist \"" + TargetDir + "\" goto done\r\nset /a n+=1\r\nif %n% gtr 90 goto done\r\nping -n 2 127.0.0.1 >nul\r\ngoto loop\r\n:done\r\ndel \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"") { UseShellExecute = false, CreateNoWindow = true });
        Log("cleanup started for " + TargetDir);
        progress(Loc.T("setup.uninstalled"), 1.0);
    }

    static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "OpenClevo-uninstall.log"), DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); }
        catch { }
    }

    static void Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(20000);
        }
        catch { }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Starts the app as the normal user (through Explorer), so it does not inherit the setup's administrator rights.</summary>
    public static void LaunchAsUser() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TargetExe}\"") { UseShellExecute = false });

    static void CreateShortcut(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = TargetExe;
        link.WorkingDirectory = TargetDir;
        link.Description = "OpenClevo";
        link.IconLocation = TargetExe + ",0";
        link.Save();
    }
}

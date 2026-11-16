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
/// What the setup does, in order: stop a running copy, unpack the app into Program Files, install the Acpi Bridge driver,
/// register the NVIDIA helper tasks (the app's own <c>--install-helper</c> mode, so the task definitions live in one place),
/// then the optional startup entry, tray setting, shortcuts and launch. Removal is done by the installed app
/// (<c>OpenClevo.exe --uninstall</c>, see the app's Uninstaller), so there is no second copy of the .NET runtime on disk.
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
                var name = entry.FullName.Replace('\\', '/');     // Windows PowerShell's Compress-Archive may write backslashes
                if (name.EndsWith('/')) continue;                 // directory entry
                var dest = Path.Combine(TargetDir, name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                progress(Loc.T("setup.progress_copy"), 0.15 + 0.5 * ++n / total);
            }
        }

        // files of earlier installs: the app is one self-contained exe now and removes itself (no Uninstall.exe copy)
        foreach (var f in new[] { "Uninstall.exe", "OpenClevo.dll", "OpenClevo.deps.json", "OpenClevo.runtimeconfig.json", "OpenClevo.Core.dll", "NAudio.Core.dll", "NAudio.Wasapi.dll" })
            TryDelete(Path.Combine(TargetDir, f));
        foreach (var f in Directory.GetFiles(TargetDir, "OpenClevo-Setup.*")) TryDelete(f);

        progress(Loc.T("setup.progress_driver"), 0.67);
        InstallDriver();

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

    /// <summary>
    /// Installs the bundled Acpi Bridge driver (ACPI\CLV0001, signed by Microsoft's hardware program) so the app works
    /// without Clevo Control Center. Windows keeps the newer of two versions, so an existing install is left alone.
    /// A failure is logged but does not stop the setup: the app shows "driver not reachable" if it really is missing.
    /// The driver stays installed on uninstall, since other Clevo software may use it.
    /// </summary>
    static void InstallDriver()
    {
        var inf = Path.Combine(TargetDir, "Drivers", "AcpiBridge", "AcpiBridge.inf");
        if (!File.Exists(inf)) { Log("driver package not in payload, skipped"); return; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("pnputil.exe", $"/add-driver \"{inf}\" /install") { UseShellExecute = false, CreateNoWindow = true });
            if (p == null || !p.WaitForExit(120000)) { Log("pnputil did not finish"); return; }
            Log("pnputil exit code " + p.ExitCode + (p.ExitCode is 0 or 3010 ? " (ok)" : " (failed)"));
        }
        catch (Exception ex) { Log("driver install failed: " + ex.Message); }
    }

    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "OpenClevo.lnk");
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenClevo.lnk");
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OpenClevo";

    static void RegisterUninstall()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "OpenClevo");
        k.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
        k.SetValue("Publisher", "Yiğit Emre Yanık");
        k.SetValue("InstallLocation", TargetDir);
        k.SetValue("DisplayIcon", TargetExe + ",0");
        k.SetValue("UninstallString", $"\"{TargetExe}\" --uninstall");
        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    static void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "OpenClevo-setup.log"), DateTime.Now.ToString("HH:mm:ss") + "  " + line + Environment.NewLine); }
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

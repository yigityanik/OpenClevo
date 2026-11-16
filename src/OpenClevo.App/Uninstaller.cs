using OpenClevo.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace OpenClevo.App;

/// <summary>
/// Removes everything the setup added: stops the app, turns the NVIDIA GPU back on if it is off, deletes the helper
/// tasks, startup entry, shortcuts and the Apps &amp; features entry, optionally the settings, then the install folder
/// itself (after this program has exited: a detached cmd waits and deletes it). The app removes itself, so the install
/// folder holds a single exe (with the .NET runtime inside) and no second copy of it for an uninstall program.
/// </summary>
static class Uninstaller
{
    const string AppKey = @"Software\OpenClevo";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OpenClevo";

    static string TargetDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenClevo");
    static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "OpenClevo.lnk");
    static string DesktopShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "OpenClevo.lnk");

    /// <summary>Apps &amp; features starts "OpenClevo.exe --uninstall" without administrator rights; removal needs them, so start an elevated copy. Returns the exit code.</summary>
    public static int Elevate()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--uninstall-elevated") { UseShellExecute = true, Verb = "runas" });
            return 0;
        }
        catch (Win32Exception) { return 1; }   // permission prompt declined
    }

    public static void Run(bool removeSettings, Action<string, double> progress)
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
}

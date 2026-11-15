using System.IO;
using System.Windows;

namespace OpenClevo.Setup;

public partial class App : Application
{
    // "--uninstall" (from Apps & features, or the copy in the install folder) opens the removal window
    protected override void OnStartup(StartupEventArgs e)
    {
        Localization.Loc.Init();
        base.OnStartup(e);
        // a copy named Uninstall.exe (double-clicked in the install folder) also opens the removal window
        bool uninstall = e.Args.Contains("--uninstall") || Path.GetFileName(Environment.ProcessPath) == "Uninstall.exe";
        Window w = uninstall ? new UninstallWindow() : new MainWindow();
        MainWindow = w;
        w.Show();
    }
}
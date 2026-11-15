using System.Windows;

namespace OpenClevo.Setup;

public partial class App : Application
{
    // "--uninstall" (from Apps & features, or the copy in the install folder) opens the removal window
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Window w = e.Args.Contains("--uninstall") ? new UninstallWindow() : new MainWindow();
        MainWindow = w;
        w.Show();
    }
}
using System.Windows;

namespace OpenClevo.Setup;

public partial class App : Application
{
    // the installer always starts in English; removal is done by the installed app itself ("OpenClevo.exe --uninstall")
    protected override void OnStartup(StartupEventArgs e)
    {
        Localization.Loc.Init();
        base.OnStartup(e);
        Localization.Loc.Set("en");
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }
}

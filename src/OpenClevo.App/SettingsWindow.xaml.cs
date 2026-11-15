using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using OpenClevo.Localization;

namespace OpenClevo.App;

/// <summary>Settings: language, start with Windows, minimise to tray on close.</summary>
public partial class SettingsWindow : Window
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly string _startChoice;

    public SettingsWindow()
    {
        InitializeComponent();
        _startChoice = Loc.SavedLanguage ?? Loc.Current;
        FlowDirection = Loc.Flow;
        // one button per language, each showing the language's own name; they wrap onto more rows as languages are added
        foreach (var code in Loc.Languages)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Segment"), GroupName = "lang", Tag = code,
                Content = Loc.NativeName(code), Margin = new Thickness(0, 0, 6, 6), IsChecked = code == _startChoice,
            };
            rb.Click += Lang_Click;
            LangBar.Children.Add(rb);
        }

        StartupToggle.IsChecked = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue("OpenClevo") != null;
        TrayToggle.IsChecked = MainWindow.CloseToTray;
    }

    void Lang_Click(object s, RoutedEventArgs e)
    {
        string choice = (string)((RadioButton)s).Tag;
        Loc.SavedLanguage = choice;
        if (choice == _startChoice) return;

        // the window text is set once at start-up, so a language change takes effect after a restart
        if (Dialog.Ask(this, Loc.T("settings.lang_restart_title"), Loc.T("settings.lang_restart_body"),
                Loc.T("settings.lang_restart_btn"), Loc.T("common.later")) == 0)
            Restart();
    }

    /// <summary>Starts a new copy after this one has exited (this copy still holds the single-instance lock for a moment).</summary>
    static void Restart()
    {
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 2 127.0.0.1 >nul & start \"\" \"{Environment.ProcessPath}\"")
        { UseShellExecute = false, CreateNoWindow = true });
        Application.Current.Shutdown();
    }

    void Startup_Click(object s, RoutedEventArgs e)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (StartupToggle.IsChecked == true) k.SetValue("OpenClevo", $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue("OpenClevo", false);
    }

    void Tray_Click(object s, RoutedEventArgs e) => MainWindow.CloseToTray = TrayToggle.IsChecked == true;

    void Close_Click(object s, RoutedEventArgs e) => Close();

    void Drag(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Button) DragMove();
    }
}

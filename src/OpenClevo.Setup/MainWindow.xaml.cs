using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace OpenClevo.Setup;

public partial class MainWindow : Window
{
    int _page;                        // 0 welcome · 1 options · 2 install · 3 done
    readonly UIElement[] _pages;

    public MainWindow()
    {
        InitializeComponent();
        _pages = [PageWelcome, PageOptions, PageInstall, PageDone];
        Show(0);
    }

    void Show(int page)
    {
        _page = page;
        for (int i = 0; i < _pages.Length; i++) _pages[i].Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
        BtnBack.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnCancel.Visibility = page is 0 or 1 ? Visibility.Visible : Visibility.Collapsed;
        BtnNext.Content = page switch { 0 => "İleri", 1 => "Kur", 3 => "Bitir", _ => "" };
        BtnNext.IsEnabled = page != 2;
    }

    void Next_Click(object s, RoutedEventArgs e)
    {
        switch (_page)
        {
            case 0: Show(1); break;
            case 1: StartInstall(); break;
            case 3: Finish(); break;
        }
    }

    void Back_Click(object s, RoutedEventArgs e) { if (_page == 1) Show(0); }

    async void StartInstall()
    {
        Show(2);
        bool startup = OptStartup.IsChecked == true, tray = OptTray.IsChecked == true, desktop = OptDesktop.IsChecked == true;
        string? error = null;
        try
        {
            await Task.Run(() => Installer.Run(startup, tray, desktop, (text, frac) =>
                Dispatcher.BeginInvoke(() =>
                {
                    StepText.Text = text;
                    Bar.Width = (PageInstall.ActualWidth > 0 ? PageInstall.ActualWidth : 456) * Math.Clamp(frac, 0, 1);
                })));
        }
        catch (Exception ex) { error = ex.Message; }

        if (error != null)
        {
            StepText.Text = error;
            BtnCancel.Visibility = Visibility.Visible;
            BtnCancel.Content = "Kapat";
            return;
        }
        PathText.Text = Installer.TargetDir;
        Show(3);
    }

    void Finish()
    {
        if (OptLaunch.IsChecked == true) Installer.LaunchAsUser();
        Close();
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();

    // drag the window from empty areas only; a press on a button must not move the window
    void Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        DragMove();
    }
}

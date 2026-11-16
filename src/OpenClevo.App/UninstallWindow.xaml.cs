using System.Windows;
using System.Windows.Input;
using OpenClevo.Localization;

namespace OpenClevo.App;

/// <summary>Removal window of the installed app (<c>OpenClevo.exe --uninstall</c>): confirm, progress, done.</summary>
public partial class UninstallWindow : Window
{
    public UninstallWindow()
    {
        InitializeComponent();
        FlowDirection = Loc.Flow;
    }

    async void Remove_Click(object s, RoutedEventArgs e)
    {
        bool settings = OptSettings.IsChecked == true;
        PageAsk.Visibility = Visibility.Collapsed;
        PageWork.Visibility = Visibility.Visible;
        BtnNext.IsEnabled = false;
        BtnCancel.IsEnabled = false;
        BtnNext.Visibility = Visibility.Collapsed;
        string? error = null;
        try
        {
            await Task.Run(() => Uninstaller.Run(settings, (text, frac) =>
                Dispatcher.BeginInvoke(() =>
                {
                    StepText.Text = text;
                    Bar.Width = 440 * Math.Clamp(frac, 0, 1);
                })));
        }
        catch (Exception ex) { error = ex.Message; }

        if (error != null)
        {
            StepText.Text = error;
            BtnCancel.IsEnabled = true;
            BtnCancel.Content = Loc.T("common.close");
            return;
        }
        PageWork.Visibility = Visibility.Collapsed;
        PageDone.Visibility = Visibility.Visible;
        BtnCancel.Visibility = Visibility.Collapsed;
        BtnNext.Visibility = Visibility.Visible;
        BtnNext.Content = Loc.T("setup.finish_btn");
        BtnNext.IsEnabled = true;
        BtnNext.Click -= Remove_Click;
        BtnNext.Click += (_, _) => Close();
    }

    void Close_Click(object s, RoutedEventArgs e) => Close();

    void Drag(object s, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        for (DependencyObject? d = e.OriginalSource as DependencyObject; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase) return;
        DragMove();
    }
}

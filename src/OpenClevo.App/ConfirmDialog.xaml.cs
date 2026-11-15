using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace OpenClevo.App;

/// <summary>The app's own confirmation / message window: same surface, type and buttons as the rest of the UI.</summary>
public partial class ConfirmDialog : Window
{
    public int Result { get; private set; } = -1;

    ConfirmDialog(string title, string body, string[] buttons)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        BodyText.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        // first button is the main action: it sits on the right, the others to its left
        for (int i = buttons.Length - 1; i >= 0; i--)
        {
            int idx = i;
            var b = new Button
            {
                Content = buttons[i],
                Style = (Style)FindResource(i == 0 ? "PrimaryBtn" : "Btn"),
                Margin = new Thickness(i == buttons.Length - 1 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
            };
            b.Click += (_, _) => { Result = idx; Close(); };
            ButtonRow.Children.Add(b);
        }
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Result = -1; Close(); } };
    }

    void Drag(object s, MouseButtonEventArgs e) { if (e.OriginalSource is not Button) DragMove(); }

    /// <summary>Shows the dialog and returns the index of the clicked button (0 = main action), or -1 for Esc / closed.</summary>
    public static int Show(Window? owner, string title, string body, params string[] buttons)
    {
        var d = new ConfirmDialog(title, body, buttons);
        if (owner is { IsVisible: true }) d.Owner = owner; else d.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        d.ShowDialog();
        return d.Result;
    }
}

static class Dialog
{
    public static int Ask(Window? owner, string title, string body, params string[] buttons) => ConfirmDialog.Show(owner, title, body, buttons);
    public static void Info(Window? owner, string body, string title = "OpenClevo") => ConfirmDialog.Show(owner, title, body, "Tamam");
}

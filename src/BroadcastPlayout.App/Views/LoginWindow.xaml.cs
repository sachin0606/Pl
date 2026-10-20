using System.IO;
using System.Windows;
using System.Windows.Input;
using BroadcastPlayout.Services;

namespace BroadcastPlayout.Views;

public partial class LoginWindow : Window
{
    private readonly SecurityStore _store = new();
    private bool _submitting;

    public LoginWindow()
    {
        InitializeComponent();
        var first = Path.Combine(Path.GetDirectoryName(SecurityStore.DatabasePath)!, "FIRST-RUN-ADMIN.txt");
        InfoText.Text = File.Exists(first)
            ? "First run: bootstrap admin credentials are in " + first + ". Change the password after signing in."
            : "Sign in with an enabled Kashtrix operator account.";
        Loaded += (_, _) => { UsernameBox.Focus(); UsernameBox.SelectAll(); };
        WindowChromeActions.ApplyCleanBorder(this);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        if (_submitting) return;
        ErrorText.Text = string.Empty;
        _submitting = true;
        SignInButton.IsEnabled = false;
        LoginProgress.Visibility = Visibility.Visible;
        try
        {
            if (_store.VerifyPassword(UsernameBox.Text, PasswordBox.Password, out var principal) && principal is not null)
            {
                AppSession.Current = principal;
                if (RememberLoginCheck.IsChecked == true) _store.RememberSession(principal);
                else _store.ForgetRememberedSession(principal.Username);
                // Setting DialogResult closes the modal window.  Do not call Close() again: the
                // App bootstrap owns lifetime and immediately creates the Playout MainWindow.
                DialogResult = true;
                return;
            }

            ErrorText.Text = "Invalid username/password or account disabled.";
            PasswordBox.SelectAll();
            PasswordBox.Focus();
        }
        finally
        {
            if (DialogResult != true)
            {
                _submitting = false;
                SignInButton.IsEnabled = true;
                LoginProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SignIn_Click(sender, new RoutedEventArgs());
        }
    }
}

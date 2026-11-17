using System.Windows;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

public partial class RegisterAdminWindow : Window
{
    public RegisterAdminWindow()
    {
        InitializeComponent();
        TxtUser.Focus();
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        if (!string.Equals(TxtPassword.Password, TxtConfirm.Password, StringComparison.Ordinal))
        {
            TxtError.Text = "两次输入的密码不一致。";
            TxtConfirm.Clear();
            TxtConfirm.Focus();
            return;
        }

        if (!AuthService.TryRegisterFirstAdmin(TxtUser.Text, TxtPassword.Password, out var error))
        {
            TxtError.Text = error;
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

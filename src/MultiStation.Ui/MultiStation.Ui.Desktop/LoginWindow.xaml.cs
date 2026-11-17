using System.Windows;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

public partial class LoginWindow : Window
{
    private int _failedAttempts;

    public LoginWindow()
    {
        InitializeComponent();

        var license = LicenseService.Evaluate();
        TxtLicense.Text = $"{license.StateText} · {license.Message}";
        TxtUser.Focus();
    }

    private void OnLogin(object sender, RoutedEventArgs e)
    {
        if (_failedAttempts >= 5)
        {
            TxtError.Text = "连续失败次数过多，请重启软件后重试。";
            return;
        }

        var session = AuthService.Authenticate(TxtUser.Text, TxtPassword.Password);
        if (session is null)
        {
            _failedAttempts++;
            TxtError.Text = $"用户名或密码错误（已失败 {_failedAttempts} 次）。";
            TxtPassword.Clear();
            TxtPassword.Focus();
            return;
        }

        SessionContext.Current = session;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

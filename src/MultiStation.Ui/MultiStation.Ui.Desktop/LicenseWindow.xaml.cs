using System.Windows;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

public partial class LicenseWindow : Window
{
    public LicenseWindow(LicenseInfo license)
    {
        InitializeComponent();

        TxtDetail.Text = $"{license.StateText}：{license.Message}"
            + (license.ExpiresAt is null ? string.Empty : $"\n到期时间：{license.ExpiresAt:yyyy-MM-dd HH:mm}");

        if (license.State == LicenseState.Tampered)
        {
            TxtHint.Text = "检测到系统时间异常。请校准系统时间后重启软件；若仍锁定，请联系供应商。";
        }
        else
        {
            TxtHint.Text = $"请联系管理员续期：编辑 {LicenseService.SecurityDirectory}\\license.json 中的 expiresAt 后重启软件。";
        }
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

using System.Windows;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// 选择基础信息：由现场人员勾选需要使用的项（SN / CarrierID / 壳体码）。
/// 未勾选的项在主界面隐藏；SN / CarrierID 留空或为 - 时测试开始自动生成。
/// </summary>
public partial class BasicInfoWindow : Window
{
    public BasicInfoWindow(bool useSn, bool useCarrierId, bool useShellCode, string sn, string carrierId, string shellCode)
    {
        InitializeComponent();

        ChkUseSn.IsChecked = useSn;
        ChkUseCarrier.IsChecked = useCarrierId;
        ChkUseShellCode.IsChecked = useShellCode;
        TxtBasicSn.Text = sn;
        TxtBasicCarrier.Text = carrierId;
        TxtBasicShellCode.Text = shellCode;
        SyncEnabled();
    }

    public bool UseSn => ChkUseSn.IsChecked == true;

    public bool UseCarrierId => ChkUseCarrier.IsChecked == true;

    public bool UseShellCode => ChkUseShellCode.IsChecked == true;

    public string Sn => TxtBasicSn.Text.Trim();

    public string CarrierId => TxtBasicCarrier.Text.Trim();

    public string ShellCode => TxtBasicShellCode.Text.Trim();

    private void OnUseChanged(object sender, RoutedEventArgs e) => SyncEnabled();

    /// <summary>未勾选时对应输入框置灰，避免误以为填写内容会被采用。</summary>
    private void SyncEnabled()
    {
        TxtBasicSn.IsEnabled = UseSn;
        TxtBasicCarrier.IsEnabled = UseCarrierId;
        TxtBasicShellCode.IsEnabled = UseShellCode;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

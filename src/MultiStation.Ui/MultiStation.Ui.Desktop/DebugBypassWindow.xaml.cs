using System.Windows;
using System.Windows.Threading;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

/// <summary>调试用权限限时屏蔽对话框：仅管理员可开启，默认 15 分钟，最长 30 分钟。</summary>
public partial class DebugBypassWindow : Window
{
    private readonly DispatcherTimer _timer;

    public DebugBypassWindow()
    {
        InitializeComponent();
        UpdateState();

        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) =>
            {
                PermissionBypass.Refresh();
                UpdateState();
            },
            Dispatcher);
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void OnEnable(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtBypassMinutes.Text.Trim(), out var amount) || amount <= 0)
        {
            TxtBypassError.Text = "请输入大于 0 的整数时长。";
            return;
        }

        var unit = CmbBypassUnit.SelectedIndex switch
        {
            0 => TimeSpan.FromMinutes(1),
            1 => TimeSpan.FromHours(1),
            2 => TimeSpan.FromDays(1),
            3 => TimeSpan.FromDays(7),
            4 => TimeSpan.FromDays(30),
            _ => TimeSpan.FromMinutes(1)
        };

        var duration = unit * amount;
        if (duration > PermissionBypass.MaxDuration)
        {
            TxtBypassError.Text =
                $"屏蔽时长不能超过 {PermissionBypass.MaxDuration.TotalDays:F0} 天（月按 30 天折算），当前为 {duration.TotalDays:F0} 天。";
            return;
        }

        if (!PermissionBypass.Enable(duration, TxtBypassReason.Text, out var error))
        {
            TxtBypassError.Text = error;
            return;
        }

        TxtBypassError.Text = string.Empty;
        DialogResult = true;
    }

    private void OnDisable(object sender, RoutedEventArgs e)
    {
        PermissionBypass.Disable();
        DialogResult = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void UpdateState()
    {
        var text = PermissionBypass.StateText;
        TxtBypassState.Text = text is null
            ? "当前未屏蔽：权限校验按账号角色执行。"
            : $"{text}（原因：{PermissionBypass.Reason}，到期 {PermissionBypass.Until:MM-dd HH:mm:ss}）";
        BtnDisableBypass.IsEnabled = text is not null;
    }
}

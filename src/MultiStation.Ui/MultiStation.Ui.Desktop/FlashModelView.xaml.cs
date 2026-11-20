using System.Windows;
using System.Windows.Controls;
using MultiStation.Platform.Core.Production;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// ECU 烧录机种 UI 视图：与通用测试界面独立，加载烧录测试序列并显示烧录日志。
/// </summary>
public partial class FlashModelView : UserControl, IModelView
{
    private readonly SingleStationTestController _controller;
    private bool _closing;

    public FlashModelView(ModelProfile model)
    {
        InitializeComponent();

        TxtModelName.Text = $"机种：{model.Name}";
        var firmware = string.IsNullOrWhiteSpace(model.FirmwarePath) ? "未指定" : model.FirmwarePath;
        TxtModelInfo.Text =
            $"固件：{firmware}\n" +
            $"CAN {model.CanDeviceIndex}:{model.CanChannelIndex}（{model.CanMode}）· " +
            $"波特率 {model.BaudRate} / 数据 {model.DataBaudRate} · " +
            $"请求 0x{model.RequestId:X3} / 响应 0x{model.ResponseId:X3} · " +
            $"烧录地址 0x{model.MemoryAddress:X8}";

        _controller = new SingleStationTestController(FlashSequenceData.Rows);
        StepGrid.ItemsSource = _controller.Steps;
        FlashLogList.ItemsSource = _controller.Logs;
        _controller.Changed += UpdateControls;
        UpdateControls();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Operate))
        {
            return;
        }

        if (!ConfirmStart("手动开始"))
        {
            return;
        }

        _controller.Start();
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Operate))
        {
            return;
        }

        _controller.PauseOrResume();
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Operate))
        {
            return;
        }

        _controller.Stop();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Operate))
        {
            return;
        }

        _controller.NextStep();
    }

    /// <summary>人工确认（T-07）：烧录入口同样必须由现场人员确认。</summary>
    private bool ConfirmStart(string source)
    {
        var confirmed = MessageBox.Show(
            Window.GetWindow(this)!,
            $"即将开始 ECU 烧录（来源：{source}）。\n\n" +
            "请确认：ECU 已进入 Bootloader、烧录文件与机种参数正确、现场安全条件满足。\n\n是否开始？",
            $"人工确认 · {source}",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

        TxtFlashStatus.Text = confirmed
            ? $"烧录已开始（已通过人工确认，来源：{source}）"
            : $"烧录未开始：未通过人工确认（来源：{source}）";
        return confirmed;
    }

    private bool RequirePermission(PermissionKind permission)
    {
        if (SessionContext.Has(permission) || PermissionBypass.IsActive)
        {
            return true;
        }

        MessageBox.Show(
            Window.GetWindow(this)!,
            $"当前账号「{SessionContext.DisplayName}」无此操作权限（需要 {permission}）。",
            "权限不足",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    private void UpdateControls()
    {
        BtnFlashStart.IsEnabled = !_controller.IsRunning;
        BtnFlashPause.IsEnabled = _controller.IsRunning && !_controller.IsStepMode;
        BtnFlashStop.IsEnabled = _controller.IsRunning;
        BtnFlashNext.IsEnabled = !_controller.IsRunning || _controller.IsPaused;

        TxtTestState.Text = _controller.StateText;
        TxtProgress.Text = $"{CompletedCount()} / {_controller.Steps.Count}";
        TxtFlashStatus.Text =
            $"判定：{_controller.Verdict} · 通过 {_controller.PassCount} · 不良 {_controller.NgCount}";
    }

    private int CompletedCount() =>
        _controller.Steps.Count(step => step.Result is "PASS" or "NG");

    /// <summary>切换机种/关闭窗口前停机。</summary>
    public Task ShutdownAsync()
    {
        if (!_closing)
        {
            _closing = true;
            _controller.Stop();
        }

        return Task.CompletedTask;
    }

    /// <summary>释放资源：停止并释放测试控制器。</summary>
    public void Dispose()
    {
        if (!_closing)
        {
            _closing = true;
        }

        _controller.Dispose();
        GC.SuppressFinalize(this);
    }
}

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiStation.Instruments.Drivers.Itech;
using MultiStation.Platform.Core.Production;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// ECU 烧录机种 UI 视图：与通用测试界面独立，加载烧录测试序列并显示烧录日志。
/// </summary>
public partial class FlashModelView : UserControl, IModelView
{
    private readonly SingleStationTestController _controller;
    private readonly UdsStepExecutor? _udsExecutor;
    private readonly bool _isExternalSequence;
    private readonly bool _requiresSupplierSerial;
    private readonly string _recipe;
    private readonly string? _powerPort;
    private readonly double _powerVoltage;
    private readonly string _powerSerialSettings;
    private readonly int _powerChannel;
    private readonly string? _reportDirectory;
    private readonly string _reportFileName;
    private readonly string? _configurationError;
    private Task? _shutdownTask;
    private bool _closing;
    private bool _wasRunning;
    private bool _sessionEndPending;
    private DateTime _unitStartedAt;
    private bool _useCarrierId = true;
    private bool _useShellCode = true;

    public FlashModelView(ModelProfile model)
    {
        InitializeComponent();

        _recipe = model.Name;
        _powerPort = model.PowerSupplyPort;
        _powerVoltage = model.PowerVoltage;
        _powerSerialSettings = model.PowerSerialSettings;
        _powerChannel = model.PowerChannel;
        _reportDirectory = model.ReportDirectory;
        _reportFileName = model.ReportFileNamePattern;
        _isExternalSequence = !string.IsNullOrWhiteSpace(model.FlashSequencePath);
        TxtModelTitle.Text = model.Name;
        TxtModelName.Text = $"机种：{model.Name}";
        var firmware = string.IsNullOrWhiteSpace(model.FirmwarePath) ? "未指定" : model.FirmwarePath;
        var modelInfo =
            $"固件：{firmware}\n" +
            $"CAN {model.CanDeviceModel} {model.CanDeviceIndex}:{model.CanChannelIndex}（{model.CanMode}）· " +
            $"波特率 {model.BaudRate} / 数据 {model.DataBaudRate}";

        FlashSequenceDefinition sequence;
        if (string.IsNullOrWhiteSpace(model.FlashSequencePath))
        {
            sequence = new FlashSequenceDefinition(FlashSequenceData.Rows);
            modelInfo +=
                $" · 请求 0x{model.RequestId:X3} / 响应 0x{model.ResponseId:X3}\n" +
                $"烧录地址 0x{model.MemoryAddress:X8}";
        }
        else
        {
            try
            {
                sequence = FlashSequenceLoader.Load(model);
                _udsExecutor = new UdsStepExecutor(model);
            }
            catch (Exception ex)
            {
                sequence = new FlashSequenceDefinition([]);
                _configurationError = $"烧录配置加载失败：{ex.Message}";
            }

            modelInfo +=
                $"\n主 ECU：请求 0x{model.RequestId:X8} / 响应 0x{model.ResponseId:X8}" +
                $"（{FrameType(model.RequestId)}）" +
                $"\nCCU：请求 0x{model.CcuRequestId:X3} / 响应 0x{model.CcuResponseId:X3}" +
                $"（{FrameType(model.CcuRequestId)}）" +
                $"\n序列：{model.FlashSequencePath}";
        }

        TxtModelInfo.Text = modelInfo;
        _requiresSupplierSerial = sequence.RequiresSupplierSerial;
        // NP12/NP13 不显示顶部的机种/CAN 信息卡片
        PnlModelInfo.Visibility = _isExternalSequence ? Visibility.Collapsed : Visibility.Visible;
        TxtControlTitle.Text = _isExternalSequence ? "测试控制" : "控制与状态";
        TxtLogTitle.Text = _isExternalSequence ? "运行日志" : "烧录日志";
        BtnFlashStart.Content = _isExternalSequence ? "开始测试" : "开始烧录";
        if (_isExternalSequence)
        {
            var preferences = BasicInfoPreferences.Load();
            _useCarrierId = preferences.UseCarrierId;
            _useShellCode = preferences.UseShellCode;
            UseQuadrantLayout();
            ApplyBasicInfo(BasicUnitInfo.Initial(DateTime.Now, _recipe, SessionContext.DisplayName));
            RefreshBasicInfoState();
        }
        else
        {
            PnlControlCard.Visibility = Visibility.Visible;
        }

        _controller = new SingleStationTestController(sequence.Rows, _udsExecutor);
        StepGrid.ItemsSource = _controller.Steps;
        FlashLogList.ItemsSource = _controller.Logs;
        _controller.Changed += UpdateControls;
        UpdateControls();
    }

    private static string FrameType(int requestId) => requestId > 0x7FF ? "29 位扩展帧" : "11 位标准帧";

    /// <summary>NP12/NP13 四板块布局：基础信息、测试控制、测试结果、运行日志各占独立面板。</summary>
    private void UseQuadrantLayout()
    {
        PnlBasicInfoCard.Visibility = Visibility.Visible;
        PnlTestResultCard.Visibility = Visibility.Visible;
        PnlControlCard.Visibility = Visibility.Visible;

        // 测试控制移入右上板块，按钮改为竖向排列。
        Grid.SetRow(PnlControlCard, 0);
        Grid.SetColumn(PnlControlCard, 4);
        Grid.SetColumnSpan(PnlControlCard, 1);

        PnlControlButtons.Orientation = Orientation.Vertical;
        foreach (var button in new[] { BtnFlashStart, BtnFlashPause, BtnFlashStop, BtnFlashNext })
        {
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Margin = new Thickness(0, 0, 0, 8);
        }

        Grid.SetRow(TxtFlashStatus, 2);
        TxtFlashStatus.HorizontalAlignment = HorizontalAlignment.Left;
        TxtFlashStatus.Margin = new Thickness(2, 8, 2, 0);

        // 运行日志位于右下板块。
        Grid.SetRow(PnlLogCard, 2);
        Grid.SetRowSpan(PnlLogCard, 1);

        foreach (var card in new[] { PnlBasicInfoCard, PnlTestResultCard, PnlStepCard, PnlControlCard, PnlLogCard })
        {
            card.CornerRadius = new CornerRadius(0);
        }
    }

    /// <summary>新件开始只刷新 CarrierID 和配方信息；F18C 使用的 SN 始终保留人工输入。</summary>
    private void StartNewUnitInfo()
    {
        if (!_isExternalSequence)
        {
            return;
        }

        _unitStartedAt = DateTime.Now;
        var generated = BasicUnitInfo.ForNewUnit(DateTime.Now, _recipe, SessionContext.DisplayName);
        ApplyBasicInfo(generated with
        {
            Sn = TxtInfoSn.Text.Trim(),
            CarrierId = _useCarrierId
                ? KeepManual(TxtInfoCarrier.Text, generated.CarrierId)
                : TxtInfoCarrier.Text,
        });
    }

    private static string KeepManual(string current, string generated) =>
        string.IsNullOrWhiteSpace(current) || current.Trim() == "-" ? generated : current.Trim();

    private void OnSelectBasicInfo(object sender, RoutedEventArgs e)
    {
        var dialog = new BasicInfoWindow(
            useSn: true,
            _useCarrierId,
            _useShellCode,
            TxtInfoSn.Text,
            TxtInfoCarrier.Text,
            TxtInfoShellCode.Text)
        {
            Owner = Window.GetWindow(this),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _useCarrierId = dialog.UseCarrierId;
        _useShellCode = dialog.UseShellCode;
        // NP12/NP13 的 SN 始终启用，需随每次配置一起记住 CarrierID / 壳体码
        new BasicInfoPreferences
        {
            UseSn = true,
            UseCarrierId = _useCarrierId,
            UseShellCode = _useShellCode,
        }.Save();
        ApplyBasicInfoValues(dialog.Sn, dialog.CarrierId, dialog.ShellCode);
        RefreshBasicInfoState();
    }

    private void ApplyBasicInfoValues(string sn, string carrierId, string shellCode)
    {
        TxtInfoSn.Text = DisplayOrDash(sn);
        TxtInfoCarrier.Text = DisplayOrDash(carrierId);
        TxtInfoShellCode.Text = DisplayOrDash(shellCode);
    }

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;

    private void RefreshBasicInfoState()
    {
        PnlInfoSn.Visibility = Visibility.Visible;
        PnlInfoCarrier.Visibility = _useCarrierId ? Visibility.Visible : Visibility.Collapsed;
        PnlInfoShellCode.Visibility = _useShellCode ? Visibility.Visible : Visibility.Collapsed;

        var enabled = 1 + (_useCarrierId ? 1 : 0) + (_useShellCode ? 1 : 0);
        TxtBasicInfoState.Text = enabled == 3 ? "全部启用" : $"启用 {enabled}/3";
    }

    private void ApplyBasicInfo(BasicUnitInfo info)
    {
        TxtInfoSn.Text = info.Sn;
        TxtInfoCarrier.Text = info.CarrierId;
        TxtInfoRecipe.Text = info.Recipe;
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Operate))
        {
            return;
        }

        if (!TryPrepareRun() || !ConfirmStart("手动开始"))
        {
            return;
        }

        StartNewUnitInfo();
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
        PowerOff();
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Operate))
        {
            return;
        }

        if (!_controller.IsRunning)
        {
            if (!TryPrepareRun() || !ConfirmStart("单步执行"))
            {
                return;
            }

            StartNewUnitInfo();
        }

        _controller.NextStep();
    }

    /// <summary>停止或异常结束时兜底断电；正常结束时序列末步也会执行断电指令。</summary>
    private void PowerOff()
    {
        if (!_isExternalSequence || _powerVoltage <= 0 || string.IsNullOrWhiteSpace(_powerPort))
        {
            return;
        }

        try
        {
            using var supply = new It6322aPowerSupply(_powerPort, _powerSerialSettings);
            supply.Connect();
            supply.PowerOff(_powerChannel);
            TxtFlashStatus.Text = $"电源已断电（CH{_powerChannel}）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or TimeoutException)
        {
            TxtFlashStatus.Text = $"断电失败：{ex.Message}";
        }
    }

    private bool TryPrepareRun()
    {
        if (_configurationError is not null)
        {
            MessageBox.Show(
                Window.GetWindow(this)!,
                _configurationError,
                "烧录配置错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        if (_controller.IsCleaningUp)
        {
            TxtFlashStatus.Text = "正在释放 CAN，请等待释放完成后再开始下一件";
            return false;
        }

        if (!_requiresSupplierSerial)
        {
            return true;
        }

        try
        {
            _udsExecutor!.SetSupplierSerialAscii(TxtInfoSn.Text);
            return true;
        }
        catch (FormatException ex)
        {
            TxtFlashStatus.Text = ex.Message;
            TxtInfoSn.Focus();
            TxtInfoSn.SelectAll();
            MessageBox.Show(
                Window.GetWindow(this)!,
                ex.Message,
                "SN 输入错误",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
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
        var canStart = !_closing && _configurationError is null
            && !_controller.IsRunning && !_controller.IsCleaningUp;
        BtnFlashStart.IsEnabled = canStart;
        BtnFlashPause.IsEnabled = _controller.IsRunning && !_controller.IsStepMode;
        BtnFlashPause.Content = _controller.IsPaused ? "继续" : "暂停";
        BtnFlashStop.IsEnabled = _controller.IsRunning;
        BtnFlashNext.IsEnabled = canStart
            || (_controller.IsRunning && _controller.IsPaused && !_controller.IsOperationRunning);
        PnlBasicInfoCard.IsEnabled = canStart;

        TxtTestState.Text = _controller.IsCleaningUp
            ? $"{_controller.StateText} · 正在释放 CAN"
            : _controller.StateText;
        TxtProgress.Text = $"{CompletedCount()} / {_controller.Steps.Count}";
        TxtFlashStatus.Text = _configurationError ??
            $"判定：{_controller.Verdict} · 通过 {_controller.PassCount} · 不良 {_controller.NgCount}";

        var total = _controller.PassCount + _controller.NgCount;
        TxtCurrentVerdict.Text = _controller.Verdict;
        TxtCurrentVerdict.Foreground = _controller.Verdict switch
        {
            "PASS" => Brushes.ForestGreen,
            "NG" => Brushes.Firebrick,
            _ => Brushes.Gray,
        };
        TxtTestPass.Text = _controller.PassCount.ToString(CultureInfo.InvariantCulture);
        TxtTestNg.Text = _controller.NgCount.ToString(CultureInfo.InvariantCulture);
        TxtTestTotal.Text = total.ToString(CultureInfo.InvariantCulture);
        TxtTestYield.Text = total == 0
            ? "--"
            : (_controller.PassCount * 100.0 / total).ToString("F1", CultureInfo.InvariantCulture) + "%";

        if (FlashLogList.Items.Count > 0)
        {
            FlashLogList.ScrollIntoView(FlashLogList.Items[^1]);
        }

        // 烧录结束：等待 CAN 清理完成后再统一断电并生成报告
        if (_controller.IsRunning)
        {
            _sessionEndPending = false;
        }
        else if (_wasRunning)
        {
            _sessionEndPending = true;
        }

        if (_sessionEndPending && !_controller.IsCleaningUp)
        {
            _sessionEndPending = false;
            PowerOff();
            WriteReport();
        }

        _wasRunning = _controller.IsRunning;
    }

    /// <summary>按机种配置的目录与文件名规则生成烧录报告。</summary>
    private void WriteReport()
    {
        if (!_isExternalSequence || !FlashReport.IsEnabled(_reportDirectory) || _unitStartedAt == default)
        {
            _unitStartedAt = default;
            return;
        }

        if (!_controller.Steps.Any(step => step.Result != "待执行"))
        {
            _unitStartedAt = default;
            return;
        }

        try
        {
            var info = new FlashReportInfo(
                _recipe,
                TxtInfoSn.Text.Trim(),
                TxtInfoCarrier.Text.Trim(),
                TxtInfoShellCode.Text.Trim(),
                _recipe,
                _unitStartedAt,
                DateTime.Now,
                _controller.Verdict,
                _controller.PassCount,
                _controller.NgCount,
                _controller.Steps
                    .Select(step => new FlashReportStep(
                        step.Number,
                        step.Code,
                        step.Project,
                        step.Result,
                        step.Measured,
                        step.Duration,
                        step.Time,
                        step.Remark))
                    .ToList());
            var path = FlashReport.Write(info, _reportDirectory, _reportFileName);
            _controller.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 报告已生成：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or FormatException or NotSupportedException)
        {
            _controller.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 报告生成失败：{ex.Message}");
        }
        finally
        {
            _unitStartedAt = default;
        }
    }

    private int CompletedCount() =>
        _controller.Steps.Count(step => step.Result is "PASS" or "NG");

    /// <summary>切换机种/关闭窗口前停机，并等待正在执行的 UDS 取消及 CAN 释放。</summary>
    public Task ShutdownAsync()
    {
        if (_shutdownTask is null)
        {
            _closing = true;
            PowerOff();
            UpdateControls();
            _shutdownTask = _controller.ShutdownAsync();
        }

        return _shutdownTask;
    }

    /// <summary>释放资源：停止并释放测试控制器。</summary>
    public void Dispose()
    {
        if (!_closing)
        {
            _closing = true;
        }

        PowerOff();
        _controller.Dispose();
        GC.SuppressFinalize(this);
    }
}

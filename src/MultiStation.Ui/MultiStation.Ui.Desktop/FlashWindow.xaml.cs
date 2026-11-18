using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using MultiStation.Instruments;
using MultiStation.Instruments.Drivers.Zlg;
using MultiStation.Instruments.Protocol.Uds.Algorithm;
using MultiStation.Instruments.Protocol.Uds.Physical;
using MultiStation.Platform.Core.Production;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// ECU 烧录测试窗口：按当前机种参数，通过 ZLG CAN 卡执行 UDS 刷写流程并实时显示日志。
/// </summary>
public partial class FlashWindow : Window, IDisposable
{
    private readonly ModelProfile _model;
    private CancellationTokenSource? _cts;
    private bool _running;

    public FlashWindow(ModelProfile model)
    {
        InitializeComponent();
        _model = model;

        Title = $"ECU 烧录测试 · {model.Name}";
        TxtTitle.Text = $"ECU 烧录测试 · {model.Name}";
        TxtFirmware.Text = model.FirmwarePath ?? string.Empty;
        TxtSummary.Text =
            $"CAN {model.CanDeviceIndex}:{model.CanChannelIndex}（{model.CanMode}），波特率 {model.BaudRate} / 数据 {model.DataBaudRate}；" +
            $"请求 0x{model.RequestId:X3}，响应 0x{model.ResponseId:X3}，烧录地址 0x{model.MemoryAddress:X8}。" +
            Environment.NewLine +
            "注意：安全访问算法当前为示例（Seed 逐字节异或 0xA5），上真实 ECU 前需替换为 OEM 算法。";
        AppendLog("就绪：确认参数后点击“开始烧录”");
    }

    private async void OnStartFlash(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            return;
        }

        var path = TxtFirmware.Text.Trim();
        if (!File.Exists(path))
        {
            MessageBox.Show(this, "请选择有效的固件文件", "ECU 烧录",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"即将对 ECU 执行烧录。\n\n机种：{_model.Name}\n" +
            $"CAN：{_model.CanDeviceIndex}:{_model.CanChannelIndex}（{_model.CanMode}）\n" +
            $"固件：{path}\n烧录地址：0x{_model.MemoryAddress:X8}\n\n" +
            "请确认 ECU 已进入 Bootloader、现场安全条件满足。是否开始？",
            "人工确认 · ECU 烧录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            AppendLog("已取消：未通过人工确认");
            return;
        }

        byte[] firmware;
        try
        {
            firmware = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"读取固件失败：{ex.Message}", "ECU 烧录",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _running = true;
        BtnStartFlash.IsEnabled = false;
        BtnBrowseFirmware.IsEnabled = false;
        BtnCancelFlash.IsEnabled = true;
        TxtStatus.Text = "烧录中…";
        AppendLog($"开始烧录：{path}（{firmware.Length} 字节）");

        _cts = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(() => RunFlashAsync(firmware, _cts.Token), _cts.Token);
            TxtStatus.Text = "烧录成功";
            AppendLog($"烧录成功：{result.TotalBytes} 字节 / {result.Blocks} 块，用时 {result.Elapsed.TotalSeconds:F1}s");
            MessageBox.Show(
                this,
                $"烧录成功。\n\nVIN：{result.Vin}\n软件版本：{result.SoftwareVersion}\n" +
                $"字节数：{result.TotalBytes}，块数：{result.Blocks}",
                "ECU 烧录", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "已中止";
            AppendLog("烧录已中止");
        }
        catch (UdsNegativeResponseException ex)
        {
            TxtStatus.Text = "烧录失败";
            AppendLog($"ECU 否定响应：NRC 0x{(byte)ex.Code:X2}（{ex.Code}）");
            MessageBox.Show(this, ex.Message, "ECU 烧录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "烧录失败";
            AppendLog($"烧录失败：{ex.Message}");
            MessageBox.Show(this, ex.Message, "ECU 烧录", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _running = false;
            BtnStartFlash.IsEnabled = true;
            BtnBrowseFirmware.IsEnabled = true;
            BtnCancelFlash.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task<UdsFlashResult> RunFlashAsync(byte[] firmware, CancellationToken cancellationToken)
    {
        var descriptor = new InstrumentDescriptor(
            "USBCANFD-400U",
            "USBCANFD-400U",
            InstrumentKind.CanBus,
            $"{_model.CanDeviceIndex}:{_model.CanChannelIndex}:{_model.CanMode}",
            Simulated: false);

        var instrument = new ZlgCanFd400UInstrument(
            descriptor,
            _model.CanDeviceIndex,
            _model.CanChannelIndex,
            _model.IsCanFd ? ZlgCanMode.CanFd : ZlgCanMode.Classic,
            _model.BaudRate,
            _model.DataBaudRate);

        await instrument.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var options = new IsoTpOptions
            {
                TxId = _model.RequestId,
                RxId = _model.ResponseId,
                PadToEightBytes = true,
                PaddingByte = 0x00,
            };
            var client = new UdsClient(new IsoTpTransport(instrument, options), options);
            var config = new UdsFlashConfig(
                _model.MemoryAddress,
                firmware,
                Encoding.ASCII.GetBytes(_model.Fingerprint),
                SecurityLevel: _model.SecurityLevel,
                EraseMemory: _model.EraseMemory,
                Log: AppendLogFromWorker);
            return await UdsFlasher.FlashAsync(client, config, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await instrument.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>释放资源：烧录中先取消，空闲时释放取消令牌。</summary>
    public void Dispose()
    {
        if (_running)
        {
            _cts?.Cancel();
            GC.SuppressFinalize(this);
            return;
        }

        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }

    private void OnCancelFlash(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        TxtStatus.Text = "正在中止…";
        _cts?.Cancel();
    }

    private void OnBrowseFirmware(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择烧录文件",
            Filter = "固件文件 (*.bin;*.hex)|*.bin;*.hex|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            TxtFirmware.Text = dialog.FileName;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_running)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            "烧录正在进行中，确定中止并关闭窗口？",
            "ECU 烧录",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            e.Cancel = true;
            return;
        }

        _cts?.Cancel();
    }

    /// <summary>刷写过程在工作线程输出日志，统一切回 UI 线程。</summary>
    private void AppendLogFromWorker(string message) =>
        Dispatcher.InvokeAsync(() => AppendLog(message));

    private void AppendLog(string message)
    {
        TxtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        TxtLog.ScrollToEnd();
    }
}

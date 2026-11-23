using System.Globalization;
using System.IO;
using System.Text;
using MultiStation.Instruments;
using MultiStation.Instruments.Drivers.Itech;
using MultiStation.Instruments.Drivers.Zlg;
using MultiStation.Instruments.Protocol.Can.Physical;
using MultiStation.Instruments.Protocol.Uds.Algorithm;
using MultiStation.Instruments.Protocol.Uds.Physical;
using MultiStation.Platform.Core.Production;

namespace MultiStation.Ui.Desktop;

public sealed record UdsStepResult(bool Passed, string Measured, string Detail);

/// <summary>真实 UDS 步骤边界；CAN 在首个诊断步骤延迟打开，并可重复关闭后重新开始。</summary>
public interface IUdsStepExecutor : IAsyncDisposable
{
    Task<UdsStepResult> ExecuteAsync(TestSequenceRow row, CancellationToken cancellationToken);

    Task CloseAsync();
}

/// <summary>按烧录工作簿中的原始 PDU 执行 UDS，不参与 AutoProgramFlash。</summary>
public sealed class UdsStepExecutor : IUdsStepExecutor
{
    private readonly ModelProfile _model;
    private readonly Func<int, ICanInstrument> _busFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICanInstrument? _mainBus;
    private ICanInstrument? _ccuBus;
    private UdsClient? _mainClient;
    private UdsClient? _ccuClient;
    private string? _supplierSerialHex;
    private CanChannelConfig? _mainConfig;
    private CanChannelConfig? _ccuConfig;
    private bool _disposed;

    /// <summary>主 ECU 固定使用 CAN0（通道 0）。</summary>
    private const int MainChannelIndex = 0;

    /// <summary>CCU 固定使用 CAN1（通道 1）。</summary>
    private const int CcuChannelIndex = 1;

    public UdsStepExecutor(ModelProfile model, Func<int, ICanInstrument>? busFactory = null)
    {
        _model = model;
        ValidateIds(model);
        _busFactory = busFactory ?? (channel => CreateBus(CurrentChannelConfig(channel)));
    }

    /// <summary>由烧录界面在每次开始前设置人工 SN，并逐字符转换为可打印 ASCII 字节。</summary>
    public void SetSupplierSerialAscii(string sn)
    {
        if (string.IsNullOrWhiteSpace(sn) || sn.Trim() == "-")
        {
            throw new FormatException("请先在基础信息中输入供应商 ECU SN");
        }

        var value = sn.Trim();
        if (value.Any(character => character is < ' ' or > '~'))
        {
            throw new FormatException("F18C SN 仅支持可打印 ASCII 字符（0x20～0x7E）");
        }

        _supplierSerialHex = Convert.ToHexString(Encoding.ASCII.GetBytes(value));
    }

    public async Task<UdsStepResult> ExecuteAsync(
        TestSequenceRow row,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = ParseCommand(row.Command);
            if (!command.TryGetValue("COM", out var com) || string.IsNullOrWhiteSpace(com))
            {
                throw new FormatException($"设备指令缺少 COM：{row.Command}");
            }

            if (com.Equals("CAN", StringComparison.OrdinalIgnoreCase))
            {
                return await InitializeCanAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (com.Equals("IT6322A", StringComparison.OrdinalIgnoreCase))
            {
                return await ControlPowerAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (!com.Equals("UDS", StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException($"不支持的设备指令：{row.Command}");
            }

            int channel;
            if (command.TryGetValue("CHANNEL", out var configuredChannel)
                && !string.IsNullOrWhiteSpace(configuredChannel))
            {
                channel = ParseInt(command, "CHANNEL", MainChannelIndex);
                if (channel is not (MainChannelIndex or CcuChannelIndex))
                {
                    throw new FormatException($"UDS 指令 CHANNEL 只能是 0 或 1：{configuredChannel}");
                }
            }
            else
            {
                // 兼容旧指令写法 TARGET=MAIN/CCU
                var target = command.TryGetValue("TARGET", out var configuredTarget)
                    ? configuredTarget.ToUpperInvariant()
                    : "MAIN";
                channel = target switch
                {
                    "MAIN" => MainChannelIndex,
                    "CCU" => CcuChannelIndex,
                    _ => throw new FormatException($"未知 UDS 目标：{target}"),
                };
            }

            var pdu = FlashSequenceLoader.ParseHex(
                command.TryGetValue("PDU", out var request) ? request : string.Empty,
                $"{row.Project} 的 PDU");
            if (pdu.Length == 0)
            {
                throw new FormatException($"{row.Project} 的 PDU 不能为空");
            }

            if (pdu[0] == 0x2E)
            {
                if (pdu.Length != 3 || !command.TryGetValue("DATA", out var configuredData))
                {
                    throw new FormatException($"{row.Project} 缺少有效的 2E DID 或写入数据");
                }

                var dataHex = configuredData.Equals("{F18C}", StringComparison.OrdinalIgnoreCase)
                    ? _supplierSerialHex ?? throw new FormatException("请先在基础信息中输入供应商 ECU SN")
                    : configuredData;
                var data = FlashSequenceLoader.ParseHex(dataHex, $"{row.Project} 的写入数据");
                pdu = [.. pdu, .. data];
            }

            byte[] response;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var timeoutMs = int.TryParse(row.TimeoutMs, out var configuredTimeout) && configuredTimeout > 0
                    ? configuredTimeout
                    : 3000;
                timeout.CancelAfter(timeoutMs);
                await EnsureConnectedAsync(timeout.Token).ConfigureAwait(false);
                var client = channel == MainChannelIndex ? _mainClient! : _ccuClient!;
                response = await client.RequestAsync(pdu, timeout.Token).ConfigureAwait(false);
            }

            var measured = ValidateResponse(row, pdu, response);
            if (pdu[0] == 0x11
                && string.Equals(row.FlowControl, "PostDelay", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(row.FlowTarget, out var delayMs) && delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }

            return new UdsStepResult(true, measured, $"CH{channel} 响应={Convert.ToHexString(response)}");
        }
        catch (UdsNegativeResponseException ex)
        {
            return new UdsStepResult(false, $"NRC=0x{(byte)ex.Code:X2}", ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return new UdsStepResult(false, "超时", ex.Message);
        }
        catch (TimeoutException ex)
        {
            return new UdsStepResult(false, "超时", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or ArgumentException)
        {
            return new UdsStepResult(false, "失败", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _mainClient = null;
            _ccuClient = null;
            if (_mainBus is not null)
            {
                await _mainBus.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _mainBus = null;
            }

            if (_ccuBus is not null)
            {
                await _ccuBus.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _ccuBus = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_mainBus is not null && _ccuBus is not null)
        {
            return;
        }

        // CAN0 接主 ECU，CAN1 接 CCU；两个通道共用同一台 ZLG 设备
        var mainConfig = CurrentChannelConfig(MainChannelIndex);
        var ccuConfig = CurrentChannelConfig(CcuChannelIndex);
        var mainBus = _busFactory(MainChannelIndex);
        var ccuBus = _busFactory(CcuChannelIndex);
        try
        {
            await mainBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await ccuBus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await mainBus.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await ccuBus.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        _mainBus = mainBus;
        _ccuBus = ccuBus;
        var mainOptions = CreateOptions(mainConfig.RequestId, mainConfig.ResponseId);
        var ccuOptions = CreateOptions(ccuConfig.RequestId, ccuConfig.ResponseId);
        _mainClient = new UdsClient(new IsoTpTransport(mainBus, mainOptions), mainOptions);
        _ccuClient = new UdsClient(new IsoTpTransport(ccuBus, ccuOptions), ccuOptions);
    }

    private static IsoTpOptions CreateOptions(int requestId, int responseId) => new()
    {
        TxId = requestId,
        RxId = responseId,
        ExtendedId = requestId > 0x7FF,
        P2Milliseconds = 5000,
        P2ExtendedMilliseconds = 30000,
        FlowControlTimeoutMilliseconds = 5000,
    };

    private static string ValidateResponse(TestSequenceRow row, byte[] request, byte[] response)
    {
        if (response.Length == 0 || response[0] != (byte)(request[0] + 0x40))
        {
            throw new IOException("UDS 正响应 SID 非法");
        }

        switch (request[0])
        {
            case 0x10:
            case 0x11:
            case 0x27:
                if (request.Length < 2 || response.Length < 2 || response[1] != request[1])
                {
                    throw new IOException($"0x{request[0]:X2} 响应子功能与请求不一致");
                }
                break;
            case 0x22:
            case 0x2E:
                if (request.Length < 3 || response.Length < 3
                    || response[1] != request[1] || response[2] != request[2])
                {
                    throw new IOException($"0x{request[0]:X2} 响应 DID 与请求不一致");
                }
                break;
        }

        if (request[0] != 0x22)
        {
            return Convert.ToHexString(response);
        }

        var data = response[3..];
        if (!string.IsNullOrWhiteSpace(row.Equal))
        {
            var expected = FlashSequenceLoader.ParseHex(row.Equal, $"{row.Project} 的期望值");
            if (!data.AsSpan().SequenceEqual(expected))
            {
                throw new IOException(
                    $"读取值不符：期望={Convert.ToHexString(expected)}，实际={Convert.ToHexString(data)}");
            }
        }

        return Convert.ToHexString(data);
    }

    private static Dictionary<string, string> ParseCommand(string command)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in command.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            result[segment[..separator].Trim()] = segment[(separator + 1)..].Trim();
        }

        return result;
    }

    private static void ValidateIds(ModelProfile model)
    {
        ValidateIdPair(model.RequestId, model.ResponseId, "主 ECU");
        ValidateIdPair(model.CcuRequestId, model.CcuResponseId, "CCU");
    }

    private static void ValidateIdPair(int requestId, int responseId, string target)
    {
        if (requestId is < 0 or > 0x1FFF_FFFF || responseId is < 0 or > 0x1FFF_FFFF)
        {
            throw new FormatException($"{target} CAN ID 超出 29 位范围");
        }

        if ((requestId > 0x7FF) != (responseId > 0x7FF))
        {
            throw new FormatException($"{target} 请求与响应 ID 必须同为标准帧或同为扩展帧");
        }
    }

    /// <summary>指定通道的有效配置：优先使用 CAN 初始化指令中的参数。</summary>
    private CanChannelConfig CurrentChannelConfig(int channel) => channel switch
    {
        MainChannelIndex => _mainConfig ?? CreateDefaultChannelConfig(channel),
        CcuChannelIndex => _ccuConfig ?? CreateDefaultChannelConfig(channel),
        _ => CreateDefaultChannelConfig(channel),
    };

    private CanChannelConfig CreateDefaultChannelConfig(int channel) => new(
        _model.CanDeviceModel,
        _model.CanDeviceIndex,
        channel,
        _model.CanMode,
        _model.BaudRate,
        _model.DataBaudRate,
        channel == MainChannelIndex ? _model.RequestId : _model.CcuRequestId,
        channel == MainChannelIndex ? _model.ResponseId : _model.CcuResponseId);

    /// <summary>CAN 通道初始化指令：CHANNEL=0 初始化 CAN0，CHANNEL=1 初始化 CAN1。</summary>
    private async Task<UdsStepResult> InitializeCanAsync(
        Dictionary<string, string> command,
        CancellationToken cancellationToken)
    {
        var channel = ParseInt(command, "CHANNEL", MainChannelIndex);
        if (channel is not (MainChannelIndex or CcuChannelIndex))
        {
            throw new FormatException($"CAN 指令 CHANNEL 只能是 0 或 1：{channel}");
        }

        var config = new CanChannelConfig(
            ValueOrDefault(command, "DEVICE", _model.CanDeviceModel),
            ParseInt(command, "INDEX", _model.CanDeviceIndex),
            channel,
            ValueOrDefault(command, "MODE", _model.CanMode),
            ParseUInt(command, "BAUD", _model.BaudRate),
            ParseUInt(command, "DBIT", _model.DataBaudRate),
            ParseHexId(command, "REQID",
                channel == MainChannelIndex ? _model.RequestId : _model.CcuRequestId),
            ParseHexId(command, "RESPID",
                channel == MainChannelIndex ? _model.ResponseId : _model.CcuResponseId));

        ZlgCanApi.ResolveDeviceType(config.DeviceModel);
        if (channel == MainChannelIndex)
        {
            _mainConfig = config;
        }
        else
        {
            _ccuConfig = config;
        }

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return new UdsStepResult(
            true,
            "CAN 已初始化",
            $"{config.DeviceModel} CH{channel} {config.Mode} {config.BaudRate}bps · " +
            $"REQID=0x{config.RequestId:X} RESPID=0x{config.ResponseId:X}");
    }

    /// <summary>IT6322A 电源指令：OUTP=ON 按电压/限流上电，OUTP=OFF 断电。</summary>
    private static async Task<UdsStepResult> ControlPowerAsync(
        Dictionary<string, string> command,
        CancellationToken cancellationToken)
    {
        var port = ValueOrDefault(command, "PORT", string.Empty);
        if (port.Length == 0)
        {
            throw new FormatException("IT6322A 指令缺少 PORT（电源串口）");
        }

        var serial = command.TryGetValue("SERIAL", out var configuredSerial) ? configuredSerial : null;
        var channel = ParseInt(command, "CH", 1);
        var output = ValueOrDefault(command, "OUTP", "ON").ToUpperInvariant();
        if (output is not ("ON" or "OFF"))
        {
            throw new FormatException($"IT6322A OUTP 只能是 ON/OFF：{output}");
        }

        var voltage = ParseDouble(command, "VOLT", 0);
        var current = ParseDouble(command, "CURR", 0);
        await Task.Run(() =>
        {
            using var supply = new It6322aPowerSupply(port, serial);
            supply.Connect();
            if (output == "OFF")
            {
                supply.PowerOff(channel);
                return;
            }

            if (voltage <= 0)
            {
                throw new FormatException("IT6322A 上电指令缺少 VOLT");
            }

            supply.PowerOn(voltage, current, channel);
        }, cancellationToken).ConfigureAwait(false);

        return new UdsStepResult(
            true,
            output == "OFF" ? "已断电" : "已上电",
            $"IT6322A CH{channel} {voltage:0.##}V（{port}）");
    }

    private static string ValueOrDefault(
        Dictionary<string, string> command,
        string key,
        string fallback) =>
        command.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : fallback;

    private static int ParseInt(Dictionary<string, string> command, string key, int fallback)
    {
        if (!command.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{key} 不是有效整数：{text}");
    }

    private static uint ParseUInt(Dictionary<string, string> command, string key, uint fallback)
    {
        if (!command.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return uint.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{key} 不是有效整数：{text}");
    }

    private static double ParseDouble(Dictionary<string, string> command, string key, double fallback)
    {
        if (!command.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new FormatException($"{key} 不是有效数值：{text}");
    }

    /// <summary>解析十六进制 UDS ID，支持 0x 前缀；非法值直接失败。</summary>
    private static int ParseHexId(Dictionary<string, string> command, string key, int fallback)
    {
        if (!command.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new FormatException($"{key} 不是有效十六进制 ID：{text}");
    }

    private static ZlgCanFd400UInstrument CreateBus(CanChannelConfig config)
    {
        var deviceType = ZlgCanApi.ResolveDeviceType(config.DeviceModel);
        var deviceName = ZlgCanApi.DescribeDevice(deviceType);
        var descriptor = new InstrumentDescriptor(
            deviceName,
            deviceName,
            InstrumentKind.CanBus,
            string.Create(CultureInfo.InvariantCulture, $"{config.DeviceIndex}:{config.Channel}:{config.Mode}"),
            Simulated: false);
        return new ZlgCanFd400UInstrument(
            descriptor,
            config.DeviceIndex,
            config.Channel,
            string.Equals(config.Mode, "fd", StringComparison.OrdinalIgnoreCase)
                ? ZlgCanMode.CanFd
                : ZlgCanMode.Classic,
            config.BaudRate,
            config.DataBaudRate,
            deviceType);
    }
}

/// <summary>单通道 CAN 初始化参数（来自序列中的 COM=CAN 指令）。</summary>
internal sealed record CanChannelConfig(
    string DeviceModel,
    int DeviceIndex,
    int Channel,
    string Mode,
    uint BaudRate,
    uint DataBaudRate,
    int RequestId,
    int ResponseId);

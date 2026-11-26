using System.Globalization;
using System.IO;
using System.Text;
using MultiStation.Instruments;
using MultiStation.Instruments.Drivers.Itech;
using MultiStation.Instruments.Drivers.Zlg;
using MultiStation.Instruments.Logging;
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
    private readonly bool _holdsDevice;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ICanInstrument? _mainBus;
    private ICanInstrument? _ccuBus;
    private UdsClient? _mainClient;
    private UdsClient? _ccuClient;
    private IDisposable? _deviceHold;
    private string? _supplierSerialHex;
    private CanChannelConfig? _mainConfig;
    private CanChannelConfig? _ccuConfig;
    private DateTime _lastRequestAt = DateTime.MinValue;
    private bool _sessionMarked;
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
        // 只有真实 ZLG 通道才需要整场持有设备；测试注入的模拟通道不加载驱动
        _holdsDevice = busFactory is null;
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

            var pdu = BuildRequestPdu(command, row);

            var timeoutMs = int.TryParse(row.TimeoutMs, out var configuredTimeout) && configuredTimeout > 0
                ? configuredTimeout
                : 3000;
            var retries = int.TryParse(row.Retry, out var configuredRetry) && configuredRetry > 0
                ? configuredRetry
                : Math.Max(0, _model.UdsRetryCount);

            byte[]? response = null;
            for (var attempt = 0; response is null; attempt++)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(timeoutMs);
                try
                {
                    await CloseIdleChannelAsync(channel).ConfigureAwait(false);
                    await EnsureChannelAsync(channel, timeout.Token).ConfigureAwait(false);
                    await WaitRequestIntervalAsync(timeout.Token).ConfigureAwait(false);
                    var client = channel == MainChannelIndex ? _mainClient! : _ccuClient!;
                    response = await client.RequestAsync(pdu, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < retries)
                {
                    // 按脚本「重试」列重发：ECU 偶发不应答时可自动恢复
                }
                catch (TimeoutException) when (attempt < retries)
                {
                }
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

    /// <summary>相邻两条诊断请求之间保持最小间隔，避免连续请求把 ECU 压满。</summary>
    private async Task WaitRequestIntervalAsync(CancellationToken cancellationToken)
    {
        var interval = _model.UdsRequestIntervalMs;
        if (interval > 0 && _lastRequestAt != DateTime.MinValue)
        {
            var elapsed = (DateTime.UtcNow - _lastRequestAt).TotalMilliseconds;
            if (elapsed < interval)
            {
                await Task.Delay((int)Math.Ceiling(interval - elapsed), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        _lastRequestAt = DateTime.UtcNow;
    }

    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectChannelsAsync().ConfigureAwait(false);
            RawFrameLogger.Mark($"诊断会话结束 机种={_model.Name}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>断开两个通道并释放客户端；调用方需自行持有 _gate。</summary>
    private async Task DisconnectChannelsAsync()
    {
        await DisconnectChannelAsync(MainChannelIndex).ConfigureAwait(false);
        await DisconnectChannelAsync(CcuChannelIndex).ConfigureAwait(false);
        _deviceHold?.Dispose();
        _deviceHold = null;
        _sessionMarked = false;
    }

    /// <summary>只断开指定通道，另一个通道保持运行。</summary>
    private async Task DisconnectChannelAsync(int channel)
    {
        if (channel == MainChannelIndex)
        {
            _mainClient = null;
            if (_mainBus is not null)
            {
                await _mainBus.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _mainBus = null;
            }
        }
        else
        {
            _ccuClient = null;
            if (_ccuBus is not null)
            {
                await _ccuBus.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _ccuBus = null;
            }
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

    /// <summary>按需连接单个通道：已连接则直接复用；关闭过则重新初始化，设备句柄保持打开。</summary>
    private async Task EnsureChannelAsync(int channel, CancellationToken cancellationToken)
    {
        var config = CurrentChannelConfig(channel);
        // 整场测试持有设备：中途只开关通道，不再反复关闭/打开设备
        if (_holdsDevice)
        {
            _deviceHold ??= ZlgDeviceSession.Hold(config.DeviceModel, config.DeviceIndex);
        }

        var bus = channel == MainChannelIndex ? _mainBus : _ccuBus;
        if (bus is null)
        {
            bus = _busFactory(channel);
            var options = CreateOptions(config.RequestId, config.ResponseId);
            var client = new UdsClient(new IsoTpTransport(bus, options), options);
            if (channel == MainChannelIndex)
            {
                _mainBus = bus;
                _mainClient = client;
            }
            else
            {
                _ccuBus = bus;
                _ccuClient = client;
            }
        }

        if (bus.State == InstrumentState.Connected)
        {
            return;
        }

        await bus.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!_sessionMarked)
        {
            _sessionMarked = true;
            RawFrameLogger.Mark($"诊断会话开始 机种={_model.Name}");
        }
    }

    /// <summary>切换到某个通道前，关闭另一路的通道（设备保持打开）。</summary>
    private async Task CloseIdleChannelAsync(int keepChannel)
    {
        var bus = keepChannel == MainChannelIndex ? _ccuBus : _mainBus;
        if (bus is ZlgCanFd400UInstrument zlg && zlg.State == InstrumentState.Connected)
        {
            await zlg.CloseChannelAsync().ConfigureAwait(false);
        }
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

    /// <summary>
    /// 构造 UDS 请求：兼容旧的 PDU= 整包写法，也支持 SID/SUB/DID/MODE/RID/DATA 分段写法。
    /// 2E 的 DATA={F18C} 使用人工输入的供应商 SN 替换。
    /// </summary>
    private byte[] BuildRequestPdu(Dictionary<string, string> command, TestSequenceRow row)
    {
        if (command.TryGetValue("PDU", out var legacy) && !string.IsNullOrWhiteSpace(legacy))
        {
            var pdu = FlashSequenceLoader.ParseHex(legacy, $"{row.Project} 的 PDU");
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

                pdu = [.. pdu, .. ParseWriteData(configuredData, row)];
            }

            return pdu;
        }

        if (!command.TryGetValue("SID", out var sidText) || string.IsNullOrWhiteSpace(sidText))
        {
            throw new FormatException($"{row.Project} 缺少 SID 或 PDU");
        }

        var bytes = new List<byte> { ParseHexByte(sidText, row, "SID") };
        if (command.TryGetValue("SUB", out var sub) && !string.IsNullOrWhiteSpace(sub))
        {
            bytes.Add(ParseHexByte(sub, row, "SUB"));
        }

        if (command.TryGetValue("DID", out var did) && !string.IsNullOrWhiteSpace(did))
        {
            bytes.AddRange(ParseHexBytes(did, 2, row, "DID"));
        }

        if (command.TryGetValue("MODE", out var mode) && !string.IsNullOrWhiteSpace(mode))
        {
            bytes.Add(ParseHexByte(mode, row, "MODE"));
        }

        if (command.TryGetValue("RID", out var rid) && !string.IsNullOrWhiteSpace(rid))
        {
            bytes.AddRange(ParseHexBytes(rid, 2, row, "RID"));
        }

        if (command.TryGetValue("DATA", out var data) && !string.IsNullOrWhiteSpace(data))
        {
            bytes.AddRange(ParseWriteData(data, row));
        }

        return [.. bytes];
    }

    private byte[] ParseWriteData(string value, TestSequenceRow row)
    {
        if (value.Equals("{F18C}", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.FromHexString(
                _supplierSerialHex ?? throw new FormatException("请先在基础信息中输入供应商 ECU SN"));
        }

        // F18B 写入当前制造日期：ECU 规范要求 3 字节 BCD（YY MM DD），
        // 例如 2026-11-25 → 0x26 0x11 0x25；写成 ASCII 会因长度不符被 NRC 0x13 拒绝
        if (value.Equals("{flash_date}", StringComparison.OrdinalIgnoreCase))
        {
            var today = DateTime.Now;
            return
            [
                ToBcd(today.Year % 100),
                ToBcd(today.Month),
                ToBcd(today.Day),
            ];
        }

        return FlashSequenceLoader.ParseHex(value, $"{row.Project} 的写入数据");
    }

    private static byte ParseHexByte(string text, TestSequenceRow row, string field) =>
        ParseHexBytes(text, 1, row, field)[0];

    /// <summary>两位十进制数转 BCD，例如 26 → 0x26。</summary>
    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    private static byte[] ParseHexBytes(string text, int length, TestSequenceRow row, string field)
    {
        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length != length * 2 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException($"{row.Project} 的 {field} 必须是 {length} 字节 HEX：{text}");
        }

        return Convert.FromHexString(value);
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

        var terminal = ValueOrDefault(command, "TERM", "0");
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
                channel == MainChannelIndex ? _model.ResponseId : _model.CcuResponseId),
            terminal is "1"
                || terminal.Equals("true", StringComparison.OrdinalIgnoreCase)
                || terminal.Equals("on", StringComparison.OrdinalIgnoreCase));

        ZlgCanApi.ResolveDeviceType(config.DeviceModel);
        var previous = channel == MainChannelIndex ? _mainConfig : _ccuConfig;
        if (channel == MainChannelIndex)
        {
            _mainConfig = config;
        }
        else
        {
            _ccuConfig = config;
        }

        // 参数变化时只重开本通道，另一路保持运行；首次初始化也不会提前用默认参数打开另一路
        if (previous != config)
        {
            await DisconnectChannelAsync(channel).ConfigureAwait(false);
        }

        await EnsureChannelAsync(channel, cancellationToken).ConfigureAwait(false);
        return new UdsStepResult(
            true,
            "CAN 已初始化",
            $"{config.DeviceModel} CH{channel} {config.Mode} {config.BaudRate}bps · " +
            $"REQID=0x{config.RequestId:X} RESPID=0x{config.ResponseId:X}");
    }

    /// <summary>IT6322B/IT6332B 电源指令：OUTP=ON 按电压/限流上电，OUTP=OFF 断电。</summary>
    private static async Task<UdsStepResult> ControlPowerAsync(
        Dictionary<string, string> command,
        CancellationToken cancellationToken)
    {
        var port = ValueOrDefault(command, "PORT", string.Empty);
        if (port.Length == 0)
        {
            throw new FormatException("电源指令缺少 PORT（电源串口）");
        }

        var serial = command.TryGetValue("SERIAL", out var configuredSerial) ? configuredSerial : null;
        var channel = ParseInt(command, "CH", 1);

        // MEAS=CURR：上电后回读产品电流，可选 MIN/MAX 判据
        var measure = ValueOrDefault(command, "MEAS", string.Empty).ToUpperInvariant();
        if (measure.Length > 0)
        {
            if (measure != "CURR")
            {
                throw new FormatException($"电源不支持的测量项：{measure}");
            }

            var delay = ParseInt(command, "DELAY", 500);
            var minimum = ParseDouble(command, "MIN", double.NaN);
            var maximum = ParseDouble(command, "MAX", double.NaN);
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var measured = await Task.Run(() =>
            {
                using var supply = new It6322aPowerSupply(port, serial);
                supply.Connect();
                return supply.ReadCurrent(channel)
                    ?? throw new IOException($"电源 CH{channel} 电流回读失败");
            }, cancellationToken).ConfigureAwait(false);

            var display = string.Create(CultureInfo.InvariantCulture, $"{measured:0.###} A");
            var outOfRange = (!double.IsNaN(minimum) && measured < minimum)
                || (!double.IsNaN(maximum) && measured > maximum);
            return new UdsStepResult(
                !outOfRange,
                display,
                outOfRange
                    ? $"CH{channel} 回读电流 {display} 超出范围（{port}）"
                    : $"CH{channel} 回读电流 {display}（{port}）");
        }

        var output = ValueOrDefault(command, "OUTP", "ON").ToUpperInvariant();
        if (output is not ("ON" or "OFF"))
        {
            throw new FormatException($"电源 OUTP 只能是 ON/OFF：{output}");
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
                throw new FormatException("电源上电指令缺少 VOLT");
            }

            supply.PowerOn(voltage, current, channel);
        }, cancellationToken).ConfigureAwait(false);

        return new UdsStepResult(
            true,
            output == "OFF" ? "已断电" : "已上电",
            $"电源 CH{channel} {voltage:0.##}V（{port}）");
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
            deviceType,
            config.TerminalResistor);
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
    int ResponseId,
    bool TerminalResistor = false);

using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using MultiStation.Instruments.Logging;
using MultiStation.Instruments.Protocol.Can.Algorithm;
using MultiStation.Instruments.Protocol.Can.Physical;

namespace MultiStation.Instruments.Drivers.Zlg;

/// <summary>ZLG CAN 通道工作模式：经典 CAN 或 CAN FD。</summary>
public enum ZlgCanMode
{
    Classic,
    CanFd,
}

/// <summary>
/// ZLG USBCANFD-400U / 200U CAN 仪器驱动：
/// 打开设备、设置波特率、初始化并启动通道，按模式收发经典 CAN 或 CAN FD 报文。
/// </summary>
public sealed class ZlgCanFd400UInstrument(
    InstrumentDescriptor descriptor,
    int deviceIndex = 0,
    int channelIndex = 0,
    ZlgCanMode mode = ZlgCanMode.CanFd,
    uint abitBaudRate = ZlgCanFd400UInstrument.DefaultAbitBaudRate,
    uint dbitBaudRate = ZlgCanFd400UInstrument.DefaultDbitBaudRate,
    uint deviceType = ZlgCanApi.DeviceTypeUsbCanFd400U,
    bool terminalResistor = false) : ICanInstrument, IDisposable
{
    public const uint DefaultAbitBaudRate = 500_000;

    public const uint DefaultDbitBaudRate = 2_000_000;

    private readonly int _deviceIndex = deviceIndex;
    private readonly int _channelIndex = channelIndex;
    private readonly uint _abitBaudRate = abitBaudRate;
    private readonly uint _dbitBaudRate = dbitBaudRate;
    private readonly uint _deviceType = deviceType;
    private readonly ZlgCanMode _mode = mode;
    private readonly bool _terminalResistor = terminalResistor;
    private Channel<CanFrame> _frames = Channel.CreateUnbounded<CanFrame>();
    private ZlgDeviceLease? _lease;
    private ZlgCanApi? _api;
    private uint _deviceHandle;
    private uint _channelHandle;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    /// <summary>
    /// 按描述地址创建：格式 "设备序号:通道序号[:模式]"，如 "0:0"、"0:1:classic"。
    /// 省略通道时为 0，省略模式时按 CAN FD。
    /// </summary>
    public ZlgCanFd400UInstrument(InstrumentDescriptor descriptor)
        : this(
            descriptor,
            ParseIndex(descriptor.Address, 0),
            ParseIndex(descriptor.Address, 1),
            ParseMode(descriptor.Address))
    {
    }

    public InstrumentDescriptor Descriptor { get; } = descriptor;

    public InstrumentState State { get; private set; } = InstrumentState.Disconnected;

    public event EventHandler<InstrumentStateChangedEventArgs>? StateChanged;

    /// <summary>已发送报文数量。</summary>
    public int SentCount { get; private set; }

    public ZlgCanMode Mode => _mode;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (State == InstrumentState.Connected)
        {
            return;
        }

        _lease ??= ZlgDeviceLease.Acquire(_deviceType, _deviceIndex);
        _api = _lease.Api;
        _deviceHandle = _lease.Handle;

        // 内置 120Ω 终端电阻，对应 ZCANPRO 的「终端电阻：使能」
        var terminalEnabled = _terminalResistor
            && _api.SetValue(
                _deviceHandle,
                string.Create(CultureInfo.InvariantCulture, $"{_channelIndex}/terminal_resistor"),
                "1") != 0;

        var isoFd = false;
        if (_mode == ZlgCanMode.CanFd)
        {
            // 显式使用 ISO CAN FD（与 ZCANPRO 默认一致）；部分驱动版本默认非 ISO，ECU 会丢弃报文
            isoFd = _api.SetValue(
                _deviceHandle,
                string.Create(CultureInfo.InvariantCulture, $"{_channelIndex}/canfd_standard"),
                "1") != 0;
            SetBaudRateOrThrow("canfd_abit_baud_rate", _abitBaudRate);
            SetBaudRateOrThrow("canfd_dbit_baud_rate", _dbitBaudRate);
            InitCanFdChannel();
        }
        else
        {
            // 不同驱动版本对经典 CAN 波特率属性支持不同：先尝试 baud_rate，再回退 canfd_abit_baud_rate
            if (!TrySetBaudRate("baud_rate", _abitBaudRate)
                && !TrySetBaudRate("canfd_abit_baud_rate", _abitBaudRate))
            {
                ReleaseDevice();
                throw new IOException($"设置经典 CAN 波特率失败（{_abitBaudRate}bps）");
            }

            InitClassicChannel();
        }

        if (_api.StartCan(_channelHandle) == 0)
        {
            ReleaseDevice();
            throw new IOException($"启动 CAN 通道 {_channelIndex} 失败");
        }

        _frames = Channel.CreateUnbounded<CanFrame>(new UnboundedChannelOptions { SingleReader = true });
        _cts = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token), CancellationToken.None);
        SetState(InstrumentState.Connected);
        RawFrameLogger.Mark(
            $"CAN 打开 DEV{_deviceIndex} CH{_channelIndex} " +
            $"{(_mode == ZlgCanMode.CanFd ? (isoFd ? "FD(ISO)" : "FD(非ISO)") : "CLASSIC")} " +
            $"{_abitBaudRate}/{_dbitBaudRate}" +
            (_mode == ZlgCanMode.CanFd && _dbitBaudRate > _abitBaudRate ? " BRS" : string.Empty) +
            (terminalEnabled ? " TERM" : string.Empty));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>释放资源：断开设备。</summary>
    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopChannelAsync(keepDevice: false).ConfigureAwait(false);
    }

    /// <summary>
    /// 只关闭本通道、保留设备句柄：下次用到时按需重新初始化，
    /// 避免整台设备被反复关闭再打开导致发送失败。
    /// </summary>
    public Task CloseChannelAsync() => StopChannelAsync(keepDevice: true);

    private async Task StopChannelAsync(bool keepDevice)
    {
        var cts = _cts;
        _cts = null;
        cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 接收循环退出异常不影响设备释放
            }

            _receiveLoop = null;
        }

        cts?.Dispose();

        if (_channelHandle != 0)
        {
            _api?.ResetCan(_channelHandle);
            _channelHandle = 0;
        }

        if (!keepDevice)
        {
            _lease?.Dispose();
            _lease = null;
            _api = null;
            _deviceHandle = 0;
            _frames.Writer.TryComplete();
        }

        SetState(InstrumentState.Disconnected);
        RawFrameLogger.Mark(keepDevice
            ? $"CAN 关闭 DEV{_deviceIndex} CH{_channelIndex}（保留设备）"
            : $"CAN 关闭 DEV{_deviceIndex} CH{_channelIndex}");
    }

    public Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (frame.Data.Length == 0 || frame.Data.Length > MaxDataLength)
        {
            throw new ArgumentException($"报文数据长度应为 1~{MaxDataLength} 字节", nameof(frame));
        }

        return _mode == ZlgCanMode.CanFd ? SendCanFdAsync(frame) : SendClassicAsync(frame);
    }

    public async IAsyncEnumerable<CanFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RequireConnected();
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private int MaxDataLength => _mode == ZlgCanMode.CanFd ? 64 : 8;

    /// <summary>CAN FD 帧的 BRS 标志：数据段切换到数据波特率。</summary>
    private const byte CanFdBrsFlag = 0x01;

    private Task SendClassicAsync(CanFrame frame)
    {
        var data = new ZlgTransmitData
        {
            Frame = new ZlgCanFrame
            {
                CanId = CanFrameCodec.Encode(frame),
                CanDlc = (byte)frame.Data.Length,
                Data = new byte[8],
            },
            TransmitType = 0,
        };

        Array.Copy(frame.Data, data.Frame.Data, frame.Data.Length);
        var sent = _api!.Transmit(_channelHandle, ref data, 1);
        if (sent == 0)
        {
            throw new IOException($"CAN 报文发送失败（CH{_channelIndex} ID=0x{frame.Id:X}）");
        }

        SentCount++;
        RawFrameLogger.Write(false, _deviceIndex, _channelIndex, frame, canFd: false);
        return Task.CompletedTask;
    }

    private Task SendCanFdAsync(CanFrame frame)
    {
        var data = new ZlgTransmitFdData
        {
            Frame = new ZlgCanFdFrame
            {
                CanId = CanFrameCodec.Encode(frame),
                Len = ToFdLengthCode(frame.Data.Length),
                // 数据段波特率高于仲裁段时启用 BRS，FD 帧才会真正切换到数据波特率
                Flags = _dbitBaudRate > _abitBaudRate ? CanFdBrsFlag : (byte)0,
                Data = new byte[64],
            },
            TransmitType = 0,
        };

        Array.Copy(frame.Data, data.Frame.Data, frame.Data.Length);
        var sent = _api!.TransmitFd(_channelHandle, ref data, 1);
        if (sent == 0)
        {
            throw new IOException($"CAN FD 报文发送失败（CH{_channelIndex} ID=0x{frame.Id:X}）");
        }

        SentCount++;
        RawFrameLogger.Write(false, _deviceIndex, _channelIndex, frame, canFd: true);
        return Task.CompletedTask;
    }

    private void InitCanFdChannel()
    {
        var config = new ZlgChannelInitConfig
        {
            CanType = 1,
            CanFd = new ZlgCanFdInitConfig
            {
                AccCode = 0,
                AccMask = 0x1FFF_FFFF,
                Filter = 0,
                Mode = 0,
            },
        };

        _channelHandle = _api!.InitCan(_deviceHandle, (uint)_channelIndex, ref config);
        if (_channelHandle == 0)
        {
            ReleaseDevice();
            throw new IOException($"初始化 CAN FD 通道 {_channelIndex} 失败");
        }
    }

    private void InitClassicChannel()
    {
        var config = new ZlgCanChannelInitConfig
        {
            CanType = 0,
            Can = new ZlgCanInitConfig
            {
                AccCode = 0,
                AccMask = 0xFFFF_FFFF,
                Filter = 0,
                Mode = 0,
            },
        };

        _channelHandle = _api!.InitCanClassic(_deviceHandle, (uint)_channelIndex, ref config);
        if (_channelHandle == 0)
        {
            ReleaseDevice();
            throw new IOException($"初始化经典 CAN 通道 {_channelIndex} 失败");
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var api = _api!;
        if (_mode == ZlgCanMode.CanFd)
        {
            await ReceiveFdLoopAsync(api, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReceiveClassicLoopAsync(api, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReceiveFdLoopAsync(ZlgCanApi api, CancellationToken cancellationToken)
    {
        var buffer = new ZlgReceiveFdData[64];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i].Frame.Data = new byte[64];
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = api.ReceiveFd(_channelHandle, buffer, (uint)buffer.Length, 50);
                for (var i = 0u; i < count; i++)
                {
                    var frame = buffer[i].Frame;
                    var length = Math.Min(DecodeFdLength(frame.Len), (byte)64);
                    var decoded = CanFrameCodec.Decode(frame.CanId, frame.Data.AsSpan(0, length));
                    RawFrameLogger.Write(true, _deviceIndex, _channelIndex, decoded, canFd: true);
                    _frames.Writer.TryWrite(decoded);
                }
            }
        }
        catch (Exception)
        {
            SetState(InstrumentState.Fault);
            _frames.Writer.TryComplete();
        }
    }

    private async Task ReceiveClassicLoopAsync(ZlgCanApi api, CancellationToken cancellationToken)
    {
        var buffer = new ZlgReceiveData[64];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i].Frame.Data = new byte[8];
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = api.Receive(_channelHandle, buffer, (uint)buffer.Length, 50);
                for (var i = 0u; i < count; i++)
                {
                    var frame = buffer[i].Frame;
                    var length = Math.Min(frame.CanDlc, (byte)8);
                    var decoded = CanFrameCodec.Decode(frame.CanId, frame.Data.AsSpan(0, length));
                    RawFrameLogger.Write(true, _deviceIndex, _channelIndex, decoded, canFd: false);
                    _frames.Writer.TryWrite(decoded);
                }
            }
        }
        catch (Exception)
        {
            SetState(InstrumentState.Fault);
            _frames.Writer.TryComplete();
        }
    }

    private bool TrySetBaudRate(string property, uint baudRate)
    {
        var path = string.Create(CultureInfo.InvariantCulture, $"{_channelIndex}/{property}");
        var value = baudRate.ToString(CultureInfo.InvariantCulture);
        return _api!.SetValue(_deviceHandle, path, value) != 0;
    }

    private void SetBaudRateOrThrow(string property, uint baudRate)
    {
        if (!TrySetBaudRate(property, baudRate))
        {
            ReleaseDevice();
            throw new IOException(
                $"设置 CAN 波特率失败（{_channelIndex}/{property}={baudRate}）");
        }
    }

    private void ReleaseDevice()
    {
        if (_api is null)
        {
            return;
        }

        if (_channelHandle != 0)
        {
            _api.ResetCan(_channelHandle);
            _channelHandle = 0;
        }

        // 同一设备的多个通道共享租约，最后一个通道释放时才真正关闭设备
        _lease?.Dispose();
        _lease = null;
        _deviceHandle = 0;
        _api = null;
    }

    private void RequireConnected()
    {
        if (State != InstrumentState.Connected)
        {
            throw new InvalidOperationException($"仪器「{Descriptor.Name}」未连接，无法收发报文");
        }
    }

    private void SetState(InstrumentState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new InstrumentStateChangedEventArgs(state));
    }

    private static int ParseIndex(string address, int position)
    {
        var parts = address.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > position
            && int.TryParse(parts[position], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                ? index
                : 0;
    }

    /// <summary>CAN FD 数据字节数转 ZLG 的 DLC 编码（len 字段是 DLC，不是字节数）。</summary>
    private static byte ToFdLengthCode(int length) => length switch
    {
        <= 8 => (byte)length,
        <= 12 => 9,
        <= 16 => 10,
        <= 20 => 11,
        <= 24 => 12,
        <= 32 => 13,
        <= 48 => 14,
        _ => 15,
    };

    /// <summary>ZLG 的 DLC 编码还原为实际数据字节数。</summary>
    private static int DecodeFdLength(byte lengthCode) => lengthCode switch
    {
        <= 8 => lengthCode,
        9 => 12,
        10 => 16,
        11 => 20,
        12 => 24,
        13 => 32,
        14 => 48,
        _ => 64,
    };

    private static ZlgCanMode ParseMode(string address)
    {
        var parts = address.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 2 && parts[2].Equals("classic", StringComparison.OrdinalIgnoreCase)
            ? ZlgCanMode.Classic
            : ZlgCanMode.CanFd;
    }
}

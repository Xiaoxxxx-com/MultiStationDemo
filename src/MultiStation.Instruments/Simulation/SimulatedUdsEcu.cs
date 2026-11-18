using System.Text;
using MultiStation.Instruments.Protocol.Uds.Algorithm;
using MultiStation.Instruments.Protocol.Uds.Physical;

namespace MultiStation.Instruments.Simulation;

/// <summary>
/// 模拟 UDS ECU：在共享 CAN 总线上应答刷写流程所需的 UDS 服务，
/// 用于没有真实 ECU 时验证 ISO-TP + UDS + 烧录脚本链路。
/// 安全访问算法与脚本默认算法一致（逐字节异或 0xA5）。
/// </summary>
public sealed class SimulatedUdsEcu(ICanInstrument bus, IsoTpOptions options) : IAsyncDisposable
{
    private static readonly byte[] Vin = Encoding.ASCII.GetBytes("LSVAA1234567890");
    private static readonly byte[] SoftwareVersion = Encoding.ASCII.GetBytes("SW-01.00.00");

    private readonly ICanInstrument _bus = bus;
    private readonly IsoTpOptions _options = options;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<byte> _downloaded = [];
    private byte[] _lastSeed = [];
    private int _expectedSize = -1;
    private byte _nextBlockCounter = 1;
    private Task? _loop;

    /// <summary>最近一次安全访问种子。</summary>
    public byte[] LastSeed => _lastSeed;

    /// <summary>已接收的下载字节数。</summary>
    public int DownloadedSize => _downloaded.Count;

    /// <summary>当前诊断会话。</summary>
    public UdsSession Session { get; private set; } = UdsSession.Default;

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _loop = null;
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var request = await ReceivePduAsync(cancellationToken).ConfigureAwait(false);
            if (request is null || request.Length == 0)
            {
                continue;
            }

            var response = Handle(request);
            await SendPduAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private byte[] Handle(byte[] request)
    {
        var service = request[0];
        switch (service)
        {
            case UdsService.DiagnosticSessionControl when request.Length >= 2:
                Session = (UdsSession)request[1];
                return [(byte)0x50, request[1], (byte)0x00, (byte)0x32, (byte)0x01, (byte)0xF4];

            case UdsService.EcuReset when request.Length >= 2:
                return [(byte)0x51, request[1]];

            case UdsService.TesterPresent:
                return [(byte)0x7E, (byte)0x00];

            case UdsService.ReadDataByIdentifier when request.Length >= 3:
                {
                    var did = (ushort)((request[1] << 8) | request[2]);
                    return did switch
                    {
                        0xF190 => Merge([0x62, 0xF1, 0x90], Vin),
                        0xF195 => Merge([0x62, 0xF1, 0x95], SoftwareVersion),
                        _ => [0x7F, service, 0x31],
                    };
                }

            case UdsService.SecurityAccess when request.Length >= 2:
                {
                    var subFunction = request[1];
                    if ((subFunction & 1) == 1)
                    {
                        _lastSeed = [0x11, 0x22, 0x33, 0x44];
                        return Merge([0x67, subFunction], _lastSeed);
                    }

                    var expected = UdsSecurityAccess.XorKey(_lastSeed);
                    var provided = request.Length > 2 ? request[2..] : [];
                    return provided.SequenceEqual(expected)
                        ? [(byte)0x67, subFunction]
                        : [0x7F, service, 0x35];
                }

            case UdsService.WriteDataByIdentifier when request.Length >= 3:
                return [(byte)0x6E, request[1], request[2]];

            case UdsService.RoutineControl when request.Length >= 4:
                {
                    var routine = (ushort)((request[2] << 8) | request[3]);
                    if (routine == UdsRoutine.EraseMemory)
                    {
                        _downloaded.Clear();
                        _expectedSize = ParseMemorySize(request);
                        _nextBlockCounter = 1;
                        return [0x71, 0x01, 0xFF, 0x00];
                    }

                    if (routine == UdsRoutine.CheckIntegrity)
                    {
                        var ok = _expectedSize >= 0 && _downloaded.Count == _expectedSize;
                        return [0x71, 0x01, 0x02, 0x02, 0xFF, 0x00, (byte)(ok ? 0x01 : 0x02)];
                    }

                    return [0x7F, service, 0x31];
                }

            case UdsService.RequestDownload when request.Length >= 4:
                {
                    _expectedSize = ParseMemorySize(request);
                    _downloaded.Clear();
                    _nextBlockCounter = 1;
                    // 长度格式 0x20：2 字节最大块长度，0x0402 = 1026 字节
                    return [0x74, 0x20, 0x04, 0x02];
                }

            case UdsService.TransferData when request.Length >= 2:
                {
                    if (request[1] != _nextBlockCounter)
                    {
                        return [0x7F, service, 0x73];
                    }

                    _downloaded.AddRange(request[2..]);
                    _nextBlockCounter = (byte)((_nextBlockCounter + 1) & 0xFF);
                    return [(byte)0x76, request[1]];
                }

            case UdsService.RequestTransferExit:
                return [0x77];

            case UdsService.ControlDtcSetting when request.Length >= 2:
                return [(byte)0xC5, request[1]];

            case UdsService.CommunicationControl when request.Length >= 2:
                return [(byte)0x68, request[1]];

            default:
                return [0x7F, service, 0x11];
        }
    }

    private static int ParseMemorySize(byte[] request)
    {
        // 请求格式：[SID, 格式, 地址长度, 长度长度, 地址..., 长度...]
        // 例程格式：[SID, 控制类型, 例程高, 例程低, 地址(4), 长度(4)]
        if (request[0] == UdsService.RoutineControl)
        {
            if (request.Length < 12)
            {
                return -1;
            }

            return (int)ReadUInt32(request, 8);
        }

        var addressLength = request[2];
        _ = request[3];
        return (int)ReadUInt32(request, 4 + addressLength);
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        uint value = 0;
        for (var i = 0; i < 4 && offset + i < data.Length; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        return value;
    }

    private static byte[] Merge(byte[] head, byte[] tail)
    {
        var result = new byte[head.Length + tail.Length];
        Array.Copy(head, result, head.Length);
        Array.Copy(tail, 0, result, head.Length, tail.Length);
        return result;
    }

    private async Task<byte[]?> ReceivePduAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<byte>(64);
        var expectedLength = -1;
        var sequence = 1;

        await foreach (var frame in _bus.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            if (frame.Id != _options.TxId)
            {
                continue;
            }

            var type = (frame.Data[0] >> 4) & 0x0F;
            switch (type)
            {
                case 0x0:
                    {
                        var length = frame.Data[0] & 0x0F;
                        return length > 0 && length <= frame.Data.Length - 1
                            ? frame.Data[1..(1 + length)]
                            : null;
                    }

                case 0x1:
                    {
                        expectedLength = ((frame.Data[0] & 0x0F) << 8) | frame.Data[1];
                        buffer.Clear();
                        buffer.AddRange(frame.Data[2..]);
                        sequence = 1;
                        await SendFrameAsync([0x30, 0x00, 0x00], cancellationToken).ConfigureAwait(false);
                        break;
                    }

                case 0x2:
                    {
                        if (expectedLength < 0 || (frame.Data[0] & 0x0F) != sequence)
                        {
                            continue;
                        }

                        sequence = (sequence + 1) & 0x0F;
                        buffer.AddRange(frame.Data[1..]);
                        break;
                    }

                default:
                    continue;
            }

            if (expectedLength >= 0 && buffer.Count >= expectedLength)
            {
                return [.. buffer.GetRange(0, expectedLength)];
            }
        }

        return null;
    }

    private async Task SendPduAsync(byte[] pdu, CancellationToken cancellationToken)
    {
        if (pdu.Length <= 7)
        {
            var single = new byte[pdu.Length + 1];
            single[0] = (byte)pdu.Length;
            Array.Copy(pdu, 0, single, 1, pdu.Length);
            await SendFrameAsync(single, cancellationToken).ConfigureAwait(false);
            return;
        }

        var first = new byte[8];
        first[0] = (byte)(0x10 | ((pdu.Length >> 8) & 0x0F));
        first[1] = (byte)(pdu.Length & 0xFF);
        Array.Copy(pdu, 0, first, 2, 6);
        await SendFrameAsync(first, cancellationToken).ConfigureAwait(false);

        var (blockSize, stMin) = await WaitFlowControlAsync(cancellationToken).ConfigureAwait(false);
        var sentInBlock = 0;
        var sequence = 1;
        var offset = 6;
        while (offset < pdu.Length)
        {
            var chunk = Math.Min(7, pdu.Length - offset);
            var consecutive = new byte[chunk + 1];
            consecutive[0] = (byte)(0x20 | (sequence & 0x0F));
            Array.Copy(pdu, offset, consecutive, 1, chunk);
            await SendFrameAsync(consecutive, cancellationToken).ConfigureAwait(false);

            offset += chunk;
            sequence = (sequence + 1) & 0x0F;
            sentInBlock++;

            if (stMin > 0 && stMin <= 0x7F)
            {
                await Task.Delay(stMin, cancellationToken).ConfigureAwait(false);
            }

            if (blockSize > 0 && sentInBlock >= blockSize && offset < pdu.Length)
            {
                (blockSize, stMin) = await WaitFlowControlAsync(cancellationToken).ConfigureAwait(false);
                sentInBlock = 0;
            }
        }
    }

    private async Task<(byte BlockSize, byte StMin)> WaitFlowControlAsync(CancellationToken cancellationToken)
    {
        await using var frames = _bus.ReceiveAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await frames.MoveNextAsync().ConfigureAwait(false))
        {
            var frame = frames.Current;
            if (frame.Id != _options.TxId || ((frame.Data[0] >> 4) & 0x0F) != 0x3)
            {
                continue;
            }

            return (frame.Data[1], frame.Data[2]);
        }

        throw new OperationCanceledException("等待诊断仪流控帧失败");
    }

    private Task SendFrameAsync(byte[] data, CancellationToken cancellationToken)
    {
        var payload = data;
        if (data.Length < 8)
        {
            payload = new byte[8];
            Array.Copy(data, payload, data.Length);
        }

        return _bus.SendAsync(new CanFrame(_options.RxId, payload, _options.ExtendedId), cancellationToken);
    }
}

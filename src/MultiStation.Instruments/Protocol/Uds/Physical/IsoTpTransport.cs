using System.IO;

namespace MultiStation.Instruments.Protocol.Uds.Physical;

/// <summary>
/// ISO-TP（ISO 15765-2）传输实现：负责 UDS 报文的分段发送、流控帧处理与多帧重组。
/// 底层使用任意 <see cref="ICanInstrument"/>，经典 CAN 与 CAN FD 均可承载。
/// </summary>
public sealed class IsoTpTransport(ICanInstrument bus, IsoTpOptions options) : IUdsTransport
{
    private const byte SingleFrame = 0x0;
    private const byte FirstFrame = 0x1;
    private const byte ConsecutiveFrame = 0x2;
    private const byte FlowControl = 0x3;

    private readonly ICanInstrument _bus = bus;
    private readonly IsoTpOptions _options = options;

    public IsoTpOptions Options => _options;

    public async Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("UDS 报文不能为空", nameof(payload));
        }

        if (payload.Length <= 7)
        {
            var single = new byte[payload.Length + 1];
            single[0] = (byte)((SingleFrame << 4) | payload.Length);
            Array.Copy(payload, 0, single, 1, payload.Length);
            await SendFrameAsync(single, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (payload.Length > 0xFFF)
        {
            throw new ArgumentException("经典 CAN 单条 UDS 报文最长 4095 字节", nameof(payload));
        }

        var first = new byte[8];
        first[0] = (byte)((FirstFrame << 4) | ((payload.Length >> 8) & 0x0F));
        first[1] = (byte)(payload.Length & 0xFF);
        Array.Copy(payload, 0, first, 2, 6);
        await SendFrameAsync(first, cancellationToken).ConfigureAwait(false);

        var (blockSize, stMin) = await WaitFlowControlAsync(cancellationToken).ConfigureAwait(false);
        var sentInBlock = 0;
        var sequence = 1;
        var offset = 6;
        while (offset < payload.Length)
        {
            var chunk = Math.Min(7, payload.Length - offset);
            var consecutive = new byte[chunk + 1];
            consecutive[0] = (byte)((ConsecutiveFrame << 4) | (sequence & 0x0F));
            Array.Copy(payload, offset, consecutive, 1, chunk);
            await SendFrameAsync(consecutive, cancellationToken).ConfigureAwait(false);

            offset += chunk;
            sequence = (sequence + 1) & 0x0F;
            sentInBlock++;

            var delay = StMinToMilliseconds(stMin);
            if (delay > 0)
            {
                await Task.Delay((int)Math.Ceiling(delay), cancellationToken).ConfigureAwait(false);
            }

            if (blockSize > 0 && sentInBlock >= blockSize && offset < payload.Length)
            {
                (blockSize, stMin) = await WaitFlowControlAsync(cancellationToken).ConfigureAwait(false);
                sentInBlock = 0;
            }
        }
    }

    public async Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var buffer = new List<byte>(64);
        var expectedLength = -1;
        var sequence = 1;

        while (true)
        {
            var frame = await WaitFrameAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (frame.Data.Length == 0)
            {
                continue;
            }

            var type = (frame.Data[0] >> 4) & 0x0F;
            switch (type)
            {
                case SingleFrame:
                    {
                        var length = frame.Data[0] & 0x0F;
                        if (length == 0 || length > frame.Data.Length - 1)
                        {
                            throw new IOException("ISO-TP 单帧长度非法");
                        }

                        return frame.Data[1..(1 + length)];
                    }

                case FirstFrame:
                    {
                        if (frame.Data.Length < 8)
                        {
                            throw new IOException("ISO-TP 首帧长度不足 8 字节");
                        }

                        expectedLength = ((frame.Data[0] & 0x0F) << 8) | frame.Data[1];
                        buffer.Clear();
                        buffer.AddRange(frame.Data[2..]);
                        sequence = 1;
                        await SendFlowControlAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }

                case ConsecutiveFrame:
                    {
                        if (expectedLength < 0)
                        {
                            throw new IOException("ISO-TP 收到无首帧的连续帧");
                        }

                        if ((frame.Data[0] & 0x0F) != sequence)
                        {
                            throw new IOException("ISO-TP 连续帧序号错乱");
                        }

                        sequence = (sequence + 1) & 0x0F;
                        buffer.AddRange(frame.Data[1..]);
                        break;
                    }

                default:
                    // 其它为流控帧或无关帧，继续等待
                    continue;
            }

            if (expectedLength >= 0 && buffer.Count >= expectedLength)
            {
                return [.. buffer.GetRange(0, expectedLength)];
            }
        }
    }

    private async Task<(byte BlockSize, byte StMin)> WaitFlowControlAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await WaitFrameAsync(
                TimeSpan.FromMilliseconds(_options.FlowControlTimeoutMilliseconds), cancellationToken).ConfigureAwait(false);
            if (frame.Data.Length < 3 || ((frame.Data[0] >> 4) & 0x0F) != FlowControl)
            {
                continue;
            }

            var status = frame.Data[0] & 0x0F;
            if (status == 1)
            {
                continue; // WAIT：继续等待
            }

            if (status == 2)
            {
                throw new IOException("ECU 流控溢出（OVFLW），请减小每块传输长度");
            }

            return (frame.Data[1], frame.Data[2]);
        }
    }

    private async Task<CanFrame> WaitFrameAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await foreach (var frame in _bus.ReceiveAsync(cts.Token).ConfigureAwait(false))
            {
                if (frame.Id == _options.RxId && frame.ExtendedId == _options.ExtendedId)
                {
                    return frame;
                }
            }

            throw new IOException("CAN 接收通道已关闭");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"等待 0x{_options.RxId:X3} 响应超时（{timeout.TotalMilliseconds:F0}ms）");
        }
    }

    private Task SendFlowControlAsync(CancellationToken cancellationToken)
    {
        var flowControl = new byte[]
        {
            (byte)((FlowControl << 4) | 0x00),
            _options.BlockSize,
            _options.StMin,
        };
        return SendFrameAsync(flowControl, cancellationToken);
    }

    private Task SendFrameAsync(byte[] data, CancellationToken cancellationToken)
    {
        var payload = data;
        if (_options.PadToEightBytes && data.Length < 8)
        {
            payload = new byte[8];
            Array.Copy(data, payload, data.Length);
            for (var i = data.Length; i < payload.Length; i++)
            {
                payload[i] = _options.PaddingByte;
            }
        }

        return _bus.SendAsync(new CanFrame(_options.TxId, payload, _options.ExtendedId), cancellationToken);
    }

    private static double StMinToMilliseconds(byte stMin) => stMin switch
    {
        <= 0x7F => stMin,
        >= 0xF1 and <= 0xF9 => (stMin - 0xF0) * 0.1,
        _ => 0,
    };
}

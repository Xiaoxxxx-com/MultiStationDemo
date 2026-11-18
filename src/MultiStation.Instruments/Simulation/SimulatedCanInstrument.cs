using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MultiStation.Instruments.Simulation;

/// <summary>
/// 模拟 CAN 总线仪器：发送的报文直接回环到接收队列，
/// 便于在没有真实总线时联调收发链路。
/// </summary>
public sealed class SimulatedCanInstrument(InstrumentDescriptor descriptor) : ICanInstrument
{
    private readonly Channel<CanFrame> _frames = Channel.CreateUnbounded<CanFrame>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });

    public InstrumentDescriptor Descriptor { get; } = descriptor;

    public InstrumentState State { get; private set; } = InstrumentState.Disconnected;

    public event EventHandler<InstrumentStateChangedEventArgs>? StateChanged;

    /// <summary>已发送报文数量。</summary>
    public int SentCount { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(InstrumentState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _frames.Writer.TryComplete();
        SetState(InstrumentState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (frame.Data.Length is 0 or > 64)
        {
            throw new ArgumentException("CAN 报文数据长度应为 1~64 字节", nameof(frame));
        }

        SentCount++;
        _frames.Writer.TryWrite(frame);
        return Task.CompletedTask;
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
}

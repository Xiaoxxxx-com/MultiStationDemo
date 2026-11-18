using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace MultiStation.Instruments.Simulation;

/// <summary>
/// 模拟 CAN 总线：允许多个端点（诊断仪 / ECU）各自独立收发，
/// 一个端点发送的报文广播给其它端点，用于离线验证多节点通信。
/// </summary>
public sealed class SimulatedCanBus
{
    private readonly List<SimulatedCanEndpoint> _endpoints = [];
    private readonly Lock _gate = new();

    public ICanInstrument CreateEndpoint(string name)
    {
        var endpoint = new SimulatedCanEndpoint(this, name);
        lock (_gate)
        {
            _endpoints.Add(endpoint);
        }

        return endpoint;
    }

    internal void Publish(SimulatedCanEndpoint source, CanFrame frame)
    {
        SimulatedCanEndpoint[] targets;
        lock (_gate)
        {
            targets = [.. _endpoints];
        }

        foreach (var target in targets)
        {
            if (!ReferenceEquals(target, source))
            {
                target.Deliver(frame);
            }
        }
    }
}

/// <summary>挂在 <see cref="SimulatedCanBus"/> 上的单个模拟节点。</summary>
public sealed class SimulatedCanEndpoint : ICanInstrument
{
    private readonly SimulatedCanBus _bus;
    private readonly Channel<CanFrame> _frames =
        Channel.CreateUnbounded<CanFrame>(new UnboundedChannelOptions { SingleReader = true });

    internal SimulatedCanEndpoint(SimulatedCanBus bus, string name)
    {
        _bus = bus;
        Descriptor = new InstrumentDescriptor(name, name, InstrumentKind.CanBus, "SIM::BUS", Simulated: true);
    }

    public InstrumentDescriptor Descriptor { get; }

    public InstrumentState State { get; private set; } = InstrumentState.Disconnected;

    public event EventHandler<InstrumentStateChangedEventArgs>? StateChanged;

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
        if (State != InstrumentState.Connected)
        {
            throw new InvalidOperationException($"模拟节点「{Descriptor.Name}」未连接，无法发送报文");
        }

        if (frame.Data.Length is 0 or > 64)
        {
            throw new ArgumentException("CAN 报文数据长度应为 1~64 字节", nameof(frame));
        }

        SentCount++;
        _bus.Publish(this, frame);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CanFrame> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    internal void Deliver(CanFrame frame) => _frames.Writer.TryWrite(frame);

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

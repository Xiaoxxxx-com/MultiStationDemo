namespace MultiStation.Instruments;

/// <summary>
/// 测试仪器通用接口：所有仪器共用连接生命周期与状态通知。
/// 命令下发由各仪器接口扩展，便于后续接入真实 SCPI / CAN 通道。
/// </summary>
public interface IInstrument
{
    InstrumentDescriptor Descriptor { get; }

    InstrumentState State { get; }

    event EventHandler<InstrumentStateChangedEventArgs>? StateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

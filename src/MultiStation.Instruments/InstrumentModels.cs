namespace MultiStation.Instruments;

/// <summary>仪器类型：当前支持可编程电源与 CAN 总线仪器。</summary>
public enum InstrumentKind
{
    PowerSupply,
    CanBus,
}

/// <summary>仪器连接状态。</summary>
public enum InstrumentState
{
    Disconnected,
    Connected,
    Fault,
}

/// <summary>
/// 仪器描述：标识、类型与通信地址。Simulated 为 true 时使用内置模拟实现。
/// </summary>
public sealed record InstrumentDescriptor(
    string Id,
    string Name,
    InstrumentKind Kind,
    string Address,
    bool Simulated = true);

/// <summary>电源回读值：输出电压、输出电流与输出开关状态。</summary>
public sealed record PowerSupplyReading(double Voltage, double Current, bool OutputOn);

/// <summary>CAN 报文：标识符、数据与是否扩展帧。</summary>
public sealed record CanFrame(int Id, byte[] Data, bool ExtendedId = false);

/// <summary>仪器状态变化事件参数。</summary>
public sealed class InstrumentStateChangedEventArgs(InstrumentState state) : EventArgs
{
    public InstrumentState State { get; } = state;
}

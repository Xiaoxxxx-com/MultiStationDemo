namespace MultiStation.Instruments;

/// <summary>CAN 总线仪器：报文发送与接收。</summary>
public interface ICanInstrument : IInstrument
{
    Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default);

    IAsyncEnumerable<CanFrame> ReceiveAsync(CancellationToken cancellationToken = default);
}

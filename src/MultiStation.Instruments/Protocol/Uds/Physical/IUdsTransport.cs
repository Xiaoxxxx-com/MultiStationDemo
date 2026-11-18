namespace MultiStation.Instruments.Protocol.Uds.Physical;

/// <summary>UDS 传输层抽象：发送 / 接收一整条 UDS 报文（已完成分段与重组）。</summary>
public interface IUdsTransport
{
    Task SendAsync(byte[] payload, CancellationToken cancellationToken = default);

    Task<byte[]> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

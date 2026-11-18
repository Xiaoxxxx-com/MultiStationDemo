namespace MultiStation.Instruments.Protocol.Scpi.Physical;

/// <summary>SCPI 文本通道：写命令、读回读值。真实仪器可走 TCP、USB 或串口。</summary>
public interface IScpiTransport
{
    string Endpoint { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync();

    Task WriteAsync(string command, CancellationToken cancellationToken = default);

    Task<string> QueryAsync(string command, CancellationToken cancellationToken = default);
}

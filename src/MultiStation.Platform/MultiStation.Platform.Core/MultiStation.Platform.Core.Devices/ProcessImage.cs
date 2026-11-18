namespace MultiStation.Platform.Core.Devices;

/// <summary>只读设备数据镜像：通信线程构造新对象后原子替换，工位线程只取快照。</summary>
public sealed record ProcessImage(
    bool Connected,
    long HeartbeatCount,
    DateTimeOffset UpdatedAt,
    string Mode,
    IReadOnlyDictionary<string, bool> Io);

public sealed class DeviceDisconnectedException(string message) : Exception(message)
{
}

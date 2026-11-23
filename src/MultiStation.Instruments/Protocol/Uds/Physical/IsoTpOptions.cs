namespace MultiStation.Instruments.Protocol.Uds.Physical;

/// <summary>ISO-TP（ISO 15765-2）参数：诊断请求 / 响应 CAN ID、流控与超时设置。</summary>
public sealed record IsoTpOptions
{
    /// <summary>诊断仪发送 ID（请求），默认 0x7E0。</summary>
    public int TxId { get; init; } = 0x7E0;

    /// <summary>ECU 响应 ID，默认 0x7E8。</summary>
    public int RxId { get; init; } = 0x7E8;

    public bool ExtendedId { get; init; }

    public byte PaddingByte { get; init; }

    /// <summary>不足 8 字节时用 PaddingByte 补齐，经典 CAN 默认开启。</summary>
    public bool PadToEightBytes { get; init; } = true;

    /// <summary>接收方向流控：一次允许 ECU 连续发送的帧数，0 表示不限。</summary>
    public byte BlockSize { get; init; }

    /// <summary>接收方向流控：连续帧最小间隔（毫秒）。</summary>
    public byte StMin { get; init; }

    /// <summary>P2：ECU 首帧响应超时（毫秒）。</summary>
    public int P2Milliseconds { get; init; } = 1000;

    /// <summary>P2*：ECU 返回 0x78 后的扩展响应超时（毫秒）。</summary>
    public int P2ExtendedMilliseconds { get; init; } = 5000;

    /// <summary>发送多帧时等待 ECU 流控帧的超时（毫秒）。</summary>
    public int FlowControlTimeoutMilliseconds { get; init; } = 1000;
}

namespace MultiStation.Platform.Core.Production;

/// <summary>机种类型：决定切换机种后加载哪一套测试序列。</summary>
public enum ModelKind
{
    /// <summary>通用测试机种：同一套测试界面 + 通用测试序列。</summary>
    Test = 0,

    /// <summary>ECU 烧录机种：同一套测试界面 + 烧录测试序列。</summary>
    Flash = 1,
}

/// <summary>
/// 机种配置：定义某个产品型号的 ECU 烧录参数（CAN 通道、诊断 ID、烧录地址等）。
/// 机种切换即在不同 <see cref="ModelProfile"/> 之间切换。
/// </summary>
public sealed record ModelProfile
{
    /// <summary>机种类型；两类机种共用同一套界面，但加载不同的测试序列。</summary>
    public ModelKind Kind { get; init; } = ModelKind.Test;

    public string Name { get; init; } = "默认机种";

    public string? FirmwarePath { get; init; }

    public int CanDeviceIndex { get; init; }

    public int CanChannelIndex { get; init; }

    /// <summary>经典 CAN 或 CAN FD：classic / fd。</summary>
    public string CanMode { get; init; } = "classic";

    public uint BaudRate { get; init; } = 500_000;

    public uint DataBaudRate { get; init; } = 2_000_000;

    public ushort RequestId { get; init; } = 0x7E0;

    public ushort ResponseId { get; init; } = 0x7E8;

    public uint MemoryAddress { get; init; } = 0x0800_0000;

    public byte SecurityLevel { get; init; } = 0x01;

    public string Fingerprint { get; init; } = "MULTISTATION";

    public bool EraseMemory { get; init; } = true;

    public string? Remark { get; init; }

    public bool IsCanFd => string.Equals(CanMode, "fd", StringComparison.OrdinalIgnoreCase);
}

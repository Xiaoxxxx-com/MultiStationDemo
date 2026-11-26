using System.IO;

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

    /// <summary>ZLG CAN 设备型号：400U（4 通道）或 200U（2 通道）；默认 200U。</summary>
    public string CanDeviceModel { get; init; } = "200U";

    public int CanChannelIndex { get; init; }

    /// <summary>经典 CAN 或 CAN FD：classic / fd。</summary>
    public string CanMode { get; init; } = "classic";

    public uint BaudRate { get; init; } = 500_000;

    public uint DataBaudRate { get; init; } = 2_000_000;

    public int RequestId { get; init; } = 0x7E0;

    public int ResponseId { get; init; } = 0x7E8;

    public int CcuRequestId { get; init; } = 0x7E4;

    public int CcuResponseId { get; init; } = 0x7EC;

    /// <summary>相邻两条诊断请求之间的最小间隔（毫秒），0 表示不限制。</summary>
    public int UdsRequestIntervalMs { get; init; } = 20;

    /// <summary>脚本「重试」列未配置时使用的默认重试次数，0 表示不重试。</summary>
    public int UdsRetryCount { get; init; }

    /// <summary>外部烧录序列（OOXML 工作簿）；为空时使用内置演示序列。</summary>
    public string? FlashSequencePath { get; init; }

    /// <summary>2E 写入数据配置，包含 [FactoryData] 节。</summary>
    public string? FactoryDataPath { get; init; }

    /// <summary>AutoProgramFlash 步骤调用的外部批处理。</summary>
    public string? FlashBatchPath { get; init; }

    /// <summary>烧录前上电所用的 IT6322B/IT6332B 串口名（如 COM3）；为空表示未配置。</summary>
    public string? PowerSupplyPort { get; init; }

    /// <summary>烧录前电源电压（V）：NP12 为 12V、NP13 为 24V；0 表示不上电。</summary>
    public double PowerVoltage { get; init; }

    /// <summary>烧录前电源限流（A）。</summary>
    public double PowerCurrentLimit { get; init; } = 3;

    /// <summary>IT6322B/IT6332B 串口参数：波特率,数据位,校验(N/E/O),停止位，默认 9600,8,N,1。</summary>
    public string PowerSerialSettings { get; init; } = "9600,8,N,1";

    /// <summary>电源输出通道：1=CH1、2=CH2、3=CH3；默认 CH1。</summary>
    public int PowerChannel { get; init; } = 1;

    /// <summary>Bat 步骤视为成功的退出码，逗号分隔；默认仅 0（AutoLoader 成功但返回 2 时可填 0,2）。</summary>
    public string BatSuccessExitCodes { get; init; } = "0";

    /// <summary>烧录报告目录，支持占位符；为空表示不生成报告。</summary>
    public string? ReportDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "E3Flash",
        "Report");

    /// <summary>报告文件名规则，支持 {Model}/{SN}/{CarrierID}/{ShellCode}/{Verdict}/{Date}/{Time}/{DateTime}。</summary>
    public string ReportFileNamePattern { get; init; } = "{Model}_{SN}_{DateTime}.txt";

    public uint MemoryAddress { get; init; } = 0x0800_0000;

    public byte SecurityLevel { get; init; } = 0x01;

    public string Fingerprint { get; init; } = "MULTISTATION";

    public bool EraseMemory { get; init; } = true;

    public string? Remark { get; init; }

    public bool IsCanFd => string.Equals(CanMode, "fd", StringComparison.OrdinalIgnoreCase);
}

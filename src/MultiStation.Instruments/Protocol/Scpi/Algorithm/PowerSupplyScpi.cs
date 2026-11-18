using System.Globalization;

namespace MultiStation.Instruments.Protocol.Scpi.Algorithm;

/// <summary>
/// 可编程电源的 SCPI 命令拼装：与通信通道解耦，固定使用 InvariantCulture，
/// 避免不同区域设置把小数点写成逗号导致仪器解析失败。
/// </summary>
public static class PowerSupplyScpi
{
    public const string Identity = "*IDN?";

    public const string ClearStatus = "*CLS";

    public const string MeasureVoltage = "MEAS:VOLT?";

    public const string MeasureCurrent = "MEAS:CURR?";

    public const string OutputState = "OUTP?";

    public static string SetVoltage(double volts) =>
        string.Format(CultureInfo.InvariantCulture, "VOLT {0:F3}", volts);

    public static string SetCurrentLimit(double amps) =>
        string.Format(CultureInfo.InvariantCulture, "CURR {0:F3}", amps);

    public static string SetOvp(double volts) =>
        string.Format(CultureInfo.InvariantCulture, "VOLT:PROT {0:F3}", volts);

    public static string Output(bool on) => on ? "OUTP ON" : "OUTP OFF";
}

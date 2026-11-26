using MultiStation.Instruments.Protocol.Can.Physical;

namespace MultiStation.Instruments.Drivers.Zlg;

/// <summary>
/// 整场测试期间持有 ZLG 设备：中途只开关通道，设备句柄始终保持打开，
/// 避免通道重配时把设备关掉又立刻打开而导致打开失败。
/// </summary>
public static class ZlgDeviceSession
{
    /// <summary>占用指定设备；返回的句柄在测试结束时释放。</summary>
    public static IDisposable Hold(string? deviceModel, int deviceIndex) =>
        ZlgDeviceLease.Acquire(ZlgCanApi.ResolveDeviceType(deviceModel), deviceIndex);
}

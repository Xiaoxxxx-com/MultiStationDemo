using MultiStation.Instruments.Protocol.Can.Physical;

namespace MultiStation.Instruments.Drivers.Zlg;

/// <summary>
/// 同一台 ZLG 设备的共享租约：首个通道打开设备，最后一个通道释放设备。
/// 便于在 200U 上同时使用 CAN0（主 ECU）与 CAN1（CCU）。
/// </summary>
internal sealed class ZlgDeviceLease : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(uint Type, int Index), ZlgDeviceLease> Leases = [];

    private readonly (uint Type, int Index) _key;
    private int _references = 1;
    private bool _disposed;

    private ZlgDeviceLease((uint Type, int Index) key, ZlgCanApi api, uint handle)
    {
        _key = key;
        Api = api;
        Handle = handle;
    }

    public ZlgCanApi Api { get; }

    public uint Handle { get; }

    /// <summary>增加一个通道引用；设备尚未打开时才真正打开。</summary>
    public static ZlgDeviceLease Acquire(uint deviceType, int deviceIndex)
    {
        var key = (deviceType, deviceIndex);
        lock (Gate)
        {
            if (Leases.TryGetValue(key, out var existing))
            {
                existing._references++;
                return existing;
            }

            var api = ZlgCanApi.Load();
            var handle = api.OpenDevice(deviceType, (uint)deviceIndex, 0);
            if (handle == 0)
            {
                api.Dispose();
                throw new IOException(
                    $"打开 {ZlgCanApi.DescribeDevice(deviceType)} 失败（设备序号 {deviceIndex}），" +
                    "请确认设备型号配置、设备已连接且未被其他软件占用");
            }

            var lease = new ZlgDeviceLease(key, api, handle);
            Leases[key] = lease;
            return lease;
        }
    }

    /// <summary>释放一个通道引用；引用归零时关闭设备并卸载驱动。</summary>
    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
            {
                return;
            }

            _references--;
            if (_references > 0)
            {
                return;
            }

            _disposed = true;
            Leases.Remove(_key);
            Api.CloseDevice(Handle);
            Api.Dispose();
        }
    }
}

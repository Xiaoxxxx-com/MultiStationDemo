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

            var tried = new List<string>();
            // 先试程序自带的那份；不匹配当前驱动时回退到 ZCANPRO 安装目录里的库
            var candidates = new List<string?> { null };
            candidates.AddRange(ZlgCanApi.AlternatePaths().Select(path => (string?)path));

            foreach (var candidate in candidates)
            {
                ZlgCanApi api;
                try
                {
                    api = ZlgCanApi.Load(candidate);
                }
                catch (Exception ex) when (ex is FileNotFoundException or DllNotFoundException
                    or BadImageFormatException)
                {
                    continue;
                }

                var handle = TryOpen(api, deviceType, deviceIndex, candidate is null ? 2 : 1);
                if (handle == 0)
                {
                    tried.Add(api.LoadedPath);
                    api.Dispose();
                    continue;
                }

                var lease = new ZlgDeviceLease(key, api, handle);
                Leases[key] = lease;
                return lease;
            }

            throw new IOException(
                $"打开 {ZlgCanApi.DescribeDevice(deviceType)} 失败（设备序号 {deviceIndex}），" +
                "请确认设备型号配置、设备已连接，且没有 ZCANPRO 或残留程序占用该设备。" +
                $"已尝试的驱动库：{(tried.Count == 0 ? "未找到 zlgcan.dll" : string.Join("；", tried))}");
        }
    }

    private static uint TryOpen(ZlgCanApi api, uint deviceType, int deviceIndex, int attempts)
    {
        uint handle = 0;
        for (var i = 0; i < attempts && handle == 0; i++)
        {
            if (i > 0)
            {
                Thread.Sleep(200);
            }

            handle = api.OpenDevice(deviceType, (uint)deviceIndex, 0);
        }

        return handle;
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

using System.Runtime.InteropServices;

namespace MultiStation.Instruments.Protocol.Can.Physical;

/// <summary>
/// zlgcan.dll（ZLG CAN 二次开发库）的动态加载与接口封装。
/// 注意：ZCANPRO 2.3.9 附带的是 32 位库，宿主进程必须以 x86 运行。
/// </summary>
public sealed class ZlgCanApi : IDisposable
{
    /// <summary>ZCAN_USBCANFD_400U 设备类型编号，与 zlgcan.h 保持一致。</summary>
    public const uint DeviceTypeUsbCanFd400U = 76;

    /// <summary>ZCAN_USBCANFD_200U 设备类型编号，与 zlgcan.h 保持一致。</summary>
    public const uint DeviceTypeUsbCanFd200U = 41;

    /// <summary>按配置的型号解析 ZLG 设备类型；空值默认 200U。</summary>
    public static uint ResolveDeviceType(string? deviceModel)
    {
        var value = deviceModel?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Equals("200U", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceTypeUsbCanFd200U;
        }

        if (value.Equals("400U", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceTypeUsbCanFd400U;
        }

        throw new FormatException($"未知的 ZLG CAN 设备型号：{deviceModel}（支持 200U / 400U）");
    }

    /// <summary>设备类型对应的显示名称，用于日志与异常提示。</summary>
    public static string DescribeDevice(uint deviceType) => deviceType switch
    {
        DeviceTypeUsbCanFd200U => "USBCANFD-200U",
        DeviceTypeUsbCanFd400U => "USBCANFD-400U",
        _ => $"ZLG CAN（类型 {deviceType}）",
    };

    private static readonly string[] CandidatePaths =
    [
        @"D:\ZCANPRO\zlgcan.dll",
        @"C:\Program Files (x86)\ZCANPRO\zlgcan.dll",
        @"C:\Program Files\ZCANPRO\zlgcan.dll",
    ];

    /// <summary>ZCANPRO 安装目录自带的驱动库；程序目录那份与当前驱动不匹配时用它回退。</summary>
    public static IEnumerable<string> AlternatePaths() =>
        CandidatePaths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

    private readonly IntPtr _module;

    /// <summary>实际加载的 zlgcan.dll 完整路径，便于现场排查版本不一致。</summary>
    public string LoadedPath { get; }

    private ZlgCanApi(IntPtr module, string loadedPath)
    {
        _module = module;
        LoadedPath = loadedPath;
        OpenDevice = Load<OpenDeviceFn>("ZCAN_OpenDevice");
        CloseDevice = Load<CloseDeviceFn>("ZCAN_CloseDevice");
        InitCan = Load<InitCanFn>("ZCAN_InitCAN");
        InitCanClassic = Load<InitCanClassicFn>("ZCAN_InitCAN");
        StartCan = Load<StartCanFn>("ZCAN_StartCAN");
        ResetCan = Load<ResetCanFn>("ZCAN_ResetCAN");
        TransmitFd = Load<TransmitFdFn>("ZCAN_TransmitFD");
        ReceiveFd = Load<ReceiveFdFn>("ZCAN_ReceiveFD");
        Transmit = Load<TransmitFn>("ZCAN_Transmit");
        Receive = Load<ReceiveFn>("ZCAN_Receive");
        SetValue = Load<SetValueFn>("ZCAN_SetValue");
    }

    public OpenDeviceFn OpenDevice { get; }

    public CloseDeviceFn CloseDevice { get; }

    public InitCanFn InitCan { get; }

    public InitCanClassicFn InitCanClassic { get; }

    public StartCanFn StartCan { get; }

    public ResetCanFn ResetCan { get; }

    public TransmitFdFn TransmitFd { get; }

    public ReceiveFdFn ReceiveFd { get; }

    public TransmitFn Transmit { get; }

    public ReceiveFn Receive { get; }

    public SetValueFn SetValue { get; }

    /// <summary>加载 zlgcan.dll：优先显式路径，其次环境变量 ZLGCAN_DLL、程序目录、系统目录与 ZCANPRO 安装目录。</summary>
    public static ZlgCanApi Load(string? explicitPath = null)
    {
        if (Environment.Is64BitProcess)
        {
            throw new NotSupportedException(
                "zlgcan.dll 为 32 位库（ZCANPRO 2.3.9 自带），请以 x86 目标运行程序，或安装 ZLG 官方 64 位驱动库");
        }

        var path = explicitPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable("ZLGCAN_DLL");
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            path = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "zlgcan.dll"),
                    Path.Combine(Environment.SystemDirectory, "zlgcan.dll"),
                }
                .Concat(CandidatePaths)
                .FirstOrDefault(File.Exists);
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new FileNotFoundException(
                "未找到 zlgcan.dll，请安装 ZLG CAN 驱动（ZCANPRO）或通过环境变量 ZLGCAN_DLL 指定完整路径");
        }

        return new ZlgCanApi(NativeLibrary.Load(path), path);
    }

    public void Dispose() => NativeLibrary.Free(_module);

    private T Load<T>(string exportName) where T : Delegate
    {
        var address = NativeLibrary.GetExport(_module, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint OpenDeviceFn(uint deviceType, uint deviceIndex, uint reserved);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint CloseDeviceFn(uint deviceHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint InitCanFn(uint deviceHandle, uint canIndex, ref ZlgChannelInitConfig config);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint StartCanFn(uint channelHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint ResetCanFn(uint channelHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint TransmitFdFn(uint channelHandle, ref ZlgTransmitFdData data, uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint ReceiveFdFn(
        uint channelHandle, [Out] ZlgReceiveFdData[] data, uint length, int waitTimeMs);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint TransmitFn(uint channelHandle, ref ZlgTransmitData data, uint length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint ReceiveFn(
        uint channelHandle, [Out] ZlgReceiveData[] data, uint length, int waitTimeMs);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate uint InitCanClassicFn(uint deviceHandle, uint canIndex, ref ZlgCanChannelInitConfig config);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate uint SetValueFn(uint deviceHandle, string path, string value);
}

/// <summary>CAN FD 通道初始化参数（对应 ZCAN_CHANNEL_INIT_CONFIG 的 canfd 分支）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgCanFdInitConfig
{
    public uint AccCode;
    public uint AccMask;
    public uint AbitTiming;
    public uint DbitTiming;
    public uint Brp;
    public byte Filter;
    public byte Mode;
    public ushort Pad;
    public uint Reserved;
}

/// <summary>通道初始化配置：CanType 0=CAN、1=CANFD。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgChannelInitConfig
{
    public uint CanType;
    public ZlgCanFdInitConfig CanFd;
}

/// <summary>CAN FD 报文（canfd_frame）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgCanFdFrame
{
    public uint CanId;
    public byte Len;
    public byte Flags;
    public byte Res0;
    public byte Res1;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] Data;
}

/// <summary>发送结构（ZCAN_TransmitFD_Data）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgTransmitFdData
{
    public ZlgCanFdFrame Frame;
    public uint TransmitType;
}

/// <summary>接收结构（ZCAN_ReceiveFD_Data），时间戳单位微秒。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgReceiveFdData
{
    public ZlgCanFdFrame Frame;
    public ulong Timestamp;
}

/// <summary>经典 CAN 通道初始化参数（对应 ZCAN_CHANNEL_INIT_CONFIG 的 can 分支，联合体固定 28 字节）。</summary>
[StructLayout(LayoutKind.Sequential, Size = 28)]
public struct ZlgCanInitConfig
{
    public uint AccCode;
    public uint AccMask;
    public uint Reserved;
    public byte Filter;
    public byte Timing0;
    public byte Timing1;
    public byte Mode;
}

/// <summary>经典 CAN 通道初始化配置：CanType 固定 0。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgCanChannelInitConfig
{
    public uint CanType;
    public ZlgCanInitConfig Can;
}

/// <summary>经典 CAN 报文（can_frame）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgCanFrame
{
    public uint CanId;
    public byte CanDlc;
    public byte Pad;
    public byte Res0;
    public byte Res1;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public byte[] Data;
}

/// <summary>经典 CAN 发送结构（ZCAN_Transmit_Data）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgTransmitData
{
    public ZlgCanFrame Frame;
    public uint TransmitType;
}

/// <summary>经典 CAN 接收结构（ZCAN_Receive_Data），时间戳单位微秒。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ZlgReceiveData
{
    public ZlgCanFrame Frame;
    public ulong Timestamp;
}

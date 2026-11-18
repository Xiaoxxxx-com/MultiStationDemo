namespace MultiStation.Instruments.Protocol.Uds.Algorithm;

/// <summary>UDS 服务标识（ISO 14229-1）。</summary>
public static class UdsService
{
    public const byte DiagnosticSessionControl = 0x10;
    public const byte EcuReset = 0x11;
    public const byte ReadDataByIdentifier = 0x22;
    public const byte SecurityAccess = 0x27;
    public const byte CommunicationControl = 0x28;
    public const byte WriteDataByIdentifier = 0x2E;
    public const byte RoutineControl = 0x31;
    public const byte RequestDownload = 0x34;
    public const byte TransferData = 0x36;
    public const byte RequestTransferExit = 0x37;
    public const byte TesterPresent = 0x3E;
    public const byte ControlDtcSetting = 0x85;
    public const byte NegativeResponse = 0x7F;
}

/// <summary>诊断会话类型。</summary>
public enum UdsSession : byte
{
    Default = 0x01,
    Programming = 0x02,
    Extended = 0x03,
}

/// <summary>例程控制类型。</summary>
public enum UdsRoutineControlType : byte
{
    Start = 0x01,
    Stop = 0x02,
    RequestResults = 0x03,
}

/// <summary>刷写常用例程 ID（示例值，需与 ECU 诊断规范对齐）。</summary>
public static class UdsRoutine
{
    public const ushort EraseMemory = 0xFF00;
    public const ushort CheckIntegrity = 0x0202;
}

/// <summary>否定响应码（NRC）。</summary>
public enum UdsNegativeResponseCode : byte
{
    GeneralReject = 0x10,
    ServiceNotSupported = 0x11,
    SubFunctionNotSupported = 0x12,
    IncorrectMessageLength = 0x13,
    ConditionsNotCorrect = 0x22,
    RequestSequenceError = 0x24,
    RequestOutOfRange = 0x31,
    SecurityAccessDenied = 0x33,
    InvalidKey = 0x35,
    ExceededNumberOfAttempts = 0x36,
    RequiredTimeDelayNotExpired = 0x37,
    UploadDownloadNotAccepted = 0x70,
    TransferDataSuspended = 0x71,
    GeneralProgrammingFailure = 0x72,
    WrongBlockSequenceCounter = 0x73,
    ResponsePending = 0x78,
}

/// <summary>ECU 返回否定响应时抛出。</summary>
public sealed class UdsNegativeResponseException(byte requestService, UdsNegativeResponseCode code) : Exception($"UDS 否定响应：服务 0x{requestService:X2}，NRC 0x{(byte)code:X2}（{code}）")
{
    public byte RequestService { get; } = requestService;

    public UdsNegativeResponseCode Code { get; } = code;
}

/// <summary>安全访问算法：默认示例为逐字节异或 0xA5，现场需替换为 OEM 的 SeedKey 算法。</summary>
public static class UdsSecurityAccess
{
    public const byte DefaultXorMask = 0xA5;

    public static byte[] XorKey(ReadOnlySpan<byte> seed, byte mask = DefaultXorMask)
    {
        var key = new byte[seed.Length];
        for (var i = 0; i < seed.Length; i++)
        {
            key[i] = (byte)(seed[i] ^ mask);
        }

        return key;
    }
}

namespace MultiStation.Instruments.Protocol.Can.Algorithm;

/// <summary>
/// CAN 标识符编解码：ZLG 设备的 can_id 高 3 位为标志位，
/// 第 31 位表示扩展帧，其余为实际 ID。
/// </summary>
public static class CanFrameCodec
{
    public const uint ExtendedFlag = 0x8000_0000;

    public const int StandardIdMask = 0x7FF;

    public const int ExtendedIdMask = 0x1FFF_FFFF;

    /// <summary>把报文标识符编码为设备使用的 can_id。</summary>
    public static uint Encode(CanFrame frame)
    {
        if (frame.ExtendedId)
        {
            return ((uint)frame.Id & ExtendedIdMask) | ExtendedFlag;
        }

        return (uint)frame.Id & StandardIdMask;
    }

    /// <summary>从设备 can_id 还原报文标识符。</summary>
    public static CanFrame Decode(uint canId, ReadOnlySpan<byte> data)
    {
        var extended = (canId & ExtendedFlag) != 0;
        var id = (int)(canId & ExtendedIdMask);
        return new CanFrame(id, data.ToArray(), extended);
    }
}

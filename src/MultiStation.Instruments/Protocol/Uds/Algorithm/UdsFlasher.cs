using System.Diagnostics;
using System.IO;
using System.Text;

namespace MultiStation.Instruments.Protocol.Uds.Algorithm;

/// <summary>ECU 刷写参数。</summary>
public sealed record UdsFlashConfig(
    uint MemoryAddress,
    byte[] Firmware,
    byte[] Fingerprint,
    ushort FingerprintDid = 0xF198,
    byte SecurityLevel = 0x01,
    bool EraseMemory = true,
    int MaxBlockFallback = 1024,
    Action<string>? Log = null);

/// <summary>刷写结果。</summary>
public sealed record UdsFlashResult(
    int TotalBytes,
    int Blocks,
    int MaxBlockLength,
    string Vin,
    string SoftwareVersion,
    TimeSpan Elapsed);

/// <summary>
/// ECU 刷写流程（UDS 应用层）：扩展会话 → 读版本 → 编程会话 → 安全访问 →
/// 写指纹 → 擦除 → 请求下载 → 传输数据 → 校验 → 复位。
/// </summary>
public static class UdsFlasher
{
    public const ushort VinDid = 0xF190;

    public const ushort SoftwareVersionDid = 0xF195;

    public static async Task<UdsFlashResult> FlashAsync(
        UdsClient client, UdsFlashConfig config, CancellationToken cancellationToken = default)
    {
        var log = config.Log ?? (_ => { });
        var stopwatch = Stopwatch.StartNew();

        log("[1/9] 切换扩展会话");
        await client.StartSessionAsync(UdsSession.Extended, cancellationToken).ConfigureAwait(false);

        log("[2/9] 读取 VIN 与软件版本");
        var vin = Encoding.ASCII.GetString(
            await client.ReadDataByIdentifierAsync(VinDid, cancellationToken).ConfigureAwait(false)).Trim();
        var software = Encoding.ASCII.GetString(
            await client.ReadDataByIdentifierAsync(SoftwareVersionDid, cancellationToken).ConfigureAwait(false)).Trim();
        log($"      VIN={vin} 软件版本={software}");
        await client.TesterPresentAsync(cancellationToken).ConfigureAwait(false);

        log("[3/9] 切换编程会话");
        await client.StartSessionAsync(UdsSession.Programming, cancellationToken).ConfigureAwait(false);

        log("[4/9] 安全访问（Seed & Key）");
        var seed = await client.SecurityAccessSeedAsync(config.SecurityLevel, cancellationToken).ConfigureAwait(false);
        log($"      Seed={Convert.ToHexString(seed)}");
        var key = UdsSecurityAccess.XorKey(seed);
        await client.SecurityAccessKeyAsync(config.SecurityLevel, key, cancellationToken).ConfigureAwait(false);

        log("[5/9] 写入指纹");
        await client.WriteDataByIdentifierAsync(
            config.FingerprintDid, config.Fingerprint, cancellationToken).ConfigureAwait(false);

        log("[6/9] 关闭 DTC 记录并禁用常规通信");
        await client.ControlDtcSettingAsync(false, cancellationToken).ConfigureAwait(false);
        await client.CommunicationControlAsync(0x03, 0x01, cancellationToken).ConfigureAwait(false);

        if (config.EraseMemory)
        {
            log("[7/9] 擦除 Flash");
            await client.RoutineStartAsync(
                UdsRoutine.EraseMemory, MemoryRange(config), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            log("[7/9] 跳过擦除（配置未启用）");
        }

        log("[8/9] 请求下载并传输数据");
        var maxBlock = await client.RequestDownloadAsync(
            config.MemoryAddress, (uint)config.Firmware.Length, 4, 4, 0x00, cancellationToken).ConfigureAwait(false);
        log($"      ECU 允许单块最大长度={maxBlock}");

        var chunkSize = maxBlock > 2 ? Math.Min(maxBlock - 2, 4093) : config.MaxBlockFallback;
        chunkSize = Math.Clamp(chunkSize, 1, 4093);

        var blocks = 0;
        byte blockCounter = 0x01;
        var offset = 0;
        while (offset < config.Firmware.Length)
        {
            var length = Math.Min(chunkSize, config.Firmware.Length - offset);
            var block = new byte[length];
            Array.Copy(config.Firmware, offset, block, 0, length);
            await client.TransferDataAsync(blockCounter, block, cancellationToken).ConfigureAwait(false);

            offset += length;
            blocks++;
            blockCounter = (byte)((blockCounter + 1) & 0xFF);

            if (blocks % 16 == 0 || offset == config.Firmware.Length)
            {
                log($"      已传输 {offset}/{config.Firmware.Length} 字节（{blocks} 块）");
            }
        }

        await client.RequestTransferExitAsync(cancellationToken).ConfigureAwait(false);

        log("[9/9] 校验完整性并复位 ECU");
        var integrity = await client.RoutineStartAsync(
            UdsRoutine.CheckIntegrity, MemoryRange(config), cancellationToken).ConfigureAwait(false);
        if (integrity.Length > 0 && integrity[^1] != 0x01)
        {
            throw new IOException($"完整性校验失败（结果：{Convert.ToHexString(integrity)}）");
        }

        await client.CommunicationControlAsync(0x01, 0x01, cancellationToken).ConfigureAwait(false);
        await client.ControlDtcSettingAsync(true, cancellationToken).ConfigureAwait(false);
        await client.EcuResetAsync(true, cancellationToken).ConfigureAwait(false);

        stopwatch.Stop();
        log($"刷写完成：{config.Firmware.Length} 字节 / {blocks} 块，用时 {stopwatch.Elapsed.TotalSeconds:F1}s");
        return new UdsFlashResult(
            config.Firmware.Length, blocks, maxBlock, vin, software, stopwatch.Elapsed);
    }

    private static byte[] MemoryRange(UdsFlashConfig config)
    {
        var range = new byte[8];
        var address = config.MemoryAddress;
        var size = (uint)config.Firmware.Length;
        for (var i = 0; i < 4; i++)
        {
            range[i] = (byte)(address >> (8 * (3 - i)));
            range[4 + i] = (byte)(size >> (8 * (3 - i)));
        }

        return range;
    }
}

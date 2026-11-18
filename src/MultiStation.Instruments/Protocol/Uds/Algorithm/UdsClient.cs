using System.IO;
using MultiStation.Instruments.Protocol.Uds.Physical;

namespace MultiStation.Instruments.Protocol.Uds.Algorithm;

/// <summary>
/// UDS 诊断客户端（ISO 14229-1）：封装刷写流程用到的服务，自动处理 0x78 响应挂起。
/// 负响应直接抛出 <see cref="UdsNegativeResponseException"/>。
/// </summary>
public sealed class UdsClient(IUdsTransport transport, IsoTpOptions options)
{
    private readonly IUdsTransport _transport = transport;
    private readonly IsoTpOptions _options = options;

    /// <summary>发送任意 UDS 请求并等待正响应（返回完整响应字节）。</summary>
    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken cancellationToken = default)
    {
        await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);

        var pending = false;
        while (true)
        {
            var timeout = TimeSpan.FromMilliseconds(
                pending ? _options.P2ExtendedMilliseconds : _options.P2Milliseconds);
            var response = await _transport.ReceiveAsync(timeout, cancellationToken).ConfigureAwait(false);

            if (response.Length >= 3 && response[0] == UdsService.NegativeResponse && response[1] == request[0])
            {
                var nrc = (UdsNegativeResponseCode)response[2];
                if (nrc == UdsNegativeResponseCode.ResponsePending)
                {
                    pending = true;
                    continue;
                }

                throw new UdsNegativeResponseException(request[0], nrc);
            }

            if (response[0] != (byte)(request[0] + 0x40))
            {
                throw new IOException(
                    $"UDS 响应 SID 不匹配：请求 0x{request[0]:X2}，响应 0x{response[0]:X2}");
            }

            return response;
        }
    }

    public async Task<UdsSession> StartSessionAsync(UdsSession session, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(
            [UdsService.DiagnosticSessionControl, (byte)session], cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[1] != (byte)session)
        {
            throw new IOException("诊断会话切换响应非法");
        }

        return (UdsSession)response[1];
    }

    public Task EcuResetAsync(bool hardReset = true, CancellationToken cancellationToken = default) =>
        RequestAsync([UdsService.EcuReset, (byte)(hardReset ? 0x01 : 0x03)], cancellationToken);

    public async Task<byte[]> ReadDataByIdentifierAsync(ushort dataIdentifier, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(
            [UdsService.ReadDataByIdentifier, (byte)(dataIdentifier >> 8), (byte)dataIdentifier],
            cancellationToken).ConfigureAwait(false);
        return response.Length > 3 ? response[3..] : [];
    }

    public async Task<byte[]> SecurityAccessSeedAsync(byte level, CancellationToken cancellationToken = default)
    {
        var response = await RequestAsync(
            [UdsService.SecurityAccess, level], cancellationToken).ConfigureAwait(false);
        return response.Length > 2 ? response[2..] : [];
    }

    public Task SecurityAccessKeyAsync(byte level, byte[] key, CancellationToken cancellationToken = default)
    {
        var request = new byte[2 + key.Length];
        request[0] = UdsService.SecurityAccess;
        request[1] = (byte)(level + 1);
        Array.Copy(key, 0, request, 2, key.Length);
        return RequestAsync(request, cancellationToken);
    }

    public Task WriteDataByIdentifierAsync(
        ushort dataIdentifier, byte[] data, CancellationToken cancellationToken = default)
    {
        var request = new byte[3 + data.Length];
        request[0] = UdsService.WriteDataByIdentifier;
        request[1] = (byte)(dataIdentifier >> 8);
        request[2] = (byte)dataIdentifier;
        Array.Copy(data, 0, request, 3, data.Length);
        return RequestAsync(request, cancellationToken);
    }

    public Task<byte[]> RoutineStartAsync(
        ushort routineIdentifier, byte[]? parameters = null, CancellationToken cancellationToken = default) =>
        RoutineControlAsync(UdsRoutineControlType.Start, routineIdentifier, parameters, cancellationToken);

    public Task<byte[]> RoutineRequestResultsAsync(
        ushort routineIdentifier, byte[]? parameters = null, CancellationToken cancellationToken = default) =>
        RoutineControlAsync(UdsRoutineControlType.RequestResults, routineIdentifier, parameters, cancellationToken);

    public async Task<byte[]> RoutineControlAsync(
        UdsRoutineControlType controlType,
        ushort routineIdentifier,
        byte[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        parameters ??= [];
        var request = new byte[4 + parameters.Length];
        request[0] = UdsService.RoutineControl;
        request[1] = (byte)controlType;
        request[2] = (byte)(routineIdentifier >> 8);
        request[3] = (byte)routineIdentifier;
        Array.Copy(parameters, 0, request, 4, parameters.Length);

        var response = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Length > 4 ? response[4..] : [];
    }

    /// <summary>请求下载：发送地址与长度，返回 ECU 允许的单块最大字节数。</summary>
    public async Task<int> RequestDownloadAsync(
        uint memoryAddress,
        uint memorySize,
        int addressLength = 4,
        int sizeLength = 4,
        byte dataFormatIdentifier = 0x00,
        CancellationToken cancellationToken = default)
    {
        var request = new byte[4 + addressLength + sizeLength];
        request[0] = UdsService.RequestDownload;
        request[1] = dataFormatIdentifier;
        request[2] = (byte)addressLength;
        request[3] = (byte)sizeLength;

        for (var i = 0; i < addressLength; i++)
        {
            request[4 + i] = (byte)(memoryAddress >> (8 * (addressLength - 1 - i)));
        }

        for (var i = 0; i < sizeLength; i++)
        {
            request[4 + addressLength + i] = (byte)(memorySize >> (8 * (sizeLength - 1 - i)));
        }

        var response = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Length < 3)
        {
            throw new IOException("请求下载响应长度非法");
        }

        var lengthBytes = (response[1] >> 4) & 0x0F;
        if (lengthBytes == 0)
        {
            lengthBytes = 2;
        }

        var maxBlock = 0;
        for (var i = 0; i < lengthBytes && 2 + i < response.Length; i++)
        {
            maxBlock = (maxBlock << 8) | response[2 + i];
        }

        return maxBlock;
    }

    public async Task TransferDataAsync(
        byte blockSequenceCounter, byte[] data, CancellationToken cancellationToken = default)
    {
        var request = new byte[2 + data.Length];
        request[0] = UdsService.TransferData;
        request[1] = blockSequenceCounter;
        Array.Copy(data, 0, request, 2, data.Length);

        var response = await RequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Length < 2 || response[1] != blockSequenceCounter)
        {
            throw new IOException("传输数据块序号不匹配");
        }
    }

    public Task RequestTransferExitAsync(CancellationToken cancellationToken = default) =>
        RequestAsync([UdsService.RequestTransferExit], cancellationToken);

    public Task TesterPresentAsync(CancellationToken cancellationToken = default) =>
        RequestAsync([UdsService.TesterPresent, (byte)0x00], cancellationToken);

    public Task ControlDtcSettingAsync(bool enable, CancellationToken cancellationToken = default) =>
        RequestAsync([UdsService.ControlDtcSetting, (byte)(enable ? 0x01 : 0x02)], cancellationToken);

    public Task CommunicationControlAsync(
        byte controlType, byte communicationType = 0x01, CancellationToken cancellationToken = default) =>
        RequestAsync([UdsService.CommunicationControl, controlType, communicationType], cancellationToken);
}

using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace MultiStation.Instruments.Protocol.Scpi.Physical;

/// <summary>
/// TCP 原始套接字 SCPI 通道：GW Instek PSW 系列默认在 2268 端口提供 SCPI 文本服务，
/// 命令以 LF 结尾，回读值按行返回。
/// </summary>
public sealed class TcpScpiTransport : IScpiTransport, IDisposable
{
    public const int DefaultPort = 2268;

    private readonly string _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public TcpScpiTransport(string host, int port = DefaultPort, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("仪器地址不能为空", nameof(host));
        }

        _host = host;
        _port = port;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
    }

    public string Endpoint => string.Create(CultureInfo.InvariantCulture, $"{_host}:{_port}");

    /// <summary>
    /// 解析仪器地址：支持 VISA 风格的 "TCPIP::192.168.1.2::2268"，
    /// 也支持简写 "192.168.1.2:2268"；省略端口时使用 2268。
    /// </summary>
    public static TcpScpiTransport FromAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("仪器地址不能为空", nameof(address));
        }

        var text = address.Trim();
        if (text.StartsWith("TCPIP::", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split("::", StringSplitOptions.RemoveEmptyEntries);
            var host = parts.Length > 1 ? parts[1] : throw new ArgumentException($"无法解析仪器地址：{address}", nameof(address));
            var port = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : DefaultPort;
            return new TcpScpiTransport(host, port);
        }

        var separator = text.LastIndexOf(':');
        if (separator > 0 && int.TryParse(text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var explicitPort))
        {
            return new TcpScpiTransport(text[..separator], explicitPort);
        }

        return new TcpScpiTransport(text);
    }

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return;
        }

        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);
        var stream = client.GetStream();
        _client = client;
        _reader = new StreamReader(stream, Encoding.ASCII);
        _writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
    }

    public Task CloseAsync()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _reader = null;
        _writer = null;
        _client = null;
        return Task.CompletedTask;
    }

    public async Task WriteAsync(string command, CancellationToken cancellationToken = default)
    {
        var writer = _writer ?? throw new InvalidOperationException("SCPI 通道未打开");
        using var timeout = CreateTimeout(cancellationToken);
        await writer.WriteLineAsync(command.AsMemory(), timeout.Token).ConfigureAwait(false);
    }

    public async Task<string> QueryAsync(string command, CancellationToken cancellationToken = default)
    {
        var reader = _reader ?? throw new InvalidOperationException("SCPI 通道未打开");
        await WriteAsync(command, cancellationToken).ConfigureAwait(false);
        using var timeout = CreateTimeout(cancellationToken);
        var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        return line ?? throw new IOException($"仪器「{Endpoint}」未返回数据（命令：{command}）");
    }

    public void Dispose() => _ = CloseAsync();

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        return cts;
    }
}

using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace MultiStation.Platform.Core.Logging;

/// <summary>
/// 数据落盘域：日志与测试结果先进有界 Channel，再由单个后台任务批量写 JSONL。
/// 工位线程永远不会因为磁盘 IO 而阻塞。
/// </summary>
public sealed class TelemetrySink
{
    private const int RecentCapacity = 500;

    private readonly Channel<LogEntry> _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(8192)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
    });

    private readonly ConcurrentQueue<LogEntry> _recent = new();
    private readonly StreamWriter _writer;
    private readonly Task _writerTask;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private long _sequence;
    private long _dropped;

    public TelemetrySink(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"run-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        _writer = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = false,
        };
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public string FilePath { get; }

    public long Dropped => Interlocked.Read(ref _dropped);

    public void Write(LogLevel level, string message, IReadOnlyDictionary<string, object?> fields, string? exception)
    {
        var entry = new LogEntry(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.Now,
            level,
            message,
            fields,
            exception);

        if (!_channel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public IReadOnlyList<LogEntry> Recent(int count)
    {
        var snapshot = _recent.ToArray();
        return snapshot.Length <= count ? snapshot : snapshot[^count..];
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var entry in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RecentCapacity && _recent.TryDequeue(out _))
            {
            }

            await _writer.WriteLineAsync(JsonSerializer.Serialize(entry, _json)).ConfigureAwait(false);
            if (_channel.Reader.Count == 0)
            {
                await _writer.FlushAsync().ConfigureAwait(false);
            }
        }

        await _writer.FlushAsync().ConfigureAwait(false);
    }
}

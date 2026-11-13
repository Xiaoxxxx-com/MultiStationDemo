namespace MultiStation.Core.Logging;

public interface IAppLog
{
    IAppLog ForContext(string key, object? value);

    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);

    /// <summary>写一条结构化测试结果（落盘级别 Result）。</summary>
    void Record(string message, IReadOnlyDictionary<string, object?> fields);
}

/// <summary>
/// 极简结构化日志器：每次 ForContext 产生一个带固定字段的子日志器，
/// 字段随事件一起写入落盘域，后端可按 StationId 还原单个工位时间线。
/// </summary>
public sealed class StructuredLog : IAppLog
{
    private readonly TelemetrySink _sink;
    private readonly Dictionary<string, object?> _context;

    public StructuredLog(TelemetrySink sink)
        : this(sink, new Dictionary<string, object?>())
    {
    }

    private StructuredLog(TelemetrySink sink, Dictionary<string, object?> context)
    {
        _sink = sink;
        _context = context;
    }

    public IAppLog ForContext(string key, object? value)
    {
        var merged = new Dictionary<string, object?>(_context)
        {
            [key] = value,
        };
        return new StructuredLog(_sink, merged);
    }

    public void Debug(string message) => Emit(LogLevel.Debug, message, null, null);

    public void Info(string message) => Emit(LogLevel.Info, message, null, null);

    public void Warn(string message) => Emit(LogLevel.Warn, message, null, null);

    public void Error(string message, Exception? exception = null) =>
        Emit(LogLevel.Error, message, exception?.ToString(), null);

    public void Record(string message, IReadOnlyDictionary<string, object?> fields) =>
        Emit(LogLevel.Result, message, null, fields);

    private void Emit(LogLevel level, string message, string? exception, IReadOnlyDictionary<string, object?>? extraFields)
    {
        IReadOnlyDictionary<string, object?> fields;
        if (extraFields is null || extraFields.Count == 0)
        {
            fields = new Dictionary<string, object?>(_context);
        }
        else
        {
            var merged = new Dictionary<string, object?>(_context);
            foreach (var pair in extraFields)
            {
                merged[pair.Key] = pair.Value;
            }

            fields = merged;
        }

        _sink.Write(level, message, fields, exception);
    }
}

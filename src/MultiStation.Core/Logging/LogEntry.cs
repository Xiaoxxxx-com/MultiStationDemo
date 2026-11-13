namespace MultiStation.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
    Result,
}

/// <summary>结构化日志事件。StationId / WorkItemId 等作为字段携带，方便按工位过滤。</summary>
public sealed record LogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, object?> Fields,
    string? Exception = null);

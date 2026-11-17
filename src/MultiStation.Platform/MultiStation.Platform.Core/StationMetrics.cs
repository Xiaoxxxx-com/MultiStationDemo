namespace MultiStation.Platform.Core;

/// <summary>工位健康指标快照，UI/调度器只读，不跨线程访问工位内部状态。</summary>
public sealed record StationMetrics(
    string StationId,
    string Name,
    StationState State,
    string? CurrentWorkId,
    int QueueDepth,
    long Processed,
    long Passed,
    long Failed,
    int ConsecutiveFailures,
    DateTimeOffset StateSince,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastError,
    string? LastMessage)
{
    public TimeSpan StateAge => DateTimeOffset.Now - StateSince;
}

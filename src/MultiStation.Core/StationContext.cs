namespace MultiStation.Core;

/// <summary>
/// 工位运行时上下文。状态迁移只在工位自己的异步循环中发生，
/// 外部（调度器/UI）只能通过信号和只读快照与它交互。
/// </summary>
public sealed class StationContext
{
    private readonly StationDefinition _definition;

    internal StationContext(StationDefinition definition)
    {
        _definition = definition;
        StateSince = DateTimeOffset.Now;
    }

    public string StationId => _definition.Id;

    public string Name => _definition.Name;

    public StationState State { get; private set; } = StationState.Idle;

    public WorkItem? CurrentWork { get; private set; }

    public DateTimeOffset StateSince { get; private set; }

    /// <summary>调度器取走工件后置位，驱动 Completed → WaitingForWork。</summary>
    public AsyncSignal DownstreamSignal { get; } = new();

    /// <summary>人工/上游复位故障工位时置位，驱动 Error → Idle。</summary>
    public AsyncSignal ResetSignal { get; } = new();

    internal long Processed { get; private set; }

    internal long Passed { get; private set; }

    internal long Failed { get; private set; }

    internal int ConsecutiveFailures { get; private set; }

    internal DateTimeOffset? LastSuccessAt { get; private set; }

    internal DateTimeOffset? LastFailureAt { get; private set; }

    internal string? LastError { get; private set; }

    internal string? LastMessage { get; private set; }

    internal void Transition(StationEvent evt)
    {
        var next = StationStateMachine.Next(State, evt);
        State = next;
        StateSince = DateTimeOffset.Now;
    }

    internal void SetCurrentWork(WorkItem? work) => CurrentWork = work;

    internal void RecordResult(StationResult result)
    {
        Processed++;
        LastMessage = $"{(result.Passed ? "PASS" : "NG")} · {result.Message}";
        if (result.Passed)
        {
            Passed++;
            ConsecutiveFailures = 0;
            LastSuccessAt = DateTimeOffset.Now;
        }
        else
        {
            Failed++;
            ConsecutiveFailures++;
            LastFailureAt = DateTimeOffset.Now;
            LastError = result.Message;
        }
    }

    internal StationMetrics BuildMetrics(int queueDepth) => new(
        StationId,
        Name,
        State,
        CurrentWork?.Id,
        queueDepth,
        Processed,
        Passed,
        Failed,
        ConsecutiveFailures,
        StateSince,
        LastSuccessAt,
        LastFailureAt,
        LastError,
        LastMessage);
}

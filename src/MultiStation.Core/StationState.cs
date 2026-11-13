namespace MultiStation.Core;

public enum StationState
{
    Idle,
    WaitingForWork,
    Processing,
    Completed,
    Error,
    Stopped,
}

public enum StationEvent
{
    Start,
    WorkArrived,
    ProcessingDone,
    ProcessingFailed,
    WorkRemoved,
    Reset,
    Stop,
}

/// <summary>
/// 用状态转移表代替嵌套 switch，新增状态/事件只改表，不改执行循环。
/// </summary>
public static class StationStateMachine
{
    private static readonly Dictionary<(StationState State, StationEvent Event), StationState> Table = new()
    {
        [(StationState.Idle, StationEvent.Start)] = StationState.WaitingForWork,
        [(StationState.WaitingForWork, StationEvent.WorkArrived)] = StationState.Processing,
        [(StationState.Processing, StationEvent.ProcessingDone)] = StationState.Completed,
        [(StationState.Processing, StationEvent.ProcessingFailed)] = StationState.Error,
        [(StationState.Completed, StationEvent.WorkRemoved)] = StationState.WaitingForWork,
        [(StationState.Error, StationEvent.Reset)] = StationState.Idle,
        [(StationState.Stopped, StationEvent.Start)] = StationState.Idle,

        [(StationState.Idle, StationEvent.Stop)] = StationState.Stopped,
        [(StationState.WaitingForWork, StationEvent.Stop)] = StationState.Stopped,
        [(StationState.Processing, StationEvent.Stop)] = StationState.Stopped,
        [(StationState.Completed, StationEvent.Stop)] = StationState.Stopped,
        [(StationState.Error, StationEvent.Stop)] = StationState.Stopped,
    };

    public static StationState Next(StationState state, StationEvent evt)
    {
        if (Table.TryGetValue((state, evt), out var next))
        {
            return next;
        }

        throw new InvalidOperationException($"工位状态机不允许从 {state} 迁移到 {evt}");
    }
}

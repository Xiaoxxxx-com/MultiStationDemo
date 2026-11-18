using System.Threading.Channels;
using MultiStation.Platform.Core.Logging;

namespace MultiStation.Platform.Core;

/// <summary>
/// 调度器：只决定工件进入哪个工位的输入队列，以及完成后路由到下游。
/// 所有路由事件在单个异步循环中串行处理，因此路由状态不需要任何锁。
/// </summary>
public sealed class Dispatcher(IAppLog log, Action<WorkItem>? onFinished = null) : IDisposable
{
    private readonly Channel<DispatchEvent> _events = Channel.CreateUnbounded<DispatchEvent>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly Channel<WorkItem> _finished = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(1024)
    {
        SingleReader = true,
    });

    private readonly Dictionary<string, StationRuntime> _stations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecipeDefinition> _recipes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, WorkProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly IAppLog _log = log;
    private readonly Action<WorkItem>? _onFinished = onFinished;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _finishedLoop;
    private long _submitted;
    private long _passed;
    private long _failed;
    private long _inProcess;

    public Func<StationCompletion, ValueTask> CompletionSink => completion =>
    {
        // 完成事件一产生就释放工位，避免工位在 Completed 上空等调度器；
        // 调度器只负责后续路由，即使下游队列满也不会把工位锁死。
        if (completion.StationState == StationState.Completed)
        {
            completion.Station.Context.DownstreamSignal.Set();
        }

        _events.Writer.TryWrite(new StationCompleted(completion));
        return ValueTask.CompletedTask;
    };

    public void RegisterRecipe(RecipeDefinition recipe) => _recipes[recipe.Id] = recipe;

    public void AttachStation(StationRuntime station) => _stations[station.Id] = station;

    public bool Submit(WorkItem work)
    {
        if (!_events.Writer.TryWrite(new WorkArrived(work)))
        {
            return false;
        }

        Interlocked.Increment(ref _submitted);
        Interlocked.Increment(ref _inProcess);
        return true;
    }

    public DispatcherMetrics Snapshot() => new(
        Interlocked.Read(ref _submitted),
        Interlocked.Read(ref _passed),
        Interlocked.Read(ref _failed),
        Interlocked.Read(ref _inProcess));

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _finishedLoop = Task.Run(DrainFinishedAsync);
    }

    /// <summary>释放资源：停止调度器。</summary>
    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    /// <summary>先停调度器（不再接收新工件），再由上层逐个停止工位。</summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        _events.Writer.TryComplete();

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _finished.Writer.TryComplete();
        if (_finishedLoop is not null)
        {
            await _finishedLoop.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var evt in _events.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                switch (evt)
                {
                    case WorkArrived arrived:
                        await HandleArrivalAsync(arrived.Work, ct).ConfigureAwait(false);
                        break;

                    case StationCompleted completed:
                        await HandleCompletionAsync(completed.Completion, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常停机路径
        }
        catch (Exception ex)
        {
            _log.LogError("调度器循环异常终止", ex);
        }
    }

    private async Task HandleArrivalAsync(WorkItem work, CancellationToken ct)
    {
        var log = _log.ForContext("WorkItemId", work.Id).ForContext("Recipe", work.RecipeId);
        try
        {
            if (!_recipes.TryGetValue(work.RecipeId, out var recipe))
            {
                log.LogError($"未注册的配方 {work.RecipeId}，工件转不良");
                Finish(work, TestVerdict.Fail);
                return;
            }

            _progress[work.Id] = new WorkProgress(recipe, 0, recipe.Stages[0].Count);
            log.Info($"工件进入系统：{recipe.Describe()}");
            await RouteToStageAsync(work, recipe.Stages[0], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError("工件路由失败，转不良", ex);
            Finish(work, TestVerdict.Fail);
        }
    }

    private async Task HandleCompletionAsync(StationCompletion completion, CancellationToken ct)
    {
        var work = completion.Work;
        var result = completion.Result;
        var log = _log.ForContext("WorkItemId", work.Id).ForContext("StationId", result.StationId);

        try
        {
            if (!_progress.TryGetValue(work.Id, out var progress))
            {
                log.Debug("工件已判定，忽略该工位的后续完成事件");
                return;
            }

            if (!result.Passed)
            {
                _progress.Remove(work.Id);
                log.Warn($"工序 NG（{result.Message}），工件转不良队列，后续工位不再加工");
                Finish(work, TestVerdict.Fail);
                return;
            }

            progress.Remaining--;
            if (progress.Remaining > 0)
            {
                log.Debug($"并行分支完成，剩余 {progress.Remaining} 个分支");
                return;
            }

            var nextStageIndex = progress.StageIndex + 1;
            if (nextStageIndex >= progress.Recipe.Stages.Count)
            {
                _progress.Remove(work.Id);
                Finish(work, TestVerdict.Pass);
                log.Info($"全部工序完成，良品下线（节拍 {work.CycleMs:F0}ms）");
                return;
            }

            var nextStage = progress.Recipe.Stages[nextStageIndex];
            progress.StageIndex = nextStageIndex;
            progress.Remaining = nextStage.Count;
            log.Info($"流转到下游：{string.Join(" ‖ ", nextStage)}");
            await RouteToStageAsync(work, nextStage, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.LogError("工件流转失败，转不良", ex);
            _progress.Remove(work.Id);
            Finish(work, TestVerdict.Fail);
        }
    }

    private async Task RouteToStageAsync(WorkItem work, IReadOnlyList<string> stage, CancellationToken ct)
    {
        foreach (var stationId in stage)
        {
            if (!_stations.TryGetValue(stationId, out var station))
            {
                throw new InvalidOperationException($"配方引用了未注册的工位 {stationId}");
            }

            // 有界队列写满时在这里形成背压，而不是把工件丢掉。
            await station.Input.WriteAsync(work, ct).ConfigureAwait(false);
        }
    }

    private void Finish(WorkItem work, TestVerdict verdict)
    {
        work.Complete(verdict);
        Interlocked.Decrement(ref _inProcess);
        if (verdict == TestVerdict.Pass)
        {
            Interlocked.Increment(ref _passed);
        }
        else
        {
            Interlocked.Increment(ref _failed);
        }

        _finished.Writer.TryWrite(work);
    }

    private async Task DrainFinishedAsync()
    {
        await foreach (var work in _finished.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                _onFinished?.Invoke(work);
            }
            catch (Exception ex)
            {
                _log.LogError("成品队列消费者异常", ex);
            }
        }
    }

    private abstract record DispatchEvent;

    private sealed record WorkArrived(WorkItem Work) : DispatchEvent;

    private sealed record StationCompleted(StationCompletion Completion) : DispatchEvent;

    private sealed class WorkProgress(RecipeDefinition recipe, int stageIndex, int remaining)
    {
        public RecipeDefinition Recipe { get; } = recipe;

        public int StageIndex { get; set; } = stageIndex;

        public int Remaining { get; set; } = remaining;
    }
}

public sealed record DispatcherMetrics(long Submitted, long Passed, long Failed, long InProcess)
{
    public long Completed => Passed + Failed;
}

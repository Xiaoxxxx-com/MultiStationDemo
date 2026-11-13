using System.Diagnostics;
using System.Threading.Channels;
using MultiStation.Core.Logging;

namespace MultiStation.Core;

/// <summary>工位完成一次加工后向调度器发出的完成事件。</summary>
public sealed record StationCompletion(
    StationRuntime Station,
    WorkItem Work,
    StationResult Result,
    StationState StationState);

/// <summary>
/// 工位执行器：一个长驻 Task 驱动一个状态机。等待全部是异步等待（Channel / AsyncSignal），
/// 挂起时不占用线程池线程；每个工位拥有独立的 CancellationToken。
/// </summary>
public sealed class StationRuntime
{
    private readonly StationDefinition _definition;
    private readonly Func<StationCompletion, ValueTask> _completionSink;
    private readonly IAppLog _log;
    private readonly Channel<WorkItem> _input;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _runCts;
    private Task? _loop;
    private StationMetrics? _metrics;

    public StationRuntime(StationDefinition definition, Func<StationCompletion, ValueTask> completionSink, IAppLog log)
    {
        _definition = definition;
        _completionSink = completionSink;
        _log = log.ForContext("StationId", definition.Id);
        Context = new StationContext(definition);
        _input = Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(definition.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        PublishMetrics();
    }

    public string Id => _definition.Id;

    public string Name => _definition.Name;

    public StationContext Context { get; }

    public ChannelWriter<WorkItem> Input => _input.Writer;

    public int QueueDepth => _input.Reader.Count;

    public bool IsRunning => _loop is { IsCompleted: false };

    /// <summary>工位异常进入 Error 时触发，供报警中心等监控端订阅。</summary>
    public event Action<StationRuntime, Exception>? Faulted;

    public StationMetrics Snapshot()
    {
        var published = Volatile.Read(ref _metrics)!;
        // 队列深度是唯一需要实时读取的字段，Channel 的 Count 本身线程安全。
        return published with { QueueDepth = _input.Reader.Count };
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            if (Context.State == StationState.Stopped)
            {
                Context.Transition(StationEvent.Start);
            }

            _runCts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_runCts.Token));
        }

        _log.Info("工位执行器已启动");
    }

    /// <summary>只停止本工位，其他工位与调度器继续运行。</summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_lifecycleGate)
        {
            _runCts?.Cancel();
            loop = _loop;
        }

        if (loop is not null)
        {
            await loop.ConfigureAwait(false);
        }

        _log.Info("工位执行器已停止");
    }

    /// <summary>请求复位处于 Error 的工位。</summary>
    public bool RequestReset()
    {
        if (Context.State != StationState.Error)
        {
            return false;
        }

        Context.ResetSignal.Set();
        return true;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PublishMetrics();
                switch (Context.State)
                {
                    case StationState.Idle:
                        await Task.Delay(150, ct).ConfigureAwait(false);
                        Context.Transition(StationEvent.Start);
                        break;

                    case StationState.WaitingForWork:
                        var work = await _input.Reader.ReadAsync(ct).ConfigureAwait(false);
                        Context.DownstreamSignal.Reset();
                        Context.SetCurrentWork(work);
                        Context.Transition(StationEvent.WorkArrived);
                        _log.ForContext("WorkItemId", work.Id).Info($"取件开始加工（配方 {work.RecipeId}）");
                        break;

                    case StationState.Processing:
                        await ProcessAsync(ct).ConfigureAwait(false);
                        break;

                    case StationState.Completed:
                        await Context.DownstreamSignal.WaitAsync(ct).ConfigureAwait(false);
                        Context.SetCurrentWork(null);
                        Context.Transition(StationEvent.WorkRemoved);
                        break;

                    case StationState.Error:
                        await Context.ResetSignal.WaitAsync(ct).ConfigureAwait(false);
                        Context.SetCurrentWork(null);
                        Context.Transition(StationEvent.Reset);
                        _log.Info("故障已复位，工位返回 Idle");
                        break;

                    case StationState.Stopped:
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 正常的停机路径
        }
        catch (Exception ex)
        {
            _log.Error("工位执行循环异常终止", ex);
        }
        finally
        {
            if (Context.State != StationState.Stopped)
            {
                Context.Transition(StationEvent.Stop);
            }

            PublishMetrics();
        }
    }

    private async Task ProcessAsync(CancellationToken ct)
    {
        var work = Context.CurrentWork!;
        var log = _log.ForContext("WorkItemId", work.Id);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var outcome = await _definition.Process(Context, work, ct).ConfigureAwait(false);
            var result = new StationResult(
                Id,
                work.Id,
                outcome.Passed,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                outcome.Message,
                outcome.Measurements);

            work.Results[Id] = result;
            Context.RecordResult(result);
            Context.Transition(StationEvent.ProcessingDone);
            PublishMetrics();

            log.Info($"加工完成：{(result.Passed ? "PASS" : "NG")}，{result.DurationMs:F0}ms，{result.Message}");
            WriteResult(log, result);
            await _completionSink(new StationCompletion(this, work, result, StationState.Completed)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var result = new StationResult(
                Id,
                work.Id,
                false,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                ex.Message);

            work.Results[Id] = result;
            Context.RecordResult(result);
            Context.Transition(StationEvent.ProcessingFailed);
            PublishMetrics();

            log.Error($"加工异常，工位进入 Error 等待复位：{ex.Message}", ex);
            WriteResult(log, result);

            try
            {
                Faulted?.Invoke(this, ex);
            }
            catch
            {
                // 监控回调不允许影响工位执行
            }

            await _completionSink(new StationCompletion(this, work, result, StationState.Error)).ConfigureAwait(false);
        }
    }

    private void PublishMetrics()
    {
        Volatile.Write(ref _metrics, Context.BuildMetrics(_input.Reader.Count));
    }

    private static void WriteResult(IAppLog log, StationResult result)
    {
        log.Record("工位测试结果", new Dictionary<string, object?>
        {
            ["Passed"] = result.Passed,
            ["DurationMs"] = Math.Round(result.DurationMs, 1),
            ["Message"] = result.Message,
            ["Measurements"] = result.Measurements,
        });
    }
}

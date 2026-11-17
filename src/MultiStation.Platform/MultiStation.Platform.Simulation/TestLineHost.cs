using System.Collections.Concurrent;
using MultiStation.Platform.Core;
using MultiStation.Platform.Core.Devices;
using MultiStation.Platform.Core.Logging;

namespace MultiStation.Platform.Simulation;

/// <summary>完工工件的历史记录，供结果追溯页面使用。</summary>
public sealed record WorkHistoryItem(
    long Sequence,
    string WorkItemId,
    string RecipeId,
    TestVerdict Verdict,
    double CycleMs,
    DateTimeOffset FinishedAt,
    IReadOnlyList<StationResult> Results);

/// <summary>工位异常报警。</summary>
public sealed record AlarmItem(
    long Sequence,
    DateTimeOffset Timestamp,
    string StationId,
    string Message);

/// <summary>
/// 一条完整测试线的组合根：设备域 + 调度器 + 6 个工位执行器 + 落盘域 + 投料。
/// 控制台看板和 WPF 窗口共用同一套装配，UI 只读快照、只发命令。
/// </summary>
public sealed class TestLineHost
{
    public const int WipLimit = 24;

    public static readonly string[] StationIds = { "ST1", "ST2", "ST3", "ST4", "ST5", "ST6" };

    private readonly TelemetrySink _sink;
    private readonly IAppLog _log;
    private readonly DemoScenario _scenario = new();
    private readonly DeviceHub _hub;
    private readonly Dispatcher _dispatcher;
    private readonly List<StationRuntime> _stations = new();
    private readonly List<RecipeDefinition> _recipes = new();
    private readonly ConcurrentQueue<WorkHistoryItem> _history = new();
    private readonly ConcurrentQueue<AlarmItem> _alarms = new();
    private readonly CancellationTokenSource _cts = new();

    private Task? _generator;
    private Task? _faultScript;
    private int _nextItemId;
    private long _historySequence;
    private long _alarmSequence;
    private bool _started;

    public TestLineHost(string logDirectory)
    {
        StartedAt = DateTimeOffset.Now;
        _sink = new TelemetrySink(logDirectory);
        _log = new StructuredLog(_sink);

        _recipes.Add(RecipeDefinition.Serial("A", StationIds));
        _recipes.Add(new RecipeDefinition("B", new IReadOnlyList<string>[]
        {
            new[] { "ST1" },
            new[] { "ST2" },
            new[] { "ST3", "ST4" }, // 并行分支：外观与尺寸同时检测
            new[] { "ST5" },
            new[] { "ST6" },
        }));

        _hub = new DeviceHub(StationIds, _log);
        _dispatcher = new Dispatcher(_log, LogFinished);
        foreach (var recipe in _recipes)
        {
            _dispatcher.RegisterRecipe(recipe);
        }

        var sequences = new StationSequences(_hub, _scenario);
        AddStation("ST1", "上料", sequences.LoadAsync);
        AddStation("ST2", "定位", sequences.LocateAsync);
        AddStation("ST3", "外观检测", sequences.AppearanceAsync);
        AddStation("ST4", "尺寸检测", sequences.DimensionAsync);
        AddStation("ST5", "功能测试", sequences.FunctionAsync);
        AddStation("ST6", "下料", sequences.UnloadAsync);
    }

    public DateTimeOffset StartedAt { get; }

    public string LogPath => _sink.FilePath;

    public IReadOnlyList<StationRuntime> Stations => _stations;

    public IReadOnlyList<RecipeDefinition> Recipes => _recipes;

    public DeviceHub Devices => _hub;

    public Dispatcher Dispatcher => _dispatcher;

    public DemoScenario Scenario => _scenario;

    public IAppLog Log => _log;

    public DispatcherMetrics Metrics => _dispatcher.Snapshot();

    public long DroppedLogs => _sink.Dropped;

    public bool IsRunning => _started && !_cts.IsCancellationRequested;

    /// <summary>上料节拍开关，UI 可以暂停投料但保留产线运行。</summary>
    public bool AutoFeed { get; set; } = true;

    public IReadOnlyList<LogEntry> RecentLogs(int count) => _sink.Recent(count);

    public IReadOnlyList<WorkHistoryItem> RecentHistory(int count)
    {
        var snapshot = _history.ToArray();
        return snapshot.Length <= count ? snapshot : snapshot[^count..];
    }

    public IReadOnlyList<AlarmItem> RecentAlarms(int count)
    {
        var snapshot = _alarms.ToArray();
        return snapshot.Length <= count ? snapshot : snapshot[^count..];
    }

    public void ClearAlarms()
    {
        while (_alarms.TryDequeue(out _))
        {
        }
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _hub.Start();
        _dispatcher.Start();
        foreach (var station in _stations)
        {
            station.Start();
        }

        _log.Info($"系统启动：{_stations.Count} 个工位执行器 + 调度器 + 设备通信域 + 落盘域");
        _generator = Task.Run(() => GenerateWorkAsync(_cts.Token));
    }

    public void StartFaultScript()
    {
        if (_faultScript is { IsCompleted: false })
        {
            return;
        }

        _faultScript = Task.Run(() => RunFaultScriptAsync(_cts.Token));
    }

    public bool Submit(string? recipeId = null)
    {
        var id = Interlocked.Increment(ref _nextItemId);
        var recipe = recipeId ?? (id % 2 == 0 ? "B" : "A");
        var work = new WorkItem($"W-{id:D4}", recipe);
        if (!_dispatcher.Submit(work))
        {
            return false;
        }

        _log.ForContext("WorkItemId", work.Id).Info($"手动投料（配方 {recipe}）");
        return true;
    }

    public bool ToggleFault(string stationId)
    {
        var enabled = _scenario.ToggleFault(stationId);
        _log.ForContext("StationId", stationId).Warn($"故障注入已{(enabled ? "开启" : "关闭")}（仿真工艺异常）");
        return enabled;
    }

    public int ResetFaults(string reason = "复位")
    {
        _scenario.ClearFaults();
        var reset = _stations.Count(station => station.RequestReset());
        _log.Warn($"{reason}：清除故障注入并复位 {reset} 个异常工位");
        return reset;
    }

    public void DisconnectCamera(string stationId, double seconds)
    {
        _hub.Camera(stationId).SimulateDisconnect(TimeSpan.FromSeconds(seconds));
    }

    public string DisconnectRandomCamera(double seconds)
    {
        var camera = _hub.Cameras.ElementAt(Random.Shared.Next(_hub.Cameras.Count));
        camera.SimulateDisconnect(TimeSpan.FromSeconds(seconds));
        return camera.StationId;
    }

    public void DisconnectPlc(double seconds) => _hub.Plc.SimulateDisconnect(TimeSpan.FromSeconds(seconds));

    public async Task StopAsync()
    {
        if (!_started)
        {
            return;
        }

        _cts.Cancel();
        if (_generator is not null)
        {
            await _generator.ConfigureAwait(false);
        }

        if (_faultScript is not null)
        {
            await _faultScript.ConfigureAwait(false);
        }

        _log.Info("收到停机指令：先停调度器（停止投料），再逐个停止工位，最后停设备与落盘域");
        await _dispatcher.StopAsync().ConfigureAwait(false);
        foreach (var station in _stations)
        {
            await station.StopAsync().ConfigureAwait(false);
        }

        await _hub.StopAsync().ConfigureAwait(false);
        _log.Info("停机完成：所有工位执行器、设备通信循环、调度循环均已退出");
        await _sink.StopAsync().ConfigureAwait(false);
        _started = false;
    }

    private void AddStation(
        string id,
        string name,
        Func<StationContext, WorkItem, CancellationToken, Task<StationOutcome>> process)
    {
        var station = new StationRuntime(new StationDefinition(id, name, process), _dispatcher.CompletionSink, _log);
        station.Faulted += (runtime, exception) => AddAlarm(runtime.Id, exception.Message);
        _dispatcher.AttachStation(station);
        _stations.Add(station);
    }

    private void AddAlarm(string stationId, string message)
    {
        var item = new AlarmItem(
            Interlocked.Increment(ref _alarmSequence),
            DateTimeOffset.Now,
            stationId,
            message);

        _alarms.Enqueue(item);
        while (_alarms.Count > 500 && _alarms.TryDequeue(out _))
        {
        }
    }

    private async Task GenerateWorkAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // 简单在制品限流，避免上游事件队列无限增长（现场对应 MES/上料节拍）
                if (!AutoFeed || _dispatcher.Snapshot().InProcess >= WipLimit)
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                var id = Interlocked.Increment(ref _nextItemId);
                var recipe = id % 2 == 0 ? "B" : "A";
                _dispatcher.Submit(new WorkItem($"W-{id:D4}", recipe));
                await Task.Delay(900, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task RunFaultScriptAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            _scenario.SetFault("ST3", true);
            _log.Warn("故障脚本：ST3 工艺故障（其他工位、设备域、UI 继续运行）");

            await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            ResetFaults("故障脚本复位");

            await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            DisconnectCamera("ST4", 6);

            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            DisconnectPlc(4);

            await Task.Delay(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
            ResetFaults("故障脚本收尾复位");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private void LogFinished(WorkItem work)
    {
        var historyItem = new WorkHistoryItem(
            Interlocked.Increment(ref _historySequence),
            work.Id,
            work.RecipeId,
            work.Verdict ?? TestVerdict.Fail,
            work.CycleMs,
            work.FinishedAt ?? DateTimeOffset.Now,
            work.Results.Values.OrderBy(result => result.StationId).ToArray());

        _history.Enqueue(historyItem);
        while (_history.Count > 1000 && _history.TryDequeue(out _))
        {
        }

        _log.ForContext("WorkItemId", work.Id)
            .ForContext("Recipe", work.RecipeId)
            .Record("工件完成", new Dictionary<string, object?>
            {
                ["Verdict"] = work.Verdict?.ToString(),
                ["CycleMs"] = Math.Round(work.CycleMs, 1),
                ["StationCount"] = work.Results.Count,
                ["Results"] = work.Results.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Passed ? "PASS" : "NG"),
            });
    }
}

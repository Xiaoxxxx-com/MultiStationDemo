using System.Threading.Channels;
using MultiStation.Platform.Core.Logging;

namespace MultiStation.Platform.Core.Devices;

public sealed record CaptureResult(string WorkItemId, string ImageName, double MeanGray, DateTimeOffset CapturedAt);

/// <summary>
/// 相机通信域：每个工位独占一台相机、一条命令队列和一条结果通道。
/// 工位不持有 SDK 句柄，只提交拍照意图并异步等待结果。
/// </summary>
public sealed class CameraDomain
{
    private readonly IAppLog _log;
    private readonly Channel<CaptureCommand> _commands = Channel.CreateUnbounded<CaptureCommand>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly CancellationTokenSource _cts = new();
    private long _disconnectUntilUtcTicks;
    private long _shotCount;
    private volatile bool _connected;
    private Task? _loop;

    public CameraDomain(string stationId, IAppLog log)
    {
        StationId = stationId;
        _log = log.ForContext("Device", "Camera").ForContext("StationId", stationId);
    }

    public string StationId { get; }

    public bool Connected => _connected;

    public long ShotCount => Interlocked.Read(ref _shotCount);

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        _commands.Writer.TryComplete();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
    }

    public void SimulateDisconnect(TimeSpan duration)
    {
        Interlocked.Exchange(ref _disconnectUntilUtcTicks, DateTimeOffset.UtcNow.Add(duration).UtcTicks);
        _log.Warn($"模拟相机断线 {duration.TotalSeconds:F0}s，仅影响本工位");
    }

    public async Task<CaptureResult> CaptureAsync(string workItemId, int exposureUs, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<CaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new CaptureCommand(workItemId, exposureUs, completion), cancellationToken)
            .ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoffMs = 500;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var until = Interlocked.Read(ref _disconnectUntilUtcTicks);
                var disconnected = until != 0 && DateTimeOffset.UtcNow.UtcTicks < until;

                if (disconnected)
                {
                    if (_connected)
                    {
                        _connected = false;
                        _log.Warn("相机连接中断");
                    }

                    FailPendingCommands("相机连接中断");
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 2000);
                    continue;
                }

                if (!_connected)
                {
                    _connected = true;
                    backoffMs = 500;
                    _log.Info("相机连接恢复");
                }

                var waitTask = _commands.Reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(waitTask, Task.Delay(300, ct)).ConfigureAwait(false);
                if (completed == waitTask && await waitTask.ConfigureAwait(false))
                {
                    while (_commands.Reader.TryRead(out var command))
                    {
                        await Task.Delay(120, ct).ConfigureAwait(false); // 模拟曝光与传输
                        var shot = Interlocked.Increment(ref _shotCount);
                        command.Completion.TrySetResult(new CaptureResult(
                            command.WorkItemId,
                            $"{StationId}-{shot:D6}.bmp",
                            128.0 + Random.Shared.NextDouble() * 40.0,
                            DateTimeOffset.Now));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            _connected = false;
            FailPendingCommands("相机通信域已停止");
        }
    }

    private void FailPendingCommands(string reason)
    {
        while (_commands.Reader.TryRead(out var command))
        {
            command.Completion.TrySetException(new DeviceDisconnectedException(reason));
        }
    }

    private sealed record CaptureCommand(
        string WorkItemId,
        int ExposureUs,
        TaskCompletionSource<CaptureResult> Completion);
}

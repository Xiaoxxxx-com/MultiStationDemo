using System.Threading.Channels;
using MultiStation.Platform.Core.Logging;

namespace MultiStation.Platform.Core.Devices;

public enum TowerLight
{
    Red,
    Yellow,
    Green,
    Off,
}

/// <summary>
/// PLC 通信域：独占一个长驻循环，维护心跳与只读数据镜像，负责断线重连退避。
/// 所有工位共享这一台 PLC，因此动作意图统一进命令队列由本域串行化。
/// </summary>
public sealed class PlcDomain(IAppLog log) : IDisposable
{
    private readonly IAppLog _log = log.ForContext("Device", "PLC");
    private readonly Channel<PlcCommand> _commands = Channel.CreateUnbounded<PlcCommand>(new UnboundedChannelOptions
    {
        SingleReader = true,
    });

    private readonly CancellationTokenSource _cts = new();
    private ProcessImage _image = new(
        true,
        0,
        DateTimeOffset.Now,
        "Run",
        new Dictionary<string, bool> { ["ConveyorRunning"] = true, ["SafetyDoorClosed"] = true, ["StationReady"] = true });
    private long _disconnectUntilUtcTicks;
    private long _heartbeat;
    private int _cycle;
    private volatile bool _connected;
    private Task? _loop;

    public ProcessImage Snapshot => Volatile.Read(ref _image);

    public bool Connected => _connected;

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>释放资源：停止 PLC 通信域。</summary>
    public void Dispose() => StopAsync().GetAwaiter().GetResult();

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
        _log.Warn($"模拟 PLC 链路断开 {duration.TotalSeconds:F0}s，通信域进入重连退避");
    }

    public async Task<bool> SetTowerLightAsync(TowerLight light, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new PlcCommand(light, completion), cancellationToken).ConfigureAwait(false);
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
                        _log.Warn("PLC 连接中断");
                    }

                    PublishImage("Disconnected");
                    FailPendingCommands("PLC 连接中断");
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
                    backoffMs = Math.Min(backoffMs * 2, 2000);
                    continue;
                }

                if (!_connected)
                {
                    _connected = true;
                    backoffMs = 500;
                    _log.Info("PLC 连接恢复，心跳与数据镜像恢复正常");
                    PublishImage("Run");
                }

                var waitTask = _commands.Reader.WaitToReadAsync(ct).AsTask();
                var completed = await Task.WhenAny(waitTask, Task.Delay(200, ct)).ConfigureAwait(false);
                if (completed == waitTask && await waitTask.ConfigureAwait(false))
                {
                    while (_commands.Reader.TryRead(out var command))
                    {
                        await Task.Delay(20, ct).ConfigureAwait(false);
                        _log.Debug($"三色灯命令已下发：{command.Light}");
                        command.Completion.TrySetResult(true);
                    }
                }

                PublishImage("Run");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            _connected = false;
            FailPendingCommands("PLC 通信域已停止");
            PublishImage("Stopped");
        }
    }

    private void PublishImage(string mode)
    {
        _heartbeat++;
        _cycle++;
        var image = new ProcessImage(
            mode == "Run",
            _heartbeat,
            DateTimeOffset.Now,
            mode,
            new Dictionary<string, bool>
            {
                ["ConveyorRunning"] = mode == "Run",
                ["SafetyDoorClosed"] = true,
                ["StationReady"] = mode == "Run",
                ["CycleCounter"] = _cycle % 2 == 1,
            });

        Volatile.Write(ref _image, image);
    }

    private void FailPendingCommands(string reason)
    {
        while (_commands.Reader.TryRead(out var command))
        {
            command.Completion.TrySetException(new DeviceDisconnectedException(reason));
        }
    }

    private sealed record PlcCommand(TowerLight Light, TaskCompletionSource<bool> Completion);
}

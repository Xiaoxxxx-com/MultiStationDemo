namespace MultiStation.Platform.Core;

/// <summary>
/// 轻量异步信号：替代 ManualResetEventSlim，等待方是真正的异步等待，不占线程。
/// Set 可以先于 Wait 发生（信号会被保留一次）。
/// </summary>
public sealed class AsyncSignal
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _pending;
    private bool _signaled;

    public void Set()
    {
        lock (_gate)
        {
            if (_pending is { } pending && !pending.Task.IsCompleted)
            {
                _pending = null;
                pending.TrySetResult();
                return;
            }

            _signaled = true;
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_signaled)
            {
                _signaled = false;
                return Task.CompletedTask;
            }

            _pending ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task.WaitAsync(cancellationToken);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _signaled = false;
        }
    }
}

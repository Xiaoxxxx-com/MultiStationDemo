using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace MultiStation.Desktop;

/// <summary>一次外部触发信号。</summary>
public sealed record TriggerSignal(string Value, string Target, DateTimeOffset Timestamp);

/// <summary>触发序列执行状态快照（供 UI 线程读取）。</summary>
public sealed record TriggerSnapshot(string StateText, int TriggerCount, string LastTime);

/// <summary>
/// 测试触发序列：独立线程循环接收外部设备（PLC R981）的触发信号，
/// UI 只读取快照；具体动作由 Triggered 事件抛回界面线程执行。
/// </summary>
public sealed class TriggerSequenceRunner
{
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _worker;
    private bool _executing;
    private int _count;
    private string _lastTime = "-";

    /// <summary>模拟外部触发节奏（真实项目对应 PLC 握手字变化）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>触发到来时在后台线程触发。</summary>
    public event Action<TriggerSignal>? Triggered;

    public void Start()
    {
        _worker ??= Task.Factory
            .StartNew(
                () => RunAsync(_cts.Token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_worker is null)
        {
            return;
        }

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public TriggerSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TriggerSnapshot(
                _executing ? "执行中" : "等待外部触发",
                _count,
                _lastTime);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // 模拟外部设备上电握手，等界面就绪后再开始监听
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var signal = new TriggerSignal("10", "开始测试", DateTimeOffset.Now);

                lock (_gate)
                {
                    _count++;
                    _executing = true;
                    _lastTime = signal.Timestamp.ToString("HH:mm:ss");
                }

                Triggered?.Invoke(signal);

                await Task.Delay(TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false);

                lock (_gate)
                {
                    _executing = false;
                }

                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}

/// <summary>触发序列行的实时状态。</summary>
public sealed class TriggerStepVm : INotifyPropertyChanged
{
    private static readonly Brush IdleBrush = CreateBrush(0x94, 0xA3, 0xB8);
    private static readonly Brush ActiveBrush = CreateBrush(0x1A, 0x7F, 0x37);

    private string _status = "待触发";
    private Brush _statusBrush = IdleBrush;

    public TriggerStepVm(TriggerSequenceRow row)
    {
        Number = row.Number;
        Code = row.Code;
        Equal = row.Equal;
        Target = row.Target;
        Command = row.Command;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Number { get; }

    public string Code { get; }

    public string Equal { get; }

    public string Target { get; }

    public string Command { get; }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => Set(ref _statusBrush, value);
    }

    public void MarkTriggered()
    {
        Status = "已触发";
        StatusBrush = ActiveBrush;
    }

    public void Reset()
    {
        Status = "待触发";
        StatusBrush = IdleBrush;
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

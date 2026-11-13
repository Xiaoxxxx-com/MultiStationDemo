using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using MultiStation.Core;

namespace MultiStation.Desktop;

/// <summary>单个工位卡片的只读视图模型，数据来自 StationMetrics 快照。</summary>
public sealed class StationCardVm : INotifyPropertyChanged
{
    private static readonly Brush WaitingBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush ProcessingBrush = CreateBrush(0x09, 0x69, 0xDA);
    private static readonly Brush CompletedBrush = CreateBrush(0x82, 0x50, 0xDF);
    private static readonly Brush ErrorBrush = CreateBrush(0xCF, 0x22, 0x2E);
    private static readonly Brush StoppedBrush = CreateBrush(0x6E, 0x77, 0x81);
    private static readonly Brush IdleBrush = CreateBrush(0x9A, 0x67, 0x00);

    private string _stateText = "Idle";
    private Brush _stateBrush = IdleBrush;
    private string _workItem = "-";
    private string _queue = "0";
    private string _counts = "0 / 0 / 0";
    private string _stateAge = "0.0s";
    private string _message = "-";
    private bool _faulted;

    public StationCardVm(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string Name { get; }

    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => Set(ref _stateBrush, value);
    }

    public string WorkItem
    {
        get => _workItem;
        private set => Set(ref _workItem, value);
    }

    public string Queue
    {
        get => _queue;
        private set => Set(ref _queue, value);
    }

    public string Counts
    {
        get => _counts;
        private set => Set(ref _counts, value);
    }

    public string StateAge
    {
        get => _stateAge;
        private set => Set(ref _stateAge, value);
    }

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    public string FaultButtonText => _faulted ? "取消故障" : "注入故障";

    public void Update(StationMetrics metrics, bool faulted)
    {
        StateText = metrics.State.ToString();
        StateBrush = BrushFor(metrics.State);
        WorkItem = metrics.CurrentWorkId ?? "-";
        Queue = metrics.QueueDepth.ToString();
        Counts = $"{metrics.Passed} / {metrics.Failed} / {metrics.ConsecutiveFailures}";
        StateAge = $"已持续 {metrics.StateAge.TotalSeconds:F1}s";
        Message = metrics.LastError ?? metrics.LastMessage ?? "-";

        if (_faulted != faulted)
        {
            _faulted = faulted;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FaultButtonText)));
        }
    }

    private static Brush BrushFor(StationState state) => state switch
    {
        StationState.WaitingForWork => WaitingBrush,
        StationState.Processing => ProcessingBrush,
        StationState.Completed => CompletedBrush,
        StationState.Error => ErrorBrush,
        StationState.Stopped => StoppedBrush,
        _ => IdleBrush,
    };

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

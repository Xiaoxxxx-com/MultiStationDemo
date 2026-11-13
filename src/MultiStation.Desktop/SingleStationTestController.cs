using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace MultiStation.Desktop;

/// <summary>单工位测试过程行。</summary>
public sealed class TestStepVm : INotifyPropertyChanged
{
    private static readonly Brush PendingBrush = CreateBrush(0x94, 0xA3, 0xB8);
    private static readonly Brush RunningBrush = CreateBrush(0x09, 0x69, 0xDA);
    private static readonly Brush PassBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush FailBrush = CreateBrush(0xCF, 0x22, 0x2E);
    private static readonly Brush StopBrush = CreateBrush(0x6E, 0x77, 0x81);

    private string _measured = "-";
    private string _result = "待执行";
    private Brush _resultBrush = PendingBrush;
    private string _duration = "-";
    private string _time = "-";

    public TestStepVm(TestSequenceRow row)
    {
        Stage = row.Stage;
        Number = row.Number;
        Code = row.Code;
        Project = row.Project;
        Kind = row.Kind;
        ParamCategory = row.ParamCategory;
        ParamName = row.ParamName;
        Min = row.Min;
        Max = row.Max;
        Equal = row.Equal;
        Device = row.Device;
        Command = row.Command;
        TimeoutMs = row.TimeoutMs;
        Retry = row.Retry;
        FlowControl = row.FlowControl;
        FlowTarget = row.FlowTarget;
        DisplayEnabled = row.DisplayEnabled;
        Enabled = row.Enabled;
        Remark = row.Remark;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Stage { get; }

    public string Number { get; }

    public string Code { get; }

    public string Project { get; }

    public string Kind { get; }

    public string ParamCategory { get; }

    public string ParamName { get; }

    public string Min { get; }

    public string Max { get; }

    public string Equal { get; }

    public string Device { get; }

    public string Command { get; }

    public string TimeoutMs { get; }

    public string Retry { get; }

    public string FlowControl { get; }

    public string FlowTarget { get; }

    public string DisplayEnabled { get; }

    public string Enabled { get; }

    public string Remark { get; }

    /// <summary>标题 / TestProject 行只做分组，不参与实际测试判定。</summary>
    public bool IsGroup => Kind is "标题" or "TestProject";

    public string Measured
    {
        get => _measured;
        private set => Set(ref _measured, value);
    }

    public string Result
    {
        get => _result;
        private set => Set(ref _result, value);
    }

    public Brush ResultBrush
    {
        get => _resultBrush;
        private set => Set(ref _resultBrush, value);
    }

    public string Duration
    {
        get => _duration;
        private set => Set(ref _duration, value);
    }

    public string Time
    {
        get => _time;
        private set => Set(ref _time, value);
    }

    public void MarkRunning()
    {
        Result = "执行中";
        ResultBrush = RunningBrush;
        Measured = "-";
        Duration = "-";
        Time = "-";
    }

    public void Complete(bool passed, string measured, double durationMs)
    {
        Measured = measured;
        Result = passed ? "PASS" : "NG";
        ResultBrush = passed ? PassBrush : FailBrush;
        Duration = (durationMs / 1000.0).ToString("F2");
        Time = DateTime.Now.ToString("HH:mm:ss");
    }

    public void MarkGroup()
    {
        Measured = "-";
        Result = "分组";
        ResultBrush = StopBrush;
        Duration = "-";
        Time = "-";
    }

    public void MarkStopped()
    {
        Result = "已停止";
        ResultBrush = StopBrush;
    }

    public void Reset()
    {
        Measured = "-";
        Result = "待执行";
        ResultBrush = PendingBrush;
        Duration = "-";
        Time = "-";
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

/// <summary>参数监控行。</summary>
public sealed class ParameterVm : INotifyPropertyChanged
{
    private static readonly Brush OkBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush BadBrush = CreateBrush(0xCF, 0x22, 0x2E);
    private static readonly Brush IdleBrush = CreateBrush(0x94, 0xA3, 0xB8);

    private string _value = "-";
    private string _state = "待采集";
    private Brush _stateBrush = IdleBrush;

    public ParameterVm(string name, string unit, string limit)
    {
        Name = name;
        Unit = unit;
        Limit = limit;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }

    public string Unit { get; }

    public string Limit { get; }

    public string Value
    {
        get => _value;
        private set => Set(ref _value, value);
    }

    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => Set(ref _stateBrush, value);
    }

    public void Update(string value, bool ok)
    {
        Value = value;
        State = ok ? "正常" : "超限";
        StateBrush = ok ? OkBrush : BadBrush;
    }

    public void Reset()
    {
        Value = "-";
        State = "待采集";
        StateBrush = IdleBrush;
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

/// <summary>
/// 单工位测试演示控制器：开始/暂停/停止/下一步，驱动测试过程与参数监控两块数据。
/// </summary>
public sealed class SingleStationTestController
{
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private int _current = -1;
    private bool _running;
    private bool _paused;
    private bool _stepMode;
    private bool _hasNg;

    public SingleStationTestController()
    {
        Steps = new ObservableCollection<TestStepVm>(TestSequenceData.Rows.Select(row => new TestStepVm(row)));

        Parameters = new ObservableCollection<ParameterVm>
        {
            new("通信状态", "-", "连接成功"),
            new("电压", "V", "4.9~5.1"),
            new("电流", "A", "0~1"),
            new("温度", "°C", "≤60"),
            new("转速", "rpm", "3000±100"),
            new("压力", "kPa", "101±5"),
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _timer.Tick += (_, _) => Advance();
    }

    public ObservableCollection<TestStepVm> Steps { get; }

    public ObservableCollection<ParameterVm> Parameters { get; }

    public ObservableCollection<string> Logs { get; } = new();

    public event Action? Changed;

    private void AddLog(string message)
    {
        Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (Logs.Count > 300)
        {
            Logs.RemoveAt(0);
        }
    }

    public bool IsRunning => _running;

    public bool IsPaused => _paused;

    public bool IsStepMode => _stepMode;

    public int PassCount { get; private set; }

    public int NgCount { get; private set; }

    public string Verdict { get; private set; } = "--";

    public string StateText { get; private set; } = "待开始";

    public void Start()
    {
        if (_running)
        {
            return;
        }

        ResetSession();
        _running = true;
        _paused = false;
        _stepMode = false;
        _hasNg = false;
        _current = 0;
        Verdict = "测试中";
        StateText = "测试中";
        AddLog("开始测试");
        MarkStepEntered(0);
        _timer.Start();
        Changed?.Invoke();
    }

    public void PauseOrResume()
    {
        if (!_running || _stepMode)
        {
            return;
        }

        if (_paused)
        {
            _timer.Start();
            _paused = false;
            Verdict = "测试中";
            StateText = "测试中";
            AddLog("继续测试");
        }
        else
        {
            _timer.Stop();
            _paused = true;
            Verdict = "已暂停";
            StateText = "已暂停";
            AddLog("测试暂停");
        }

        Changed?.Invoke();
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _timer.Stop();
        _running = false;
        _paused = false;
        _stepMode = false;

        if (_current >= 0 && _current < Steps.Count)
        {
            Steps[_current].MarkStopped();
        }

        _current = -1;
        Verdict = "已停止";
        StateText = "已停止";
        AddLog("测试停止");
        Changed?.Invoke();
    }

    public void NextStep()
    {
        if (!_running)
        {
            ResetSession();
            _running = true;
            _paused = true;
            _stepMode = true;
            _current = 0;
            _hasNg = false;
            Verdict = "单步";
            StateText = "单步执行";
            AddLog("进入单步模式");
            MarkStepEntered(0);
            Changed?.Invoke();
            return;
        }

        if (!_paused || _current < 0)
        {
            return;
        }

        Advance();
        if (!_running)
        {
            return;
        }

        _paused = true;
        _timer.Stop();
        Changed?.Invoke();
    }

    private void Advance()
    {
        if (!_running)
        {
            return;
        }

        if (_current >= 0 && _current < Steps.Count)
        {
            CompleteStep(_current);
        }

        var next = _current + 1;
        if (next >= Steps.Count)
        {
            FinishSession();
            return;
        }

        _current = next;
        MarkStepEntered(next);
        Changed?.Invoke();
    }

    private void CompleteStep(int index)
    {
        var step = Steps[index];
        if (step.IsGroup)
        {
            step.MarkGroup();
            return;
        }

        var passed = _random.NextDouble() > 0.12;
        var duration = 480 + _random.NextDouble() * 420;
        var measured = BuildMeasured(step, passed);

        step.Complete(passed, measured, duration);
        AddLog($"步骤 {index + 1} {step.Project} {(passed ? "PASS" : "NG")} · {measured} · {duration:F0} ms");
        UpdateParameter(step, passed);

        if (!passed)
        {
            _hasNg = true;
        }
    }

    private void MarkStepEntered(int index)
    {
        var step = Steps[index];
        if (step.IsGroup)
        {
            step.MarkGroup();
            AddLog($"步骤 {index + 1} {step.Project}（分组）");
            return;
        }

        step.MarkRunning();
        AddLog($"步骤 {index + 1} {step.Project} 开始");
    }

    private string BuildMeasured(TestStepVm step, bool passed)
    {
        if (!string.IsNullOrEmpty(step.Equal))
        {
            return passed ? step.Equal : $"≠{step.Equal}";
        }

        if (double.TryParse(step.Min, out var min) && double.TryParse(step.Max, out var max))
        {
            var value = passed
                ? min + _random.NextDouble() * (max - min)
                : max + Math.Max(0.01, (max - min) * 0.2);
            return $"{value:F2}";
        }

        if (step.Kind == "Wait")
        {
            return passed ? "等待完成" : "超时";
        }

        return passed ? "OK" : "失败";
    }

    private void UpdateParameter(TestStepVm step, bool passed)
    {
        switch (step.ParamName.ToLowerInvariant())
        {
            case "voltage":
                Parameters[1].Update($"{5.0 + (_random.NextDouble() - 0.5) * 0.2:F2}", passed);
                break;

            case "current":
                Parameters[2].Update($"{0.5 + _random.NextDouble() * 0.4:F2}", passed);
                break;
        }
    }

    private void FinishSession()
    {
        _timer.Stop();
        _running = false;
        _paused = false;
        _stepMode = false;
        _current = -1;

        if (_hasNg)
        {
            NgCount++;
            Verdict = "NG";
            StateText = "测试完成（NG）";
        }
        else
        {
            PassCount++;
            Verdict = "PASS";
            StateText = "测试完成（PASS）";
        }

        AddLog($"测试完成：{Verdict}");
        Changed?.Invoke();
    }

    private void ResetSession()
    {
        _timer.Stop();
        _current = -1;
        _hasNg = false;
        Logs.Clear();

        foreach (var step in Steps)
        {
            step.Reset();
        }

        foreach (var parameter in Parameters)
        {
            parameter.Reset();
        }

        Verdict = "--";
        StateText = "待开始";
    }
}

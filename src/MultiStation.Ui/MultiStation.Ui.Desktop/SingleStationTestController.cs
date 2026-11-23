﻿using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using MultiStation.Platform.Core.Execution;

namespace MultiStation.Ui.Desktop;

/// <summary>单工位测试过程行。</summary>
public sealed class TestStepVm(TestSequenceRow row) : INotifyPropertyChanged
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Stage { get; } = row.Stage;

    public string Number { get; } = row.Number;

    public string Code { get; } = row.Code;

    public string Project { get; } = row.Project;

    public string Kind { get; } = row.Kind;

    public string ParamCategory { get; } = row.ParamCategory;

    public string ParamName { get; } = row.ParamName;

    public string Min { get; } = row.Min;

    public string Max { get; } = row.Max;

    public string Equal { get; } = row.Equal;

    public string Device { get; } = row.Device;

    public string Command { get; } = row.Command;

    public string TimeoutMs { get; } = row.TimeoutMs;

    public string Retry { get; } = row.Retry;

    public string FlowControl { get; } = row.FlowControl;

    public string FlowTarget { get; } = row.FlowTarget;

    public string DisplayEnabled { get; } = row.DisplayEnabled;

    public string Enabled { get; } = row.Enabled;

    public string Remark { get; } = row.Remark;

    /// <summary>标题 / TestProject 行只做分组，不参与实际测试判定。</summary>
    public bool IsGroup => Kind is "标题" or "TestProject";

    /// <summary>Bat 步骤：调用外部批处理，退出码 0 判 PASS。</summary>
    public bool IsBat => string.Equals(Kind, "Bat", StringComparison.OrdinalIgnoreCase);

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
        Duration = (durationMs / 1000.0).ToString("F2", CultureInfo.InvariantCulture);
        Time = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
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

    public void MarkSkipped()
    {
        Measured = "-";
        Result = "已跳过";
        ResultBrush = StopBrush;
        Duration = "-";
        Time = "-";
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
public sealed class ParameterVm(string name, string unit, string limit) : INotifyPropertyChanged
{
    private static readonly Brush OkBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush BadBrush = CreateBrush(0xCF, 0x22, 0x2E);
    private static readonly Brush IdleBrush = CreateBrush(0x94, 0xA3, 0xB8);

    private string _value = "-";
    private string _state = "待采集";
    private Brush _stateBrush = IdleBrush;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; } = name;

    public string Unit { get; } = unit;

    public string Limit { get; } = limit;

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
public sealed class SingleStationTestController : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private static readonly char[] _newLineChars = ['\r', '\n'];
    private readonly Random _random = new();
    private readonly IReadOnlyList<TestSequenceRow> _rows;
    private readonly IUdsStepExecutor? _udsExecutor;
    private int _current = -1;
    private bool _running;
    private bool _paused;
    private bool _stepMode;
    private bool _hasNg;
    private int _session;
    private CancellationTokenSource? _batCts;
    private bool _batRunning;
    private CancellationTokenSource? _udsCts;
    private bool _udsRunning;
    private bool _stepCommitted;
    private Task _cleanupTask = Task.CompletedTask;
    private Task? _shutdownTask;
    private bool _disposed;

    public SingleStationTestController(
        IReadOnlyList<TestSequenceRow> rows,
        IUdsStepExecutor? udsExecutor = null)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _rows = rows;
        _udsExecutor = udsExecutor;
        Steps = new ObservableCollection<TestStepVm>(rows.Select(row => new TestStepVm(row)));

        Parameters =
        [
            new("通信状态", "-", "连接成功"),
            new("电压", "V", "4.9~5.1"),
            new("电流", "A", "0~1"),
            new("温度", "°C", "≤60"),
            new("转速", "rpm", "3000±100"),
            new("压力", "kPa", "101±5"),
        ];

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _timer.Tick += (_, _) => Advance();
    }

    public ObservableCollection<TestStepVm> Steps { get; }

    public ObservableCollection<ParameterVm> Parameters { get; }

    public ObservableCollection<string> Logs { get; } = [];

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

    /// <summary>Bat 或 UDS 原子步骤仍在执行时，暂停和单步不得并发推进。</summary>
    public bool IsOperationRunning => _batRunning || _udsRunning;

    /// <summary>执行器释放完成前禁止下一件开始，避免与 AutoLoader 争用 CAN。</summary>
    public bool IsCleaningUp => !_cleanupTask.IsCompleted;

    public int PassCount { get; private set; }

    public int NgCount { get; private set; }

    public string Verdict { get; private set; } = "--";

    public string StateText { get; private set; } = "待开始";

    public void Start()
    {
        if (_running || _shutdownTask is not null || IsCleaningUp || Steps.Count == 0)
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

    /// <summary>停止当前序列并等待 CAN 释放；视图切换时必须等待此任务。</summary>
    public Task ShutdownAsync()
    {
        if (_shutdownTask is not null)
        {
            return _shutdownTask;
        }

        Stop();
        BeginExecutorCleanup();
        _shutdownTask = _cleanupTask;
        return _shutdownTask;
    }

    /// <summary>释放资源：停止计时器、取消后台步骤并关闭执行器。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ShutdownAsync().GetAwaiter().GetResult();
        _timer.Stop();
        _udsExecutor?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        _timer.Stop();
        CancelBat();
        CancelUds();
        _running = false;
        _paused = false;
        _stepMode = false;
        _stepCommitted = false;
        _session++;

        if (_current >= 0 && _current < Steps.Count)
        {
            Steps[_current].MarkStopped();
        }

        _current = -1;
        Verdict = "已停止";
        StateText = "已停止";
        AddLog("测试停止");
        BeginExecutorCleanup();
        Changed?.Invoke();
    }

    public void NextStep()
    {
        if (!_running)
        {
            if (_shutdownTask is not null || IsCleaningUp || Steps.Count == 0)
            {
                return;
            }

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

        // Bat / UDS 步骤在后台执行，完成前不推进序列
        if (IsOperationRunning)
        {
            return;
        }

        if (_current >= 0 && _current < Steps.Count && !_stepCommitted)
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
        _stepCommitted = false;
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

        // Bat / 真实 UDS 步骤的结果由后台回调写入，这里不参与随机模拟
        if (step.IsBat || ShouldExecuteUds(step))
        {
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

        if (step.IsBat)
        {
            BeginBat(index, step);
        }
        else if (ShouldExecuteUds(step))
        {
            BeginUds(index, step);
        }
    }

    private bool ShouldExecuteUds(TestStepVm step) =>
        _udsExecutor is not null
        && step.Command.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Trim().StartsWith("PDU=", StringComparison.OrdinalIgnoreCase))
        && step.Command.Contains("COM=UDS", StringComparison.OrdinalIgnoreCase);

    /// <summary>启动 Bat 步骤：后台执行，完成后回到界面线程写结果。</summary>
    private void BeginBat(int index, TestStepVm step)
    {
        var (file, arguments, workDirectory) = ParseBatCommand(step.Command);
        var timeoutMs = int.TryParse(step.TimeoutMs, out var parsed) && parsed > 0
            ? parsed
            : BatRunner.DefaultTimeoutMs;

        _stepCommitted = false;
        _batRunning = true;
        _batCts = new CancellationTokenSource();

        var session = _session;
        var token = _batCts.Token;
        var displayArgs = string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments;
        AddLog($"步骤 {index + 1} 调用批处理 {file}{displayArgs}（超时 {timeoutMs} ms）");

        _ = RunBatAsync(index, step, session, file, arguments, workDirectory, timeoutMs, token);
    }

    private async Task RunBatAsync(
        int index,
        TestStepVm step,
        int session,
        string file,
        string? arguments,
        string? workDirectory,
        int timeoutMs,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        BatResult result;
        try
        {
            result = await BatRunner.RunAsync(file, arguments, workDirectory, timeoutMs, token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new BatResult(false, -1, false, string.Empty, string.Empty, $"执行异常：{ex.Message}");
        }

        stopwatch.Stop();
        _ = _dispatcher.BeginInvoke(() => OnBatCompleted(
            index, step, session, result, stopwatch.Elapsed.TotalMilliseconds));
    }

    private void OnBatCompleted(
        int index,
        TestStepVm step,
        int session,
        BatResult result,
        double elapsedMs)
    {
        if (session != _session || !_running || index != _current)
        {
            return;
        }

        _batRunning = false;
        _batCts?.Dispose();
        _batCts = null;

        var measured = !result.Started
            ? "启动失败"
            : result.TimedOut
                ? "超时"
                : $"exit={result.ExitCode}";

        step.Complete(result.Passed, measured, elapsedMs);
        AddLog($"步骤 {index + 1} {step.Project} {(result.Passed ? "PASS" : "NG")} · {measured} · {elapsedMs:F0} ms · {result.Message}");

        foreach (var line in Tail(result.StandardOutput, result.StandardError))
        {
            AddLog($"[bat] {line}");
        }

        var isPreOrMain = step.Stage is "PreSeq" or "MainSeq";
        var isPost = string.Equals(step.Stage, "PostSeq", StringComparison.OrdinalIgnoreCase);
        if (!result.Passed && !isPost)
        {
            _hasNg = true;
        }

        _stepCommitted = true;
        if (!result.Passed && isPreOrMain)
        {
            if (TryMoveToPostSeq("Bat"))
            {
                Changed?.Invoke();
                return;
            }

            AddLog("未找到 PostSeq，终止后续步骤");
            FinishSession();
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>启动真实 UDS 步骤；执行器只在首个诊断请求时打开 CAN。</summary>
    private void BeginUds(int index, TestStepVm step)
    {
        _stepCommitted = false;
        _udsRunning = true;
        _udsCts = new CancellationTokenSource();

        var session = _session;
        var token = _udsCts.Token;
        AddLog($"步骤 {index + 1} 执行真实 UDS（超时 {step.TimeoutMs} ms）");
        _ = RunUdsAsync(index, step, session, token);
    }

    private async Task RunUdsAsync(
        int index,
        TestStepVm step,
        int session,
        CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        UdsStepResult result;
        try
        {
            result = await _udsExecutor!.ExecuteAsync(_rows[index], token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停止或视图关闭已更新当前行，取消回调不再改写结果
            return;
        }
        catch (Exception ex)
        {
            result = new UdsStepResult(false, "失败", $"执行异常：{ex.Message}");
        }

        stopwatch.Stop();
        _ = _dispatcher.BeginInvoke(() => OnUdsCompleted(
            index, step, session, result, stopwatch.Elapsed.TotalMilliseconds));
    }

    private void OnUdsCompleted(
        int index,
        TestStepVm step,
        int session,
        UdsStepResult result,
        double elapsedMs)
    {
        if (session != _session || !_running || index != _current)
        {
            return;
        }

        _udsRunning = false;
        _udsCts?.Dispose();
        _udsCts = null;

        step.Complete(result.Passed, result.Measured, elapsedMs);
        AddLog($"步骤 {index + 1} {step.Project} {(result.Passed ? "PASS" : "NG")} · " +
               $"{result.Measured} · {elapsedMs:F0} ms · {result.Detail}");

        var isPreOrMain = step.Stage is "PreSeq" or "MainSeq";
        var isPost = string.Equals(step.Stage, "PostSeq", StringComparison.OrdinalIgnoreCase);
        if (!result.Passed && !isPost)
        {
            _hasNg = true;
        }

        _stepCommitted = true;
        if (!result.Passed && isPreOrMain)
        {
            if (TryMoveToPostSeq("UDS"))
            {
                Changed?.Invoke();
                return;
            }

            AddLog("未找到 PostSeq，终止后续步骤");
            FinishSession();
            return;
        }

        Changed?.Invoke();
    }

    private bool TryMoveToPostSeq(string source)
    {
        var postIndex = -1;
        for (var i = _current + 1; i < Steps.Count; i++)
        {
            if (string.Equals(Steps[i].Stage, "PostSeq", StringComparison.OrdinalIgnoreCase))
            {
                postIndex = i;
                break;
            }
        }

        if (postIndex < 0)
        {
            return false;
        }

        var skippedCount = postIndex - _current - 1;
        for (var i = _current + 1; i < postIndex; i++)
        {
            Steps[i].MarkSkipped();
        }

        _current = postIndex - 1;
        StateText = "前段失败，转入后段清理";
        AddLog($"{source} 步骤失败，跳过 {skippedCount} 个前段步骤，转入 PostSeq");
        return true;
    }

    /// <summary>截取脚本文本输出的前若干行，避免刷屏。</summary>
    private static string[] Tail(string standardOutput, string standardError)
    {
        const int maxLines = 8;
        var lines = (standardOutput + Environment.NewLine + standardError)
            .Split(_newLineChars, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(maxLines)
            .ToArray();

        return lines;
    }

    /// <summary>解析 Bat 步骤命令：FILE=脚本,ARGS=参数,WORKDIR=工作目录。</summary>
    private static (string File, string? Arguments, string? WorkDirectory) ParseBatCommand(string command)
    {
        string? file = null;
        string? arguments = null;
        string? workDirectory = null;

        foreach (var segment in command.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();

            switch (key.ToUpperInvariant())
            {
                case "FILE":
                    file = value;
                    break;
                case "ARGS":
                    arguments = value;
                    break;
                case "WORKDIR":
                    workDirectory = value;
                    break;
            }
        }

        return (file ?? string.Empty, arguments, workDirectory);
    }

    private void CancelBat()
    {
        try
        {
            _batCts?.Cancel();
        }
        catch (Exception)
        {
            // 已释放或已取消
        }

        _batCts?.Dispose();
        _batCts = null;
        _batRunning = false;
    }

    private void CancelUds()
    {
        try
        {
            _udsCts?.Cancel();
        }
        catch (Exception)
        {
            // 已释放或已取消
        }

        _udsCts?.Dispose();
        _udsCts = null;
        _udsRunning = false;
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
        CancelBat();
        CancelUds();
        _running = false;
        _paused = false;
        _stepMode = false;
        _stepCommitted = false;
        _session++;
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
        BeginExecutorCleanup();
        Changed?.Invoke();
    }

    private void BeginExecutorCleanup()
    {
        if (_udsExecutor is null || !_cleanupTask.IsCompleted)
        {
            return;
        }

        _cleanupTask = CloseExecutorAsync();
    }

    private async Task CloseExecutorAsync()
    {
        string? error = null;
        try
        {
            await _udsExecutor!.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _ = _dispatcher.BeginInvoke(() =>
        {
            AddLog(error is null ? "CAN 资源已释放" : $"CAN 资源释放失败：{error}");
            Changed?.Invoke();
        });
    }

    private void ResetSession()
    {
        _timer.Stop();
        CancelBat();
        CancelUds();
        _current = -1;
        _hasNg = false;
        _stepCommitted = false;
        _session++;
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

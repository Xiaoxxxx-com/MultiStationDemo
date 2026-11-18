using System.Text;
using MultiStation.Platform.Core;
using MultiStation.Platform.Simulation;

namespace MultiStation.Ui.Demo;

/// <summary>
/// 控制台宿主：只负责看板渲染、按键与生命周期，测试线装配全部在 TestLineHost。
/// </summary>
internal sealed class DemoApp(DemoOptions options) : IDisposable
{
    private readonly DemoOptions _options = options;
    private readonly TestLineHost _host = new(options.LogDirectory);
    private readonly CancellationTokenSource _uiCts = new();

    /// <summary>释放资源：取消界面循环并释放取消令牌。</summary>
    public void Dispose()
    {
        _uiCts.Cancel();
        _uiCts.Dispose();
    }

    public async Task RunAsync()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var interactive = _options.ForceUi
                          || (!_options.Plain && !Console.IsOutputRedirected && !Console.IsInputRedirected);
        PrintBanner(interactive);

        if (_options.Seconds > 0)
        {
            _uiCts.CancelAfter(TimeSpan.FromSeconds(_options.Seconds));
        }

        void onCancel(object? _, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            _uiCts.Cancel();
        }
        Console.CancelKeyPress += onCancel;

        _host.Start();
        if (_options.ScriptedFaults)
        {
            _host.StartFaultScript();
        }

        try
        {
            if (interactive)
            {
                await RunDashboardAsync().ConfigureAwait(false);
            }
            else
            {
                await RunPlainAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        _uiCts.Cancel();
        await _host.StopAsync().ConfigureAwait(false);

        if (interactive)
        {
            Console.Write("\u001b[?25h\u001b[0m");
        }

        Console.WriteLine();
        PrintSummary();
    }

    private async Task RunDashboardAsync()
    {
        var dashboard = new Dashboard(_host);
        try
        {
            while (!_uiCts.IsCancellationRequested)
            {
                HandleKeys();
                dashboard.Render();
                await Task.Delay(250, _uiCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Dashboard.Restore();
        }
    }

    private async Task RunPlainAsync()
    {
        var reporter = new PlainReporter(_host);
        try
        {
            while (!_uiCts.IsCancellationRequested)
            {
                reporter.Tick();
                await Task.Delay(500, _uiCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void HandleKeys()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        while (Console.KeyAvailable)
        {
            var key = char.ToUpperInvariant(Console.ReadKey(intercept: true).KeyChar);
            switch (key)
            {
                case >= '1' and <= '6':
                    _host.ToggleFault(_host.Stations[key - '1'].Id);
                    break;

                case 'R':
                    _host.ResetFaults("手动复位");
                    break;

                case 'C':
                    _host.DisconnectRandomCamera(5);
                    break;

                case 'P':
                    _host.DisconnectPlc(4);
                    break;

                case 'I':
                    _host.Submit();
                    break;

                case 'T':
                    ToggleSingleStation();
                    break;

                case 'Q':
                    _uiCts.Cancel();
                    break;
            }
        }
    }

    /// <summary>演示单工位取消：只取消 ST4 自己的令牌，调度器与其他工位继续运行。</summary>
    private void ToggleSingleStation()
    {
        var station = _host.Stations[3];
        if (station.IsRunning)
        {
            _host.Log.ForContext("StationId", station.Id).Warn("手动停止本工位：只取消该工位令牌，其他工位不受影响");
            _ = station.StopAsync();
        }
        else
        {
            station.Start();
            _host.Log.ForContext("StationId", station.Id).Warn("手动启动本工位：继续消费输入队列");
        }
    }

    private void PrintBanner(bool interactive)
    {
        Console.WriteLine("多工位异步测试软件 Demo");
        Console.WriteLine("  调度器 + 独立工位状态机 + 有界 Channel + 设备通信域 + 结构化日志落盘");
        foreach (var recipe in _host.Recipes)
        {
            Console.WriteLine($"  配方 {recipe.Id}: {recipe.Describe()}");
        }

        Console.WriteLine($"  日志文件: {_host.LogPath}");
        if (interactive)
        {
            Console.WriteLine("  按键: 1-6 故障注入 | R 复位 | C 相机断线 | P PLC 断线 | I 投料 | T 停/启 ST4 | Q 退出");
        }

        Console.WriteLine();
    }

    private void PrintSummary()
    {
        var metrics = _host.Metrics;
        var elapsed = DateTimeOffset.Now - _host.StartedAt;

        Console.WriteLine("================ 运行摘要 ================");
        Console.WriteLine(
            $"运行时长 {elapsed.TotalSeconds:F1}s   投入 {metrics.Submitted}   良品 {metrics.Passed}   " +
            $"不良 {metrics.Failed}   未完成 {metrics.InProcess}");
        foreach (var station in _host.Stations)
        {
            var s = station.Snapshot();
            Console.WriteLine(
                $"  {s.StationId} {s.Name}  状态={s.State}  处理={s.Processed}  OK={s.Passed}  NG={s.Failed}  " +
                $"连续失败={s.ConsecutiveFailures}  队列={s.QueueDepth}");
        }

        Console.WriteLine($"结构化日志: {_host.LogPath}");
    }
}

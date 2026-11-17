using MultiStation.Platform.Core.Logging;
using MultiStation.Platform.Simulation;

namespace MultiStation.Ui.Demo;

/// <summary>纯文本输出，用于重定向/CI 场景下的自检。</summary>
internal sealed class PlainReporter
{
    private readonly TestLineHost _host;
    private long _lastSequence;
    private int _ticks;

    public PlainReporter(TestLineHost host)
    {
        _host = host;
    }

    public void Tick()
    {
        foreach (var entry in _host.RecentLogs(400))
        {
            if (entry.Sequence <= _lastSequence)
            {
                continue;
            }

            _lastSequence = entry.Sequence;
            if (entry.Level == LogLevel.Debug)
            {
                continue;
            }

            Console.WriteLine(Format(entry));
        }

        if (++_ticks % 10 == 0)
        {
            PrintStatus();
        }
    }

    private void PrintStatus()
    {
        var metrics = _host.Metrics;
        var elapsed = DateTimeOffset.Now - _host.StartedAt;
        var plc = _host.Devices.Plc.Snapshot;

        Console.WriteLine(
            $"[{elapsed:mm\\:ss}] 投入={metrics.Submitted} 良品={metrics.Passed} 不良={metrics.Failed} " +
            $"在制={metrics.InProcess} PLC={(plc.Connected ? "在线" : "断线")}");

        var stations = string.Join(
            "  ",
            _host.Stations.Select(station =>
            {
                var s = station.Snapshot();
                return $"{s.StationId}:{s.State}/队列{s.QueueDepth}/OK{s.Passed}/NG{s.Failed}";
            }));

        Console.WriteLine("           " + stations);
    }

    private static string Format(LogEntry entry)
    {
        var station = entry.Fields.TryGetValue("StationId", out var s) ? s?.ToString() : null;
        var work = entry.Fields.TryGetValue("WorkItemId", out var w) ? w?.ToString() : null;
        var level = entry.Level switch
        {
            LogLevel.Result => "RSLT",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERR ",
            LogLevel.Debug => "DBG ",
            _ => "INFO",
        };

        return $"{entry.Timestamp:HH:mm:ss.fff} {level} [{station ?? "-"}|{work ?? "-"}] {entry.Message}";
    }
}

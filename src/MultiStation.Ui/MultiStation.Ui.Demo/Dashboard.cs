using System.Globalization;
using System.Text;
using MultiStation.Platform.Core;
using MultiStation.Platform.Core.Logging;
using MultiStation.Platform.Simulation;

namespace MultiStation.Ui.Demo;

/// <summary>
/// 控制台看板：只读取工位/调度器/设备的只读快照，不做任何阻塞调用。
/// </summary>
internal sealed class Dashboard(TestLineHost host)
{
    private const string Esc = "\u001b[";
    private const string Reset = Esc + "0m";
    private const string Bold = Esc + "1m";
    private const string Dim = Esc + "90m";
    private const string Red = Esc + "31m";
    private const string Green = Esc + "32m";
    private const string Yellow = Esc + "33m";
    private const string Blue = Esc + "34m";
    private const string Cyan = Esc + "36m";
    private const string BoldRed = Esc + "1;31m";

    private readonly TestLineHost _host = host;
    private bool _firstFrame = true;

    public void Render()
    {
        var width = Math.Max(90, GetWindowWidth() - 1);
        var lines = BuildLines(width);
        var sb = new StringBuilder(lines.Count * 140);

        if (_firstFrame)
        {
            try
            {
                Console.Clear();
            }
            catch (IOException)
            {
                // 输出被重定向时没有控制台句柄，忽略清屏
            }

            Console.Write(Esc + "?25l");
            _firstFrame = false;
        }

        sb.Append(Esc).Append('H');
        foreach (var line in lines)
        {
            sb.Append(line).Append(Esc).Append("K\n");
        }

        try
        {
            Console.Write(sb.ToString());
        }
        catch (IOException)
        {
            // 终端被关闭时忽略绘制失败
        }
    }

    public static void Restore()
    {
        Console.Write(Esc + "?25h" + Reset);
    }

    private static int GetWindowWidth()
    {
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return 140;
        }
    }

    private List<string> BuildLines(int width)
    {
        var metrics = _host.Metrics;
        var elapsed = DateTimeOffset.Now - _host.StartedAt;
        var throughput = metrics.Completed / Math.Max(elapsed.TotalSeconds, 1);

        var lines = new List<string>
        {
            Bold + "多工位异步测试软件 Demo" + Reset + Dim + "   调度器 + 工位状态机 + Channel 流转" + Reset,
            Dim + Fit($"运行 {Format(elapsed)}   累计吞吐 {throughput:F2} 件/s   日志 {_host.LogPath}", width) + Reset,
        };

        foreach (var recipe in _host.Recipes)
        {
            lines.Add(Dim + $"配方 {recipe.Id}: {recipe.Describe()}" + Reset);
        }

        lines.Add(string.Empty);
        lines.Add(
            $"{Bold}调度域{Reset}  投入 {metrics.Submitted}  |  " +
            $"{Green}良品 {metrics.Passed}{Reset}  |  {Red}不良 {metrics.Failed}{Reset}  |  " +
            $"在制 {metrics.InProcess}  |  {Dim}丢弃日志 {_host.DroppedLogs}{Reset}");
        lines.Add(DeviceLine());
        lines.Add(FaultLine());
        lines.Add(string.Empty);
        lines.Add(HeaderLine(width));

        foreach (var station in _host.Stations)
        {
            lines.Add(StationLine(station, width));
        }

        lines.Add(string.Empty);
        lines.Add(Bold + "最近日志" + Reset + Dim + "（按 StationId 过滤即可还原单个工位时间线）" + Reset);

        var recent = _host.RecentLogs(80).Where(e => e.Level != LogLevel.Debug).TakeLast(9).ToArray();
        if (recent.Length == 0)
        {
            lines.Add(Dim + "（暂无日志）" + Reset);
        }

        foreach (var entry in recent)
        {
            lines.Add(LogLine(entry, width));
        }

        lines.Add(string.Empty);
        lines.Add(Dim + Fit("按键  1-6 故障注入 | R 复位 | C 相机断线 | P PLC 断线 | I 投料 | T 停/启 ST4 | Q 退出", width) + Reset);

        return lines;
    }

    private static string HeaderLine(int width)
    {
        return Bold
               + Fit("工位", 5)
               + Fit("名称", 11)
               + Fit("状态", 16)
               + Fit("当前工件", 9)
               + Fit("队列", 7)
               + Fit("OK/NG/连败", 14)
               + Fit("状态时长", 10)
               + Fit("最近结果 / 错误", width - 72)
               + Reset;
    }

    private static string StationLine(StationRuntime station, int width)
    {
        var s = station.Snapshot();
        var last = s.LastError ?? s.LastMessage ?? "-";
        return Fit(s.StationId, 5)
               + Fit(s.Name, 11)
               + Colorize(Fit(s.State.ToString(), 16), StateColor(s.State))
               + Fit(s.CurrentWorkId ?? "-", 9)
               + Fit($"{s.QueueDepth}", 7)
               + Fit($"{s.Passed}/{s.Failed}/{s.ConsecutiveFailures}", 14)
               + Fit($"{s.StateAge.TotalSeconds:F1}s", 10)
               + Fit(last, width - 72);
    }

    private static string LogLine(LogEntry entry, int width)
    {
        const int prefixWidth = 37;
        var level = entry.Level switch
        {
            LogLevel.Result => "RSLT",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERR ",
            LogLevel.Debug => "DBG ",
            _ => "INFO",
        };

        var levelColor = entry.Level switch
        {
            LogLevel.Result => Cyan,
            LogLevel.Warn => Yellow,
            LogLevel.Error => BoldRed,
            LogLevel.Debug => Dim,
            _ => Green,
        };

        var stationId = entry.Fields.TryGetValue("StationId", out var station) ? station?.ToString() : null;
        var workItemId = entry.Fields.TryGetValue("WorkItemId", out var work) ? work?.ToString() : null;
        var message = entry.Message.Replace('\r', ' ').Replace('\n', ' ');

        return Dim + entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + Reset
               + "  " + Colorize(Fit(level, 4), levelColor)
               + "  " + Fit(stationId ?? "-", 5)
               + " " + Fit(workItemId ?? "-", 8)
               + "  " + Fit(message, Math.Max(10, width - prefixWidth));
    }

    private string DeviceLine()
    {
        var plc = _host.Devices.Plc.Snapshot;
        var plcText = plc.Connected
            ? $"{Green}PLC 在线{Reset}{Dim}(心跳 {plc.HeartbeatCount}, {plc.Mode}){Reset}"
            : $"{BoldRed}PLC 断线{Reset}{Dim}(重连退避中){Reset}";

        var cameras = string.Join(
            " ",
            _host.Devices.Cameras.Select(camera => camera.Connected
                ? $"{Dim}{camera.StationId}{Reset}{Green}✔{Reset}"
                : $"{Dim}{camera.StationId}{Reset}{BoldRed}✘{Reset}"));

        return $"{Bold}设备域{Reset}  {plcText}  |  相机 {cameras}";
    }

    private string FaultLine()
    {
        var injected = _host.Stations.Where(s => _host.Scenario.IsFaulted(s.Id)).Select(s => s.Id).ToArray();
        var camerasDown = _host.Devices.Cameras.Where(c => !c.Connected).Select(c => c.StationId).ToArray();

        if (injected.Length == 0 && camerasDown.Length == 0)
        {
            return $"{Bold}故障注入{Reset}  无";
        }

        var text = $"{Bold}故障注入{Reset}  ";
        if (injected.Length > 0)
        {
            text += $"{Yellow}工艺故障 {string.Join(",", injected)}{Reset}  ";
        }

        if (camerasDown.Length > 0)
        {
            text += $"{Red}相机断线 {string.Join(",", camerasDown)}{Reset}";
        }

        return text.TrimEnd();
    }

    private static string StateColor(StationState state) => state switch
    {
        StationState.WaitingForWork => Green,
        StationState.Processing => Cyan,
        StationState.Completed => Blue,
        StationState.Error => BoldRed,
        StationState.Stopped => Dim,
        _ => Yellow,
    };

    private static string Colorize(string text, string color) => color + text + Reset;

    private static string Format(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

    /// <summary>按终端显示宽度补齐/截断（中文按 2 列计）。</summary>
    private static string Fit(string text, int width)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var current = DisplayWidth(text);
        if (current == width)
        {
            return text;
        }

        if (current < width)
        {
            return text + new string(' ', width - current);
        }

        var builder = new StringBuilder(width);
        var used = 0;
        foreach (var ch in text)
        {
            var charWidth = CharWidth(ch);
            if (used + charWidth > width)
            {
                break;
            }

            builder.Append(ch);
            used += charWidth;
        }

        if (used < width)
        {
            builder.Append(' ', width - used);
        }

        return builder.ToString();
    }

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text)
        {
            width += CharWidth(ch);
        }

        return width;
    }

    private static int CharWidth(char ch) => ch >= 0x2E80 ? 2 : 1;
}

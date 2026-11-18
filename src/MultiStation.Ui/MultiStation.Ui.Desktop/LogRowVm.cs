using System.Globalization;
using System.Windows.Media;
using MultiStation.Platform.Core.Logging;

namespace MultiStation.Ui.Desktop;

/// <summary>日志行视图模型。</summary>
public sealed class LogRowVm
{
    private static readonly Brush InfoBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush WarnBrush = CreateBrush(0x9A, 0x67, 0x00);
    private static readonly Brush ErrorBrush = CreateBrush(0xCF, 0x22, 0x2E);
    private static readonly Brush ResultBrush = CreateBrush(0x09, 0x69, 0xDA);
    private static readonly Brush DebugBrush = CreateBrush(0x6E, 0x77, 0x81);

    private LogRowVm(string time, string level, string tag, string message, Brush levelBrush, string stationId, LogLevel sourceLevel)
    {
        Time = time;
        Level = level;
        Tag = tag;
        Message = message;
        LevelBrush = levelBrush;
        StationId = stationId;
        SourceLevel = sourceLevel;
    }

    public string Time { get; }

    public string Level { get; }

    public string Tag { get; }

    public string Message { get; }

    public Brush LevelBrush { get; }

    public string StationId { get; }

    public LogLevel SourceLevel { get; }

    public static LogRowVm From(LogEntry entry)
    {
        var station = entry.Fields.TryGetValue("StationId", out var s) ? s?.ToString() : null;
        var work = entry.Fields.TryGetValue("WorkItemId", out var w) ? w?.ToString() : null;

        var tag = station is null && work is null
            ? "系统"
            : $"{station ?? "-"} · {work ?? "-"}";

        var (level, brush) = entry.Level switch
        {
            LogLevel.Warn => ("WARN", WarnBrush),
            LogLevel.Error => ("ERR", ErrorBrush),
            LogLevel.Result => ("RSLT", ResultBrush),
            LogLevel.Debug => ("DBG", DebugBrush),
            _ => ("INFO", InfoBrush),
        };

        return new LogRowVm(
            entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            level,
            tag,
            entry.Message,
            brush,
            station ?? string.Empty,
            entry.Level);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

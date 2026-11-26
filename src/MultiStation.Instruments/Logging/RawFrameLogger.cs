using System.Globalization;
using System.IO;

namespace MultiStation.Instruments.Logging;

/// <summary>
/// 原始 CAN 报文记录：按天写入 logs\raw\raw-yyyyMMdd.log，
/// 每行包含时间、方向、设备/通道、帧类型、CAN ID、长度与数据字节。
/// 记录失败不会影响测试流程。
/// </summary>
public static class RawFrameLogger
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static StreamWriter? _writer;
    private static DateOnly _fileDate;
    private static volatile bool _enabled;

    /// <summary>是否记录原始报文；关闭时不产生任何文件写入。</summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>当前日志文件路径；尚未写入时为 null。</summary>
    public static string? CurrentFile { get; private set; }

    /// <summary>写入分隔标记，用于区分测试会话边界。</summary>
    public static void Mark(string text)
    {
        WriteLine($"===== {Timestamp()} {text} =====");
    }

    /// <summary>记录一帧原始 CAN / CAN FD 报文。</summary>
    public static void Write(
        bool received,
        int deviceIndex,
        int channelIndex,
        CanFrame frame,
        bool canFd)
    {
        if (!_enabled)
        {
            return;
        }

        var direction = received ? "RX" : "TX";
        var id = frame.ExtendedId
            ? $"0x{frame.Id:X8}"
            : $"0x{frame.Id:X3}";
        var data = string.Join(' ', frame.Data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        WriteLine(
            $"{Timestamp()} {direction} DEV{deviceIndex} CH{channelIndex} " +
            $"{(canFd ? "FD" : "CAN")} {id} [{frame.Data.Length}] {data}");
    }

    private static void WriteLine(string line)
    {
        if (!_enabled)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (_writer is null || _fileDate != today)
                {
                    OpenFile(today);
                }

                _writer?.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写日志失败不能中断测试
                _writer = null;
            }
        }
    }

    private static void OpenFile(DateOnly today)
    {
        _writer?.Dispose();
        _writer = null;
        _directory ??= ResolveDirectory();
        var path = Path.Combine(_directory, $"raw-{today:yyyyMMdd}.log");
        _writer = new StreamWriter(path, append: true) { AutoFlush = true };
        _fileDate = today;
        CurrentFile = path;
    }

    private static string ResolveDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiStationDemo",
                "logs",
                "raw"),
            Path.Combine(AppContext.BaseDirectory, "logs", "raw"),
            Path.Combine(Path.GetTempPath(), "MultiStationDemo", "logs", "raw"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
            catch (Exception)
            {
                // 尝试下一个候选目录
            }
        }

        return candidates[^1];
    }

    private static string Timestamp() =>
        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

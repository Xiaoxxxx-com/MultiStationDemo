namespace MultiStation.Platform.Core.Logging;

/// <summary>
/// 日志目录解析：优先本地应用数据目录，不可写时回退到程序目录与临时目录，
/// 避免因目录权限导致整个上位机无法启动。
/// </summary>
public static class LogPaths
{
    /// <summary>解析并创建可写的日志目录。</summary>
    public static string ResolveLogDirectory()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiStationDemo", "logs"),
            Path.Combine(AppContext.BaseDirectory, "logs"),
            Path.Combine(Path.GetTempPath(), "MultiStationDemo", "logs")
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(candidate);
                var probe = Path.Combine(candidate, $".write-probe-{Guid.NewGuid():N}");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return candidate;
            }
            catch (Exception)
            {
                // 尝试下一个候选目录
            }
        }

        return candidates[^1];
    }
}

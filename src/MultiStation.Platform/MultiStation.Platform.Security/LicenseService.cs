using System.Text.Json;

namespace MultiStation.Platform.Security;

/// <summary>授权文件内容。</summary>
public sealed class LicenseOptions
{
    /// <summary>到期时间（本地时间，含时分秒）。为空表示未配置。</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>首次运行时自动创建的试用天数。</summary>
    public int TrialDays { get; set; } = 30;

    /// <summary>进入「即将到期」提醒的阈值天数。</summary>
    public int ExpiringWarningDays { get; set; } = 15;

    /// <summary>允许的时钟回拨容差（分钟），超过即判定为 Tampered。</summary>
    public int ClockRollbackToleranceMinutes { get; set; } = 10;
}

/// <summary>
/// 定时锁（授权到期锁定）服务。
/// 规则：启动时评估一次，运行期按分钟复评；过期、缺失或时钟回拨均锁定软件。
/// </summary>
public static class LicenseService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>安全目录：优先本地应用数据，不可写时回退到程序目录与临时目录。</summary>
    public static string SecurityDirectory { get; } = ResolveSecurityDirectory();

    public static string LicensePath => Path.Combine(SecurityDirectory, "license.json");

    public static string ClockPath => Path.Combine(SecurityDirectory, "lastseen.json");

    private static string ResolveSecurityDirectory()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MULTISTATION_SECURITY_DIR"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiStationDemo", "security"),
            Path.Combine(AppContext.BaseDirectory, "security"),
            Path.Combine(Path.GetTempPath(), "MultiStationDemo", "security")
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
                return candidate;
            }
            catch (Exception)
            {
                // 尝试下一个候选目录
            }
        }

        // 最后一个候选为固定生成的临时目录，必定非空
        return candidates[^1]!;
    }

    /// <summary>评估当前授权状态。nowOverride 仅用于测试。</summary>
    public static LicenseInfo Evaluate(DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.Now;

        try
        {
            Directory.CreateDirectory(SecurityDirectory);
        }
        catch (Exception ex)
        {
            return new LicenseInfo(null, 0, LicenseState.Missing, $"无法创建授权目录：{ex.Message}");
        }

        var options = LoadOrCreate(out var created);
        if (options is null || options.ExpiresAt is null)
        {
            return new LicenseInfo(null, 0, LicenseState.Missing, "授权文件无法解析或缺少到期时间");
        }

        if (!created && IsClockRolledBack(now, options))
        {
            return new LicenseInfo(options.ExpiresAt, 0, LicenseState.Tampered, "检测到系统时间被回拨，授权已锁定");
        }

        TouchClock(now);

        var expiresAt = options.ExpiresAt.Value;
        var remaining = (int)Math.Floor((expiresAt - now).TotalDays);

        if (expiresAt <= now)
        {
            return new LicenseInfo(expiresAt, 0, LicenseState.Expired,
                $"授权已于 {expiresAt:yyyy-MM-dd HH:mm} 到期，软件已锁定");
        }

        if (created)
        {
            return new LicenseInfo(expiresAt, remaining, LicenseState.Trial,
                $"试用授权，剩余 {remaining} 天（到期 {expiresAt:yyyy-MM-dd}）");
        }

        if (remaining <= Math.Max(0, options.ExpiringWarningDays))
        {
            return new LicenseInfo(expiresAt, remaining, LicenseState.Expiring,
                $"授权即将到期，剩余 {remaining} 天（到期 {expiresAt:yyyy-MM-dd}）");
        }

        return new LicenseInfo(expiresAt, remaining, LicenseState.Valid,
            $"授权有效，剩余 {remaining} 天（到期 {expiresAt:yyyy-MM-dd}）");
    }

    private static LicenseOptions? LoadOrCreate(out bool created)
    {
        created = false;

        if (File.Exists(LicensePath))
        {
            try
            {
                return JsonSerializer.Deserialize<LicenseOptions>(File.ReadAllText(LicensePath), JsonOptions);
            }
            catch (Exception)
            {
                return null;
            }
        }

        var options = new LicenseOptions
        {
            ExpiresAt = DateTimeOffset.Now.AddDays(30)
        };

        try
        {
            File.WriteAllText(LicensePath, JsonSerializer.Serialize(options, JsonOptions));
            created = true;
        }
        catch (Exception)
        {
            return options;
        }

        return options;
    }

    private static bool IsClockRolledBack(DateTimeOffset now, LicenseOptions options)
    {
        if (!File.Exists(ClockPath))
        {
            return false;
        }

        try
        {
            var last = JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(ClockPath));
            var tolerance = TimeSpan.FromMinutes(Math.Max(0, options.ClockRollbackToleranceMinutes));
            return now < last - tolerance;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void TouchClock(DateTimeOffset now)
    {
        try
        {
            if (File.Exists(ClockPath))
            {
                var last = JsonSerializer.Deserialize<DateTimeOffset>(File.ReadAllText(ClockPath));
                if (last >= now)
                {
                    return;
                }
            }

            File.WriteAllText(ClockPath, JsonSerializer.Serialize(now, JsonOptions));
        }
        catch (Exception)
        {
            // 时钟文件写入失败不阻断业务，下次启动重新判断
        }
    }
}

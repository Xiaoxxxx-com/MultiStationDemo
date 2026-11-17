using System.IO;
using System.Text;
using System.Text.Json;

namespace MultiStation.Platform.Security;

/// <summary>
/// 权限校验限时屏蔽（仅用于调试）。
/// 屏蔽期间权限校验一律放行，调试入口仍需管理员；屏蔽状态持久化到安全目录，
/// 重启后仍在有效期内则免登录直接以管理员会话进入（主界面显示醒目横幅）。
/// 到期自动恢复，开启 / 关闭 / 到期 / 恢复均写入 debug-bypass.log 审计。
/// </summary>
public static class PermissionBypass
{
    /// <summary>单次屏蔽时长上限，避免误操作导致权限长期失守。</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(90);

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static DateTimeOffset? _until;
    private static string _reason = string.Empty;

    /// <summary>状态变化（开启、关闭、到期）时触发。</summary>
    public static event Action? Changed;

    /// <summary>当前是否处于屏蔽期。</summary>
    public static bool IsActive => Remaining > TimeSpan.Zero;

    /// <summary>屏蔽截止时间，未开启时为 null。</summary>
    public static DateTimeOffset? Until
    {
        get
        {
            lock (Gate)
            {
                return _until;
            }
        }
    }

    /// <summary>本次屏蔽原因（审计用）。</summary>
    public static string Reason
    {
        get
        {
            lock (Gate)
            {
                return _reason;
            }
        }
    }

    /// <summary>剩余屏蔽时长，未开启或已到期为 0。</summary>
    public static TimeSpan Remaining
    {
        get
        {
            lock (Gate)
            {
                if (_until is null)
                {
                    return TimeSpan.Zero;
                }

                var remaining = _until.Value - DateTimeOffset.Now;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }

    /// <summary>界面展示用状态文本；未开启返回 null。超过一天时显示「N天 hh:mm:ss」。</summary>
    public static string? StateText
    {
        get
        {
            var remaining = Remaining;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            return remaining.TotalDays >= 1
                ? $"权限屏蔽中 剩余 {(int)remaining.TotalDays}天{remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}"
                : $"权限屏蔽中 剩余 {remaining.Hours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}";
        }
    }

    /// <summary>开启限时屏蔽。时长必须大于 0 且不超过 90 天，原因必填。</summary>
    public static bool Enable(TimeSpan duration, string reason, out string? error)
    {
        if (duration <= TimeSpan.Zero || duration > MaxDuration)
        {
            error = $"屏蔽时长需大于 0 且不超过 {MaxDuration.TotalDays:F0} 天。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            error = "必须填写屏蔽原因，便于审计追溯。";
            return false;
        }

        DateTimeOffset until;
        string trimmedReason = reason.Trim();
        lock (Gate)
        {
            until = DateTimeOffset.Now.Add(duration);
            _until = until;
            _reason = trimmedReason;
        }

        Persist(until, trimmedReason);
        Audit($"开启 时长={duration.TotalMinutes:F1}分钟 原因={trimmedReason}");
        Changed?.Invoke();
        error = null;
        return true;
    }

    /// <summary>提前关闭屏蔽。</summary>
    public static void Disable(string reason = "手动关闭")
    {
        lock (Gate)
        {
            if (_until is null)
            {
                return;
            }

            _until = null;
            _reason = string.Empty;
        }

        DeletePersisted();
        Audit("关闭 " + reason);
        Changed?.Invoke();
    }

    /// <summary>清理已到期的屏蔽状态（由界面定时调用，权限判断本身按时间实时生效）。</summary>
    public static void Refresh()
    {
        lock (Gate)
        {
            if (_until is null || DateTimeOffset.Now < _until.Value)
            {
                return;
            }

            _until = null;
            _reason = string.Empty;
        }

        DeletePersisted();
        Audit("到期自动恢复");
        Changed?.Invoke();
    }

    /// <summary>
    /// 启动时恢复持久化屏蔽：仍在有效期内返回 true（调用方可免登录进入），
    /// 到期、超出上限或文件损坏时清除文件并返回 false。
    /// </summary>
    public static bool Restore()
    {
        lock (Gate)
        {
            _until = null;
            _reason = string.Empty;
        }

        if (!File.Exists(StatePath))
        {
            return false;
        }

        PersistedState? state;
        try
        {
            state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(StatePath), JsonOptions);
        }
        catch (Exception)
        {
            DeletePersisted();
            Audit("持久化屏蔽文件损坏，已清除");
            return false;
        }

        if (state is null || state.Until <= DateTimeOffset.Now)
        {
            DeletePersisted();
            Audit("持久化屏蔽已到期，已清除");
            return false;
        }

        var remaining = state.Until - DateTimeOffset.Now;
        if (remaining > MaxDuration)
        {
            DeletePersisted();
            Audit("持久化屏蔽超出上限，已忽略");
            return false;
        }

        lock (Gate)
        {
            _until = state.Until;
            _reason = state.Reason;
        }

        Audit($"恢复屏蔽 剩余={remaining.TotalHours:F1}小时 原因={state.Reason} 开启人={state.EnabledBy}");
        Changed?.Invoke();
        return true;
    }

    /// <summary>审计日志路径。</summary>
    public static string AuditPath => Path.Combine(LicenseService.SecurityDirectory, "debug-bypass.log");

    /// <summary>持久化状态文件路径。</summary>
    public static string StatePath => Path.Combine(LicenseService.SecurityDirectory, "debug-bypass.json");

    private sealed record PersistedState(
        DateTimeOffset Until,
        string Reason,
        string EnabledBy,
        DateTimeOffset EnabledAt);

    private static void Persist(DateTimeOffset until, string reason)
    {
        try
        {
            Directory.CreateDirectory(LicenseService.SecurityDirectory);
            var state = new PersistedState(until, reason, SessionContext.DisplayName, DateTimeOffset.Now);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception)
        {
            // 持久化失败不阻断本次屏蔽，仅本次进程生效
        }
    }

    private static void DeletePersisted()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                File.Delete(StatePath);
            }
        }
        catch (Exception)
        {
            // 删除失败不影响内存状态
        }
    }

    private static void Audit(string action)
    {
        try
        {
            Directory.CreateDirectory(LicenseService.SecurityDirectory);
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\t{SessionContext.DisplayName}\t{action}{Environment.NewLine}";
            File.AppendAllText(AuditPath, line, Encoding.UTF8);
        }
        catch (Exception)
        {
            // 审计写入失败不阻断调试流程
        }
    }
}

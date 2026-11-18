namespace MultiStation.Platform.Security;

/// <summary>授权状态。</summary>
public enum LicenseState
{
    /// <summary>有效期内，且未进入到期提醒区间。</summary>
    Valid,
    /// <summary>试用期（首次运行自动创建，默认 30 天）。</summary>
    Trial,
    /// <summary>即将到期，进入提醒区间。</summary>
    Expiring,
    /// <summary>已过期，软件锁定。</summary>
    Expired,
    /// <summary>授权文件缺失且无法创建。</summary>
    Missing,
    /// <summary>系统时间回拨，判定为不可信。</summary>
    Tampered
}

/// <summary>授权评估结果。</summary>
public sealed record LicenseInfo(
    DateTimeOffset? ExpiresAt,
    int RemainingDays,
    LicenseState State,
    string Message)
{
    /// <summary>是否允许进入主界面。</summary>
    public bool IsUsable => State is LicenseState.Valid or LicenseState.Trial or LicenseState.Expiring;

    /// <summary>状态的中文名称。</summary>
    public string StateText => State switch
    {
        LicenseState.Valid => "有效",
        LicenseState.Trial => "试用",
        LicenseState.Expiring => "即将到期",
        LicenseState.Expired => "已过期",
        LicenseState.Missing => "授权缺失",
        LicenseState.Tampered => "授权异常",
        _ => State.ToString()
    };
}

/// <summary>账号角色，数值越大权限越高。</summary>
public enum UserRole
{
    Operator = 1,
    Engineer = 2,
    Admin = 3
}

/// <summary>可被授予的操作权限。</summary>
public enum PermissionKind
{
    /// <summary>启停测试、投料、复位故障等产线操作。</summary>
    Operate,
    /// <summary>修改配置、配方、序列。</summary>
    Configure,
    /// <summary>管理账号与查看审计。</summary>
    ManageUsers
}

/// <summary>账号记录（密码只保存盐与摘要）。</summary>
public sealed record UserAccount(string UserName, string Salt, string Hash, UserRole Role);

/// <summary>登录会话。</summary>
public sealed record UserSession(string UserName, UserRole Role, DateTimeOffset LoginAt)
{
    public bool Can(PermissionKind permission) => Role switch
    {
        UserRole.Admin => true,
        UserRole.Engineer => permission is PermissionKind.Operate or PermissionKind.Configure,
        UserRole.Operator => permission is PermissionKind.Operate,
        _ => false
    };

    public string DisplayName => $"{UserName}（{Role}）";
}

/// <summary>当前登录会话的全局访问点，由启动流程写入。</summary>
public static class SessionContext
{
    /// <summary>调试屏蔽免登录会话的显示名。</summary>
    public const string DebugBypassUserName = "调试屏蔽";

    public static UserSession? Current { get; set; }

    public static bool Has(PermissionKind permission) => Current?.Can(permission) == true;

    public static string DisplayName => Current?.DisplayName ?? "未登录";

    /// <summary>当前是否为调试屏蔽免登录会话。</summary>
    public static bool IsDebugBypassSession => Current?.UserName == DebugBypassUserName;
}

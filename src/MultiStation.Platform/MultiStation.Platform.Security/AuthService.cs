using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MultiStation.Platform.Security;

/// <summary>
/// 账号与登录服务。账号文件位于安全目录 users.json，
/// 密码以「盐 + SHA256」保存，不存明文。
/// 首次运行由用户在界面上注册唯一的管理员账号，代码中不预置任何账号。
/// </summary>
public static class AuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string UsersPath => Path.Combine(LicenseService.SecurityDirectory, "users.json");

    /// <summary>读取账号；文件不存在或损坏时返回空列表，不自动创建任何账号。</summary>
    public static IReadOnlyList<UserAccount> LoadUsers()
    {
        try
        {
            if (!File.Exists(UsersPath))
            {
                return Array.Empty<UserAccount>();
            }

            var users = JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(UsersPath), JsonOptions);
            return users ?? (IReadOnlyList<UserAccount>)Array.Empty<UserAccount>();
        }
        catch (Exception)
        {
            return Array.Empty<UserAccount>();
        }
    }

    /// <summary>是否已完成首次初始化（存在至少一个账号）。</summary>
    public static bool HasAnyUser() => LoadUsers().Count > 0;

    /// <summary>
    /// 首次初始化：注册唯一的管理员账号。已存在账号时拒绝，避免绕过登录重置权限。
    /// </summary>
    public static bool TryRegisterFirstAdmin(string userName, string password, out string? error)
    {
        error = ValidateUserName(userName) ?? ValidatePassword(password);

        if (error is null && HasAnyUser())
        {
            error = "系统已存在账号，不能重复初始化；如需重置请联系管理员删除 users.json 后重试。";
        }

        if (error is not null)
        {
            return false;
        }

        try
        {
            var (salt, hash) = CreateCredential(password);
            var users = new List<UserAccount>
            {
                new(userName.Trim(), salt, hash, UserRole.Admin)
            };

            Directory.CreateDirectory(LicenseService.SecurityDirectory);
            File.WriteAllText(UsersPath, JsonSerializer.Serialize(users, JsonOptions));
            SessionContext.Current = new UserSession(userName.Trim(), UserRole.Admin, DateTimeOffset.Now);
            return true;
        }
        catch (Exception ex)
        {
            error = $"账号写入失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>校验账号密码，成功返回会话，失败返回 null。</summary>
    public static UserSession? Authenticate(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        var account = LoadUsers().FirstOrDefault(u =>
            string.Equals(u.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));

        if (account is null)
        {
            return null;
        }

        var actual = Hash(account.Salt, password);
        return FixedTimeEquals(actual, account.Hash)
            ? new UserSession(account.UserName, account.Role, DateTimeOffset.Now)
            : null;
    }

    /// <summary>生成新的盐与摘要，供后续用户管理使用。</summary>
    public static (string Salt, string Hash) CreateCredential(string password)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return (salt, Hash(salt, password));
    }

    public static string Hash(string salt, string password)
    {
        var bytes = Encoding.UTF8.GetBytes(salt + ":" + password);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string? ValidateUserName(string userName)
    {
        var name = userName?.Trim() ?? string.Empty;
        if (name.Length is < 3 or > 32)
        {
            return "用户名长度需为 3~32 个字符。";
        }

        foreach (var ch in name)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '-' && ch != '.')
            {
                return "用户名只能包含字母、数字与 _ - . 。";
            }
        }

        return null;
    }

    private static string? ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            return "密码至少 8 位。";
        }

        var hasLetter = password.Any(char.IsLetter);
        var hasDigit = password.Any(char.IsDigit);
        if (!hasLetter || !hasDigit)
        {
            return "密码需同时包含字母与数字。";
        }

        return null;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using MultiStation.Platform.Security;
using MultiStation.Ui.Smoke;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        // 逻辑层边界用例直接调用 LicenseService，必须先把安全目录指向可写的隔离目录，
        // 且要在任何 LicenseService 静态成员被访问之前完成（SecurityDirectory 首次访问即定值）。
        var logicDir = Path.Combine(Path.GetTempPath(), "MultiStationUiSmoke", "logic");
        if (Directory.Exists(logicDir))
        {
            Directory.Delete(logicDir, true);
        }

        Directory.CreateDirectory(logicDir);
        Environment.SetEnvironmentVariable("MULTISTATION_SECURITY_DIR", logicDir);

        var exe = ResolveExe();
        if (!File.Exists(exe))
        {
            Console.WriteLine($"FAIL 未找到可执行文件：{exe}");
            return 1;
        }

        Console.WriteLine($"EXE {exe}");

        RunBoundaryLogic();
        RunM01(exe);
        RunM02(exe);
        RunM03(exe);
        RunM04(exe);
        RunUB01(exe);
        RunUB02(exe);

        Console.WriteLine($"SUMMARY failures={_failures}");
        return _failures == 0 ? 0 : 1;
    }

    // M-01：注册时两次密码不一致 → 提示且不创建账号
    private static void RunM01(string exe)
    {
        var dir = NewCaseDirectory("m01");
        WriteValidLicense(dir);

        using var driver = UiDriver.Launch(exe, dir);
        var window = driver.WaitWindow("首次初始化");
        if (window is null)
        {
            Fail("M-01", "未出现注册窗口");
            return;
        }

        var password = NewPassword();
        Set(window, "TxtUser", "admin01");
        Set(window, "TxtPassword", password);
        Set(window, "TxtConfirm", password + "x");

        var button = UiDriver.ByName(window, "创建并登录");
        if (button is null || !UiDriver.Click(button))
        {
            Fail("M-01", "未找到或无法点击「创建并登录」");
            return;
        }

        var prompted = UiDriver.WaitText(window, "两次输入的密码不一致");
        var usersCreated = File.Exists(Path.Combine(dir, "users.json"));
        Check("M-01", prompted && !usersCreated,
            $"提示不一致={prompted} 未创建账号={!usersCreated}");
    }

    // M-02：连续失败 5 次 → 禁止继续尝试
    private static void RunM02(string exe)
    {
        var dir = NewCaseDirectory("m02");
        WriteValidLicense(dir);
        WriteUsers(dir, "admin01", NewPassword(), "Admin");

        using var driver = UiDriver.Launch(exe, dir);
        var window = driver.WaitWindow("登录");
        if (window is null)
        {
            Fail("M-02", "未出现登录窗口");
            return;
        }

        Set(window, "TxtUser", "admin01");
        var loginButton = UiDriver.ByName(window, "登录");
        if (loginButton is null)
        {
            Fail("M-02", "未找到「登录」按钮");
            return;
        }

        for (var i = 0; i < 5; i++)
        {
            Set(window, "TxtPassword", "Wrong1" + i + Guid.NewGuid().ToString("N")[..4]);
            UiDriver.Click(loginButton);
            Thread.Sleep(350);
        }

        // 第 6 次点击应触发「连续失败次数过多」
        UiDriver.Click(loginButton);
        var locked = UiDriver.WaitText(window, "连续失败次数过多");
        var mainOpened = driver.WaitWindow("多工位测试上位机 · MultiStation", 1500) is not null;
        Check("M-02", locked && !mainOpened, $"提示锁定={locked} 未进入主界面={!mainOpened}");
    }

    // M-03：Operator 点击「字段配置」→ 权限不足且不弹出菜单
    private static void RunM03(string exe)
    {
        var dir = NewCaseDirectory("m03");
        WriteValidLicense(dir);
        var operatorPassword = NewPassword();
        WriteUsers(dir, "op01", operatorPassword, "Operator");

        using var driver = UiDriver.Launch(exe, dir);
        var loginWindow = driver.WaitWindow("登录");
        if (loginWindow is null)
        {
            Fail("M-03", "未出现登录窗口");
            return;
        }

        Set(loginWindow, "TxtUser", "op01");
        Set(loginWindow, "TxtPassword", operatorPassword);
        var loginButton = UiDriver.ByName(loginWindow, "登录");
        if (loginButton is null || !UiDriver.Click(loginButton))
        {
            Fail("M-03", "无法提交登录");
            return;
        }

        var main = driver.WaitWindow("多工位测试上位机 · MultiStation", 25000);
        if (main is null)
        {
            var names = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty, driver.ProcessId))
                .Cast<AutomationElement>()
                .Select(e => $"{e.Current.ControlType.ProgrammaticName}:{e.Current.Name}");
            Fail("M-03", "Operator 登录后未进入主界面；进程窗口=" + string.Join(" | ", names));
            return;
        }

        var fieldButton = UiDriver.ById(main, "BtnFieldConfig");
        if (fieldButton is null)
        {
            Fail("M-03", "未找到「字段配置」按钮");
            return;
        }

        var enabled = fieldButton.Current.IsEnabled;
        var offscreen = fieldButton.Current.IsOffscreen;
        var invokeError = string.Empty;
        try
        {
            ((InvokePattern)fieldButton.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        }
        catch (Exception ex)
        {
            invokeError = ex.GetType().Name + ":" + ex.Message;
        }

        var dialog = UiDriver.WaitDialog("权限不足", 15000);
        if (dialog is null)
        {
            var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                .Cast<AutomationElement>()
                .Select(e => $"{e.Current.ProcessId}:{e.Current.ClassName}:{e.Current.Name}");
            var menus = main.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu))
                .Count;
            Fail("M-03", "未出现「权限不足」提示；顶层窗口=" + string.Join(" | ", windows)
                + $"；菜单数={menus}；按钮启用={enabled} 离屏={offscreen} 调用异常={invokeError}");
            return;
        }

        var messageShown = UiDriver.WaitText(dialog, "无此操作权限");
        var ok = UiDriver.ByName(dialog, "确定") ?? UiDriver.ByName(dialog, "OK");
        if (ok is not null)
        {
            UiDriver.Click(ok);
        }

        Check("M-03", messageShown, $"提示文案={messageShown}");
    }

    // M-04：授权过期 → 锁定窗口显示到期信息与续期路径，退出后进程结束
    private static void RunM04(string exe)
    {
        var dir = NewCaseDirectory("m04");
        WriteExpiredLicense(dir);

        using var driver = UiDriver.Launch(exe, dir);
        var window = driver.WaitWindow("授权锁定", 15000);
        if (window is null)
        {
            Fail("M-04", "未出现授权锁定窗口");
            return;
        }

        var expiredShown = UiDriver.WaitText(window, "已过期");
        var pathShown = UiDriver.WaitText(window, "license.json");
        var mainOpened = driver.WaitWindow("MultiStation", 1500) is not null;

        var exitButton = UiDriver.ByName(window, "退出");
        var exited = false;
        if (exitButton is not null)
        {
            UiDriver.Click(exitButton);
            exited = driver.WaitForExit(8000);
        }

        Check("M-04", expiredShown && pathShown && !mainOpened && exited,
            $"过期提示={expiredShown} 续期路径={pathShown} 未进主界面={!mainOpened} 退出={exited}");
    }

    // ---------------- 边界值（逻辑层） ----------------

    private static void RunBoundaryLogic()
    {
        var dir = LicenseService.SecurityDirectory;
        Directory.CreateDirectory(dir);
        var now = DateTimeOffset.Now;

        void Reset()
        {
            foreach (var name in new[] { "license.json", "lastseen.json", "users.json" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // 剩余天数边界：16 天仍为有效，15 天进入即将到期
        Reset();
        WriteLicenseAt(dir, now.AddDays(16).AddMinutes(5));
        var b01 = LicenseService.Evaluate();
        Check("B-L01", b01.State == LicenseState.Valid && b01.RemainingDays == 16,
            $"state={b01.State} remain={b01.RemainingDays}");

        Reset();
        WriteLicenseAt(dir, now.AddDays(15).AddMinutes(5));
        var b02 = LicenseService.Evaluate();
        Check("B-L02", b02.State == LicenseState.Expiring && b02.RemainingDays == 15,
            $"state={b02.State} remain={b02.RemainingDays}");

        // 剩余 0 天（尚未到期）：仍可用，判为即将到期
        Reset();
        WriteLicenseAt(dir, now.AddSeconds(30));
        var b03 = LicenseService.Evaluate();
        Check("B-L03", b03.State == LicenseState.Expiring && b03.IsUsable && b03.RemainingDays == 0,
            $"state={b03.State} remain={b03.RemainingDays} usable={b03.IsUsable}");

        // 到期后 1 秒：立即锁定
        Reset();
        WriteLicenseAt(dir, now.AddSeconds(-1));
        var b04 = LicenseService.Evaluate();
        Check("B-L04", b04.State == LicenseState.Expired && !b04.IsUsable,
            $"state={b04.State} usable={b04.IsUsable}");

        // 时钟回拨容差边界：9 分钟不触发，11 分钟触发
        Reset();
        WriteLicenseAt(dir, now.AddDays(30));
        WriteClock(dir, now.AddMinutes(9));
        var b05 = LicenseService.Evaluate();
        Check("B-L05", b05.State == LicenseState.Valid, $"state={b05.State}");

        Reset();
        WriteLicenseAt(dir, now.AddDays(30));
        WriteClock(dir, now.AddMinutes(11));
        var b06 = LicenseService.Evaluate();
        Check("B-L06", b06.State == LicenseState.Tampered, $"state={b06.State}");

        // 授权文件损坏 / 缺字段：判为缺失，锁定
        Reset();
        File.WriteAllText(Path.Combine(dir, "license.json"), "{ not-a-json");
        var b07 = LicenseService.Evaluate();
        Check("B-L07", b07.State == LicenseState.Missing && !b07.IsUsable, $"state={b07.State}");

        Reset();
        File.WriteAllText(Path.Combine(dir, "license.json"),
            "{\"TrialDays\":30,\"ExpiringWarningDays\":15,\"ClockRollbackToleranceMinutes\":10}");
        var b08 = LicenseService.Evaluate();
        Check("B-L08", b08.State == LicenseState.Missing && !b08.IsUsable, $"state={b08.State}");

        // 用户名长度边界：2 拒绝 / 3 通过 / 32 通过 / 33 拒绝
        Reset();
        var password = "Aa1" + Guid.NewGuid().ToString("N")[..8];

        var u2 = AuthService.TryRegisterFirstAdmin(new string('u', 2), password, out _);
        Check("B-A01", !u2, $"len=2 register={u2}");

        var u3 = AuthService.TryRegisterFirstAdmin(new string('u', 3), password, out _);
        Check("B-A02", u3, $"len=3 register={u3}");
        Reset();

        var u32 = AuthService.TryRegisterFirstAdmin(new string('u', 32), password, out _);
        Check("B-A03", u32, $"len=32 register={u32}");
        Reset();

        var u33 = AuthService.TryRegisterFirstAdmin(new string('u', 33), password, out _);
        Check("B-A04", !u33, $"len=33 register={u33}");

        // 密码长度与组成边界：7 位拒绝 / 8 位含字母数字通过 / 8 位纯字母拒绝
        Reset();
        var p7 = AuthService.TryRegisterFirstAdmin("user01", "Aa1bcde", out _);
        Check("B-A05", !p7, $"len=7 register={p7}");

        var p8 = AuthService.TryRegisterFirstAdmin("user01", "Aa1bcdef", out _);
        Check("B-A06", p8, $"len=8 register={p8}");
        Reset();

        var pLetters = AuthService.TryRegisterFirstAdmin("user01", "Abcdefgh", out _);
        Check("B-A07", !pLetters, $"letters-only register={pLetters}");

        // 用户名字符边界：含 @ 拒绝
        var uIllegal = AuthService.TryRegisterFirstAdmin("ab@cd", password, out _);
        Check("B-A08", !uIllegal, $"illegal-char register={uIllegal}");

        Reset();
    }

    // ---------------- 边界值（界面层） ----------------

    // U-B01：失败 4 次后仍可登录成功（锁定阈值为第 5 次之后）
    private static void RunUB01(string exe)
    {
        var dir = NewCaseDirectory("ub01");
        WriteValidLicense(dir);
        var password = NewPassword();
        WriteUsers(dir, "admin01", password, "Admin");

        using var driver = UiDriver.Launch(exe, dir);
        var window = driver.WaitWindow("登录");
        if (window is null)
        {
            Fail("U-B01", "未出现登录窗口");
            return;
        }

        Set(window, "TxtUser", "admin01");
        var loginButton = UiDriver.ByName(window, "登录");
        if (loginButton is null)
        {
            Fail("U-B01", "未找到「登录」按钮");
            return;
        }

        for (var i = 0; i < 4; i++)
        {
            Set(window, "TxtPassword", "Wrong1" + i + Guid.NewGuid().ToString("N")[..4]);
            UiDriver.Click(loginButton);
            Thread.Sleep(300);
        }

        Set(window, "TxtPassword", password);
        UiDriver.Click(loginButton);

        var main = driver.WaitWindow("MultiStation", 20000) is not null;
        Check("U-B01", main, $"4 次失败后正确登录进入主界面={main}");
    }

    // U-B02：授权剩余 1 分钟仍可进入登录（未到期不锁定）
    private static void RunUB02(string exe)
    {
        var dir = NewCaseDirectory("ub02");
        WriteLicenseAt(dir, DateTimeOffset.Now.AddMinutes(1));
        WriteUsers(dir, "admin01", NewPassword(), "Admin");

        using var driver = UiDriver.Launch(exe, dir);
        var locked = driver.WaitWindow("授权锁定", 3000) is not null;
        var login = driver.WaitWindow("登录", 12000) is not null;
        Check("U-B02", !locked && login, $"未锁定={!locked} 出现登录窗口={login}");
    }

    // ---------------- 辅助 ----------------

    private static void Set(AutomationElement window, string automationId, string text)
    {
        var element = UiDriver.ById(window, automationId);
        if (element is null || !UiDriver.SetText(element, text))
        {
            throw new InvalidOperationException($"无法写入控件 {automationId}");
        }
    }

    private static void Check(string id, bool ok, string detail)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {detail}");
        if (!ok)
        {
            _failures++;
        }
    }

    private static void Fail(string id, string message)
    {
        Console.WriteLine($"FAIL {id} {message}");
        _failures++;
    }

    private static string NewPassword() => "Aa1" + Guid.NewGuid().ToString("N")[..10];

    private static string NewCaseDirectory(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "MultiStationUiSmoke", name);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteLicenseAt(string dir, DateTimeOffset expires)
    {
        File.WriteAllText(
            Path.Combine(dir, "license.json"),
            $"{{\"ExpiresAt\":\"{expires:O}\",\"TrialDays\":30,\"ExpiringWarningDays\":15,\"ClockRollbackToleranceMinutes\":10}}");
    }

    private static void WriteClock(string dir, DateTimeOffset seen)
    {
        File.WriteAllText(Path.Combine(dir, "lastseen.json"), $"\"{seen:O}\"");
    }

    private static void WriteValidLicense(string dir)
    {
        var expires = DateTimeOffset.Now.AddDays(30).ToString("O");
        File.WriteAllText(
            Path.Combine(dir, "license.json"),
            $"{{\"ExpiresAt\":\"{expires}\",\"TrialDays\":30,\"ExpiringWarningDays\":15,\"ClockRollbackToleranceMinutes\":10}}");
    }

    private static void WriteExpiredLicense(string dir)
    {
        var expires = DateTimeOffset.Now.AddDays(-1).ToString("O");
        File.WriteAllText(
            Path.Combine(dir, "license.json"),
            $"{{\"ExpiresAt\":\"{expires}\",\"TrialDays\":30,\"ExpiringWarningDays\":15,\"ClockRollbackToleranceMinutes\":10}}");
    }

    private static void WriteUsers(string dir, string userName, string password, string role)
    {
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + password)));
        var json = JsonSerializer.Serialize(new[]
        {
            new { UserName = userName, Salt = salt, Hash = hash, Role = role }
        });
        File.WriteAllText(Path.Combine(dir, "users.json"), json);
    }

    private static string ResolveExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MultiStationDemo.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("未找到解决方案根目录");
        return Path.Combine(
            root,
            "src", "MultiStation.Ui", "MultiStation.Ui.Desktop",
            "bin", "Debug", "net10.0-windows", "MultiStation.Ui.Desktop.exe");
    }
}

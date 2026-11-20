using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Automation;
using MultiStation.Platform.Core.Execution;
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

        RunBatChecks();
        RunBypassChecks();
        RunBoundaryLogic();
        RunM01(exe);
        RunM02(exe);
        RunM03(exe);
        RunM04(exe);
        RunUB01(exe);
        RunUB02(exe);
        RunUB03(exe);
        RunUB04(exe);
        RunUB05(exe);
        RunUBP01(exe);
        RunUBP02(exe);
        RunUBP03(exe);
        RunUCF01(exe);
        RunUCF02(exe);
        RunUAU01(exe);

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

        var main = LoginToMain(driver, loginWindow, "op01", operatorPassword);
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

    // ---------------- 测试序列 · 外部 BAT 执行 ----------------

    private static void RunBatChecks()
    {
        var dir = NewCaseDirectory("bat");
        File.WriteAllText(Path.Combine(dir, "ok.bat"), "@echo off\r\necho BAT-OK\r\nexit /b 0\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(dir, "ng.bat"), "@echo off\r\necho BAT-NG\r\nexit /b 2\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(dir, "slow.bat"), "@echo off\r\nping -n 6 127.0.0.1 >nul\r\nexit /b 0\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(dir, "note.txt"), "not a script", Encoding.ASCII);

        var ok = BatRunner.RunAsync(Path.Combine(dir, "ok.bat"), null, null, 15000, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("BAT-01", ok.Passed && ok.ExitCode == 0 && ok.StandardOutput.Contains("BAT-OK"),
            $"started={ok.Started} exit={ok.ExitCode} out={ok.StandardOutput.Trim()}");

        var ng = BatRunner.RunAsync(Path.Combine(dir, "ng.bat"), null, null, 15000, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("BAT-02", ng.Started && !ng.Passed && ng.ExitCode == 2,
            $"started={ng.Started} passed={ng.Passed} exit={ng.ExitCode}");

        var slow = BatRunner.RunAsync(Path.Combine(dir, "slow.bat"), null, null, 800, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("BAT-03", slow.TimedOut && !slow.Passed, $"timedOut={slow.TimedOut} passed={slow.Passed}");

        var missing = BatRunner.RunAsync(Path.Combine(dir, "missing.bat"), null, null, 5000, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("BAT-04", !missing.Started && !missing.Passed, $"started={missing.Started} message={missing.Message}");

        var unsupported = BatRunner.RunAsync(Path.Combine(dir, "note.txt"), null, null, 5000, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("BAT-05", !unsupported.Started && unsupported.Message.Contains("仅支持"),
            $"started={unsupported.Started} message={unsupported.Message}");

        // 序列中实际配置的示例脚本：相对路径从程序目录逐级向上查找
        // check 模式只解析路径不启动 QQ，避免自动化用例真的拉起外部程序
        var sample = BatRunner.RunAsync(@"Script\Bat\StartQQ.bat", "check", null, 15000, CancellationToken.None)
            .GetAwaiter().GetResult();
        Check("BAT-06", sample.Passed && sample.StandardOutput.Contains("[StartQQ]"),
            $"started={sample.Started} exit={sample.ExitCode} out={sample.StandardOutput.Trim()}");
    }

    // ---------------- 权限限时屏蔽（调试功能） ----------------

    private static void RunBypassChecks()
    {
        PermissionBypass.Disable("用例前置清理");
        SessionContext.Current = null;

        var enabled = PermissionBypass.Enable(TimeSpan.FromMinutes(15), "调试权限拦截", out var error);
        Check("BP-01", enabled && PermissionBypass.IsActive
                && PermissionBypass.Remaining > TimeSpan.FromMinutes(14)
                && PermissionBypass.StateText?.Contains("权限屏蔽中") == true,
            $"enabled={enabled} active={PermissionBypass.IsActive} remain={PermissionBypass.Remaining:mm\\:ss} error={error}");

        var tooLong = PermissionBypass.Enable(TimeSpan.FromDays(91), "超限", out var tooLongError);
        Check("BP-02", !tooLong && tooLongError is not null && PermissionBypass.IsActive,
            $"enabled={tooLong} error={tooLongError}");

        var zero = PermissionBypass.Enable(TimeSpan.Zero, "零时长", out var zeroError);
        Check("BP-03", !zero && zeroError is not null, $"enabled={zero} error={zeroError}");

        var noReason = PermissionBypass.Enable(TimeSpan.FromMinutes(5), "   ", out var noReasonError);
        Check("BP-04", !noReason && noReasonError is not null, $"enabled={noReason} error={noReasonError}");

        // 屏蔽不改变会话角色：绕过只发生在权限校验入口
        var operatorSession = new UserSession("op", UserRole.Operator, DateTimeOffset.Now);
        SessionContext.Current = operatorSession;
        var sessionStillLimited = !operatorSession.Can(PermissionKind.Configure)
            && !SessionContext.Has(PermissionKind.Configure)
            && PermissionBypass.IsActive;
        SessionContext.Current = null;
        Check("BP-05", sessionStillLimited, $"bypass-active={PermissionBypass.IsActive} session-can-configure={operatorSession.Can(PermissionKind.Configure)}");

        PermissionBypass.Disable("用例关闭");
        Check("BP-06", !PermissionBypass.IsActive && PermissionBypass.Until is null,
            $"active={PermissionBypass.IsActive} until={PermissionBypass.Until}");

        // 到期自动恢复
        var shortBypass = PermissionBypass.Enable(TimeSpan.FromSeconds(1.2), "到期用例", out _);
        Thread.Sleep(1500);
        var expired = shortBypass && !PermissionBypass.IsActive && PermissionBypass.Remaining == TimeSpan.Zero;
        PermissionBypass.Refresh();
        Check("BP-07", expired, $"enabled={shortBypass} active={PermissionBypass.IsActive}");

        // 长时长边界：90 天（上限）允许，超限拒绝见 BP-02，状态文本按「天」展示
        var maxAllowed = PermissionBypass.Enable(TimeSpan.FromDays(90), "上限用例", out var maxError);
        var maxState = PermissionBypass.StateText ?? string.Empty;
        Check("BP-09", maxAllowed && PermissionBypass.Remaining > TimeSpan.FromDays(89)
                && maxState.Contains('天'),
            $"enabled={maxAllowed} active={PermissionBypass.IsActive} state={maxState} error={maxError}");

        // 周单位折算：1 周 = 7 天
        var week = PermissionBypass.Enable(TimeSpan.FromDays(7), "一周用例", out _);
        Check("BP-10", week && PermissionBypass.Remaining > TimeSpan.FromDays(6)
                && PermissionBypass.Remaining <= TimeSpan.FromDays(7),
            $"enabled={week} remain={PermissionBypass.Remaining.TotalDays:F2}天");

        PermissionBypass.Disable("长时长用例收尾");

        var audit = File.Exists(PermissionBypass.AuditPath)
            ? File.ReadAllText(PermissionBypass.AuditPath)
            : string.Empty;
        Check("BP-08",
            audit.Contains("开启") && audit.Contains("调试权限拦截") && audit.Contains("关闭")
                && audit.Contains("到期自动恢复"),
            $"audit={audit.Replace(Environment.NewLine, " | ").Trim()}");

        // 持久化：开启写文件，关闭删除
        PermissionBypass.Disable("持久化用例前置清理");
        var persistedEnable = PermissionBypass.Enable(TimeSpan.FromDays(2), "持久化用例", out _);
        var persistedExists = File.Exists(PermissionBypass.StatePath);
        PermissionBypass.Disable("持久化用例收尾");
        var persistedDeleted = !File.Exists(PermissionBypass.StatePath);
        Check("BP-11", persistedEnable && persistedExists && persistedDeleted,
            $"enabled={persistedEnable} 文件写入={persistedExists} 关闭后删除={persistedDeleted}");

        // 重启恢复：文件仍在有效期 → 恢复屏蔽（免登录依据）
        WriteBypassState(LicenseService.SecurityDirectory, DateTimeOffset.Now.AddDays(2), "重启恢复用例");
        var restored = PermissionBypass.Restore();
        Check("BP-12", restored && PermissionBypass.IsActive
                && PermissionBypass.Reason == "重启恢复用例" && File.Exists(PermissionBypass.StatePath),
            $"restored={restored} active={PermissionBypass.IsActive} reason={PermissionBypass.Reason}");

        // 已到期文件 → 不恢复且删除
        WriteBypassState(LicenseService.SecurityDirectory, DateTimeOffset.Now.AddMinutes(-1), "过期用例");
        var restoreExpired = PermissionBypass.Restore();
        Check("BP-13", !restoreExpired && !PermissionBypass.IsActive && !File.Exists(PermissionBypass.StatePath),
            $"restored={restoreExpired} active={PermissionBypass.IsActive} 文件残留={File.Exists(PermissionBypass.StatePath)}");

        // 超出 90 天上限（文件被篡改）→ 不恢复且删除
        WriteBypassState(LicenseService.SecurityDirectory, DateTimeOffset.Now.AddDays(120), "超限用例");
        var restoreOverLimit = PermissionBypass.Restore();
        Check("BP-14", !restoreOverLimit && !PermissionBypass.IsActive && !File.Exists(PermissionBypass.StatePath),
            $"restored={restoreOverLimit} active={PermissionBypass.IsActive} 文件残留={File.Exists(PermissionBypass.StatePath)}");
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

        // 时钟文件损坏：视为无记录，不误判为回拨
        Reset();
        WriteLicenseAt(dir, now.AddDays(30));
        File.WriteAllText(Path.Combine(dir, "lastseen.json"), "{ broken");
        var b09 = LicenseService.Evaluate();
        Check("B-L09", b09.State == LicenseState.Valid, $"state={b09.State}");

        // 到期时刻恰好到达：立即锁定
        Reset();
        WriteLicenseAt(dir, now);
        var b10 = LicenseService.Evaluate();
        Check("B-L10", b10.State == LicenseState.Expired && !b10.IsUsable,
            $"state={b10.State} usable={b10.IsUsable}");

        // 容差为 0 分钟：回拨 1 分钟即判定异常
        Reset();
        WriteLicenseAt(dir, now.AddDays(30), toleranceMinutes: 0);
        WriteClock(dir, now.AddMinutes(1));
        var b11 = LicenseService.Evaluate();
        Check("B-L11", b11.State == LicenseState.Tampered, $"state={b11.State}");

        // 回拨恰好等于容差 10 分钟：不触发（边界含端点）
        Reset();
        WriteLicenseAt(dir, now.AddDays(30), toleranceMinutes: 10);
        WriteClock(dir, now.AddMinutes(10));
        var b12 = LicenseService.Evaluate();
        Check("B-L12", b12.State == LicenseState.Valid, $"state={b12.State}");

        // 用户名首尾空格：trim 后 3 位通过，且存储名已去空格
        Reset();
        var uTrim = AuthService.TryRegisterFirstAdmin("  abc  ", password, out _);
        var users = AuthService.LoadUsers();
        var stored = users.Count > 0 ? users[0].UserName : null;
        Check("B-A09", uTrim && stored == "abc", $"register={uTrim} stored={stored}");

        // 用户名首尾空格：trim 后 2 位仍拒绝
        Reset();
        var uTrimShort = AuthService.TryRegisterFirstAdmin("  ab  ", password, out _);
        Check("B-A10", !uTrimShort, $"trimmed-len=2 register={uTrimShort}");

        // 用户名合法字符集边界：字母、数字与 _ - . 全部允许
        Reset();
        var uChars = AuthService.TryRegisterFirstAdmin("a_b-c.d", password, out _);
        Check("B-A11", uChars, $"allowed-chars register={uChars}");

        // 空凭据：空用户名或空密码均拒绝登录
        Reset();
        var emptyUser = AuthService.Authenticate(string.Empty, password);
        var emptyPassword = AuthService.Authenticate("abc", string.Empty);
        Check("B-A12", emptyUser is null && emptyPassword is null,
            $"empty-user={emptyUser is null} empty-password={emptyPassword is null}");

        // 用户名大小写不敏感：全大写仍可登录
        Reset();
        AuthService.TryRegisterFirstAdmin("Admin14", password, out _);
        var upperLogin = AuthService.Authenticate("ADMIN14", password);
        Check("B-A13", upperLogin is not null, $"upper-login={upperLogin is not null}");

        // 密码 8 位但纯数字：缺字母，拒绝
        Reset();
        var pDigits = AuthService.TryRegisterFirstAdmin("user01", "12345678", out _);
        Check("B-A14", !pDigits, $"digits-only register={pDigits}");

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

    // U-B03：注册界面下限——用户名 3 位、密码 8 位，恰好通过并进入主界面
    private static void RunUB03(string exe)
    {
        var dir = NewCaseDirectory("ub03");
        WriteValidLicense(dir);

        using var driver = UiDriver.Launch(exe, dir);
        var window = driver.WaitWindow("首次初始化");
        if (window is null)
        {
            Fail("U-B03", "未出现注册窗口");
            return;
        }

        Set(window, "TxtUser", "abc");
        Set(window, "TxtPassword", "Aa1bcdef");
        Set(window, "TxtConfirm", "Aa1bcdef");

        var button = UiDriver.ByName(window, "创建并登录");
        if (button is null || !UiDriver.Click(button))
        {
            Fail("U-B03", "未找到或无法点击「创建并登录」");
            return;
        }

        var main = driver.WaitWindow("多工位测试上位机 · MultiStation", 25000) is not null;
        var usersCreated = File.Exists(Path.Combine(dir, "users.json"));
        Check("U-B03", main && usersCreated, $"进入主界面={main} 已创建账号={usersCreated}");
    }

    // U-B04：注册界面下限外——密码 7 位，提示且不创建账号
    private static void RunUB04(string exe)
    {
        var dir = NewCaseDirectory("ub04");
        WriteValidLicense(dir);

        using var driver = UiDriver.Launch(exe, dir);
        var window = driver.WaitWindow("首次初始化");
        if (window is null)
        {
            Fail("U-B04", "未出现注册窗口");
            return;
        }

        Set(window, "TxtUser", "abc");
        Set(window, "TxtPassword", "Aa1bcde");
        Set(window, "TxtConfirm", "Aa1bcde");

        var button = UiDriver.ByName(window, "创建并登录");
        if (button is null || !UiDriver.Click(button))
        {
            Fail("U-B04", "未找到或无法点击「创建并登录」");
            return;
        }

        var prompted = UiDriver.WaitText(window, "密码至少 8 位");
        var usersCreated = File.Exists(Path.Combine(dir, "users.json"));
        var mainOpened = driver.WaitWindow("MultiStation", 1500) is not null;
        Check("U-B04", prompted && !usersCreated && !mainOpened,
            $"提示={prompted} 未创建账号={!usersCreated} 未进主界面={!mainOpened}");
    }

    // U-B05：角色边界——Engineer 点击「字段配置」应放行并展开菜单（Operator 见 M-03）
    private static void RunUB05(string exe)
    {
        var dir = NewCaseDirectory("ub05");
        WriteValidLicense(dir);
        var password = NewPassword();
        WriteUsers(dir, "eng01", password, "Engineer");

        using var driver = UiDriver.Launch(exe, dir);
        var loginWindow = driver.WaitWindow("登录");
        if (loginWindow is null)
        {
            Fail("U-B05", "未出现登录窗口");
            return;
        }

        var main = LoginToMain(driver, loginWindow, "eng01", password);
        if (main is null)
        {
            Fail("U-B05", "Engineer 登录后未进入主界面");
            return;
        }

        var fieldButton = UiDriver.ById(main, "BtnFieldConfig");
        if (fieldButton is null || !UiDriver.Click(fieldButton))
        {
            Fail("U-B05", "未找到或无法点击「字段配置」");
            return;
        }

        var menuShown = false;
        var menuCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
            new PropertyCondition(AutomationElement.ProcessIdProperty, driver.ProcessId));
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && !menuShown)
        {
            try
            {
                // ContextMenu 是独立弹窗，按进程范围在整棵 UIA 树中查找
                menuShown = AutomationElement.RootElement.FindFirst(TreeScope.Subtree, menuCondition) is not null;
            }
            catch (ElementNotAvailableException)
            {
                break;
            }

            if (!menuShown)
            {
                Thread.Sleep(200);
            }
        }

        var denied = UiDriver.WaitDialog("权限不足", 1200) is not null;
        Check("U-B05", menuShown && !denied, $"菜单展开={menuShown} 未提示权限不足={!denied}");
    }

    // U-BP01：Operator 点击「调试屏蔽」→ 仅限管理员，且不弹出屏蔽窗口
    private static void RunUBP01(string exe)
    {
        var dir = NewCaseDirectory("ubp01");
        WriteValidLicense(dir);
        var password = NewPassword();
        WriteUsers(dir, "op01", password, "Operator");

        using var driver = UiDriver.Launch(exe, dir);
        var loginWindow = driver.WaitWindow("登录");
        if (loginWindow is null)
        {
            Fail("U-BP01", "未出现登录窗口");
            return;
        }

        var main = LoginToMain(driver, loginWindow, "op01", password);
        if (main is null)
        {
            Fail("U-BP01", "Operator 登录后未进入主界面");
            return;
        }

        var button = UiDriver.ById(main, "BtnDebugBypass");
        if (button is null || !UiDriver.Click(button))
        {
            Fail("U-BP01", "未找到或无法点击「调试屏蔽」");
            return;
        }

        var dialog = UiDriver.WaitDialog("权限不足", 15000);
        var message = dialog is not null && UiDriver.WaitText(dialog, "仅限管理员");
        var debugWindow = driver.WaitOwnedWindow("权限限时屏蔽", 1500) is not null;

        if (dialog is not null)
        {
            var ok = UiDriver.ByName(dialog, "确定") ?? UiDriver.ByName(dialog, "OK");
            if (ok is not null)
            {
                UiDriver.Click(ok);
            }
        }

        Check("U-BP01", dialog is not null && message && !debugWindow,
            $"提示={dialog is not null} 文案={message} 未弹屏蔽窗={!debugWindow}");
    }

    // U-BP02：Admin 开启 1 分钟屏蔽 → 顶部显示剩余时间；再次进入关闭 → 状态清空
    private static void RunUBP02(string exe)
    {
        var dir = NewCaseDirectory("ubp02b");
        WriteValidLicense(dir);
        var password = NewPassword();
        WriteUsers(dir, "admin02", password, "Admin");

        using var driver = UiDriver.Launch(exe, dir);
        var loginWindow = driver.WaitWindow("登录");
        if (loginWindow is null)
        {
            Fail("U-BP02", "未出现登录窗口");
            return;
        }

        var main = LoginToMain(driver, loginWindow, "admin02", password);
        if (main is null)
        {
            Fail("U-BP02", "Admin 登录后未进入主界面");
            return;
        }

        var button = UiDriver.ById(main, "BtnDebugBypass");
        if (button is null || !UiDriver.Click(button))
        {
            Fail("U-BP02", "未找到或无法点击「调试屏蔽」");
            return;
        }

        var dialog = driver.WaitOwnedWindow("权限限时屏蔽", 15000);
        if (dialog is null)
        {
            var windows = AutomationElement.RootElement.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                .Cast<AutomationElement>()
                .Where(element => element.Current.ProcessId == driver.ProcessId)
                .Select(element => $"{element.Current.ClassName}:{element.Current.Name}");
            var denied = UiDriver.WaitDialog("权限不足", 1000) is not null;
            Fail("U-BP02", "未出现权限屏蔽窗口；进程窗口=" + string.Join(" | ", windows)
                + $"；权限不足提示={denied}");
            return;
        }

        Set(dialog, "TxtBypassMinutes", "2");
        var unitCombo = UiDriver.ById(dialog, "CmbBypassUnit");
        var selectedUnit = unitCombo is not null && SelectComboBoxItem(unitCombo, "天");
        Set(dialog, "TxtBypassReason", "自动化调试");
        var enableButton = UiDriver.ById(dialog, "BtnEnableBypass");
        if (enableButton is null || !UiDriver.Click(enableButton))
        {
            Fail("U-BP02", "未找到或无法点击「开启屏蔽」");
            return;
        }

        var dialogClosed = WaitUntil(() => driver.WaitOwnedWindow("权限限时屏蔽", 120) is null);
        var indicator = WaitUntil(() => UiDriver.WaitText(main, "权限屏蔽中", 300));
        var longState = WaitUntil(() =>
            UiDriver.ById(main, "TxtBypassState")?.Current.Name?.Contains('天') == true);
        var buttonText = WaitUntil(() => UiDriver.ById(main, "BtnDebugBypass")?.Current.Name == "屏蔽中");

        // 再次打开并关闭屏蔽
        var reopen = UiDriver.ById(main, "BtnDebugBypass");
        var closedByUser = false;
        if (reopen is not null && UiDriver.Click(reopen))
        {
            var dialog2 = driver.WaitOwnedWindow("权限限时屏蔽", 15000);
            var disableButton = dialog2 is null ? null : UiDriver.ById(dialog2, "BtnDisableBypass");
            if (disableButton is not null && UiDriver.Click(disableButton))
            {
                closedByUser = WaitUntil(() => driver.WaitOwnedWindow("权限限时屏蔽", 120) is null);
            }
        }

        var cleared = WaitUntil(() => string.IsNullOrEmpty(UiDriver.ById(main, "TxtBypassState")?.Current.Name));
        Check("U-BP02", dialogClosed && indicator && longState && buttonText && closedByUser && cleared,
            $"窗口关闭={dialogClosed} 顶部提示={indicator} 天单位={longState} 按钮态={buttonText} 关闭成功={closedByUser} 状态清空={cleared}");
    }

    /// <summary>选择下拉框选项：展开后按文本定位 ComboBoxItem 并 Select。</summary>
    private static bool SelectComboBoxItem(AutomationElement combo, string itemName)
    {
        try
        {
            var expanded = false;
            if (combo.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
            {
                ((ExpandCollapsePattern)expandPattern).Expand();
                expanded = true;
                Thread.Sleep(200);
            }

            var item = combo.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, itemName));
            var selected = false;
            if (item is not null
                && item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectPattern))
            {
                ((SelectionItemPattern)selectPattern).Select();
                selected = true;
            }

            if (expanded)
            {
                ((ExpandCollapsePattern)expandPattern).Collapse();
            }

            return selected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return condition();
    }

    // U-BP03：屏蔽持久化仍在有效期 → 重启免登录进入，标题与顶部横幅标注
    private static void RunUBP03(string exe)
    {
        var dir = NewCaseDirectory("ubp03");
        WriteValidLicense(dir);
        WriteUsers(dir, "admin04", NewPassword(), "Admin");
        WriteBypassState(dir, DateTimeOffset.Now.AddDays(2), "免登录用例");

        using var driver = UiDriver.Launch(exe, dir);
        var main = driver.WaitWindow("多工位测试上位机 · MultiStation", 25000);
        if (main is null)
        {
            Fail("U-BP03", "免登录启动后未进入主界面");
            return;
        }

        var title = main.Current.Name;
        var titled = title.Contains("免登录调试");
        var banner = UiDriver.ById(main, "TxtBypassBanner")?.Current.Name ?? string.Empty;
        var bannerShown = WaitUntil(() =>
            (UiDriver.ById(main, "TxtBypassBanner")?.Current.Name ?? string.Empty).Contains("权限屏蔽中"));

        Check("U-BP03", titled && bannerShown,
            $"标题={title} 横幅文本={banner}");
    }

    // U-CF01：开始测试必须人工确认；取消不进入测试，确认后才开始
    private static void RunUCF01(string exe)
    {
        var dir = NewCaseDirectory("ucf01");
        WriteValidLicense(dir);
        var password = NewPassword();
        WriteUsers(dir, "admin03", password, "Admin");

        using var driver = UiDriver.Launch(exe, dir);
        var loginWindow = driver.WaitWindow("登录");
        if (loginWindow is null)
        {
            Fail("U-CF01", "未出现登录窗口");
            return;
        }

        var main = LoginToMain(driver, loginWindow, "admin03", password);
        if (main is null)
        {
            Fail("U-CF01", "Admin 登录后未进入主界面");
            return;
        }

        var startButton = UiDriver.ById(main, "BtnTestStart");
        if (startButton is null || !UiDriver.Click(startButton))
        {
            Fail("U-CF01", "未找到或无法点击「开始」");
            return;
        }

        var dialog = UiDriver.WaitDialog("人工确认", 15000);
        if (dialog is null)
        {
            Fail("U-CF01", "开始测试前未弹出人工确认");
            return;
        }

        var prompted = UiDriver.WaitText(dialog, "请确认");
        var clickedNo = ClickDialogButton(dialog, "否", "No");

        var cancelled = WaitUntil(() => UiDriver.WaitText(main, "未通过人工确认", 300));
        var idleAfterCancel = UiDriver.ById(main, "TxtTestState")?.Current.Name == "待开始";

        // 第二次：确认后进入测试
        startButton = UiDriver.ById(main, "BtnTestStart");
        if (startButton is null || !UiDriver.Click(startButton))
        {
            Fail("U-CF01", "第二次未找到或无法点击「开始」");
            return;
        }

        var dialog2 = UiDriver.WaitDialog("人工确认", 15000);
        var clickedYes = dialog2 is not null && ClickDialogButton(dialog2, "是", "Yes");

        var confirmed = WaitUntil(() => UiDriver.WaitText(main, "已通过人工确认", 500));
        var running = WaitUntil(() => UiDriver.ById(main, "TxtTestState")?.Current.Name == "测试中", 3000);

        // 测试开始后基础信息区应生成 SN 与 CarrierID
        var sn = UiDriver.GetText(UiDriver.ById(main, "TxtInfoSn"));
        var carrier = UiDriver.GetText(UiDriver.ById(main, "TxtInfoCarrier"));
        var infoReady = sn.StartsWith("SN", StringComparison.Ordinal)
            && carrier.StartsWith("CAR", StringComparison.Ordinal);

        Check("U-CF01", prompted && clickedNo && cancelled && idleAfterCancel && clickedYes && confirmed && running && infoReady,
            $"确认提示={prompted} 点击否={clickedNo} 取消未测试={cancelled} 状态待开始={idleAfterCancel} 点击是={clickedYes} 确认={confirmed} 进入测试={running} 基础信息={sn}/{carrier}");
    }

    // U-CF02：冒烟自检同样需要人工确认；未确认时不启动测试，自检仍完成并退出
    private static void RunUCF02(string exe)
    {
        var dir = NewCaseDirectory("ucf02");
        WriteValidLicense(dir);

        using var driver = UiDriver.Launch(exe, dir, "--smoke");
        var dialog = UiDriver.WaitDialog("人工确认 · 冒烟自检", 30000);
        if (dialog is null)
        {
            Fail("U-CF02", "冒烟模式未弹出人工确认");
            return;
        }

        var prompted = UiDriver.WaitText(dialog, "冒烟自检");
        var clickedNo = ClickDialogButton(dialog, "否", "No");
        var exited = driver.WaitForExit(30000);

        var smokeFile = Path.Combine(Path.GetTempPath(), "multistation-smoke.txt");
        var evidence = File.Exists(smokeFile) ? File.ReadAllText(smokeFile) : string.Empty;
        var recorded = evidence.Contains("testStartConfirmed=False");

        Check("U-CF02", prompted && clickedNo && exited && recorded,
            $"确认提示={prompted} 点击否={clickedNo} 自检退出={exited} 未确认留痕={recorded} 证据={evidence.Trim()}");
    }

    /// <summary>按文本前缀点击对话框按钮（中文系统的 MessageBox 可能显示「是(Y)」「否(N)」）。</summary>
    private static bool ClickDialogButton(AutomationElement dialog, params string[] namePrefixes)
    {
        var buttons = dialog.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

        foreach (AutomationElement button in buttons)
        {
            try
            {
                var name = button.Current.Name.Trim();
                if (namePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    return UiDriver.Click(button);
                }
            }
            catch (ElementNotAvailableException)
            {
                // 对话框已关闭，跳过
            }
        }

        return false;
    }

    // U-AU01：启动后不自动投料、不自动触发；等待 13 秒无确认框、无工件、测试保持待开始
    private static void RunUAU01(string exe)
    {
        var dir = NewCaseDirectory("uau01");
        WriteValidLicense(dir);
        var password = NewPassword();
        WriteUsers(dir, "admin05", password, "Admin");

        using var driver = UiDriver.Launch(exe, dir);
        var loginWindow = driver.WaitWindow("登录");
        if (loginWindow is null)
        {
            Fail("U-AU01", "未出现登录窗口");
            return;
        }

        var main = LoginToMain(driver, loginWindow, "admin05", password);
        if (main is null)
        {
            Fail("U-AU01", "Admin 登录后未进入主界面");
            return;
        }

        Thread.Sleep(1200);
        var autoDialog = UiDriver.WaitDialog("人工确认", 13000) is not null;
        var state = UiDriver.ById(main, "TxtTestState")?.Current.Name ?? "-";

        Check("U-AU01", !autoDialog && state == "待开始",
            $"自动确认框={autoDialog} 测试状态={state}");
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

    /// <summary>登录并返回主窗口。密码框依赖 SendKeys，偶发焦点丢失时重新输入重试。</summary>
    private static AutomationElement? LoginToMain(
        UiDriver driver,
        AutomationElement loginWindow,
        string userName,
        string password,
        int perAttemptMs = 10000)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            Set(loginWindow, "TxtUser", userName);
            Set(loginWindow, "TxtPassword", password);
            var button = UiDriver.ByName(loginWindow, "登录");
            if (button is null)
            {
                return null;
            }

            UiDriver.Click(button);
            var main = driver.WaitWindow("多工位测试上位机 · MultiStation", perAttemptMs);
            if (main is not null)
            {
                return main;
            }

            Thread.Sleep(300);
        }

        return null;
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

    private static void WriteLicenseAt(string dir, DateTimeOffset expires, int toleranceMinutes = 10)
    {
        File.WriteAllText(
            Path.Combine(dir, "license.json"),
            $"{{\"ExpiresAt\":\"{expires:O}\",\"TrialDays\":30,\"ExpiringWarningDays\":15,\"ClockRollbackToleranceMinutes\":{toleranceMinutes}}}");
    }

    private static void WriteClock(string dir, DateTimeOffset seen)
    {
        File.WriteAllText(Path.Combine(dir, "lastseen.json"), $"\"{seen:O}\"");
    }

    /// <summary>写入调试屏蔽持久化文件，模拟上次运行遗留的屏蔽状态。</summary>
    private static void WriteBypassState(string dir, DateTimeOffset until, string reason)
    {
        Directory.CreateDirectory(dir);
        var json = "{\"Until\":\"" + until.ToString("O")
            + "\",\"Reason\":\"" + reason
            + "\",\"EnabledBy\":\"测试\",\"EnabledAt\":\"" + DateTimeOffset.Now.ToString("O") + "\"}";
        File.WriteAllText(Path.Combine(dir, "debug-bypass.json"), json);
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

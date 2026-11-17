using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Automation;

namespace MultiStation.Ui.Smoke;

/// <summary>界面自动化驱动：启动进程、按 AutomationId / 名称定位控件、读写文本。</summary>
internal sealed class UiDriver : IDisposable
{
    private readonly Process _process;

    private UiDriver(Process process) => _process = process;

    public int ProcessId => _process.Id;

    public bool HasExited => _process.HasExited;

    /// <summary>等待进程退出，返回是否在超时内退出。</summary>
    public bool WaitForExit(int timeoutMs) => _process.WaitForExit(timeoutMs);

    public static UiDriver Launch(string exePath, string securityDir)
    {
        var info = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath)!
        };
        info.Environment["MULTISTATION_SECURITY_DIR"] = securityDir;

        var process = Process.Start(info) ?? throw new InvalidOperationException("进程启动失败");
        return new UiDriver(process);
    }

    /// <summary>等待属于本进程、标题包含指定文本的顶层窗口。</summary>
    public AutomationElement? WaitWindow(string titlePart, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var condition = new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _process.Id),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

            foreach (AutomationElement element in AutomationElement.RootElement.FindAll(TreeScope.Children, condition))
            {
                if (element.Current.Name.Contains(titlePart, StringComparison.Ordinal))
                {
                    return element;
                }
            }

            Thread.Sleep(200);
        }

        return null;
    }

    public static AutomationElement? ById(AutomationElement root, string automationId)
        => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));

    public static AutomationElement? ByName(AutomationElement root, string name)
        => root.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.NameProperty, name));

    /// <summary>写入文本：优先 ValuePattern，不支持时聚焦后用键盘输入。</summary>
    public static bool SetText(AutomationElement element, string text)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern))
            {
                ((ValuePattern)valuePattern).SetValue(text);
                return true;
            }
        }
        catch (Exception)
        {
            // 回退到键盘输入
        }

        try
        {
            element.SetFocus();
            Thread.Sleep(150);
            System.Windows.Forms.SendKeys.SendWait(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool Click(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return true;
            }
        }
        catch (Exception)
        {
            // 忽略，最后再试一次
        }

        try
        {
            element.SetFocus();
            Thread.Sleep(120);
            System.Windows.Forms.SendKeys.SendWait("{ENTER}");
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>等待窗口内出现包含指定文本的元素（文本块或标签）。</summary>
    public static bool WaitText(AutomationElement root, string textPart, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                foreach (AutomationElement element in root.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
                {
                    if (element.Current.Name.Contains(textPart, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                return false;
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>等待出现指定标题的对话框（MessageBox 可能被子窗口持有，按子树搜索）。</summary>
    public static AutomationElement? WaitDialog(string titlePart, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var byName = new PropertyCondition(AutomationElement.NameProperty, titlePart);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var found = AutomationElement.RootElement.FindFirst(TreeScope.Subtree, byName);
                if (found is not null)
                {
                    return found;
                }

                foreach (AutomationElement element in AutomationElement.RootElement.FindAll(
                             TreeScope.Subtree,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
                {
                    if (element.Current.Name.Contains(titlePart, StringComparison.Ordinal))
                    {
                        return element;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                // 继续轮询
            }

            Thread.Sleep(200);
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        catch (Exception)
        {
            // 进程已退出
        }

        _process.Dispose();
    }
}

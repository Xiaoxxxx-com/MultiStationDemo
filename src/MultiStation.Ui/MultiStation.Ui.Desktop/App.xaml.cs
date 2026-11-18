using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "MultiStationDemo.Desktop.SingleInstance";
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 启动阶段用显式关闭：登录/注册窗口是首个窗口，WPF 会把它当作 MainWindow，
        // 若沿用默认的 OnLastWindowClose 或 OnMainWindowClose，登录窗关闭会直接退出进程。
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 单实例校验：先占用命名互斥体，已存在实例时激活其主窗口并直接退出，避免重复打开
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            if (!ActivateExistingInstance())
            {
                MessageBox.Show(
                    "多工位测试上位机已在运行，请勿重复打开。",
                    "多工位测试上位机",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        try
        {
            var isSmoke = e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase);

            // 定时锁：授权过期、缺失或时钟回拨时锁定软件，不进入主界面
            var license = LicenseService.Evaluate();
            if (!license.IsUsable)
            {
                var locked = new LicenseWindow(license);
                MainWindow = locked;
                locked.ShowDialog();
                Shutdown();
                return;
            }

            // 权限登入：冒烟模式跳过交互；调试屏蔽持久化仍在有效期时免登录进入
            var bypassRestored = PermissionBypass.Restore();
            if (isSmoke)
            {
                SessionContext.Current = new UserSession("smoke", UserRole.Admin, DateTimeOffset.Now);
            }
            else if (bypassRestored)
            {
                SessionContext.Current = new UserSession(
                    SessionContext.DebugBypassUserName, UserRole.Admin, DateTimeOffset.Now);
            }
            else if (!AuthService.HasAnyUser())
            {
                // 首次运行：由用户自行注册管理员账号，代码中不预置任何账号
                var register = new RegisterAdminWindow();
                if (register.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
            }
            else
            {
                var login = new LoginWindow();
                if (login.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
            }

            var window = new MainWindow();
            MainWindow = window;
            // 主窗口就绪后恢复为「主窗口关闭即退出」
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();

            if (isSmoke)
            {
                // 冒烟自检同样遵守 T-07：启动测试前必须人工确认，未确认则只做界面自检
                var smokeTestConfirmed = window.ConfirmSmokeTestStart();

                // 冒烟模式：加载窗口并运行 5 秒后优雅停机，用于 CI / 自检
                var bindingLog = Path.Combine(Path.GetTempPath(), "multistation-binding.log");
                var listener = new TextWriterTraceListener(bindingLog);
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                var tick = 0;
                timer.Tick += (_, _) =>
                {
                    tick++;
                    var temp = Path.GetTempPath();

                    if (tick == 2)
                    {
                        window.TriggerSmokeFault();
                        if (smokeTestConfirmed)
                        {
                            window.TriggerSmokeTest();
                        }
                    }
                    else if (tick == 3)
                    {
                        CaptureWindow(window, Path.Combine(temp, "multistation-ui-monitor.png"));
                    }
                    else if (tick == 4)
                    {
                        if (window.FindName("NavHistory") is RadioButton history)
                        {
                            history.IsChecked = true;
                        }
                    }
                    else if (tick == 5)
                    {
                        CaptureWindow(window, Path.Combine(temp, "multistation-ui-history.png"));
                        if (window.FindName("NavAlarms") is RadioButton alarms)
                        {
                            alarms.IsChecked = true;
                        }
                    }
                    else if (tick == 6)
                    {
                        CaptureWindow(window, Path.Combine(temp, "multistation-ui-alarms.png"));
                    }
                    else if (tick >= 7)
                    {
                        timer.Stop();
                        if (window.FindName("HistoryGrid") is DataGrid grid)
                        {
                            File.WriteAllText(
                                Path.Combine(temp, "multistation-smoke.txt"),
                                $"history items={grid.Items.Count} testStartConfirmed={smokeTestConfirmed}");
                        }

                        listener.Flush();
                        PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                        listener.Dispose();
                        window.Close();
                    }
                };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            WriteCrashLog(ex);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前线程未持有互斥体时忽略
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    /// <summary>把已在运行实例的主窗口还原并置前，返回是否找到窗口。</summary>
    private static bool ActivateExistingInstance()
    {
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || process.MainWindowHandle == IntPtr.Zero)
                {
                    continue;
                }

                ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(process.MainWindowHandle);
                return true;
            }
        }

        return false;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hWnd);

    private static void CaptureWindow(Window window, string path)
    {
        var width = Math.Max(1, (int)window.ActualWidth);
        var height = Math.Max(1, (int)window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }


    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "multistation-desktop-crash.log");
            File.WriteAllText(path, exception.ToString());
        }
        catch (IOException)
        {
        }
    }
}

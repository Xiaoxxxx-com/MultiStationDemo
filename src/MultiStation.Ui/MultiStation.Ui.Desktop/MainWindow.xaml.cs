using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiStation.Platform.Core.Production;
using MultiStation.Platform.Security;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// 主窗口外壳：只负责标题栏、Logo、机种切换与生命周期；
/// 窗口主体由各机种独立的 UI 视图挂载，切换机种时整体替换。
/// </summary>
public partial class MainWindow : Window, IDisposable
{
    private static readonly string[] LogoExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private static readonly Brush OrangeBrush = CreateBrush(0x9A, 0x67, 0x00);

    private static readonly Brush MutedBrush = CreateBrush(0x6B, 0x72, 0x80);

    private readonly ModelProfileStore _modelStore;
    private readonly string _baseTitle;
    private readonly ModelProfile _currentModel;
    private readonly string _configDirectory;
    private readonly IModelView _modelView;
    private readonly DispatcherTimer _clockTimer;
    private bool _closing;

    public MainWindow(ModelProfile model)
    {
        InitializeComponent();

        // 标题栏补充当前账号、授权剩余天数与当前机种
        _baseTitle = Title;

        _modelStore = new ModelProfileStore();
        _currentModel = model;
        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiStationDemo");

        TxtModelName.Text = model.Name;
        ApplyTitle();
        LoadStationName();
        ApplyFlashStationTitle(model);
        LoadLogo();

        // 每种机种挂载自己的 UI 视图
        _modelView = model.Kind == ModelKind.Flash
            ? new FlashModelView(model)
            : new TestModelView();
        ModelHost.Content = _modelView;
        MainMenu.Visibility = model.Kind == ModelKind.Flash ? Visibility.Collapsed : Visibility.Visible;

        _clockTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            OnClockTick,
            Dispatcher);
        _clockTimer.Start();
        OnClockTick(null, EventArgs.Empty);
    }

    /// <summary>当前挂载的通用测试视图；ECU 烧录机种为 null。</summary>
    internal TestModelView? TestView => ModelHost.Content as TestModelView;

    /// <summary>冒烟自检用：注入一个工位故障，验证报警中心链路。</summary>
    internal void TriggerSmokeFault() => TestView?.TriggerSmokeFault();

    internal void TriggerSmokeTest() => TestView?.TriggerSmokeTest();

    /// <summary>冒烟自检启动测试前的人工确认（T-07 同样适用于冒烟测试）。</summary>
    internal bool ConfirmSmokeTestStart() => TestView?.ConfirmSmokeTestStart() ?? false;

    private void OnClockTick(object? sender, EventArgs e)
    {
        TxtClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        PermissionBypass.Refresh();
        RefreshBypassState();
    }

    /// <summary>标题栏统一拼接账号、授权与当前机种信息。</summary>
    private void ApplyTitle()
    {
        var license = LicenseService.Evaluate();
        var title = $"{_baseTitle} · {SessionContext.DisplayName} · 授权剩余 {license.RemainingDays} 天";
        if (SessionContext.IsDebugBypassSession)
        {
            title += " · 免登录调试";
        }

        if (!string.IsNullOrEmpty(_currentModel.Name))
        {
            title += $" · 机种 {_currentModel.Name}";
        }

        Title = title;
    }

    private async void OnPreviousModel(object sender, RoutedEventArgs e) =>
        await SwitchRelativeModelAsync(-1);

    private async void OnNextModel(object sender, RoutedEventArgs e) =>
        await SwitchRelativeModelAsync(1);

    private async Task SwitchRelativeModelAsync(int delta)
    {
        if (!RequirePermission(PermissionKind.Configure))
        {
            return;
        }

        await RunModelSwitchAsync(() => ((App)Application.Current).SwitchModelRelativeAsync(delta));
    }

    /// <summary>机种切换：由 App 关闭当前窗口套件并打开新机种的窗口套件。</summary>
    private async void OnSwitchModel(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(PermissionKind.Configure))
        {
            return;
        }

        var dialog = new ModelSwitchWindow(_modelStore, _currentModel) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedModel is not { } selectedModel)
        {
            return;
        }

        await RunModelSwitchAsync(() => ((App)Application.Current).SwitchModelAsync(selectedModel));
    }

    private async Task RunModelSwitchAsync(Func<Task> switchAction)
    {
        SetModelSwitchButtons(false);
        try
        {
            await switchAction();
        }
        finally
        {
            if (!_closing)
            {
                SetModelSwitchButtons(true);
            }
        }
    }

    private void SetModelSwitchButtons(bool enabled)
    {
        BtnModelPrevious.IsEnabled = enabled;
        BtnModelNext.IsEnabled = enabled;
        BtnModelSwitch.IsEnabled = enabled;
    }

    private void OnOpenHistoryWindow(object sender, RoutedEventArgs e) => TestView?.OpenHistoryWindow();

    private void OnOpenAlarmsWindow(object sender, RoutedEventArgs e) => TestView?.OpenAlarmsWindow();

    /// <summary>权限校验：不满足时提示并拒绝执行，返回 false。调试屏蔽期内一律放行。</summary>
    private bool RequirePermission(PermissionKind permission)
    {
        if (SessionContext.Has(permission) || PermissionBypass.IsActive)
        {
            return true;
        }

        MessageBox.Show(
            this,
            $"当前账号「{SessionContext.DisplayName}」无此操作权限（需要 {permission}）。",
            "权限不足",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        return false;
    }

    /// <summary>调试屏蔽：仅管理员可用，用于临时跳过权限校验。</summary>
    private void OnOpenDebugBypass(object sender, RoutedEventArgs e)
    {
        if (SessionContext.Current?.Role != UserRole.Admin)
        {
            MessageBox.Show(
                this,
                $"调试屏蔽仅限管理员使用，当前账号「{SessionContext.DisplayName}」无权限。",
                "权限不足",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new DebugBypassWindow { Owner = this };
        dialog.ShowDialog();
        RefreshBypassState();
    }

    /// <summary>刷新顶部调试屏蔽状态：横幅、剩余时间与按钮文案。</summary>
    private void RefreshBypassState()
    {
        var text = PermissionBypass.StateText;
        TxtBypassState.Text = text ?? string.Empty;
        BtnDebugBypass.Content = text is null ? "调试屏蔽" : "屏蔽中";
        BtnDebugBypass.Foreground = text is null ? MutedBrush : OrangeBrush;

        BypassBanner.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (text is not null)
        {
            var loginNote = SessionContext.IsDebugBypassSession ? "免登录、" : string.Empty;
            TxtBypassBanner.Text =
                $"{text}：{loginNote}权限校验已放行；原因：{PermissionBypass.Reason}。调试结束后请及时关闭。";
        }
    }

    private void LoadStationName()
    {
        var name = "MTCU老化测试";
        try
        {
            var path = Path.Combine(_configDirectory, "station-name.txt");
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path).Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    name = text;
                }
            }
        }
        catch
        {
            // 读取失败时使用默认工站名
        }

        TxtStationName.Text = name;
    }

    /// <summary>外部烧录机种（NP12/NP13）的页面大标题显示对应烧录标题。</summary>
    private void ApplyFlashStationTitle(ModelProfile model)
    {
        if (model.Kind == ModelKind.Flash && !string.IsNullOrWhiteSpace(model.FlashSequencePath))
        {
            TxtStationName.Text = $"{model.Name} 烧录";
        }
    }

    private void LoadLogo()
    {
        try
        {
            var file = Directory.EnumerateFiles(_configDirectory, "station-logo.*")
                .FirstOrDefault(path => LogoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()));
            if (file is null)
            {
                return;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();

            ImgLogo.Source = bitmap;
            TxtLogoHint.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 损坏的 Logo 文件不影响界面
        }
    }

    private void OnImportLogo(object sender, MouseButtonEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择工厂 Logo",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_configDirectory);
            foreach (var old in Directory.EnumerateFiles(_configDirectory, "station-logo.*"))
            {
                File.Delete(old);
            }

            var target = Path.Combine(
                _configDirectory,
                "station-logo" + Path.GetExtension(dialog.FileName).ToLowerInvariant());
            File.Copy(dialog.FileName, target, true);
            LoadLogo();
            MessageBox.Show(
                this,
                "已导入工厂 Logo：" + Path.GetFileName(dialog.FileName),
                "工厂 Logo",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "导入 Logo 失败：" + ex.Message,
                "工厂 Logo",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>切换机种/关闭窗口前优雅停机：交给当前机种视图处理。</summary>
    public async Task ShutdownAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _clockTimer.Stop();
        await _modelView.ShutdownAsync();
    }

    /// <summary>
    /// 适配 1080P 及高缩放屏幕：窗口尺寸限制在可用工作区内，
    /// 避免 125%/150% 缩放下窗口超出屏幕造成内容挤压或裁切。
    /// </summary>
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        // 还原窗口时的尺寸也不超过工作区，避免高缩放屏幕下超出屏幕
        Width = Math.Min(Width, workArea.Width);
        Height = Math.Min(Height, workArea.Height);
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        await ShutdownAsync();
        // 停机可能同步完成；待当前 Closing 事件返回后再关闭，避免重复进入关闭流程。
        _ = Dispatcher.BeginInvoke(Close);
    }

    /// <summary>释放资源：停止时钟并释放当前机种视图。</summary>
    public void Dispose()
    {
        _clockTimer.Stop();
        _modelView.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

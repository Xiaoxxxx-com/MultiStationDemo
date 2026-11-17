﻿using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MultiStation.Platform.Core.Logging;
using MultiStation.Platform.Security;
using MultiStation.Platform.Simulation;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// 测试上位机主窗口：左侧导航 + 运行监控 / 结果追溯 / 报警中心。
/// UI 线程只做显示与命令下发，所有数据来自 TestLineHost 的只读快照。
/// </summary>
public partial class MainWindow : Window
{
    private static readonly string[] StationFilterOptions = { "全部", "ST1", "ST2", "ST3", "ST4", "ST5", "ST6" };
    private static readonly string[] LevelFilterOptions = { "全部", "信息", "警告", "错误", "结果" };
    private static readonly string[] VerdictFilterOptions = { "全部", "良品", "不良" };
    private static readonly string[] LogoExtensions = { ".png", ".jpg", ".jpeg", ".bmp" };
    private static readonly string FieldConfigPath = Path.Combine(
        ResolveConfigRoot(), "Config", "test-process-fields.json");
    private static readonly string TriggerLogPath = Path.Combine(
        ResolveConfigRoot(), "Log", "trigger.log");

    private static readonly Brush GreenBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush RedBrush = CreateBrush(0xCF, 0x22, 0x2E);
    private static readonly Brush OrangeBrush = CreateBrush(0x9A, 0x67, 0x00);
    private static readonly Brush MutedBrush = CreateBrush(0x6B, 0x72, 0x80);
    private static readonly Brush GrayBrush = CreateBrush(0x6E, 0x77, 0x81);

    private readonly TestLineHost _host;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<StationCardVm> _stations;
    private readonly ObservableCollection<LogRowVm> _logs = new();
    private readonly ObservableCollection<HistoryRowVm> _history = new();
    private readonly ObservableCollection<AlarmRowVm> _alarms = new();
    private readonly ICollectionView _logView;
    private readonly ICollectionView _historyView;
    private readonly string _configDirectory;
    private readonly SingleStationTestController _testController;
    private readonly TriggerSequenceRunner _triggerRunner;
    private readonly ObservableCollection<TriggerStepVm> _triggerSteps;

    private long _lastLogSequence;
    private long _lastHistorySequence;
    private long _lastAlarmSequence;
    private int _logStationIndex;
    private int _logLevelIndex;
    private int _historyVerdictIndex;
    private bool _closing;
    private HistoryWindow? _historyWindow;
    private AlarmsWindow? _alarmsWindow;

    public MainWindow()
    {
        InitializeComponent();

        // 标题栏补充当前账号与授权剩余天数，便于现场确认
        var license = LicenseService.Evaluate();
        Title = $"{Title} · {SessionContext.DisplayName} · 授权剩余 {license.RemainingDays} 天";

        var logDirectory = LogPaths.ResolveLogDirectory();

        _host = new TestLineHost(logDirectory);
        _stations = new ObservableCollection<StationCardVm>(
            _host.Stations.Select(station => new StationCardVm(station.Id, station.Name)));

        DataContext = this;

        StationList.ItemsSource = _stations;
        StationList.SelectedIndex = 0;

        _logView = CollectionViewSource.GetDefaultView(_logs);
        _logView.Filter = item => item is LogRowVm row && PassesLogFilter(row);
        LogList.ItemsSource = _logView;

        _historyView = CollectionViewSource.GetDefaultView(_history);
        _historyView.Filter = item => item is HistoryRowVm row && PassesHistoryFilter(row);
        HistoryGrid.ItemsSource = _historyView;
        AlarmGrid.ItemsSource = _alarms;

        TxtLogPath.Text = _host.LogPath;
        TxtLogPath.ToolTip = _host.LogPath;

        _host.AutoFeed = false;
        _host.Start();
        TxtStatus.Text = "测试线已启动，未自动投料；点击“开始投料”或“手动投料”开始测试";

        _configDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MultiStationDemo");
        LoadStationName();
        LoadLogo();
        TxtClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        _testController = new SingleStationTestController();
        StepGrid.ItemsSource = _testController.Steps;
        LoadFieldConfig();
        ParamGrid.ItemsSource = _testController.Parameters;
        StationLogList.ItemsSource = _testController.Logs;
        _testController.Changed += UpdateTestControls;
        UpdateTestControls();

        _triggerSteps = new ObservableCollection<TriggerStepVm>(
            TriggerSequenceData.Rows.Select(row => new TriggerStepVm(row)));
        TriggerGrid.ItemsSource = _triggerSteps;
        TxtTriggerLogPath.Text = "触发日志：" + TriggerLogPath;

        _triggerRunner = new TriggerSequenceRunner();
        _triggerRunner.Triggered += OnTriggerReceived;
        _triggerRunner.Start();

        NavMonitor.IsChecked = true;

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(250),
            DispatcherPriority.Background,
            OnTick,
            Dispatcher);
        _timer.Start();
        UpdateUi();
    }

    public ObservableCollection<StationCardVm> StationCards => _stations;

    /// <summary>冒烟自检用：注入一个工位故障，验证报警中心链路。</summary>
    internal void TriggerSmokeFault() => _host.ToggleFault("ST3");

    internal void TriggerSmokeTest() => _testController.Start();

    private void OnTick(object? sender, EventArgs e) => UpdateUi();

    private void OnTestStart(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;
        _testController.Start();
    }

    private void OnTestPause(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;
        _testController.PauseOrResume();
    }

    private void OnTestStop(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;
        _testController.Stop();
    }

    private void OnTestNext(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;
        _testController.NextStep();
    }

    /// <summary>权限校验：不满足时提示并拒绝执行，返回 false。</summary>
    private bool RequirePermission(Permission permission)
    {
        if (SessionContext.Has(permission))
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

    /// <summary>外部触发到来（后台线程）：切回界面线程执行动作。</summary>
    private void OnTriggerReceived(TriggerSignal signal) =>
        Dispatcher.BeginInvoke(() => HandleTrigger(signal));

    private void HandleTrigger(TriggerSignal signal)
    {
        if (_closing)
        {
            return;
        }

        var step = _triggerSteps.FirstOrDefault(item => item.Equal == signal.Value) ?? _triggerSteps.FirstOrDefault();
        step?.MarkTriggered();

        if (signal.Target == "开始测试")
        {
            if (_testController.IsRunning)
            {
                WriteTriggerLog(signal, "触发，测试进行中已忽略");
            }
            else
            {
                _testController.Start();
                WriteTriggerLog(signal, "→ 开始测试");
            }

            return;
        }

        WriteTriggerLog(signal, "→ " + signal.Target);
    }

    /// <summary>触发事件追加写入根目录 Log\trigger.log。</summary>
    private static void WriteTriggerLog(TriggerSignal signal, string result)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(TriggerLogPath)!);
            File.AppendAllText(
                TriggerLogPath,
                $"{signal.Timestamp:yyyy-MM-dd HH:mm:ss.fff}\tR981={signal.Value}\t{result}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 日志写盘失败不影响触发流程
        }
    }

    /// <summary>字段配置：勾选决定测试过程列表显示哪些列。</summary>
    private void OnFieldConfig(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Configure)) return;

        var menu = new ContextMenu
        {
            PlacementTarget = BtnFieldConfig,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        foreach (var column in StepGrid.Columns)
        {
            var item = new MenuItem
            {
                Header = column.Header?.ToString() ?? string.Empty,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Checked += (_, _) =>
            {
                column.Visibility = Visibility.Visible;
                SaveFieldConfig();
            };
            item.Unchecked += (_, _) =>
            {
                column.Visibility = Visibility.Collapsed;
                SaveFieldConfig();
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>定位程序根目录：向上查找解决方案文件，找不到时退回 exe 所在目录。</summary>
    private static string ResolveConfigRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MultiStationDemo.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>读取 Config 目录下的字段显示配置（记录被隐藏的列）。</summary>
    private void LoadFieldConfig()
    {
        try
        {
            if (!File.Exists(FieldConfigPath))
            {
                return;
            }

            var hidden = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FieldConfigPath));
            if (hidden is null)
            {
                return;
            }

            foreach (var column in StepGrid.Columns)
            {
                if (hidden.Contains(column.Header?.ToString() ?? string.Empty))
                {
                    column.Visibility = Visibility.Collapsed;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            TxtStatus.Text = "字段配置读取失败：" + ex.Message;
        }
    }

    /// <summary>把当前隐藏的列写入 Config 目录，下次启动自动恢复。</summary>
    private void SaveFieldConfig()
    {
        try
        {
            var hidden = StepGrid.Columns
                .Where(column => column.Visibility != Visibility.Visible)
                .Select(column => column.Header?.ToString() ?? string.Empty)
                .ToArray();

            Directory.CreateDirectory(Path.GetDirectoryName(FieldConfigPath)!);
            File.WriteAllText(
                FieldConfigPath,
                JsonSerializer.Serialize(hidden, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TxtStatus.Text = "字段配置保存失败：" + ex.Message;
        }
    }

    private void UpdateTestControls()
    {
        var controller = _testController;
        var total = controller.PassCount + controller.NgCount;

        BtnTestStart.IsEnabled = !controller.IsRunning;
        BtnTestPause.IsEnabled = controller.IsRunning && !controller.IsStepMode;
        BtnTestPause.Content = controller.IsPaused ? "继续" : "暂停";
        BtnTestStop.IsEnabled = controller.IsRunning;
        BtnTestNext.IsEnabled = !controller.IsRunning || controller.IsPaused;

        TxtTestState.Text = controller.StateText;
        TxtCurrentVerdict.Text = controller.Verdict;
        TxtCurrentVerdict.Foreground = controller.Verdict switch
        {
            "PASS" => GreenBrush,
            "NG" => RedBrush,
            _ => MutedBrush,
        };

        TxtTestPass.Text = controller.PassCount.ToString();
        TxtTestNg.Text = controller.NgCount.ToString();
        TxtTestTotal.Text = total.ToString();
        TxtTestYield.Text = total == 0 ? "--" : (controller.PassCount * 100.0 / total).ToString("F1") + "%";

        if (StationLogList.Items.Count > 0)
        {
            StationLogList.ScrollIntoView(StationLogList.Items[StationLogList.Items.Count - 1]);
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
            TxtStatus.Text = "已导入工厂 Logo：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "导入 Logo 失败：" + ex.Message;
        }
    }

    private void UpdateUi()
    {
        TxtClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var metrics = _host.Metrics;
        TxtSubmitted.Text = metrics.Submitted.ToString();
        TxtPassed.Text = metrics.Passed.ToString();
        TxtFailed.Text = metrics.Failed.ToString();
        TxtInProcess.Text = metrics.InProcess.ToString();

        var elapsed = DateTimeOffset.Now - _host.StartedAt;
        TxtRuntime.Text = $"运行 {(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        var throughput = metrics.Completed / Math.Max(elapsed.TotalSeconds, 1);
        TxtThroughput.Text = throughput.ToString("F2");
        TxtRate.Text = $"吞吐 {throughput:F2} 件/s";

        var running = _host.IsRunning;
        LineStateDot.Fill = running ? GreenBrush : GrayBrush;
        TxtLineState.Text = running ? "产线运行中" : "产线已停止";

        var plc = _host.Devices.Plc.Snapshot;
        TxtSidePlc.Text = plc.Connected
            ? $"PLC 在线 · 心跳 {plc.HeartbeatCount}"
            : "PLC 断线 · 重连退避中";
        TxtSidePlc.Foreground = plc.Connected ? GreenBrush : RedBrush;

        var cameras = _host.Devices.Cameras;
        var online = cameras.Count(camera => camera.Connected);
        TxtSideCameras.Text = $"相机 {online}/{cameras.Count} 在线";
        TxtSideCameras.Foreground = online == cameras.Count ? MutedBrush : OrangeBrush;

        for (var i = 0; i < _stations.Count; i++)
        {
            var station = _host.Stations[i];
            _stations[i].Update(station.Snapshot(), _host.Scenario.IsFaulted(station.Id));
        }

        BtnFeedPause.Content = _host.AutoFeed ? "暂停投料" : "开始投料";

        if (StationList.SelectedItem is StationCardVm selected)
        {
            var station = _host.Stations.First(item => item.Id == selected.Id);
            BtnStationToggle.Content = station.IsRunning ? "停止本工位" : "启动本工位";
        }

        AppendLogs();
        AppendHistory();
        AppendAlarms();

        var trigger = _triggerRunner.Snapshot();
        TxtTriggerState.Text = trigger.StateText;
        TxtTriggerState.Foreground = trigger.StateText == "执行中" ? GreenBrush : MutedBrush;
        TxtTriggerCount.Text = trigger.TriggerCount.ToString();
        TxtTriggerLast.Text = trigger.LastTime;
    }

    private void AppendLogs()
    {
        var added = false;
        foreach (var entry in _host.RecentLogs(400))
        {
            if (entry.Sequence <= _lastLogSequence)
            {
                continue;
            }

            _lastLogSequence = entry.Sequence;
            if (entry.Level == LogLevel.Debug)
            {
                continue;
            }

            _logs.Add(LogRowVm.From(entry));
            added = true;
        }

        while (_logs.Count > 500)
        {
            _logs.RemoveAt(0);
        }

        if (!added)
        {
            return;
        }

        var lastVisible = _logView.Cast<LogRowVm>().LastOrDefault();
        if (lastVisible is not null)
        {
            LogList.ScrollIntoView(lastVisible);
        }
    }

    private void AppendHistory()
    {
        foreach (var item in _host.RecentHistory(400))
        {
            if (item.Sequence <= _lastHistorySequence)
            {
                continue;
            }

            _lastHistorySequence = item.Sequence;
            _history.Insert(0, HistoryRowVm.From(item));
        }

        while (_history.Count > 800)
        {
            _history.RemoveAt(_history.Count - 1);
        }
    }

    private void AppendAlarms()
    {
        var added = false;
        foreach (var item in _host.RecentAlarms(200))
        {
            if (item.Sequence <= _lastAlarmSequence)
            {
                continue;
            }

            _lastAlarmSequence = item.Sequence;
            _alarms.Insert(0, AlarmRowVm.From(item));
            added = true;
        }

        while (_alarms.Count > 300)
        {
            _alarms.RemoveAt(_alarms.Count - 1);
        }

        if (added)
        {
            NavAlarms.Content = $"报警中心   {_alarms.Count}";
        }
    }

    private bool PassesLogFilter(LogRowVm row)
    {
        var station = StationFilterOptions[_logStationIndex];
        if (station != "全部" && !string.Equals(row.StationId, station, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return LevelFilterOptions[_logLevelIndex] switch
        {
            "信息" => row.SourceLevel == LogLevel.Info,
            "警告" => row.SourceLevel == LogLevel.Warn,
            "错误" => row.SourceLevel == LogLevel.Error,
            "结果" => row.SourceLevel == LogLevel.Result,
            _ => true,
        };
    }

    private bool PassesHistoryFilter(HistoryRowVm row)
    {
        var verdict = VerdictFilterOptions[_historyVerdictIndex];
        if (verdict != "全部" && row.Verdict != verdict)
        {
            return false;
        }

        var search = TxtHistorySearch.Text.Trim();
        return search.Length == 0 || row.WorkItemId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowPage(FrameworkElement page)
    {
        PageMonitor.Visibility = ReferenceEquals(page, PageMonitor) ? Visibility.Visible : Visibility.Collapsed;
        PageHistory.Visibility = ReferenceEquals(page, PageHistory) ? Visibility.Visible : Visibility.Collapsed;
        PageAlarms.Visibility = ReferenceEquals(page, PageAlarms) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavMonitor(object sender, RoutedEventArgs e) => ShowPage(PageMonitor);

    private void OnNavHistory(object sender, RoutedEventArgs e)
    {
        ShowPage(PageHistory);
        _historyView.Refresh();
    }

    private void OnNavAlarms(object sender, RoutedEventArgs e) => ShowPage(PageAlarms);

    private void OnOpenHistoryWindow(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is { IsVisible: true })
        {
            _historyWindow.Activate();
            return;
        }

        _historyWindow = new HistoryWindow(_history) { Owner = this };
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void OnOpenAlarmsWindow(object sender, RoutedEventArgs e)
    {
        if (_alarmsWindow is { IsVisible: true })
        {
            _alarmsWindow.Activate();
            return;
        }

        _alarmsWindow = new AlarmsWindow(_alarms, () =>
        {
            _host.ClearAlarms();
            _alarms.Clear();
            NavAlarms.Content = "报警中心";
        })
        {
            Owner = this,
        };
        _alarmsWindow.Closed += (_, _) => _alarmsWindow = null;
        _alarmsWindow.Show();
    }

    private void OnStationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationList.SelectedItem is StationCardVm selected)
        {
            var station = _host.Stations.First(item => item.Id == selected.Id);
            BtnStationToggle.Content = station.IsRunning ? "停止本工位" : "启动本工位";
        }
    }

    private async void OnToggleStation(object sender, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is not StationCardVm selected)
        {
            return;
        }

        var station = _host.Stations.First(item => item.Id == selected.Id);
        BtnStationToggle.IsEnabled = false;
        try
        {
            if (station.IsRunning)
            {
                _host.Log.ForContext("StationId", station.Id).Warn("手动停止本工位：仅取消该工位令牌，其他工位继续运行");
                TxtStatus.Text = $"{station.Id} 正在停止（仅该工位）…";
                await station.StopAsync();
                TxtStatus.Text = $"{station.Id} 已停止，其他工位与调度器不受影响";
            }
            else
            {
                station.Start();
                TxtStatus.Text = $"{station.Id} 已重新启动，继续消费输入队列";
            }
        }
        finally
        {
            BtnStationToggle.IsEnabled = true;
            UpdateUi();
        }
    }

    private void OnToggleFault(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string stationId })
        {
            return;
        }

        var enabled = _host.ToggleFault(stationId);
        TxtStatus.Text = $"{stationId} 故障注入已{(enabled ? "开启" : "关闭")}，其他工位继续运行";
    }

    private void OnDetailFault(object sender, RoutedEventArgs e)
    {
        if (StationList.SelectedItem is not StationCardVm selected)
        {
            return;
        }

        var enabled = _host.ToggleFault(selected.Id);
        TxtStatus.Text = $"{selected.Id} 故障注入已{(enabled ? "开启" : "关闭")}";
    }

    private void OnToggleFeed(object sender, RoutedEventArgs e)
    {
        _host.AutoFeed = !_host.AutoFeed;
        BtnFeedPause.Content = _host.AutoFeed ? "暂停投料" : "开始投料";
        TxtStatus.Text = _host.AutoFeed
            ? "已开始自动投料，测试线连续运行"
            : "已暂停自动投料，在制工件继续运行";
    }

    private void OnFeed(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;

        var recipe = RecipeB.IsChecked == true ? "B" : "A";
        _host.Submit(recipe);
        TxtStatus.Text = $"已手动投入一个工件（配方 {recipe}）";
    }

    private void OnResetFaults(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;

        var reset = _host.ResetFaults("界面复位");
        TxtStatus.Text = $"已清除故障注入并复位 {reset} 个异常工位";
    }

    private void OnCameraFault(object sender, RoutedEventArgs e)
    {
        var stationId = _host.DisconnectRandomCamera(5);
        TxtStatus.Text = $"已模拟 {stationId} 相机断线 5 秒，仅影响该工位";
    }

    private void OnPlcFault(object sender, RoutedEventArgs e)
    {
        _host.DisconnectPlc(4);
        TxtStatus.Text = "已模拟 PLC 断线 4 秒，心跳与数据镜像进入重连退避";
    }

    private void OnFaultScript(object sender, RoutedEventArgs e)
    {
        _host.StartFaultScript();
        TxtStatus.Text = "故障演练：ST3 工艺故障 → 复位 → ST4 相机断线 → PLC 断线 → 收尾复位";
    }

    private void OnCycleLogStation(object sender, RoutedEventArgs e)
    {
        _logStationIndex = (_logStationIndex + 1) % StationFilterOptions.Length;
        BtnLogStation.Content = $"工位: {StationFilterOptions[_logStationIndex]}";
        _logView.Refresh();
    }

    private void OnCycleLogLevel(object sender, RoutedEventArgs e)
    {
        _logLevelIndex = (_logLevelIndex + 1) % LevelFilterOptions.Length;
        BtnLogLevel.Content = $"级别: {LevelFilterOptions[_logLevelIndex]}";
        _logView.Refresh();
    }

    private void OnCycleHistoryVerdict(object sender, RoutedEventArgs e)
    {
        _historyVerdictIndex = (_historyVerdictIndex + 1) % VerdictFilterOptions.Length;
        BtnHistoryVerdict.Content = $"判定: {VerdictFilterOptions[_historyVerdictIndex]}";
        _historyView.Refresh();
    }

    private void OnHistorySearchChanged(object sender, TextChangedEventArgs e)
    {
        _historyView?.Refresh();
    }

    private void OnExportHistory(object sender, RoutedEventArgs e)
    {
        if (!RequirePermission(Permission.Operate)) return;

        var dialog = new SaveFileDialog
        {
            Filter = "CSV 文件|*.csv",
            FileName = $"test-results-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var builder = new StringBuilder();
        builder.AppendLine("完成时间,工件号,配方,判定,节拍(ms),工位结果");
        var count = 0;
        foreach (var row in _historyView.Cast<HistoryRowVm>())
        {
            builder.AppendLine(string.Join(
                ",",
                Csv(row.Time),
                Csv(row.WorkItemId),
                Csv(row.Recipe),
                Csv(row.Verdict),
                Csv(row.Cycle),
                Csv(row.Summary)));
            count++;
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
        TxtStatus.Text = $"已导出 {count} 条结果到 {dialog.FileName}";
    }

    private void OnClearAlarms(object sender, RoutedEventArgs e)
    {
        _host.ClearAlarms();
        _alarms.Clear();
        NavAlarms.Content = "报警中心";
        TxtStatus.Text = "报警已清空";
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Path.GetDirectoryName(_host.LogPath)}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "无法打开日志目录：" + ex.Message;
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        e.Cancel = true;
        await StopLineAsync();
    }

    private async Task StopLineAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        _timer.Stop();
        await _triggerRunner.StopAsync();
        StationList.IsEnabled = false;
        TxtLineState.Text = "正在停机…";
        LineStateDot.Fill = OrangeBrush;
        TxtStatus.Text = "正在优雅停机：停止投料 → 停调度器 → 逐个停止工位 → 停设备 → 停落盘…";

        try
        {
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "停机异常：" + ex.Message;
        }

        Close();
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

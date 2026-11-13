using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MultiStation.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.Handled = true;
            Shutdown(1);
        };

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            if (e.Args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
            {
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
                        window.TriggerSmokeTest();
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
                                $"history items={grid.Items.Count}");
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

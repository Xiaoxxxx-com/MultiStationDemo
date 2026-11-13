using System.Collections.ObjectModel;
using System.Windows;

namespace MultiStation.Desktop;

/// <summary>报警中心独立窗口，与主窗口共享同一个报警集合。</summary>
public partial class AlarmsWindow : Window
{
    private readonly ObservableCollection<AlarmRowVm> _rows;
    private readonly Action _clear;

    public AlarmsWindow(ObservableCollection<AlarmRowVm> rows, Action clear)
    {
        InitializeComponent();
        _rows = rows;
        _clear = clear;
        AlarmGrid.ItemsSource = _rows;

        UpdateCount();
        _rows.CollectionChanged += (_, _) => UpdateCount();
    }

    private void UpdateCount() => TxtCount.Text = $"{_rows.Count} 条";

    private void OnClear(object sender, RoutedEventArgs e) => _clear();
}

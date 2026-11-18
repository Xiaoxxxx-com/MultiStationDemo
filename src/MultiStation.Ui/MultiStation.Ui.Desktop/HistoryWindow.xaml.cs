using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Data;
using Microsoft.Win32;

namespace MultiStation.Ui.Desktop;

/// <summary>结果追溯独立窗口，与主窗口共享同一个历史集合。</summary>
public partial class HistoryWindow : Window
{
    private static readonly string[] VerdictOptions = ["全部", "良品", "不良"];

    private readonly ObservableCollection<HistoryRowVm> _rows;
    private readonly ICollectionView _view;
    private int _verdictIndex;

    public HistoryWindow(ObservableCollection<HistoryRowVm> rows)
    {
        InitializeComponent();
        _rows = rows;

        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = item => item is HistoryRowVm row && PassesFilter(row);
        HistoryGrid.ItemsSource = _view;

        UpdateCount();
        _rows.CollectionChanged += (_, _) => UpdateCount();
    }

    private void UpdateCount() => TxtCount.Text = $"{_rows.Count} 条";

    private bool PassesFilter(HistoryRowVm row)
    {
        var verdict = VerdictOptions[_verdictIndex];
        if (verdict != "全部" && row.Verdict != verdict)
        {
            return false;
        }

        var search = TxtSearch.Text.Trim();
        return search.Length == 0 || row.WorkItemId.Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    private void OnCycleVerdict(object sender, RoutedEventArgs e)
    {
        _verdictIndex = (_verdictIndex + 1) % VerdictOptions.Length;
        BtnVerdict.Content = $"判定: {VerdictOptions[_verdictIndex]}";
        _view.Refresh();
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => _view?.Refresh();

    private void OnExport(object sender, RoutedEventArgs e)
    {
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
        foreach (var row in _view.Cast<HistoryRowVm>())
        {
            builder.AppendLine(string.Join(
                ",",
                Csv(row.Time),
                Csv(row.WorkItemId),
                Csv(row.Recipe),
                Csv(row.Verdict),
                Csv(row.Cycle),
                Csv(row.Summary)));
        }

        File.WriteAllText(dialog.FileName, builder.ToString(), new UTF8Encoding(true));
        MessageBox.Show(this, "导出完成：" + dialog.FileName, "结果追溯", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}

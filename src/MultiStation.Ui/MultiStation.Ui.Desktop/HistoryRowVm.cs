using System.Globalization;
using System.Windows.Media;
using MultiStation.Platform.Simulation;

namespace MultiStation.Ui.Desktop;

/// <summary>结果追溯表格行。</summary>
public sealed class HistoryRowVm
{
    private static readonly Brush PassBrush = CreateBrush(0x1A, 0x7F, 0x37);
    private static readonly Brush FailBrush = CreateBrush(0xCF, 0x22, 0x2E);

    private HistoryRowVm(
        string time,
        string workItemId,
        string recipe,
        string verdict,
        Brush verdictBrush,
        string cycle,
        string summary,
        string detail)
    {
        Time = time;
        WorkItemId = workItemId;
        Recipe = recipe;
        Verdict = verdict;
        VerdictBrush = verdictBrush;
        Cycle = cycle;
        Summary = summary;
        Detail = detail;
    }

    public string Time { get; }

    public string WorkItemId { get; }

    public string Recipe { get; }

    public string Verdict { get; }

    public Brush VerdictBrush { get; }

    public string Cycle { get; }

    public string Summary { get; }

    public string Detail { get; }

    public static HistoryRowVm From(WorkHistoryItem item)
    {
        var pass = item.Verdict == MultiStation.Platform.Core.TestVerdict.Pass;
        var summary = item.Results.Count == 0
            ? "—"
            : string.Join("  ", item.Results.Select(result => $"{result.StationId}{(result.Passed ? "✔" : "✘")}"));

        var detail = string.Join(
            Environment.NewLine,
            item.Results.Select(result =>
                $"{result.StationId} {(result.Passed ? "PASS" : "NG")} {result.DurationMs:F0}ms {result.Message}"));

        return new HistoryRowVm(
            item.FinishedAt.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            item.WorkItemId,
            item.RecipeId,
            pass ? "良品" : "不良",
            pass ? PassBrush : FailBrush,
            item.CycleMs.ToString("F0", CultureInfo.InvariantCulture),
            summary,
            string.IsNullOrEmpty(detail) ? summary : detail);
    }

    private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

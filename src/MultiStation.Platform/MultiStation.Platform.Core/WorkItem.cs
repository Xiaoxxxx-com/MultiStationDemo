using System.Collections.Concurrent;

namespace MultiStation.Platform.Core;

public enum TestVerdict
{
    Pass,
    Fail,
}

/// <summary>
/// 在工位之间流转的工件。工位只写入本工位的结果，判定由调度器统一完成。
/// </summary>
public sealed class WorkItem(string id, string recipeId)
{
    public string Id { get; } = id;

    public string RecipeId { get; } = recipeId;

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    public DateTimeOffset? FinishedAt { get; private set; }

    public ConcurrentDictionary<string, StationResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);

    public TestVerdict? Verdict { get; private set; }

    public double CycleMs => FinishedAt is { } finished ? (finished - CreatedAt).TotalMilliseconds : 0;

    public bool AnyFailure => Results.Values.Any(r => !r.Passed);

    public void Complete(TestVerdict verdict)
    {
        Verdict = verdict;
        FinishedAt = DateTimeOffset.Now;
    }
}

/// <summary>单个工位产出的测试结果。</summary>
public sealed record StationResult(
    string StationId,
    string WorkItemId,
    bool Passed,
    double DurationMs,
    string Message,
    IReadOnlyDictionary<string, double>? Measurements = null);

/// <summary>工艺序列的返回值。StationId / 耗时 / WorkItemId 由工位执行器补齐。</summary>
public sealed record StationOutcome(
    bool Passed,
    string Message,
    IReadOnlyDictionary<string, double>? Measurements = null);

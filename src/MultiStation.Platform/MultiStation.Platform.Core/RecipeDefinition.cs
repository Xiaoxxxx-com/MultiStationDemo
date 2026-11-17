namespace MultiStation.Platform.Core;

/// <summary>
/// 配方 = 有序工序阶段；一个阶段内的多个工位表示并行分支，全部完成后才进入下一阶段。
/// 路由知识全部集中在这里和调度器中，工位内部代码不感知上下游。
/// </summary>
public sealed class RecipeDefinition
{
    public RecipeDefinition(string id, IReadOnlyList<IReadOnlyList<string>> stages)
    {
        if (stages.Count == 0 || stages.Any(s => s.Count == 0))
        {
            throw new ArgumentException("配方至少需要一个非空阶段", nameof(stages));
        }

        Id = id;
        Stages = stages;
    }

    public string Id { get; }

    public IReadOnlyList<IReadOnlyList<string>> Stages { get; }

    public static RecipeDefinition Serial(string id, params string[] stationIds) =>
        new(id, stationIds.Select(stationId => (IReadOnlyList<string>)new[] { stationId }).ToArray());

    public string Describe() => string.Join(
        " → ",
        Stages.Select(stage => stage.Count == 1 ? stage[0] : $"({string.Join(" ‖ ", stage)})"));
}

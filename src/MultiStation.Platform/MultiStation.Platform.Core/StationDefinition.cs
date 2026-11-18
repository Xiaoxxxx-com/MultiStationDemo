namespace MultiStation.Platform.Core;

/// <summary>工位的静态定义：编号、名称、输入队列容量、工艺序列。</summary>
public sealed class StationDefinition(
    string id,
    string name,
    Func<StationContext, WorkItem, CancellationToken, Task<StationOutcome>> process,
    int queueCapacity = 4)
{
    public string Id { get; } = id;

    public string Name { get; } = name;

    public int QueueCapacity { get; } = queueCapacity;

    /// <summary>本工位的实际测试逻辑，只关心“怎么测”，不关心工件从哪来、到哪去。</summary>
    public Func<StationContext, WorkItem, CancellationToken, Task<StationOutcome>> Process { get; } = process;
}

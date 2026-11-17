namespace MultiStation.Platform.Core;

/// <summary>工位的静态定义：编号、名称、输入队列容量、工艺序列。</summary>
public sealed class StationDefinition
{
    public StationDefinition(
        string id,
        string name,
        Func<StationContext, WorkItem, CancellationToken, Task<StationOutcome>> process,
        int queueCapacity = 4)
    {
        Id = id;
        Name = name;
        Process = process;
        QueueCapacity = queueCapacity;
    }

    public string Id { get; }

    public string Name { get; }

    public int QueueCapacity { get; }

    /// <summary>本工位的实际测试逻辑，只关心“怎么测”，不关心工件从哪来、到哪去。</summary>
    public Func<StationContext, WorkItem, CancellationToken, Task<StationOutcome>> Process { get; }
}

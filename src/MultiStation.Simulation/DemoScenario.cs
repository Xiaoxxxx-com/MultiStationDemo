using System.Collections.Concurrent;

namespace MultiStation.Simulation;

/// <summary>演示用故障注入开关：工艺故障、随机不良。</summary>
public sealed class DemoScenario
{
    private readonly ConcurrentDictionary<string, bool> _faults = new(StringComparer.OrdinalIgnoreCase);

    public double DefectRate { get; init; } = 0.03;

    public bool IsFaulted(string stationId) => _faults.TryGetValue(stationId, out var enabled) && enabled;

    public bool ToggleFault(string stationId)
    {
        var next = !IsFaulted(stationId);
        _faults[stationId] = next;
        return next;
    }

    public void SetFault(string stationId, bool enabled) => _faults[stationId] = enabled;

    public void ClearFaults() => _faults.Clear();

    public bool NextDefect() => Random.Shared.NextDouble() < DefectRate;
}

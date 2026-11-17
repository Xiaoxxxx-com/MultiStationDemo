using MultiStation.Platform.Core;
using MultiStation.Platform.Core.Devices;

namespace MultiStation.Platform.Simulation;

/// <summary>
/// 模拟工艺序列：真实项目里这里换成相机 SDK / PLC 读写 / 仪器通信调用。
/// 所有等待都接受 CancellationToken，保证工位可以被单独停止。
/// </summary>
public sealed class StationSequences
{
    private readonly DeviceHub _hub;
    private readonly DemoScenario _scenario;

    public StationSequences(DeviceHub hub, DemoScenario scenario)
    {
        _hub = hub;
        _scenario = scenario;
    }

    public async Task<StationOutcome> LoadAsync(StationContext ctx, WorkItem item, CancellationToken ct)
    {
        EnsureHealthy(ctx);
        if (!_hub.Plc.Snapshot.Connected)
        {
            throw new DeviceDisconnectedException("PLC 未连接，无法上料");
        }

        await _hub.Plc.SetTowerLightAsync(TowerLight.Green, ct);
        await Task.Delay(160, ct);
        return new StationOutcome(true, "上料完成", Measure(("FeedPressure", 0.42)));
    }

    public async Task<StationOutcome> LocateAsync(StationContext ctx, WorkItem item, CancellationToken ct)
    {
        EnsureHealthy(ctx);
        var capture = await _hub.Camera(ctx.StationId).CaptureAsync(item.Id, 4000, ct);
        await Task.Delay(90, ct);
        return new StationOutcome(true, $"定位完成 {capture.ImageName}", new Dictionary<string, double>
        {
            ["OffsetX"] = Math.Round(Random.Shared.NextDouble() * 0.02, 4),
            ["OffsetY"] = Math.Round(Random.Shared.NextDouble() * 0.02, 4),
        });
    }

    public async Task<StationOutcome> AppearanceAsync(StationContext ctx, WorkItem item, CancellationToken ct)
    {
        EnsureHealthy(ctx);
        var capture = await _hub.Camera(ctx.StationId).CaptureAsync(item.Id, 5000, ct);
        await Task.Delay(140, ct);

        if (_scenario.NextDefect())
        {
            return new StationOutcome(false, "外观缺陷：划痕", Measure(("Score", 0.41)));
        }

        return new StationOutcome(true, $"外观合格 {capture.ImageName}", Measure(("Score", 0.97)));
    }

    public async Task<StationOutcome> DimensionAsync(StationContext ctx, WorkItem item, CancellationToken ct)
    {
        EnsureHealthy(ctx);
        await _hub.Camera(ctx.StationId).CaptureAsync(item.Id, 3000, ct);
        await Task.Delay(170, ct);

        if (_scenario.NextDefect())
        {
            return new StationOutcome(false, "尺寸超差 0.08mm", Measure(("Diameter", 10.08)));
        }

        return new StationOutcome(true, "尺寸合格", Measure(("Diameter", 10.0 + Math.Round(Random.Shared.NextDouble() * 0.02, 3))));
    }

    public async Task<StationOutcome> FunctionAsync(StationContext ctx, WorkItem item, CancellationToken ct)
    {
        EnsureHealthy(ctx);
        var image = _hub.Plc.Snapshot;
        if (!image.Connected)
        {
            throw new DeviceDisconnectedException("PLC 未连接，功能测试无法读取 IO");
        }

        await Task.Delay(260, ct);

        if (_scenario.NextDefect())
        {
            return new StationOutcome(false, "功能测试失败：电流超限", Measure(("Current", 1.92)));
        }

        return new StationOutcome(true, "功能测试通过", Measure(("Torque", 3.2 + Math.Round(Random.Shared.NextDouble() * 0.2, 2))));
    }

    public async Task<StationOutcome> UnloadAsync(StationContext ctx, WorkItem item, CancellationToken ct)
    {
        EnsureHealthy(ctx);
        await Task.Delay(110, ct);
        return new StationOutcome(true, "下料完成");
    }

    private void EnsureHealthy(StationContext ctx)
    {
        if (_scenario.IsFaulted(ctx.StationId))
        {
            throw new InvalidOperationException("故障注入：模拟工艺/设备异常");
        }
    }

    private static IReadOnlyDictionary<string, double> Measure((string Key, double Value) measurement) =>
        new Dictionary<string, double> { [measurement.Key] = measurement.Value };
}

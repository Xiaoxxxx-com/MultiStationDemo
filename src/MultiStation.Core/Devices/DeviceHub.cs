using MultiStation.Core.Logging;

namespace MultiStation.Core.Devices;

/// <summary>设备通信域集合：一台共用 PLC + 每个工位一台独立相机。</summary>
public sealed class DeviceHub
{
    private readonly Dictionary<string, CameraDomain> _cameras;

    public DeviceHub(IEnumerable<string> stationIds, IAppLog log)
    {
        Plc = new PlcDomain(log);
        _cameras = stationIds.ToDictionary(id => id, id => new CameraDomain(id, log), StringComparer.OrdinalIgnoreCase);
    }

    public PlcDomain Plc { get; }

    public IReadOnlyCollection<CameraDomain> Cameras => _cameras.Values;

    public CameraDomain Camera(string stationId) => _cameras[stationId];

    public void Start()
    {
        Plc.Start();
        foreach (var camera in _cameras.Values)
        {
            camera.Start();
        }
    }

    public async Task StopAsync()
    {
        foreach (var camera in _cameras.Values)
        {
            await camera.StopAsync().ConfigureAwait(false);
        }

        await Plc.StopAsync().ConfigureAwait(false);
    }
}

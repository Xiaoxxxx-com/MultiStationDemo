using MultiStation.Instruments.Drivers.GwInstek;
using MultiStation.Instruments.Drivers.Zlg;
using MultiStation.Instruments.Protocol.Scpi.Physical;
using MultiStation.Instruments.Simulation;

namespace MultiStation.Instruments;

/// <summary>
/// 仪器集合：按描述创建并统一管理连接生命周期。
/// 仪器是跨工位共享的测试资源，与 DeviceHub 中的 PLC / 相机并列。
/// </summary>
public sealed class InstrumentHub(IEnumerable<InstrumentDescriptor> descriptors) : IAsyncDisposable
{
    private readonly Dictionary<string, IInstrument> _instruments = descriptors.ToDictionary(
            descriptor => descriptor.Id, Create, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IInstrument> Instruments => _instruments.Values;

    /// <summary>按描述创建仪器：Simulated 为 true 时使用模拟实现，否则使用真实驱动。</summary>
    public static IInstrument Create(InstrumentDescriptor descriptor) => descriptor.Kind switch
    {
        InstrumentKind.PowerSupply => descriptor.Simulated
            ? new SimulatedPowerSupply(descriptor)
            : new Psw80135PowerSupply(descriptor, TcpScpiTransport.FromAddress(descriptor.Address)),
        InstrumentKind.CanBus => descriptor.Simulated
            ? new SimulatedCanInstrument(descriptor)
            : new ZlgCanFd400UInstrument(descriptor),
        _ => throw new ArgumentOutOfRangeException(
            nameof(descriptor), descriptor.Kind, "不支持的仪器类型"),
    };

    public IInstrument Get(string id) =>
        _instruments.TryGetValue(id, out var instrument)
            ? instrument
            : throw new KeyNotFoundException($"未注册仪器：{id}");

    public T Get<T>(string id) where T : class, IInstrument =>
        Get(id) as T ?? throw new InvalidCastException($"仪器「{id}」不是 {typeof(T).Name} 类型");

    public async Task ConnectAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var instrument in _instruments.Values)
        {
            await instrument.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var instrument in _instruments.Values)
        {
            await instrument.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAllAsync().ConfigureAwait(false);
}

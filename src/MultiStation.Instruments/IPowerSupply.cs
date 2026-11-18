namespace MultiStation.Instruments;

/// <summary>可编程电源：设压设流、OVP 保护、输出开关与电压电流回读。</summary>
public interface IPowerSupply : IInstrument
{
    double VoltageSetpoint { get; }

    double CurrentLimit { get; }

    double OvpLimit { get; }

    bool OutputOn { get; }

    Task SetVoltageAsync(double volts, CancellationToken cancellationToken = default);

    Task SetCurrentLimitAsync(double amps, CancellationToken cancellationToken = default);

    Task SetOvpAsync(double volts, CancellationToken cancellationToken = default);

    Task SetOutputAsync(bool enabled, CancellationToken cancellationToken = default);

    Task<PowerSupplyReading> ReadAsync(CancellationToken cancellationToken = default);
}

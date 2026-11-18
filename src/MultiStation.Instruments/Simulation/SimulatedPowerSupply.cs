using MultiStation.Instruments.Protocol.Scpi.Algorithm;

namespace MultiStation.Instruments.Simulation;

/// <summary>
/// 模拟可编程电源：无真实设备时提供与接口一致的行为，
/// 按负载电阻换算回读电流、超压时自动断开输出，并记录下发的 SCPI 命令便于核对。
/// </summary>
public sealed class SimulatedPowerSupply(InstrumentDescriptor descriptor) : IPowerSupply
{
    public const double MaxVoltage = 60;

    public const double MaxCurrent = 10;

    public const double MaxOvp = 65;

    private readonly List<string> _scpiLog = [];

    public InstrumentDescriptor Descriptor { get; } = descriptor;

    public InstrumentState State { get; private set; } = InstrumentState.Disconnected;

    public event EventHandler<InstrumentStateChangedEventArgs>? StateChanged;

    public double VoltageSetpoint { get; private set; }

    public double CurrentLimit { get; private set; } = 1;

    public double OvpLimit { get; private set; } = 20;

    public bool OutputOn { get; private set; }

    /// <summary>模拟负载电阻（欧姆），用于换算回读电流。</summary>
    public double LoadOhms { get; set; } = 10;

    /// <summary>已下发的 SCPI 命令序列，便于现场核对命令格式。</summary>
    public IReadOnlyList<string> ScpiLog => _scpiLog;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetState(InstrumentState.Connected);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        OutputOn = false;
        _scpiLog.Add(PowerSupplyScpi.Output(false));
        SetState(InstrumentState.Disconnected);
        return Task.CompletedTask;
    }

    public Task SetVoltageAsync(double volts, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (volts < 0 || volts > MaxVoltage)
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "电压设定范围 0~60V");
        }

        _scpiLog.Add(PowerSupplyScpi.SetVoltage(volts));
        VoltageSetpoint = volts;
        return Task.CompletedTask;
    }

    public Task SetCurrentLimitAsync(double amps, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (amps <= 0 || amps > MaxCurrent)
        {
            throw new ArgumentOutOfRangeException(nameof(amps), amps, "限流设定范围 0~10A");
        }

        _scpiLog.Add(PowerSupplyScpi.SetCurrentLimit(amps));
        CurrentLimit = amps;
        return Task.CompletedTask;
    }

    public Task SetOvpAsync(double volts, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (volts <= 0 || volts > MaxOvp)
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "OVP 设定范围 0~65V");
        }

        _scpiLog.Add(PowerSupplyScpi.SetOvp(volts));
        OvpLimit = volts;
        return Task.CompletedTask;
    }

    public Task SetOutputAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        _scpiLog.Add(PowerSupplyScpi.Output(enabled));
        OutputOn = enabled;
        return Task.CompletedTask;
    }

    public Task<PowerSupplyReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        _scpiLog.Add(PowerSupplyScpi.MeasureVoltage);

        // OVP 保护：设定电压超过保护值时断开输出，回读为 0
        if (OutputOn && VoltageSetpoint > OvpLimit)
        {
            OutputOn = false;
            _scpiLog.Add(PowerSupplyScpi.Output(false));
        }

        var voltage = OutputOn ? VoltageSetpoint : 0;
        var current = OutputOn ? Math.Min(VoltageSetpoint / LoadOhms, CurrentLimit) : 0;
        return Task.FromResult(new PowerSupplyReading(voltage, current, OutputOn));
    }

    private void RequireConnected()
    {
        if (State != InstrumentState.Connected)
        {
            throw new InvalidOperationException($"仪器「{Descriptor.Name}」未连接，无法下发命令");
        }
    }

    private void SetState(InstrumentState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, new InstrumentStateChangedEventArgs(state));
    }
}

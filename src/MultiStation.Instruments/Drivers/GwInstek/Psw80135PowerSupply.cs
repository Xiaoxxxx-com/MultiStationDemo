using MultiStation.Instruments.Protocol.Scpi.Algorithm;
using MultiStation.Instruments.Protocol.Scpi.Physical;

namespace MultiStation.Instruments.Drivers.GwInstek;

/// <summary>
/// GW Instek PSW80-13.5 可编程电源驱动（80V / 13.5A）：
/// 通过 SCPI 文本通道设压设流、OVP 保护、输出开关与电压电流回读。
/// </summary>
public sealed class Psw80135PowerSupply(InstrumentDescriptor descriptor, IScpiTransport transport) : IPowerSupply
{
    public const double MaxVoltage = 80;

    public const double MaxCurrent = 13.5;

    public const double MaxOvp = 88;

    private readonly IScpiTransport _transport = transport;

    public InstrumentDescriptor Descriptor { get; } = descriptor;

    public InstrumentState State { get; private set; } = InstrumentState.Disconnected;

    public event EventHandler<InstrumentStateChangedEventArgs>? StateChanged;

    public double VoltageSetpoint { get; private set; }

    public double CurrentLimit { get; private set; } = 1;

    public double OvpLimit { get; private set; } = 88;

    public bool OutputOn { get; private set; }

    /// <summary>仪器身份（*IDN? 回读），连接成功后可用。</summary>
    public string? Identity { get; private set; }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        Identity = await _transport.QueryAsync(PowerSupplyScpi.Identity, cancellationToken).ConfigureAwait(false);
        SetState(InstrumentState.Connected);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (State == InstrumentState.Connected)
            {
                await _transport.WriteAsync(PowerSupplyScpi.Output(false), cancellationToken).ConfigureAwait(false);
                OutputOn = false;
            }
        }
        catch (Exception) when (State == InstrumentState.Connected)
        {
            // 断开阶段关闭输出失败不阻塞释放连接
        }

        await _transport.CloseAsync().ConfigureAwait(false);
        SetState(InstrumentState.Disconnected);
    }

    public async Task SetVoltageAsync(double volts, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (volts < 0 || volts > MaxVoltage)
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "PSW80-13.5 电压设定范围 0~80V");
        }

        await _transport.WriteAsync(PowerSupplyScpi.SetVoltage(volts), cancellationToken).ConfigureAwait(false);
        VoltageSetpoint = volts;
    }

    public async Task SetCurrentLimitAsync(double amps, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (amps <= 0 || amps > MaxCurrent)
        {
            throw new ArgumentOutOfRangeException(nameof(amps), amps, "PSW80-13.5 限流设定范围 0~13.5A");
        }

        await _transport.WriteAsync(PowerSupplyScpi.SetCurrentLimit(amps), cancellationToken).ConfigureAwait(false);
        CurrentLimit = amps;
    }

    public async Task SetOvpAsync(double volts, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        if (volts <= 0 || volts > MaxOvp)
        {
            throw new ArgumentOutOfRangeException(nameof(volts), volts, "PSW80-13.5 OVP 设定范围 0~88V");
        }

        await _transport.WriteAsync(PowerSupplyScpi.SetOvp(volts), cancellationToken).ConfigureAwait(false);
        OvpLimit = volts;
    }

    public async Task SetOutputAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        RequireConnected();
        await _transport.WriteAsync(PowerSupplyScpi.Output(enabled), cancellationToken).ConfigureAwait(false);
        OutputOn = enabled;
    }

    public async Task<PowerSupplyReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        RequireConnected();
        var voltage = ScpiNumber.Parse(await _transport.QueryAsync(PowerSupplyScpi.MeasureVoltage, cancellationToken).ConfigureAwait(false));
        var current = ScpiNumber.Parse(await _transport.QueryAsync(PowerSupplyScpi.MeasureCurrent, cancellationToken).ConfigureAwait(false));
        var on = ScpiNumber.ParseBool(await _transport.QueryAsync(PowerSupplyScpi.OutputState, cancellationToken).ConfigureAwait(false));
        OutputOn = on;
        return new PowerSupplyReading(voltage, current, on);
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

using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace MultiStation.Instruments.Drivers.Itech;

/// <summary>
/// ITECH IT6322A 三路可编程直流电源：通过 RS232 串口发送 SCPI 命令，
/// 在烧录前给被测件上电（CH1），并可关闭输出。
/// </summary>
public sealed class It6322aPowerSupply : IDisposable
{
    public const string DefaultSerialSettings = "9600,8,N,1";

    private readonly string _portName;
    private readonly SerialSettings _settings;
    private SerialPort? _port;

    public It6322aPowerSupply(string portName, string? serialSettings = null)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new ArgumentException("电源串口不能为空", nameof(portName));
        }

        _portName = portName.Trim();
        _settings = ParseSettings(serialSettings);
    }

    /// <summary>串口参数：波特率,数据位,校验(N/E/O/M/S),停止位(1/1.5/2)，空值使用 9600,8,N,1。</summary>
    public static SerialSettings ParseSettings(string? serialSettings)
    {
        var text = string.IsNullOrWhiteSpace(serialSettings)
            ? DefaultSerialSettings
            : serialSettings.Trim();
        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            throw new FormatException("串口参数格式应为：波特率,数据位,校验,停止位（如 9600,8,N,1）");
        }

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var baudRate)
            || baudRate <= 0)
        {
            throw new FormatException($"波特率不正确：{parts[0]}");
        }

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var dataBits)
            || dataBits is < 5 or > 8)
        {
            throw new FormatException($"数据位应为 5～8：{parts[1]}");
        }

        var parity = parts[2].ToUpperInvariant() switch
        {
            "N" or "NONE" => Parity.None,
            "E" or "EVEN" => Parity.Even,
            "O" or "ODD" => Parity.Odd,
            "M" or "MARK" => Parity.Mark,
            "S" or "SPACE" => Parity.Space,
            _ => throw new FormatException($"校验位不正确（N/E/O/M/S）：{parts[2]}"),
        };

        var stopBits = parts[3].ToUpperInvariant() switch
        {
            "1" or "ONE" => StopBits.One,
            "1.5" or "ONEPOINTFIVE" => StopBits.OnePointFive,
            "2" or "TWO" => StopBits.Two,
            _ => throw new FormatException($"停止位不正确（1/1.5/2）：{parts[3]}"),
        };

        return new SerialSettings(baudRate, dataBits, parity, stopBits);
    }

    /// <summary>当前电脑可用串口，用于界面提示。</summary>
    public static IReadOnlyList<string> AvailablePorts => SerialPort.GetPortNames();

    public string PortName => _portName;

    /// <summary>打开串口并进入远程控制，校验设备身份。</summary>
    public void Connect()
    {
        if (_port is not null)
        {
            return;
        }

        var port = new SerialPort(_portName, _settings.BaudRate, _settings.Parity, _settings.DataBits, _settings.StopBits)
        {
            NewLine = "\n",
            Encoding = Encoding.ASCII,
            ReadTimeout = 1500,
            WriteTimeout = 1500,
            DtrEnable = true,
            RtsEnable = true,
        };

        port.Open();
        _port = port;
        Send("SYST:REM");

        var identity = Query("*IDN?");
        if (!identity.Contains("IT", StringComparison.OrdinalIgnoreCase))
        {
            Dispose();
            throw new IOException($"串口 {_portName} 未识别到 IT6322A（*IDN?={identity}）");
        }
    }

    /// <summary>设置指定通道的电压与限流，然后打开输出（默认 CH1）。</summary>
    public void PowerOn(double voltageVolts, double currentLimitAmps, int channel = 1)
    {
        Send(ChannelCommand(channel));
        Send(string.Create(CultureInfo.InvariantCulture, $"VOLT {voltageVolts:0.###}"));
        Send(string.Create(CultureInfo.InvariantCulture, $"CURR {currentLimitAmps:0.###}"));
        Send("OUTP ON");

        var state = Query("OUTP?");
        if (!IsOn(state))
        {
            throw new IOException($"IT6322A 输出未打开（CH{channel} OUTP?={state}）");
        }
    }

    /// <summary>关闭指定通道输出（默认 CH1）。</summary>
    public void PowerOff(int channel = 1)
    {
        Send(ChannelCommand(channel));
        Send("OUTP OFF");
    }

    /// <summary>读取指定通道当前电压（V）；无法解析时返回 null。</summary>
    public double? ReadVoltage(int channel = 1)
    {
        Send(ChannelCommand(channel));
        var text = Query("MEAS:VOLT?");
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var voltage)
            ? voltage
            : null;
    }

    /// <summary>通道选择命令；IT6322A 的可编程输出为 CH1/CH2。</summary>
    public static string ChannelCommand(int channel) => channel is 1 or 2
        ? $"INST CH{channel}"
        : throw new ArgumentOutOfRangeException(nameof(channel), "IT6322A 输出通道应为 1 或 2");

    public void Dispose()
    {
        var port = _port;
        _port = null;
        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (IOException)
        {
            // 关闭失败时仍释放句柄
        }

        port.Dispose();
    }

    private void Send(string command)
    {
        EnsureOpen();
        _port!.Write(command + "\n");
    }

    private string Query(string command)
    {
        EnsureOpen();
        _port!.DiscardInBuffer();
        _port.Write(command + "\n");

        // 部分 RS232 固件会回显命令，跳过回显行后再取响应
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var line = _port.ReadLine().Trim();
            if (line.Length == 0 || line.Equals(command, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line;
        }

        throw new IOException($"IT6322A 未响应命令：{command}");
    }

    private static bool IsOn(string state) =>
        state.Equals("ON", StringComparison.OrdinalIgnoreCase) || state == "1";

    private void EnsureOpen()
    {
        if (_port is null || !_port.IsOpen)
        {
            throw new InvalidOperationException("IT6322A 串口尚未连接");
        }
    }
}

/// <summary>IT6322A 串口通讯参数。</summary>
public readonly record struct SerialSettings(int BaudRate, int DataBits, Parity Parity, StopBits StopBits);

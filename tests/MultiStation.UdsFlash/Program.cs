using System.Globalization;
using MultiStation.Instruments;
using MultiStation.Instruments.Drivers.Zlg;
using MultiStation.Instruments.Protocol.Uds.Algorithm;
using MultiStation.Instruments.Protocol.Uds.Physical;
using MultiStation.Instruments.Simulation;

namespace MultiStation.UdsFlash;

/// <summary>
/// ECU 烧录测试脚本：基于 ZLG CAN 卡执行 UDS 刷写流程。
/// 不带参数运行时打印用法；--sim 使用模拟总线与模拟 ECU 离线验证；--hardware 使用真实 CAN 卡。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        var options = FlashOptions.Parse(args);
        using var timeout = new CancellationTokenSource(options.Timeout);
        try
        {
            var result = options.Simulate
                ? await RunSimulatedAsync(options, timeout.Token).ConfigureAwait(false)
                : await RunHardwareAsync(options, timeout.Token).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine("===== 烧录成功 =====");
            Console.WriteLine($"VIN={result.Vin} 软件版本={result.SoftwareVersion}");
            Console.WriteLine($"字节数={result.TotalBytes} 块数={result.Blocks} 单块上限={result.MaxBlockLength}");
            Console.WriteLine($"总耗时={result.Elapsed.TotalSeconds:F1}s");
            return 0;
        }
        catch (UdsNegativeResponseException ex)
        {
            Console.Error.WriteLine($"ECU 返回否定响应：NRC 0x{(byte)ex.Code:X2}（{ex.Code}）");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"烧录失败：{ex.Message}");
            return 1;
        }
    }

    private static async Task<UdsFlashResult> RunSimulatedAsync(FlashOptions options, CancellationToken cancellationToken)
    {
        Console.WriteLine("模式：模拟总线（SimulatedCanBus + SimulatedUdsEcu）");
        var bus = new SimulatedCanBus();
        var tester = bus.CreateEndpoint("Tester");
        var ecuEndpoint = bus.CreateEndpoint("ECU");
        await tester.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await ecuEndpoint.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var isoOptions = options.ToIsoTpOptions();
        await using var ecu = new SimulatedUdsEcu(ecuEndpoint, isoOptions);
        ecu.Start();

        var firmware = CreateDemoFirmware(options.FirmwareSize);
        var client = new UdsClient(new IsoTpTransport(tester, isoOptions), isoOptions);
        var config = options.ToFlashConfig(firmware);

        var result = await UdsFlasher.FlashAsync(client, config, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"模拟 ECU 收到 {ecu.DownloadedSize} 字节，会话={ecu.Session}");
        return result;
    }

    private static async Task<UdsFlashResult> RunHardwareAsync(FlashOptions options, CancellationToken cancellationToken)
    {
        var firmware = LoadFirmware(options);
        Console.WriteLine("模式：真实 ZLG CAN 卡");
        Console.WriteLine($"通道={options.DeviceIndex}:{options.ChannelIndex} 模式={options.Mode} " +
                          $"波特率={options.BaudRate} 请求ID=0x{options.TxId:X3} 响应ID=0x{options.RxId:X3}");

        var descriptor = new InstrumentDescriptor(
            "USBCANFD-400U",
            "USBCANFD-400U",
            InstrumentKind.CanBus,
            string.Create(CultureInfo.InvariantCulture, $"{options.DeviceIndex}:{options.ChannelIndex}:{options.Mode}"),
            Simulated: false);

        var instrument = new ZlgCanFd400UInstrument(
            descriptor,
            options.DeviceIndex,
            options.ChannelIndex,
            options.Mode == "classic" ? ZlgCanMode.Classic : ZlgCanMode.CanFd,
            options.BaudRate,
            options.DataBaudRate);

        await instrument.ConnectAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var isoOptions = options.ToIsoTpOptions();
            var client = new UdsClient(new IsoTpTransport(instrument, isoOptions), isoOptions);
            return await UdsFlasher.FlashAsync(client, options.ToFlashConfig(firmware), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await instrument.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static byte[] LoadFirmware(FlashOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.FirmwarePath))
        {
            throw new ArgumentException("硬件模式必须通过 --file 指定烧录文件");
        }

        if (!File.Exists(options.FirmwarePath))
        {
            throw new FileNotFoundException($"烧录文件不存在：{options.FirmwarePath}");
        }

        var firmware = File.ReadAllBytes(options.FirmwarePath);
        Console.WriteLine($"烧录文件：{options.FirmwarePath}（{firmware.Length} 字节）");
        return firmware;
    }

    private static byte[] CreateDemoFirmware(int size)
    {
        var firmware = new byte[size];
        for (var i = 0; i < size; i++)
        {
            firmware[i] = (byte)(i * 31 + 7);
        }

        return firmware;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            ECU 烧录测试脚本（UDS over ISO-TP / ZLG USBCANFD-400U）

            离线验证：
              dotnet run --project tests\MultiStation.UdsFlash -- --sim [--size 4096]

            真实 ECU：
              dotnet run --project tests\MultiStation.UdsFlash -- --hardware --file app.bin ^
                [--channel 0:0] [--mode classic|fd] [--baud 500000] [--data-baud 2000000] ^
                [--tx 7E0] [--rx 7E8] [--address 08000000] [--fingerprint MULTISTATION] [--no-erase]

            参数说明：
              --channel     设备序号:通道序号，默认 0:0
              --mode        classic（经典 CAN，默认）或 fd（CAN FD）
              --baud        仲裁波特率，默认 500000
              --data-baud   CAN FD 数据波特率，默认 2000000
              --tx/--rx     诊断请求 / 响应 CAN ID（十六进制），默认 7E0 / 7E8
              --address     烧录起始地址（十六进制），默认 08000000
              --fingerprint 写入 DID F198 的指纹文本
              --no-erase    跳过擦除例程 0xFF00
              --timeout     整体超时秒数，默认 300
            """);
    }

    private sealed record FlashOptions
    {
        public bool Simulate { get; init; }
        public int FirmwareSize { get; init; } = 4096;
        public string? FirmwarePath { get; init; }
        public int DeviceIndex { get; init; }
        public int ChannelIndex { get; init; }
        public string Mode { get; init; } = "classic";
        public uint BaudRate { get; init; } = 500_000;
        public uint DataBaudRate { get; init; } = 2_000_000;
        public ushort TxId { get; init; } = 0x7E0;
        public ushort RxId { get; init; } = 0x7E8;
        public uint MemoryAddress { get; init; } = 0x0800_0000;
        public string Fingerprint { get; init; } = "MULTISTATION";
        public bool EraseMemory { get; init; } = true;
        public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

        public static FlashOptions Parse(string[] args)
        {
            var options = new FlashOptions();
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--sim":
                        options = options with { Simulate = true };
                        break;
                    case "--hardware":
                        options = options with { Simulate = false };
                        break;
                    case "--size" when i + 1 < args.Length:
                        options = options with { FirmwareSize = int.Parse(args[++i], CultureInfo.InvariantCulture) };
                        break;
                    case "--file" when i + 1 < args.Length:
                        options = options with { FirmwarePath = args[++i] };
                        break;
                    case "--channel" when i + 1 < args.Length:
                        {
                            var parts = args[++i].Split(':');
                            options = options with
                            {
                                DeviceIndex = parts.Length > 0 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0,
                                ChannelIndex = parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0,
                            };
                            break;
                        }
                    case "--mode" when i + 1 < args.Length:
                        options = options with { Mode = args[++i].ToLowerInvariant() };
                        break;
                    case "--baud" when i + 1 < args.Length:
                        options = options with { BaudRate = ParseUInt(args[++i]) };
                        break;
                    case "--data-baud" when i + 1 < args.Length:
                        options = options with { DataBaudRate = ParseUInt(args[++i]) };
                        break;
                    case "--tx" when i + 1 < args.Length:
                        options = options with { TxId = (ushort)ParseUInt(args[++i]) };
                        break;
                    case "--rx" when i + 1 < args.Length:
                        options = options with { RxId = (ushort)ParseUInt(args[++i]) };
                        break;
                    case "--address" when i + 1 < args.Length:
                        options = options with { MemoryAddress = ParseUInt(args[++i]) };
                        break;
                    case "--fingerprint" when i + 1 < args.Length:
                        options = options with { Fingerprint = args[++i] };
                        break;
                    case "--no-erase":
                        options = options with { EraseMemory = false };
                        break;
                    case "--timeout" when i + 1 < args.Length:
                        options = options with
                        {
                            Timeout = TimeSpan.FromSeconds(int.Parse(args[++i], CultureInfo.InvariantCulture)),
                        };
                        break;
                }
            }

            return options;
        }

        public IsoTpOptions ToIsoTpOptions() => new()
        {
            TxId = TxId,
            RxId = RxId,
            PadToEightBytes = true,
            PaddingByte = 0x00,
        };

        public UdsFlashConfig ToFlashConfig(byte[] firmware) => new(
            MemoryAddress,
            firmware,
            System.Text.Encoding.ASCII.GetBytes(Fingerprint),
            EraseMemory: EraseMemory,
            Log: Console.WriteLine);

        private static uint ParseUInt(string text) =>
            uint.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
                text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture);
    }
}

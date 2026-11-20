using System.Runtime.InteropServices;
using System.Text;
using MultiStation.Instruments;
using MultiStation.Instruments.Drivers.GwInstek;
using MultiStation.Instruments.Drivers.Zlg;
using MultiStation.Instruments.Protocol.Can.Algorithm;
using MultiStation.Instruments.Protocol.Can.Physical;
using MultiStation.Instruments.Protocol.Scpi.Algorithm;
using MultiStation.Instruments.Protocol.Scpi.Physical;
using MultiStation.Instruments.Protocol.Uds.Algorithm;
using MultiStation.Instruments.Protocol.Uds.Physical;
using MultiStation.Instruments.Simulation;

namespace MultiStation.Ui.UnitTests;

/// <summary>测试仪器模块的单元测试：协议拼装、模拟实现、驱动结构与真实设备连通性。</summary>
internal static class InstrumentTests
{
    public static void Run()
    {
        // INS-01 SCPI 命令拼装：固定三位小数与 InvariantCulture
        Program.Check("INS-01",
            PowerSupplyScpi.SetVoltage(12) == "VOLT 12.000"
                && PowerSupplyScpi.SetCurrentLimit(1.5) == "CURR 1.500"
                && PowerSupplyScpi.SetOvp(15) == "VOLT:PROT 15.000"
                && PowerSupplyScpi.Output(true) == "OUTP ON"
                && PowerSupplyScpi.Output(false) == "OUTP OFF"
                && PowerSupplyScpi.MeasureVoltage == "MEAS:VOLT?",
            $"volt={PowerSupplyScpi.SetVoltage(12)} curr={PowerSupplyScpi.SetCurrentLimit(1.5)} ovp={PowerSupplyScpi.SetOvp(15)}");

        // INS-02 可编程电源：设压设流后回读，12V / 10Ω 负载且限流 1A
        var psu = new SimulatedPowerSupply(
            new InstrumentDescriptor("PSU-1", "模拟电源", InstrumentKind.PowerSupply, "SIM::PSU-1"));
        psu.ConnectAsync().GetAwaiter().GetResult();
        psu.SetVoltageAsync(12).GetAwaiter().GetResult();
        psu.SetCurrentLimitAsync(1).GetAwaiter().GetResult();
        psu.SetOvpAsync(15).GetAwaiter().GetResult();
        psu.SetOutputAsync(true).GetAwaiter().GetResult();
        var reading = psu.ReadAsync().GetAwaiter().GetResult();
        Program.Check("INS-02",
            reading.Voltage == 12 && Math.Abs(reading.Current - 1) < 1e-9 && reading.OutputOn,
            $"voltage={reading.Voltage} current={reading.Current} output={reading.OutputOn}");

        // INS-03 OVP 保护：设定电压高于保护值时输出自动断开，回读归零
        psu.SetVoltageAsync(16).GetAwaiter().GetResult();
        var tripped = psu.ReadAsync().GetAwaiter().GetResult();
        Program.Check("INS-03",
            !tripped.OutputOn && tripped.Voltage == 0 && tripped.Current == 0,
            $"voltage={tripped.Voltage} current={tripped.Current} output={tripped.OutputOn}");

        // INS-04 未连接时拒绝下发命令
        var disconnected = new SimulatedPowerSupply(
            new InstrumentDescriptor("PSU-2", "未连接电源", InstrumentKind.PowerSupply, "SIM::PSU-2"));
        var threw = false;
        try
        {
            disconnected.SetVoltageAsync(5).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Program.Check("INS-04", threw, $"未连接下发命令抛异常={threw}");

        // INS-05 CAN 总线：发送报文回环到接收队列
        var can = new SimulatedCanInstrument(
            new InstrumentDescriptor("CAN-1", "模拟 CAN", InstrumentKind.CanBus, "SIM::CAN-1"));
        can.ConnectAsync().GetAwaiter().GetResult();
        can.SendAsync(new CanFrame(0x123, [1, 2, 3])).GetAwaiter().GetResult();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var frames = can.ReceiveAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var moved = frames.MoveNextAsync().AsTask().GetAwaiter().GetResult();
        var looped = moved ? frames.Current : null;
        frames.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Program.Check("INS-05",
            looped is not null && looped.Id == 0x123
                && looped.Data.SequenceEqual(new byte[] { 1, 2, 3 }) && can.SentCount == 1,
            $"received={looped?.Id} bytes={looped?.Data.Length} sent={can.SentCount}");

        // INS-06 InstrumentHub：按描述创建仪器并统一连接 / 断开
        var hub = new InstrumentHub(
        [
            new InstrumentDescriptor("PSU-9", "电源", InstrumentKind.PowerSupply, "SIM::PSU-9"),
            new InstrumentDescriptor("CAN-9", "CAN", InstrumentKind.CanBus, "SIM::CAN-9"),
        ]);
        hub.ConnectAllAsync().GetAwaiter().GetResult();
        var connected = hub.Instruments.All(instrument => instrument.State == InstrumentState.Connected);
        var typed = hub.Get<IPowerSupply>("PSU-9") is SimulatedPowerSupply
            && hub.Get<ICanInstrument>("CAN-9") is SimulatedCanInstrument;
        hub.DisposeAsync().AsTask().GetAwaiter().GetResult();
        var disconnectedAfterDispose = hub.Instruments.All(
            instrument => instrument.State == InstrumentState.Disconnected);
        Program.Check("INS-06", connected && typed && disconnectedAfterDispose,
            $"connected={connected} typed={typed} disposed={disconnectedAfterDispose}");

        // INS-07 ZLG 互操作结构体大小必须与 zlgcan.h 一致
        var layout = Marshal.SizeOf<ZlgCanFdInitConfig>() == 28
            && Marshal.SizeOf<ZlgChannelInitConfig>() == 32
            && Marshal.SizeOf<ZlgCanFdFrame>() == 72
            && Marshal.SizeOf<ZlgTransmitFdData>() == 76
            && Marshal.SizeOf<ZlgReceiveFdData>() == 80;
        Program.Check("INS-07", layout,
            $"initCfg={Marshal.SizeOf<ZlgCanFdInitConfig>()} channel={Marshal.SizeOf<ZlgChannelInitConfig>()} frame={Marshal.SizeOf<ZlgCanFdFrame>()} tx={Marshal.SizeOf<ZlgTransmitFdData>()} rx={Marshal.SizeOf<ZlgReceiveFdData>()}");

        // INS-08 CAN 标识符编解码：标准帧 11 位 / 扩展帧 29 位
        var standard = CanFrameCodec.Encode(new CanFrame(0x123, [1]));
        var extended = CanFrameCodec.Encode(new CanFrame(0x18FF50E5, [1], ExtendedId: true));
        var decodedStandard = CanFrameCodec.Decode(standard, [1]);
        var decodedExtended = CanFrameCodec.Decode(extended, [1]);
        Program.Check("INS-08",
            standard == 0x123 && extended == 0x98FF50E5
                && decodedStandard.Id == 0x123 && !decodedStandard.ExtendedId
                && decodedExtended.Id == 0x18FF50E5 && decodedExtended.ExtendedId,
            $"std=0x{standard:X} ext=0x{extended:X} decodedStd={decodedStandard.Id:X} decodedExt={decodedExtended.Id:X}");

        // INS-09 仪器地址解析：VISA 风格与简写
        var visa = TcpScpiTransport.FromAddress("TCPIP::192.168.1.2::2268");
        var shortForm = TcpScpiTransport.FromAddress("192.168.1.2:2268");
        var defaultPort = TcpScpiTransport.FromAddress("192.168.1.2");
        Program.Check("INS-09",
            visa.Endpoint == "192.168.1.2:2268"
                && shortForm.Endpoint == "192.168.1.2:2268"
                && defaultPort.Endpoint == "192.168.1.2:2268",
            $"visa={visa.Endpoint} short={shortForm.Endpoint} default={defaultPort.Endpoint}");

        // INS-10 SCPI 回读解析：普通小数、科学计数法与分号分隔
        Program.Check("INS-10",
            Math.Abs(ScpiNumber.Parse("-0.001") + 0.001) < 1e-12
                && Math.Abs(ScpiNumber.Parse("+1.234E+01") - 12.34) < 1e-9
                && ScpiNumber.Parse("1;2") == 1
                && ScpiNumber.ParseBool("1") && !ScpiNumber.ParseBool("0"),
            $"a={ScpiNumber.Parse("-0.001")} b={ScpiNumber.Parse("+1.234E+01")}");

        // INS-11 ISO-TP：多帧分段发送与重组（模拟总线两个节点）
        var bus = new SimulatedCanBus();
        var testerNode = bus.CreateEndpoint("Tester");
        var peerNode = bus.CreateEndpoint("Peer");
        testerNode.ConnectAsync().GetAwaiter().GetResult();
        peerNode.ConnectAsync().GetAwaiter().GetResult();
        var testerOptions = new IsoTpOptions { TxId = 0x7E0, RxId = 0x7E8 };
        var peerOptions = new IsoTpOptions { TxId = 0x7E8, RxId = 0x7E0 };
        var testerLink = new IsoTpTransport(testerNode, testerOptions);
        var peerLink = new IsoTpTransport(peerNode, peerOptions);
        var payload = new byte[40];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i + 1);
        }

        // 接收方需先进入监听，多帧发送才能收到流控帧
        var receiveTask = peerLink.ReceiveAsync(TimeSpan.FromSeconds(2));
        testerLink.SendAsync(payload).GetAwaiter().GetResult();
        var reassembled = receiveTask.GetAwaiter().GetResult();
        Program.Check("INS-11", reassembled.SequenceEqual(payload),
            $"sent={payload.Length} received={reassembled.Length}");

        // INS-12 UDS 刷写全流程：模拟 ECU 完成会话 / 安全访问 / 下载 / 校验
        var ecuNode = bus.CreateEndpoint("ECU");
        ecuNode.ConnectAsync().GetAwaiter().GetResult();
        var ecu = new SimulatedUdsEcu(ecuNode, testerOptions);
        ecu.Start();
        var client = new UdsClient(testerLink, testerOptions);
        var firmware = new byte[3000];
        for (var i = 0; i < firmware.Length; i++)
        {
            firmware[i] = (byte)(i * 31 + 7);
        }

        var flashConfig = new UdsFlashConfig(
            0x0800_0000, firmware, Encoding.ASCII.GetBytes("MULTISTATION"));
        var flashResult = UdsFlasher.FlashAsync(client, flashConfig).GetAwaiter().GetResult();
        Program.Check("INS-12",
            flashResult.TotalBytes == firmware.Length && flashResult.Blocks > 0
                && ecu.DownloadedSize == firmware.Length && flashResult.Vin.StartsWith("LSV", StringComparison.Ordinal),
            $"bytes={flashResult.TotalBytes} blocks={flashResult.Blocks} ecuBytes={ecu.DownloadedSize} vin={flashResult.Vin}");
        ecu.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    /// <summary>真实设备连通性检查（需显式传 --hardware）：只做连接与只读回读，不下发输出与报文。</summary>
    public static void RunHardware()
    {
        var psuDescriptor = new InstrumentDescriptor(
            "PSW80-13.5", "PSW80-13.5", InstrumentKind.PowerSupply, "TCPIP::192.168.1.2::2268", Simulated: false);
        var psu = (Psw80135PowerSupply)InstrumentHub.Create(psuDescriptor);
        try
        {
            psu.ConnectAsync().GetAwaiter().GetResult();
            var reading = psu.ReadAsync().GetAwaiter().GetResult();
            Program.Check("HW-01", psu.Identity?.Contains("PSW80-13.5", StringComparison.OrdinalIgnoreCase) == true,
                $"identity={psu.Identity} voltage={reading.Voltage:F3} current={reading.Current:F3} output={reading.OutputOn}");
        }
        catch (Exception ex)
        {
            Program.Check("HW-01", false, $"PSW80-13.5 连接失败：{ex.Message}");
        }
        finally
        {
            psu.DisconnectAsync().GetAwaiter().GetResult();
        }

        var canDescriptor = new InstrumentDescriptor(
            "USBCANFD-400U", "USBCANFD-400U", InstrumentKind.CanBus, "0:0", Simulated: false);
        var can = (ZlgCanFd400UInstrument)InstrumentHub.Create(canDescriptor);
        try
        {
            can.ConnectAsync().GetAwaiter().GetResult();
            Program.Check("HW-02", can.State == InstrumentState.Connected, $"state={can.State}");
        }
        catch (Exception ex)
        {
            Program.Check("HW-02", false, $"USBCANFD-400U 连接失败：{ex.Message}");
        }
        finally
        {
            can.DisconnectAsync().GetAwaiter().GetResult();
        }

        // HW-03 经典 CAN 模式：UDS 刷写使用 500k 经典帧
        var classic = new ZlgCanFd400UInstrument(
            new InstrumentDescriptor(
                "USBCANFD-400U-classic", "USBCANFD-400U（经典 CAN）", InstrumentKind.CanBus, "0:0", Simulated: false),
            0, 0, ZlgCanMode.Classic);
        try
        {
            classic.ConnectAsync().GetAwaiter().GetResult();
            Program.Check("HW-03", classic.State == InstrumentState.Connected, $"classic state={classic.State}");
        }
        catch (Exception ex)
        {
            Program.Check("HW-03", false, $"经典 CAN 通道连接失败：{ex.Message}");
        }
        finally
        {
            classic.DisconnectAsync().GetAwaiter().GetResult();
        }
    }
}

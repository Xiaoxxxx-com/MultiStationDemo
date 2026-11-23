using System.IO;
using System.Windows.Threading;
using MultiStation.Instruments;
using MultiStation.Instruments.Protocol.Uds.Physical;
using MultiStation.Instruments.Simulation;
using MultiStation.Platform.Core.Production;
using MultiStation.Ui.Desktop;

namespace MultiStation.Ui.UnitTests;

/// <summary>NP12/NP13 工作簿、真实 UDS 执行器和控制器生命周期的纯离线测试。</summary>
internal static class Np12Tests
{
    public static void Run()
    {
        RunLoaderTests();
        RunExecutorTests();
        RunControllerTests();
    }

    private static void RunLoaderTests()
    {
        var model = ModelProfileStore.CreateDefaults().Single(model => model.Name == "NP12");
        if (!File.Exists(model.FlashSequencePath))
        {
            Console.WriteLine($"SKIP NP12-01 未找到现场工作簿：{model.FlashSequencePath}");
        }
        else
        {
            try
            {
                var sequence = FlashSequenceLoader.Load(model);
                var rows = sequence.Rows;
                var ccuRows = rows.Where(row => row.Command.StartsWith("COM=UDS", StringComparison.Ordinal)
                    && row.Command.Contains("CHANNEL=1", StringComparison.Ordinal)).ToArray();
                var mainReads = rows.Where(row => row.Command.StartsWith("COM=UDS", StringComparison.Ordinal)
                    && row.Command.Contains("CHANNEL=0", StringComparison.Ordinal)
                    && row.Command.Contains("PDU=22", StringComparison.Ordinal)).ToArray();

                Program.Check("NP12-01",
                    rows.Count == 46
                        && rows.Count(row => row.Kind == "Bat") == 2
                        && mainReads.Length == 20
                        && ccuRows.Length == 12
                        && ccuRows.All(row => row.Command.Contains("PDU=22", StringComparison.Ordinal))
                        && mainReads.Any(row => row.Equal.Length > 0)
                        && mainReads.Any(row => row.Equal.Length == 0)
                        && rows[0].Code == "POWER_ON"
                        && rows[0].Command.Contains("OUTP=ON", StringComparison.Ordinal)
                        && rows.Any(row => row.Code == "CAN_INIT_CH0"
                            && row.Command.Contains("CHANNEL=0", StringComparison.Ordinal)
                            && row.Command.Contains("BAUD=500000", StringComparison.Ordinal)
                            && row.Command.Contains("MODE=classic", StringComparison.Ordinal)
                            && row.Command.Contains("REQID=180FF7A2", StringComparison.Ordinal)
                            && row.Command.Contains("RESPID=180FF7AA", StringComparison.Ordinal))
                        && rows.Any(row => row.Code == "CAN_INIT_CH1"
                            && row.Command.Contains("CHANNEL=1", StringComparison.Ordinal)
                            && row.Command.Contains("REQID=7E4", StringComparison.Ordinal)
                            && row.Command.Contains("RESPID=7EC", StringComparison.Ordinal))
                        && rows.Where(row => row.Command.StartsWith("COM=CAN", StringComparison.Ordinal))
                            .All(row => !row.Command.Contains("MAIN=", StringComparison.Ordinal)
                                && !row.Command.Contains("CCU=", StringComparison.Ordinal))
                        && rows[^1].Code == "POWER_OFF"
                        && rows[^1].Command.Contains("OUTP=OFF", StringComparison.Ordinal)
                        && rows.Any(row => row.Command.Contains(
                            "PDU=275212345678", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "PDU=2EF18B,DATA=250507", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "PDU=2EF18C,DATA={F18C}", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "PDU=2EF191,DATA=1211015801", StringComparison.Ordinal))
                        && sequence.RequiresSupplierSerial,
                    $"rows={rows.Count} mainReads={mainReads.Length} ccuReads={ccuRows.Length} " +
                    $"f18c={sequence.RequiresSupplierSerial}");
            }
            catch (Exception ex)
            {
                Program.Check("NP12-01", false, ex.Message);
            }
        }

        var np13Model = ModelProfileStore.CreateDefaults().Single(model => model.Name == "NP13");
        if (!File.Exists(np13Model.FlashSequencePath))
        {
            Console.WriteLine($"SKIP NP13-01 未找到现场工作簿：{np13Model.FlashSequencePath}");
        }
        else
        {
            try
            {
                var sequence = FlashSequenceLoader.Load(np13Model);
                var rows = sequence.Rows;
                var ccuRows = rows.Where(row => row.Command.StartsWith("COM=UDS", StringComparison.Ordinal)
                    && row.Command.Contains("CHANNEL=1", StringComparison.Ordinal)).ToArray();
                var mainReads = rows.Where(row => row.Command.StartsWith("COM=UDS", StringComparison.Ordinal)
                    && row.Command.Contains("CHANNEL=0", StringComparison.Ordinal)
                    && row.Command.Contains("PDU=22", StringComparison.Ordinal)).ToArray();

                Program.Check("NP13-01",
                    rows.Count == 43
                        && rows.Count(row => row.Kind == "Bat") == 2
                        && mainReads.Length == 20
                        && ccuRows.Length == 9
                        && ccuRows.All(row => row.Command.Contains("PDU=22", StringComparison.Ordinal))
                        && rows[0].Code == "POWER_ON"
                        && rows.Any(row => row.Code == "CAN_INIT_CH0")
                        && rows.Any(row => row.Code == "CAN_INIT_CH1")
                        && rows[^1].Code == "POWER_OFF"
                        && rows.Any(row => row.Command.Contains(
                            "PDU=2EF18B,DATA=250507", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "PDU=2EF18C,DATA={F18C}", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "PDU=2EF191,DATA=1211015801", StringComparison.Ordinal))
                        && sequence.RequiresSupplierSerial,
                    $"rows={rows.Count} mainReads={mainReads.Length} ccuReads={ccuRows.Length} " +
                    $"f18c={sequence.RequiresSupplierSerial}");
            }
            catch (Exception ex)
            {
                Program.Check("NP13-01", false, ex.Message);
            }
        }

        var parsed = FlashSequenceLoader.ParseHex("0x27 52 12 34 56 78", "测试 HEX");
        var oddRejected = Throws<FormatException>(() =>
            FlashSequenceLoader.ParseHex("123", "奇数长度"));
        var characterRejected = Throws<FormatException>(() =>
            FlashSequenceLoader.ParseHex("12XZ", "非法字符"));
        Program.Check("NP12-02",
            parsed.SequenceEqual(new byte[] { 0x27, 0x52, 0x12, 0x34, 0x56, 0x78 })
                && oddRejected && characterRejected,
            $"bytes={Convert.ToHexString(parsed)} odd={oddRejected} invalid={characterRejected}");
    }

    private static void RunExecutorTests()
    {
        var model = new ModelProfile
        {
            Name = "NP12-Offline",
            Kind = ModelKind.Flash,
            RequestId = 0x180FF7A2,
            ResponseId = 0x180FF7AA,
            CcuRequestId = 0x07E4,
            CcuResponseId = 0x07EC,
            CanMode = "classic",
        };
        var mainBus = new SimulatedCanBus();
        var ccuBus = new SimulatedCanBus();
        var mainTester = new RecordingCanInstrument(mainBus.CreateEndpoint("Tester-Main"));
        var ccuTester = new RecordingCanInstrument(ccuBus.CreateEndpoint("Tester-Ccu"));
        var mainNode = new ScriptedUdsNode(
            mainBus.CreateEndpoint("MainECU"),
            new IsoTpOptions
            {
                TxId = model.ResponseId,
                RxId = model.RequestId,
                ExtendedId = true,
            },
            MainResponse);
        var ccuNode = new ScriptedUdsNode(
            ccuBus.CreateEndpoint("CCU"),
            new IsoTpOptions { TxId = model.CcuResponseId, RxId = model.CcuRequestId },
            CcuResponse);
        var factoryCalls = 0;
        var executor = new UdsStepExecutor(model, channel =>
        {
            factoryCalls++;
            return channel == 0 ? mainTester : ccuTester;
        });

        try
        {
            var lazy = factoryCalls == 0 && mainTester.ConnectCount == 0 && ccuTester.ConnectCount == 0;
            var session = Execute(executor, UdsRow("1003"));
            Program.Check("NP12-03",
                lazy && session.Passed && factoryCalls == 2
                    && mainTester.ConnectCount == 1 && ccuTester.ConnectCount == 1
                    && mainTester.SentFrames.Any(frame => frame.Id == model.RequestId && frame.ExtendedId),
                $"lazy={lazy} passed={session.Passed} factory={factoryCalls} " +
                $"mainConnected={mainTester.ConnectCount} ccuConnected={ccuTester.ConnectCount} " +
                $"detail={session.Detail}");

            var key = Execute(executor, UdsRow("275212345678"));
            Program.Check("NP12-04",
                key.Passed && mainNode.Requests[^1].SequenceEqual(
                    new byte[] { 0x27, 0x52, 0x12, 0x34, 0x56, 0x78 }),
                $"passed={key.Passed} request={Convert.ToHexString(mainNode.Requests[^1])}");

            var missingSerial = Execute(executor, UdsRow("2EF18C", data: "{F18C}"));
            var blankRejected = Throws<FormatException>(() => executor.SetSupplierSerialAscii("  "));
            var dashRejected = Throws<FormatException>(() => executor.SetSupplierSerialAscii(" - "));
            var unicodeRejected = Throws<FormatException>(() => executor.SetSupplierSerialAscii("SN123中文"));
            var controlRejected = Throws<FormatException>(() => executor.SetSupplierSerialAscii("SN\n123"));
            var longAccepted = !Throws<FormatException>(() =>
                executor.SetSupplierSerialAscii("12345678901234567"));
            executor.SetSupplierSerialAscii("1121830A00000002");
            var f18c = Execute(executor, UdsRow("2EF18C", data: "{F18C}"));
            var f191 = Execute(executor, UdsRow("2EF191", data: "1211015801"));
            Program.Check("NP12-05",
                !missingSerial.Passed && blankRejected && dashRejected && unicodeRejected && controlRejected
                    && longAccepted && f18c.Passed && f191.Passed
                    && mainNode.Requests[^2].SequenceEqual(
                        new byte[]
                        {
                            0x2E, 0xF1, 0x8C,
                            0x31, 0x31, 0x32, 0x31, 0x38, 0x33, 0x30, 0x41,
                            0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x32,
                        })
                    && mainNode.Requests[^1].SequenceEqual(
                        new byte[] { 0x2E, 0xF1, 0x91, 0x12, 0x11, 0x01, 0x58, 0x01 }),
                $"missing={missingSerial.Passed} blank={blankRejected} dash={dashRejected} " +
                $"unicode={unicodeRejected} control={controlRejected} long={longAccepted} f18c={f18c.Passed} " +
                $"f191={f191.Passed} frames={mainTester.SentFrames.Count}");

            var staticRead = Execute(executor, UdsRow("22F190", expected: "1234"));
            var dynamicRead = Execute(executor, UdsRow("22F190"));
            Program.Check("NP12-06",
                staticRead.Passed && staticRead.Measured == "1234"
                    && dynamicRead.Passed && dynamicRead.Measured == "1234",
                $"static={staticRead.Passed}/{staticRead.Measured} " +
                $"dynamic={dynamicRead.Passed}/{dynamicRead.Measured}");

            var negative = Execute(executor, UdsRow("22F192"));
            Program.Check("NP12-07",
                !negative.Passed && negative.Measured == "NRC=0x31",
                $"passed={negative.Passed} measured={negative.Measured} detail={negative.Detail}");

            var mismatch = Execute(executor, UdsRow("22F190", expected: "1235"));
            var wrongDid = Execute(executor, UdsRow("22F194"));
            Program.Check("NP12-08",
                !mismatch.Passed && !wrongDid.Passed,
                $"valueMismatch={mismatch.Detail} didMismatch={wrongDid.Detail}");

            var timeout = Execute(executor, UdsRow("22F193", timeout: "50"));
            Program.Check("NP12-09",
                !timeout.Passed && timeout.Measured == "超时",
                $"passed={timeout.Passed} measured={timeout.Measured} detail={timeout.Detail}");

            var ccu = Execute(executor, UdsRow("22F1A0", target: "CCU"));
            var ccuFrame = ccuTester.SentFrames.Last(frame => frame.Id == model.CcuRequestId);
            Program.Check("NP12-10",
                ccu.Passed && ccu.Measured == "AABB" && !ccuFrame.ExtendedId
                    && factoryCalls == 2 && mainTester.ConnectCount == 1 && ccuTester.ConnectCount == 1
                    && mainTester.SentFrames.All(frame => frame.Id != model.CcuRequestId),
                $"passed={ccu.Passed} measured={ccu.Measured} id=0x{ccuFrame.Id:X3} " +
                $"extended={ccuFrame.ExtendedId} factory={factoryCalls}");

            executor.CloseAsync().GetAwaiter().GetResult();
            Program.Check("NP12-11",
                mainTester.DisconnectCount == 1 && ccuTester.DisconnectCount == 1 && factoryCalls == 2,
                $"mainDisconnect={mainTester.DisconnectCount} ccuDisconnect={ccuTester.DisconnectCount} " +
                $"factory={factoryCalls}");

            // NP12-14 序列中的 CAN 初始化指令：CHANNEL=0/1 切换通道，并携带 UDS REQID/RESPID
            var can0 = Execute(executor, CanRow(
                "COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=0,MODE=classic,BAUD=500000,DBIT=2000000," +
                "REQID=180FF7A2,RESPID=180FF7AA"));
            var can1 = Execute(executor, CanRow(
                "COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=1,MODE=classic,BAUD=500000,DBIT=2000000," +
                "REQID=7E4,RESPID=7EC"));
            var badId = Execute(executor, CanRow(
                "COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=0,MODE=classic,BAUD=500000,DBIT=2000000," +
                "REQID=ZZ,RESPID=180FF7AA"));
            Program.Check("NP12-14",
                can0.Passed && can0.Detail.Contains("REQID=0x180FF7A2", StringComparison.Ordinal)
                    && can1.Passed && can1.Detail.Contains("REQID=0x7E4", StringComparison.Ordinal)
                    && !badId.Passed
                    && factoryCalls == 4
                    && mainTester.ConnectCount == 2 && ccuTester.ConnectCount == 2,
                $"ch0={can0.Passed}/{can0.Detail} ch1={can1.Passed}/{can1.Detail} " +
                $"badId={badId.Passed} factory={factoryCalls} " +
                $"main={mainTester.ConnectCount} ccu={ccuTester.ConnectCount}");
        }
        catch (Exception ex)
        {
            Program.Check("NP12-EX", false, $"未处理异常：{ex}");
        }
        finally
        {
            executor.DisposeAsync().AsTask().GetAwaiter().GetResult();
            mainNode.DisposeAsync().AsTask().GetAwaiter().GetResult();
            ccuNode.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void RunControllerTests()
    {
        var failureExecutor = new FakeStepExecutor
        {
            Handler = (_, _) => Task.FromResult(new UdsStepResult(false, "NRC=0x31", "离线 NG")),
        };
        using (var controller = new SingleStationTestController(
                   [UdsRow("1003"), UdsRow("2751"), UdsRow("22F190")], failureExecutor))
        {
            controller.NextStep();
            PumpUntil(() => !controller.IsRunning);
            Program.Check("NP12-12",
                failureExecutor.ExecuteCount == 1 && controller.Steps[0].Result == "NG"
                    && controller.Steps[1].Result == "待执行" && controller.Verdict == "NG"
                    && failureExecutor.CloseCount >= 1,
                $"executed={failureExecutor.ExecuteCount} first={controller.Steps[0].Result} " +
                $"second={controller.Steps[1].Result} close={failureExecutor.CloseCount}");
        }

        var closeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stoppingExecutor = new FakeStepExecutor
        {
            Handler = async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("不可达");
            },
            CloseHandler = () => closeGate.Task,
        };
        using (var controller = new SingleStationTestController([UdsRow("1003")], stoppingExecutor))
        {
            controller.NextStep();
            PumpUntil(() => stoppingExecutor.ExecuteCount == 1);
            controller.Stop();
            controller.Start();
            var blocked = !controller.IsRunning && controller.IsCleaningUp;
            closeGate.SetResult();
            PumpUntil(() => !controller.IsCleaningUp);
            Program.Check("NP12-13",
                blocked && stoppingExecutor.CloseCount >= 1 && !controller.IsCleaningUp,
                $"blocked={blocked} close={stoppingExecutor.CloseCount} cleaning={controller.IsCleaningUp}");
        }
    }

    private static UdsStepResult Execute(UdsStepExecutor executor, TestSequenceRow row) =>
        executor.ExecuteAsync(row, CancellationToken.None).GetAwaiter().GetResult();

    private static TestSequenceRow CanRow(string command) => new(
        "InitSeq", "1", "CAN_INIT", "CAN 通道初始化", "Action",
        "", "", "", "", "", "can_zlg_card0", command,
        "5000", "0", "Abort", "", "T", "T", "主 ECU 使用 CAN0，CCU 使用 CAN1");

    private static TestSequenceRow UdsRow(
        string pdu,
        string target = "MAIN",
        string expected = "",
        string timeout = "3000",
        string? data = null)
    {
        var channel = string.Equals(target, "CCU", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var command = $"COM=UDS,CHANNEL={channel},PDU={pdu}";
        if (data is not null)
        {
            command += $",DATA={data}";
        }

        return new TestSequenceRow(
            "MainSeq", "1", $"TEST_{pdu}", pdu, "Test", "Hex", pdu,
            "", "", expected, "can_zlg_card0", command, timeout, "0", "Abort", "", "T", "T", "");
    }

    private static byte[]? MainResponse(byte[] request)
    {
        if (request.Length >= 3 && request[0] == 0x22 && request[1] == 0xF1)
        {
            switch (request[2])
            {
                case 0x92:
                    return [0x7F, 0x22, 0x31];
                case 0x93:
                    return null;
                case 0x94:
                    return [0x62, 0xF1, 0x95, 0x12];
            }
        }

        return request[0] switch
        {
            0x10 => [0x50, request[1]],
            0x11 => [0x51, request[1]],
            0x22 => [0x62, request[1], request[2], 0x12, 0x34],
            0x27 => [0x67, request[1]],
            0x2E => [0x6E, request[1], request[2]],
            _ => [0x7F, request[0], 0x11],
        };
    }

    private static byte[] CcuResponse(byte[] request) => request[0] == 0x22
        ? [0x62, request[1], request[2], 0xAA, 0xBB]
        : [0x7F, request[0], 0x11];

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > 3000)
            {
                throw new TimeoutException("等待控制器离线回调超时");
            }

            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                () => frame.Continue = false);
            Dispatcher.PushFrame(frame);
        }
    }

    private sealed class FakeStepExecutor : IUdsStepExecutor
    {
        public required Func<TestSequenceRow, CancellationToken, Task<UdsStepResult>> Handler { get; init; }

        public Func<Task>? CloseHandler { get; init; }

        public int ExecuteCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task<UdsStepResult> ExecuteAsync(TestSequenceRow row, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Handler(row, cancellationToken);
        }

        public Task CloseAsync()
        {
            CloseCount++;
            return CloseHandler?.Invoke() ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingCanInstrument(ICanInstrument inner) : ICanInstrument
    {
        public InstrumentDescriptor Descriptor => inner.Descriptor;

        public InstrumentState State => inner.State;

        public List<CanFrame> SentFrames { get; } = [];

        public int ConnectCount { get; private set; }

        public int DisconnectCount { get; private set; }

        public event EventHandler<InstrumentStateChangedEventArgs>? StateChanged
        {
            add => inner.StateChanged += value;
            remove => inner.StateChanged -= value;
        }

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            await inner.ConnectAsync(cancellationToken);
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            DisconnectCount++;
            await inner.DisconnectAsync(cancellationToken);
        }

        public async Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
        {
            SentFrames.Add(frame);
            await inner.SendAsync(frame, cancellationToken);
        }

        public IAsyncEnumerable<CanFrame> ReceiveAsync(CancellationToken cancellationToken = default) =>
            inner.ReceiveAsync(cancellationToken);
    }

    private sealed class ScriptedUdsNode : IAsyncDisposable
    {
        private readonly ICanInstrument _endpoint;
        private readonly IsoTpTransport _transport;
        private readonly Func<byte[], byte[]?> _handler;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _task;

        public ScriptedUdsNode(
            ICanInstrument endpoint,
            IsoTpOptions options,
            Func<byte[], byte[]?> handler)
        {
            _endpoint = endpoint;
            _handler = handler;
            _endpoint.ConnectAsync().GetAwaiter().GetResult();
            _transport = new IsoTpTransport(endpoint, options);
            _task = Task.Run(RunAsync);
        }

        public List<byte[]> Requests { get; } = [];

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _endpoint.DisconnectAsync();
            try
            {
                await _task;
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }

            _cts.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[] request;
                try
                {
                    request = await _transport.ReceiveAsync(TimeSpan.FromMilliseconds(100), _cts.Token);
                }
                catch (TimeoutException)
                {
                    continue;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }

                Requests.Add(request);
                var response = _handler(request);
                if (response is not null)
                {
                    await _transport.SendAsync(response, _cts.Token);
                }
            }
        }
    }
}

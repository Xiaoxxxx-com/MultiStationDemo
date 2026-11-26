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
                    && row.Command.Contains("SID=22", StringComparison.Ordinal)).ToArray();

                Program.Check("NP12-01",
                    rows.Count == 50
                        && Ch1InitBeforeCcu(rows, "NP12_CAN_INIT_CH1")
                        && rows.Count(row => row.Kind == "Bat") == 2
                        && rows.Where(row => row.Kind == "Bat")
                            .All(row => row.Equal == "0,2")
                        && mainReads.Length == 20
                        && ccuRows.Length == 12
                        && ccuRows.All(row => row.Command.Contains("SID=22", StringComparison.Ordinal))
                        && mainReads.Any(row => row.Equal.Length > 0)
                        && mainReads.Any(row => row.Equal.Length == 0)
                        && rows[0].Stage == "StartSeq" && rows[0].Kind == "标题"
                        // 电源行已在脚本中取消使能（Q=F），加载时被跳过
                        && rows.All(row => !row.Code.Contains("POWER_", StringComparison.Ordinal))
                        && rows.Any(row => row.Code == "NP12_CAN_INIT_CH0"
                            && row.Command.Contains("CHANNEL=0", StringComparison.Ordinal)
                            && row.Command.Contains("BAUD=500000", StringComparison.Ordinal)
                            && row.Command.Contains("MODE=classic", StringComparison.Ordinal)
                            && row.Command.Contains("TERM=1", StringComparison.Ordinal)
                            && row.Command.Contains("REQID=180FF7A2", StringComparison.Ordinal)
                            && row.Command.Contains("RESPID=180FF7AA", StringComparison.Ordinal))
                        && rows.Any(row => row.Code == "NP12_CAN_INIT_CH1"
                            && row.Command.Contains("CHANNEL=1", StringComparison.Ordinal)
                            && row.Command.Contains("MODE=fd", StringComparison.Ordinal)
                            && row.Command.Contains("TERM=1", StringComparison.Ordinal)
                            && row.Command.Contains("REQID=7E4", StringComparison.Ordinal)
                            && row.Command.Contains("RESPID=7EC", StringComparison.Ordinal))
                        && rows.Where(row => row.Command.StartsWith("COM=CAN", StringComparison.Ordinal))
                            .All(row => !row.Command.Contains("MAIN=", StringComparison.Ordinal)
                                && !row.Command.Contains("CCU=", StringComparison.Ordinal))
                        && rows[^1].Stage == "EndSeq"
                        && rows.Any(row => row.Command.Contains(
                            "SID=27,SUB=52,DATA=12345678", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=2E,DID=F18B,DATA={flash_date}", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=22,DID=F191", StringComparison.Ordinal)
                            && row.Equal == "1211015801")
                        && rows.Any(row => row.Command.Contains(
                            "SID=2E,DID=F18C,DATA={F18C}", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=2E,DID=F191,DATA=1211015801", StringComparison.Ordinal))
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
                    && row.Command.Contains("SID=22", StringComparison.Ordinal)).ToArray();

                Program.Check("NP13-01",
                    rows.Count == 47
                        && Ch1InitBeforeCcu(rows, "NP13_CAN_INIT_CH1")
                        && rows.Count(row => row.Kind == "Bat") == 2
                        && rows.Where(row => row.Kind == "Bat")
                            .All(row => row.Equal == "0,2")
                        && mainReads.Length == 20
                        && ccuRows.Length == 9
                        && ccuRows.All(row => row.Command.Contains("SID=22", StringComparison.Ordinal))
                        // 电源行已在脚本中取消使能（Q=F），加载时被跳过
                        && rows.All(row => !row.Code.Contains("POWER_", StringComparison.Ordinal))
                        && rows.Any(row => row.Code == "NP13_CAN_INIT_CH0"
                            && row.Command.Contains("TERM=1", StringComparison.Ordinal))
                        && rows.Any(row => row.Code == "NP13_CAN_INIT_CH1"
                            && row.Command.Contains("MODE=fd", StringComparison.Ordinal)
                            && row.Command.Contains("TERM=1", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=27,SUB=52,DATA=12345678", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=2E,DID=F18B,DATA={flash_date}", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=22,DID=F191", StringComparison.Ordinal)
                            && row.Equal == "1211015801")
                        && rows.Any(row => row.Command.Contains(
                            "SID=2E,DID=F18C,DATA={F18C}", StringComparison.Ordinal))
                        && rows.Any(row => row.Command.Contains(
                            "SID=2E,DID=F191,DATA=1211015801", StringComparison.Ordinal))
                        && sequence.RequiresSupplierSerial,
                    $"rows={rows.Count} mainReads={mainReads.Length} ccuReads={ccuRows.Length} " +
                    $"f18c={sequence.RequiresSupplierSerial}");
            }
            catch (Exception ex)
            {
                Program.Check("NP13-01", false, ex.Message);
            }

            // NP12-15 工作簿自带仪器指令时按脚本顺序执行，不再自动补充
            var customRows = new List<TestSequenceRow>
            {
                CanRow("COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=0,MODE=classic,BAUD=500000," +
                    "DBIT=2000000,REQID=180FF7A2,RESPID=180FF7AA"),
                UdsRow("1003"),
            };
            var applied = FlashSequenceLoader.ApplyInstrumentSteps(model, customRows);
            Program.Check("NP12-15",
                applied.Count == 2
                    && applied[0].Command.StartsWith("COM=CAN", StringComparison.Ordinal)
                    && applied[1].Command.StartsWith("COM=UDS", StringComparison.Ordinal),
                $"count={applied.Count}");

            // NP12-22 配置复制到其他电脑后 FlashConfig.ini 缺失，不应阻断序列加载
            try
            {
                var withoutFactory = model with
                {
                    FactoryDataPath = Path.Combine(Path.GetTempPath(), "missing-FlashConfig.ini"),
                };
                var tolerated = FlashSequenceLoader.Load(withoutFactory);
                Program.Check("NP12-22", tolerated.Rows.Count == 50,
                    $"rows={tolerated.Rows.Count}");
            }
            catch (Exception ex)
            {
                Program.Check("NP12-22", false, ex.Message);
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

    /// <summary>CH1 初始化行必须排在第一条 CCU 步骤之前，保证 CCU 部分开始时才打开通道。</summary>
    private static bool Ch1InitBeforeCcu(IReadOnlyList<TestSequenceRow> rows, string initCode)
    {
        var initIndex = -1;
        var ccuIndex = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (initIndex < 0 && rows[i].Code == initCode)
            {
                initIndex = i;
            }

            if (ccuIndex < 0
                && rows[i].Command.StartsWith("COM=UDS", StringComparison.Ordinal)
                && rows[i].Command.Contains("CHANNEL=1", StringComparison.Ordinal))
            {
                ccuIndex = i;
            }
        }

        return initIndex >= 0 && ccuIndex > initIndex;
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
                lazy && session.Passed && factoryCalls == 1
                    && mainTester.ConnectCount == 1 && ccuTester.ConnectCount == 0
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

            // NP12-16 新格式 SID/SUB/DID/DATA 分段指令：由执行器组装成完整 PDU
            var sidKey = Execute(executor, SidRow("COM=UDS,CHANNEL=0,SID=27,SUB=52,DATA=12345678"));
            var sidSerial = Execute(executor, SidRow("COM=UDS,CHANNEL=0,SID=2E,DID=F18C,DATA={F18C}"));
            var sidRead = Execute(executor, SidRow("COM=UDS,CHANNEL=0,SID=22,DID=F190", expected: "1234"));
            Program.Check("NP12-16",
                sidKey.Passed
                    && mainNode.Requests[^3].SequenceEqual(
                        new byte[] { 0x27, 0x52, 0x12, 0x34, 0x56, 0x78 })
                    && sidSerial.Passed
                    && mainNode.Requests[^2].SequenceEqual(
                        new byte[]
                        {
                            0x2E, 0xF1, 0x8C,
                            0x31, 0x31, 0x32, 0x31, 0x38, 0x33, 0x30, 0x41,
                            0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x30, 0x32,
                        })
                    && sidRead.Passed && sidRead.Measured == "1234",
                $"key={sidKey.Passed}/{sidKey.Detail} serial={sidSerial.Passed}/{sidSerial.Detail} " +
                $"read={sidRead.Passed}/{sidRead.Measured}/{sidRead.Detail}");

            // NP12-17 F18B 写入当前制造日期：3 字节 BCD（YY MM DD）
            var dateWrite = Execute(executor, SidRow("COM=UDS,CHANNEL=0,SID=2E,DID=F18B,DATA={flash_date}"));
            var today = DateTime.Now;
            var dateBytes = new[]
            {
                (byte)(((today.Year % 100) / 10 << 4) | (today.Year % 10)),
                (byte)((today.Month / 10 << 4) | (today.Month % 10)),
                (byte)((today.Day / 10 << 4) | (today.Day % 10)),
            };
            Program.Check("NP12-17",
                dateWrite.Passed
                    && dateBytes.Length == 3
                    && mainNode.Requests[^1].SequenceEqual(
                        new byte[] { 0x2E, 0xF1, 0x8B }.Concat(dateBytes).ToArray()),
                $"passed={dateWrite.Passed} request={Convert.ToHexString(mainNode.Requests[^1])} " +
                $"date={Convert.ToHexString(dateBytes)} detail={dateWrite.Detail}");

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
                    // 每个通道只按自己的参数打开一次，不再连带重开另一路
                    && factoryCalls == 4
                    && mainTester.ConnectCount == 2 && ccuTester.ConnectCount == 2,
                $"ch0={can0.Passed}/{can0.Detail} ch1={can1.Passed}/{can1.Detail} " +
                $"badId={badId.Passed} factory={factoryCalls} " +
                $"main={mainTester.ConnectCount} ccu={ccuTester.ConnectCount}");

            // NP12-24 CAN1 单独使用 CAN FD：CH1 初始化行要按 FD 重新打开通道
            var fdModel = new ModelProfile
            {
                Name = "NP12-FD",
                Kind = ModelKind.Flash,
                RequestId = 0x180FF7A2,
                ResponseId = 0x180FF7AA,
                CcuRequestId = 0x07E4,
                CcuResponseId = 0x07EC,
                CanMode = "classic",
            };
            var fdBuses = new List<int>();
            var fdExecutor = new UdsStepExecutor(fdModel, channel =>
            {
                fdBuses.Add(channel);
                return new RecordingCanInstrument(new SimulatedCanBus().CreateEndpoint($"FD-{channel}"));
            });
            try
            {
                var fd0 = Execute(fdExecutor, CanRow(
                    "COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=0,MODE=classic,BAUD=500000,DBIT=2000000," +
                    "REQID=180FF7A2,RESPID=180FF7AA"));
                var fd1 = Execute(fdExecutor, CanRow(
                    "COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=1,MODE=fd,BAUD=500000,DBIT=2000000," +
                    "REQID=7E4,RESPID=7EC"));
                Program.Check("NP12-24",
                    fd0.Passed && fd1.Passed
                        && fd1.Detail.Contains("CH1 fd", StringComparison.Ordinal)
                        // CH0 行不再提前用默认参数打开 CH1；CH1 只按 FD 打开一次
                        && fdBuses.Count(ch => ch == 0) == 1
                        && fdBuses.Count(ch => ch == 1) == 1,
                    $"ch0={fd0.Detail} ch1={fd1.Detail} factory={string.Join(",", fdBuses)}");
            }
            finally
            {
                fdExecutor.CloseAsync().GetAwaiter().GetResult();
            }

            // NP12-25 脚本「重试」列：超时后按配置次数重发，用尽后仍判超时
            var retryTester = new RecordingCanInstrument(new SimulatedCanBus().CreateEndpoint("Retry-Main"));
            var retryCcu = new RecordingCanInstrument(new SimulatedCanBus().CreateEndpoint("Retry-Ccu"));
            var retryExecutor = new UdsStepExecutor(model, channel => channel == 0 ? retryTester : retryCcu);
            try
            {
                var retried = Execute(retryExecutor, UdsRow("22F190", timeout: "100", retry: "2"));
                Program.Check("NP12-25",
                    !retried.Passed && retried.Measured == "超时" && retryTester.SentFrames.Count == 3,
                    $"passed={retried.Passed} measured={retried.Measured} " +
                    $"sends={retryTester.SentFrames.Count}");
            }
            finally
            {
                retryExecutor.CloseAsync().GetAwaiter().GetResult();
            }

            // NP12-26 机种配置的默认重试次数：脚本「重试」列为 0 时生效
            var defaultTester = new RecordingCanInstrument(new SimulatedCanBus().CreateEndpoint("RetryDefault-Main"));
            var defaultCcu = new RecordingCanInstrument(new SimulatedCanBus().CreateEndpoint("RetryDefault-Ccu"));
            var defaultExecutor = new UdsStepExecutor(
                model with { UdsRetryCount = 1, UdsRequestIntervalMs = 0 },
                channel => channel == 0 ? defaultTester : defaultCcu);
            try
            {
                var defaultRetried = Execute(defaultExecutor, UdsRow("22F190", timeout: "100"));
                Program.Check("NP12-26",
                    !defaultRetried.Passed && defaultTester.SentFrames.Count == 2,
                    $"passed={defaultRetried.Passed} sends={defaultTester.SentFrames.Count}");
            }
            finally
            {
                defaultExecutor.CloseAsync().GetAwaiter().GetResult();
            }
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

        // NP12-19 新格式 SID= 写法的 UDS 步骤必须真实执行，不能走随机模拟
        var sidFailure = new FakeStepExecutor
        {
            Handler = (_, _) => Task.FromResult(new UdsStepResult(false, "超时", "未接 ECU")),
        };
        using (var controller = new SingleStationTestController(
                   [SidRow("COM=UDS,CHANNEL=0,SID=10,SUB=03")], sidFailure))
        {
            controller.NextStep();
            PumpUntil(() => !controller.IsRunning);
            Program.Check("NP12-19",
                sidFailure.ExecuteCount == 1 && controller.Steps[0].Result == "NG"
                    && controller.Verdict == "NG",
                $"executed={sidFailure.ExecuteCount} result={controller.Steps[0].Result} " +
                $"verdict={controller.Verdict}");
        }

        // NP12-20 CAN/电源等仪器步骤必须交给真实执行器，未接硬件判 NG
        var canFailure = new FakeStepExecutor
        {
            Handler = (_, _) => Task.FromResult(new UdsStepResult(false, "失败", "CAN 未连接")),
        };
        using (var controller = new SingleStationTestController(
                   [CanRow("COM=CAN,DEVICE=200U,INDEX=0,CHANNEL=0,MODE=classic,BAUD=500000," +
                       "DBIT=2000000,REQID=180FF7A2,RESPID=180FF7AA")], canFailure))
        {
            controller.NextStep();
            PumpUntil(() => !controller.IsOperationRunning
                && controller.Steps[0].Result is "PASS" or "NG");
            Program.Check("NP12-20",
                canFailure.ExecuteCount == 1 && controller.Steps[0].Result == "NG",
                $"executed={canFailure.ExecuteCount} result={controller.Steps[0].Result}");
        }

        // NP12-21 无设备指令的步骤标记为跳过，不再随机生成 PASS/NG
        var skippedRow = new TestSequenceRow(
            "MainSeq", "1", "SKIP_NO_DEVICE", "无设备步骤", "Action", "Num", "占位",
            "", "", "", "", "", "3000", "0", "Abort", "", "T", "T", "无 COM 指令");
        using (var controller = new SingleStationTestController([skippedRow]))
        {
            controller.NextStep();
            controller.NextStep();
            PumpUntil(() => !controller.IsRunning);
            Program.Check("NP12-21",
                controller.Steps[0].Result == "已跳过" && controller.Verdict == "--",
                $"result={controller.Steps[0].Result} verdict={controller.Verdict} " +
                $"pass={controller.PassCount} ng={controller.NgCount}");
        }

        // NP12-23 关闭「NG 后停止」后，判 NG 仍继续执行后续步骤，最终结果仍为 NG
        var continueExecutor = new FakeStepExecutor
        {
            Handler = (_, _) => Task.FromResult(new UdsStepResult(false, "NRC=0x31", "离线 NG")),
        };
        using (var controller = new SingleStationTestController(
                   [UdsRow("1003"), UdsRow("2751"), UdsRow("22F190")], continueExecutor))
        {
            controller.StopOnNg = false;
            controller.NextStep();
            PumpUntil(() => controller.Steps[0].Result is "PASS" or "NG");
            controller.NextStep();
            PumpUntil(() => controller.Steps[1].Result is "PASS" or "NG");
            controller.NextStep();
            PumpUntil(() => controller.Steps[2].Result is "PASS" or "NG");
            controller.NextStep();
            PumpUntil(() => !controller.IsRunning);
            Program.Check("NP12-23",
                continueExecutor.ExecuteCount == 3
                    && controller.Steps.All(step => step.Result == "NG")
                    && controller.Verdict == "NG",
                $"executed={continueExecutor.ExecuteCount} verdict={controller.Verdict} " +
                $"results={string.Join(",", controller.Steps.Select(step => step.Result))}");
        }

        // NP12-18 Bat 步骤：脚本 PASS= 优先于机种配置，未配置时仍用机种退出码
        var batPath = Path.Combine(Path.GetTempPath(), $"multistation-bat-{Guid.NewGuid():N}.bat");
        File.WriteAllText(batPath, "@echo off\r\nexit /b 2\r\n");
        try
        {
            var scriptPassRow = new TestSequenceRow(
                "PreSeq", "1", "BAT_SCRIPT_PASS", "Bat 退出码", "Bat", "", "AutoProgramFlash",
                "", "", "0,2", "bat", $"FILE={batPath}", "10000", "0", "Abort", "", "T", "T",
                "脚本评判标准(等值)=0,2");
            string scriptResult;
            using (var controller = new SingleStationTestController([scriptPassRow], batSuccessExitCodes: "0"))
            {
                controller.NextStep();
                PumpUntil(() => !controller.IsOperationRunning
                    && controller.Steps[0].Result is "PASS" or "NG");
                scriptResult = controller.Steps[0].Result;
            }

            string fallbackResult;
            var fallbackRow = scriptPassRow with { Equal = "" };
            using (var controller = new SingleStationTestController([fallbackRow], batSuccessExitCodes: "0"))
            {
                controller.NextStep();
                PumpUntil(() => !controller.IsOperationRunning
                    && controller.Steps[0].Result is "PASS" or "NG");
                fallbackResult = controller.Steps[0].Result;
            }

            Program.Check("NP12-18",
                scriptResult == "PASS" && fallbackResult == "NG",
                $"scriptPass={scriptResult} modelDefault={fallbackResult}");
        }
        finally
        {
            File.Delete(batPath);
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
        string? data = null,
        string retry = "0")
    {
        var channel = string.Equals(target, "CCU", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var command = $"COM=UDS,CHANNEL={channel},PDU={pdu}";
        if (data is not null)
        {
            command += $",DATA={data}";
        }

        return new TestSequenceRow(
            "MainSeq", "1", $"TEST_{pdu}", pdu, "Test", "Hex", pdu,
            "", "", expected, "can_zlg_card0", command, timeout, retry, "Abort", "", "T", "T", "");
    }

    private static TestSequenceRow SidRow(string command, string expected = "") => new(
        "MainSeq", "1", "TEST_SID", "SID 分段指令", "Test", "Hex", "SID",
        "", "", expected, "can_zlg_card0", command, "3000", "0", "Abort", "", "T", "T", "");

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

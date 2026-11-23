using System.IO;
using MultiStation.Platform.Core.Production;
using MultiStation.Ui.Desktop;

namespace MultiStation.Ui.UnitTests;

/// <summary>机种配置的单元测试：默认机种、JSON 持久化与当前机种记录。</summary>
internal static class ModelTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "multistation-models-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelProfileStore(directory);
            var defaults = store.Load();

            var model = new ModelProfile
            {
                Name = "MTCU-A",
                FirmwarePath = @"D:\firmware\a.bin",
                CanMode = "fd",
                BaudRate = 500_000,
                DataBaudRate = 2_000_000,
                RequestId = 0x7E1,
                ResponseId = 0x7E9,
                MemoryAddress = 0x0801_0000,
                SecurityLevel = 0x11,
                Fingerprint = "MTCU-A",
                EraseMemory = false,
                Remark = "机种切换测试",
            };

            store.Save([model]);
            store.SaveSelected(model.Name);

            var reloaded = new ModelProfileStore(directory);
            var models = reloaded.Load();
            var loaded = models.Single(m => m.Name == model.Name);
            var defaultOrder = string.Join(" > ", defaults.Select(m => m.Name));
            var migratedOrder = string.Join(" > ", models.Select(m => m.Name));
            var repeatedOrder = string.Join(" > ", reloaded.Load().Select(m => m.Name));

            Program.Check("MB-01",
                defaults.Count == 4
                    && defaultOrder == "NP12 > NP13 > ECU 烧录 > 通用机种"
                    && migratedOrder == "NP12 > NP13 > ECU 烧录 > MTCU-A"
                    && repeatedOrder == migratedOrder
                    && models.Any(m => m.Kind == ModelKind.Flash)
                    && loaded.Name == model.Name
                    && loaded.FirmwarePath == model.FirmwarePath
                    && loaded.IsCanFd
                    && loaded.RequestId == 0x7E1
                    && loaded.ResponseId == 0x7E9
                    && loaded.MemoryAddress == 0x0801_0000
                    && loaded.SecurityLevel == 0x11
                    && !loaded.EraseMemory
                    && reloaded.LoadSelected() == model.Name,
                $"defaults={defaults.Count} models={models.Count} name={loaded.Name} " +
                $"request=0x{loaded.RequestId:X3} address=0x{loaded.MemoryAddress:X8}");

            var np12 = models.Single(m => m.Name == "NP12");
            var np13 = models.Single(m => m.Name == "NP13");
            Program.Check("MB-03",
                np12.RequestId == 0x180FF7A2 && np12.ResponseId == 0x180FF7AA
                    && np12.CcuRequestId == 0x07E4 && np12.CcuResponseId == 0x07EC
                    && np13.RequestId == np12.RequestId && np13.ResponseId == np12.ResponseId
                    && np13.CcuRequestId == np12.CcuRequestId
                    && np13.CcuResponseId == np12.CcuResponseId
                    && !np12.IsCanFd && !np13.IsCanFd
                    && np12.BaudRate == 500_000 && np13.BaudRate == 500_000
                    && Path.GetFileName(np12.FlashSequencePath) == "NP12.csv"
                    && Path.GetFileName(np12.FactoryDataPath) == "FlashConfig.ini"
                    && Path.GetFileName(np12.FlashBatchPath) == "NP12.bat"
                    && Path.GetFileName(np13.FlashSequencePath) == "NP13.csv"
                    && Path.GetFileName(np13.FactoryDataPath) == "FlashConfig.ini"
                    && Path.GetFileName(np13.FlashBatchPath) == "NP13.bat",
                $"NP12/NP13 main=0x{np12.RequestId:X8}/0x{np12.ResponseId:X8} " +
                $"ccu=0x{np12.CcuRequestId:X3}/0x{np12.CcuResponseId:X3}");

            Program.Check("MB-05",
                np12.PowerVoltage == 12 && np13.PowerVoltage == 24
                    && np12.PowerCurrentLimit > 0 && np13.PowerCurrentLimit > 0
                    && string.IsNullOrEmpty(np12.PowerSupplyPort)
                    && np12.PowerSerialSettings == "9600,8,N,1"
                    && np12.PowerChannel == 1 && np13.PowerChannel == 1,
                $"np12={np12.PowerVoltage}V np13={np13.PowerVoltage}V " +
                $"current={np12.PowerCurrentLimit}A port={np12.PowerSupplyPort ?? "-"} " +
                $"serial={np12.PowerSerialSettings} channel=CH{np12.PowerChannel}");

            Program.Check("MB-06",
                !string.IsNullOrWhiteSpace(np12.ReportDirectory)
                    && np12.ReportFileNamePattern == "{Model}_{SN}_{DateTime}.txt"
                    && np13.ReportFileNamePattern == np12.ReportFileNamePattern,
                $"reportDir={np12.ReportDirectory} pattern={np12.ReportFileNamePattern}");

            Program.Check("MB-04",
                App.SelectRelativeModel(defaults, "NP12", 1).Name == "NP13"
                    && App.SelectRelativeModel(defaults, "NP12", -1).Name == "通用机种"
                    && App.SelectRelativeModel(defaults, "通用机种", 1).Name == "NP12"
                    && App.SelectRelativeModel(defaults, "NP13", -1).Name == "NP12"
                    && App.SelectRelativeModel(defaults, "NP13", 1).Name == "ECU 烧录"
                    && App.SelectRelativeModel(defaults, "ECU 烧录", -1).Name == "NP13"
                    && App.SelectRelativeModel(models, "ECU 烧录", 1).Name == "MTCU-A"
                    && App.SelectRelativeModel(defaults, "已删除机种", 1).Name == "NP12",
                "NP12 首页、NP13 双向导航、首尾循环、自定义机种及缺失回退");

            // MB-02 ECU 烧录机种使用独立测试序列（与通用序列不同，全部来自 UDS 烧录流程）
            var flashRows = FlashSequenceData.Rows;
            var commonRows = TestSequenceData.Rows;
            Program.Check("MB-02",
                flashRows.Length > 0
                    && flashRows.Any(row => row.Code == "FLASH_EXT_SESSION")
                    && flashRows.Any(row => row.Command.Contains("COM=UDS", StringComparison.Ordinal))
                    && !commonRows.Any(row => row.Code == "FLASH_EXT_SESSION"),
                $"flashRows={flashRows.Length} commonRows={commonRows.Length}");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}

using System.IO;
using MultiStation.Platform.Core.Production;

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
            var loaded = models[0];

            Program.Check("MB-01",
                defaults.Count == 1
                    && models.Count == 1
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

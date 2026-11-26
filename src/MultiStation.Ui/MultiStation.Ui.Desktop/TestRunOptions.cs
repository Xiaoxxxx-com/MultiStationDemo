using System.IO;
using System.Text.Json;

namespace MultiStation.Ui.Desktop;

/// <summary>测试运行选项的本地记忆：NG 后是停止还是继续执行后续步骤。</summary>
public sealed record TestRunOptions
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiStationDemo",
        "test-run.json");

    /// <summary>true=NG 后停止（默认，保持原有行为）；false=NG 后继续执行后续步骤。</summary>
    public bool StopOnNg { get; init; } = true;

    /// <summary>是否把 CAN 原始报文记录到 logs\raw 下（默认开启）。</summary>
    public bool LogRawFrames { get; init; } = true;

    public static TestRunOptions Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<TestRunOptions>(File.ReadAllText(FilePath))
                    ?? new TestRunOptions()
                : new TestRunOptions();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new TestRunOptions();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (IOException)
        {
            // 保存失败时不影响本次设置生效
        }
    }
}

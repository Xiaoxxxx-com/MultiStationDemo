using System.IO;
using System.Text.Json;

namespace MultiStation.Ui.Desktop;

/// <summary>
/// 基础信息勾选项的本地记忆：在「选择基础信息」中确认后保存，下次启动自动恢复。
/// </summary>
public sealed record BasicInfoPreferences
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MultiStationDemo",
        "basic-info.json");

    public bool UseSn { get; init; } = true;

    public bool UseCarrierId { get; init; } = true;

    public bool UseShellCode { get; init; } = true;

    public static BasicInfoPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<BasicInfoPreferences>(File.ReadAllText(FilePath))
                    ?? new BasicInfoPreferences()
                : new BasicInfoPreferences();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new BasicInfoPreferences();
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
            // 保存失败时不影响本次勾选生效
        }
    }
}

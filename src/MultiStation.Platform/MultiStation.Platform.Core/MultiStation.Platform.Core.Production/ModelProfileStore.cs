using System.IO;
using System.Text.Json;

namespace MultiStation.Platform.Core.Production;

/// <summary>
/// 机种配置存储：models.json 保存全部机种，selected-model.txt 记录当前使用的机种。
/// 默认目录为 %LOCALAPPDATA%\MultiStationDemo。
/// </summary>
public sealed class ModelProfileStore
{
    public const string FileName = "models.json";

    private const string SelectedFileName = "selected-model.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _directory;
    private readonly string _filePath;
    private readonly string _selectedPath;

    public ModelProfileStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MultiStationDemo");
        _filePath = Path.Combine(_directory, FileName);
        _selectedPath = Path.Combine(_directory, SelectedFileName);
    }

    public string FilePath => _filePath;

    /// <summary>读取全部机种；文件缺失或损坏时返回默认机种并落盘。</summary>
    public IReadOnlyList<ModelProfile> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var models = JsonSerializer.Deserialize<List<ModelProfile>>(
                    File.ReadAllText(_filePath), JsonOptions);
                if (models is { Count: > 0 })
                {
                    return models;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 配置损坏时退回默认机种，避免影响启动
        }

        var defaults = CreateDefaults();
        Save(defaults);
        return defaults;
    }

    public void Save(IEnumerable<ModelProfile> models)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_filePath, JsonSerializer.Serialize(models.ToList(), JsonOptions));
    }

    /// <summary>读取上次使用的机种名称；不存在时返回 null。</summary>
    public string? LoadSelected()
    {
        try
        {
            return File.Exists(_selectedPath) ? File.ReadAllText(_selectedPath).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void SaveSelected(string modelName)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_selectedPath, modelName);
    }

    public static IReadOnlyList<ModelProfile> CreateDefaults() =>
    [
        new ModelProfile { Name = "通用机种", Remark = "默认烧录参数，可在机种切换中修改" },
    ];
}

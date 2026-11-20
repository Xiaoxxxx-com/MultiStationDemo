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
                    // 老版本配置没有 ECU 烧录机种，升级后自动补齐内置机种
                    if (EnsureBuiltInModels(models))
                    {
                        Save(models);
                    }

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

    /// <summary>保证内置 ECU 烧录机种始终存在；返回是否发生变更。</summary>
    private static bool EnsureBuiltInModels(List<ModelProfile> models)
    {
        if (models.Any(model => model.Kind == ModelKind.Flash))
        {
            return false;
        }

        models.Add(CreateFlashModel());
        return true;
    }

    public static IReadOnlyList<ModelProfile> CreateDefaults() =>
    [
        new ModelProfile { Name = "通用机种", Remark = "通用测试机种，可在机种切换中新增并维护" },
        CreateFlashModel(),
    ];

    /// <summary>内置 ECU 烧录机种：共用测试界面，切换后加载烧录测试序列。</summary>
    private static ModelProfile CreateFlashModel() => new()
    {
        Name = "ECU 烧录",
        Kind = ModelKind.Flash,
        Remark = "独立烧录机种：共用测试界面，切换后加载烧录测试序列",
    };
}

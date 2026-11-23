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
                    var changed = EnsureBuiltInModels(models);
                    // 老配置缺少电源参数时，按机种补齐 NP12 12V / NP13 24V
                    changed |= EnsurePowerDefaults(models);
                    if (changed)
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

    /// <summary>保证内置烧录机种存在并统一显示顺序；返回是否发生变更。</summary>
    private static bool EnsureBuiltInModels(List<ModelProfile> models)
    {
        var changed = false;
        if (!models.Any(model => model.Kind == ModelKind.Flash && model.Name == "ECU 烧录"))
        {
            models.Add(CreateFlashModel());
            changed = true;
        }

        if (!models.Any(model => model.Kind == ModelKind.Flash && model.Name == "NP12"))
        {
            models.Add(CreateNp12Model());
            changed = true;
        }

        if (!models.Any(model => model.Kind == ModelKind.Flash && model.Name == "NP13"))
        {
            models.Add(CreateNp13Model());
            changed = true;
        }

        var ordered = models.OrderBy(ModelDisplayOrder).ToList();
        if (!models.SequenceEqual(ordered))
        {
            models.Clear();
            models.AddRange(ordered);
            changed = true;
        }

        return changed;
    }

    /// <summary>老配置缺少电源参数时，按机种补齐 NP12 12V / NP13 24V 与默认限流。</summary>
    private static bool EnsurePowerDefaults(List<ModelProfile> models)
    {
        var changed = false;
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            if (model.Kind != ModelKind.Flash || string.IsNullOrWhiteSpace(model.FlashSequencePath))
            {
                continue;
            }

            var voltage = model.PowerVoltage > 0
                ? model.PowerVoltage
                : model.Name.Equals("NP12", StringComparison.OrdinalIgnoreCase)
                    ? 12
                    : model.Name.Equals("NP13", StringComparison.OrdinalIgnoreCase) ? 24 : 0;
            var current = model.PowerCurrentLimit > 0 ? model.PowerCurrentLimit : 3;
            var serial = string.IsNullOrWhiteSpace(model.PowerSerialSettings)
                ? "9600,8,N,1"
                : model.PowerSerialSettings;
            var channel = model.PowerChannel is 1 or 2 ? model.PowerChannel : 1;
            if (voltage != model.PowerVoltage || current != model.PowerCurrentLimit
                || serial != model.PowerSerialSettings || channel != model.PowerChannel)
            {
                models[index] = model with
                {
                    PowerVoltage = voltage,
                    PowerCurrentLimit = current,
                    PowerSerialSettings = serial,
                    PowerChannel = channel,
                };
                changed = true;
            }
        }

        return changed;
    }

    private static int ModelDisplayOrder(ModelProfile model) => (model.Kind, model.Name) switch
    {
        (ModelKind.Flash, "NP12") => 0,
        (ModelKind.Flash, "NP13") => 1,
        (ModelKind.Flash, "ECU 烧录") => 2,
        (ModelKind.Test, "通用机种") => 3,
        _ => 4,
    };

    public static IReadOnlyList<ModelProfile> CreateDefaults() =>
    [
        CreateNp12Model(),
        CreateNp13Model(),
        CreateFlashModel(),
        new ModelProfile { Name = "通用机种", Remark = "通用测试机种，可在机种切换中新增并维护" },
    ];

    /// <summary>内置 ECU 烧录机种：共用测试界面，切换后加载烧录测试序列。</summary>
    private static ModelProfile CreateFlashModel() => new()
    {
        Name = "ECU 烧录",
        Kind = ModelKind.Flash,
        Remark = "独立烧录机种：共用测试界面，切换后加载烧录测试序列",
    };

    private static ModelProfile CreateNp12Model() => CreateWorkbookFlashModel("NP12");
    private static ModelProfile CreateNp13Model() => CreateWorkbookFlashModel("NP13");

    private static ModelProfile CreateWorkbookFlashModel(string name)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "E3Flash");
        return new ModelProfile
        {
            Name = name,
            Kind = ModelKind.Flash,
            CanMode = "classic",
            RequestId = 0x180FF7A2,
            ResponseId = 0x180FF7AA,
            CcuRequestId = 0x07E4,
            CcuResponseId = 0x07EC,
            FlashSequencePath = Path.Combine(root, "Config", $"{name}.csv"),
            FactoryDataPath = Path.Combine(root, "FlashConfig.ini"),
            FlashBatchPath = Path.Combine(root, $"{name}.bat"),
            // NP12 烧录前上电 12V，NP13 为 24V；串口由现场在机种切换中配置
            PowerVoltage = name.Equals("NP12", StringComparison.OrdinalIgnoreCase)
                ? 12
                : name.Equals("NP13", StringComparison.OrdinalIgnoreCase) ? 24 : 0,
            PowerCurrentLimit = 3,
            ReportDirectory = Path.Combine(root, "Report"),
            ReportFileNamePattern = "{Model}_{SN}_{DateTime}.txt",
            Remark = $"{name}：AutoLoader 基础刷写后执行主 ECU/CCU 真实 UDS 序列",
        };
    }
}

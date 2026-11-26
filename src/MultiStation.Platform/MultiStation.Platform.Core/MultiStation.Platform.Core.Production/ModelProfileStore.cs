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

    private const string BatExitMigrationFile = "bat-exit-codes.migrated";

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
                    // 旧版本写入的 0 一次性升级为 0,2（AutoLoader 刷写成功可能返回 2）
                    changed |= MigrateBatExitCodes(models);
                    // 配置从其他电脑复制过来时，绝对路径按当前桌面重新定位
                    changed |= EnsurePortablePaths(models);
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

        var defaults = CreateDefaults().ToList();
        // 默认值同样做一次便携解析，避免首次运行时指向原电脑的绝对路径
        EnsurePortablePaths(defaults);
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
            // AutoLoader 刷写成功时可能返回 2，NP12/NP13 默认接受 0 与 2
            var successCodes = !string.IsNullOrWhiteSpace(model.BatSuccessExitCodes)
                ? model.BatSuccessExitCodes
                : model.Name.Equals("NP12", StringComparison.OrdinalIgnoreCase)
                    || model.Name.Equals("NP13", StringComparison.OrdinalIgnoreCase)
                        ? "0,2"
                        : "0";
            if (voltage != model.PowerVoltage || current != model.PowerCurrentLimit
                || serial != model.PowerSerialSettings || channel != model.PowerChannel
                || successCodes != model.BatSuccessExitCodes)
            {
                models[index] = model with
                {
                    PowerVoltage = voltage,
                    PowerCurrentLimit = current,
                    PowerSerialSettings = serial,
                    PowerChannel = channel,
                    BatSuccessExitCodes = successCodes,
                };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// 一次性把 NP12/NP13 的成功退出码从 0 升级为 0,2；
    /// 用标记文件避免覆盖用户后续手动修改。
    /// </summary>
    private bool MigrateBatExitCodes(List<ModelProfile> models)
    {
        var marker = Path.Combine(_directory, BatExitMigrationFile);
        if (File.Exists(marker))
        {
            return false;
        }

        var changed = false;
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            if (model.Kind != ModelKind.Flash || string.IsNullOrWhiteSpace(model.FlashSequencePath)
                || !(model.Name.Equals("NP12", StringComparison.OrdinalIgnoreCase)
                    || model.Name.Equals("NP13", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(model.BatSuccessExitCodes)
                || model.BatSuccessExitCodes.Trim() == "0")
            {
                models[index] = model with { BatSuccessExitCodes = "0,2" };
                changed = true;
            }
        }

        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(marker, DateTime.Now.ToString("s"));
        }
        catch (IOException)
        {
            // 标记写入失败时下次启动会再判断一次，不影响本次结果
        }

        return changed;
    }

    /// <summary>
    /// 配置迁移到其他电脑后，models.json 里保存的绝对路径仍指向原用户目录。
    /// 这里把含 E3Flash 的路径重定位到当前桌面，并优先使用实际存在的文件。
    /// </summary>
    private static bool EnsurePortablePaths(List<ModelProfile> models)
    {
        var changed = false;
        for (var index = 0; index < models.Count; index++)
        {
            var model = models[index];
            if (model.Kind != ModelKind.Flash || string.IsNullOrWhiteSpace(model.FlashSequencePath))
            {
                continue;
            }

            var sequence = ResolveFlashFile(model.FlashSequencePath);
            var batch = ResolveFlashFile(model.FlashBatchPath);
            var factory = ResolveFlashFile(model.FactoryDataPath);
            var report = ResolveReportDirectory(model.ReportDirectory);
            if (sequence != model.FlashSequencePath || batch != model.FlashBatchPath
                || factory != model.FactoryDataPath || report != model.ReportDirectory)
            {
                models[index] = model with
                {
                    FlashSequencePath = sequence,
                    FlashBatchPath = batch,
                    FactoryDataPath = factory,
                    ReportDirectory = report,
                };
                changed = true;
            }
        }

        return changed;
    }

    private static string FlashRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "E3Flash");

    private static string ResolveFlashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var fileName = Path.GetFileName(path);

        // 桌面 MultiStationDemo\Script 为权威脚本目录；配置在别处或原文件改名时使用同名脚本
        var scriptCandidate = FindScriptFile(ScriptRoot(), fileName);
        if (scriptCandidate is not null
            && !string.Equals(scriptCandidate, path, StringComparison.OrdinalIgnoreCase))
        {
            return scriptCandidate;
        }

        var missing = !File.Exists(path);
        // 旧配置指向 E3Flash，或原文件已不存在时，优先使用脚本目录下的同名脚本
        var preferScript = missing
            || path.Contains("E3Flash" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (preferScript)
        {
            // 桌面脚本目录未命中时，回退到程序目录\Script（便携部署）
            var candidate = FindScriptFile(Path.Combine(AppContext.BaseDirectory, "Script"), fileName);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        if (!missing)
        {
            return path;
        }

        // 2) 旧配置仍指向 E3Flash 时按当前桌面重定位
        var rebased = RebaseToFlashRoot(path);
        if (File.Exists(rebased))
        {
            return rebased;
        }

        foreach (var candidate in new[]
        {
            Path.Combine(FlashRoot(), fileName),
            Path.Combine(FlashRoot(), "Config", fileName),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return rebased;
    }

    /// <summary>在脚本目录中查找文件，找不到时尝试 .csv / .xlsx 互为别名。</summary>
    private static string? FindScriptFile(string root, string fileName)
    {
        var exact = Path.Combine(root, fileName);
        if (File.Exists(exact))
        {
            return exact;
        }

        var alternateName = AlternateExtension(fileName);
        if (!string.Equals(alternateName, fileName, StringComparison.OrdinalIgnoreCase))
        {
            var alternate = Path.Combine(root, alternateName);
            if (File.Exists(alternate))
            {
                return alternate;
            }
        }

        return null;
    }

    private static string AlternateExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase))
        {
            return Path.ChangeExtension(fileName, ".xlsx");
        }

        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            ? Path.ChangeExtension(fileName, ".csv")
            : fileName;
    }

    private static string ScriptRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "MultiStationDemo",
        "Script");

    private static string ResolveReportDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
        {
            return path ?? string.Empty;
        }

        return RebaseToFlashRoot(path);
    }

    private static string RebaseToFlashRoot(string path)
    {
        var marker = "E3Flash" + Path.DirectorySeparatorChar;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return path;
        }

        return Path.Combine(FlashRoot(), path[(index + marker.Length)..]);
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

    /// <summary>
    /// 默认脚本路径：优先程序目录下的 Script（随程序复制即可用），
    /// 其次工作区桌面的 Script（开发机）；都不存在时返回程序目录路径便于提示。
    /// </summary>
    private static string DefaultScriptPath(string name)
    {
        var fileName = $"{name}.xlsx";
        var appScript = Path.Combine(AppContext.BaseDirectory, "Script", fileName);
        if (File.Exists(appScript))
        {
            return appScript;
        }

        var desktopScript = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "MultiStationDemo",
            "Script",
            fileName);
        return File.Exists(desktopScript) ? desktopScript : appScript;
    }

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
            FlashSequencePath = DefaultScriptPath(name),
            FactoryDataPath = Path.Combine(root, "FlashConfig.ini"),
            FlashBatchPath = Path.Combine(root, $"{name}.bat"),
            // NP12 烧录前上电 12V，NP13 为 24V；串口由现场在机种切换中配置
            PowerVoltage = name.Equals("NP12", StringComparison.OrdinalIgnoreCase)
                ? 12
                : name.Equals("NP13", StringComparison.OrdinalIgnoreCase) ? 24 : 0,
            PowerCurrentLimit = 3,
            BatSuccessExitCodes = "0,2",
            ReportDirectory = Path.Combine(root, "Report"),
            ReportFileNamePattern = "{Model}_{SN}_{DateTime}.txt",
            Remark = $"{name}：AutoLoader 基础刷写后执行主 ECU/CCU 真实 UDS 序列",
        };
    }
}

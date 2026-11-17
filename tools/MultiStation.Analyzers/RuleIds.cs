using System;

namespace MultiStation.Analyzers;

/// <summary>
/// 分析器诊断编号与 Ruler 规则编号的映射。
/// Ruler 目录中的规则文件为唯一依据，本类只做引用。
/// </summary>
public static class RuleIds
{
    /// <summary>R-04 / R-05：命名空间必须与物理目录一致，且不得使用泛化层级名。</summary>
    public const string NamingConvention = "MS0001";

    /// <summary>R-10：模块之间禁止直接引用，只允许引用契约族。</summary>
    public const string ModuleReference = "MS0002";

    /// <summary>C-13 / M-11：执行循环内禁止阻塞调用（.Result / .Wait() / Thread.Sleep）。</summary>
    public const string BlockingCall = "MS0003";

    /// <summary>C-12：投递必须检查 DeliveryResult，不得丢弃返回值。</summary>
    public const string UncheckedDeliveryResult = "MS0004";

    /// <summary>M-07：模块总类不得重写模板方法。</summary>
    public const string TemplateMethodOverride = "MS0005";

    /// <summary>M-03 / M-04：Part 子类必须继承 ModulePart，且一个子类只承担一个部分。</summary>
    public const string PartStructure = "MS0006";

    /// <summary>C-03：超过 4KB 的负载必须走 BufferHandle，不得直接放入消息体。</summary>
    public const string LargePayload = "MS0007";

    /// <summary>规则来源文件（对应 Ruler 目录）。</summary>
    public static string RuleSource(string diagnosticId) => diagnosticId switch
    {
        NamingConvention => "Ruler/01_目录与命名规则.md（R-04、R-05）",
        ModuleReference => "Ruler/01_目录与命名规则.md（R-10）",
        BlockingCall => "Ruler/03_通信与调度规则.md（C-13）、02_模块编写规则.md（M-11）",
        UncheckedDeliveryResult => "Ruler/03_通信与调度规则.md（C-12）",
        TemplateMethodOverride => "Ruler/02_模块编写规则.md（M-07）",
        PartStructure => "Ruler/02_模块编写规则.md（M-03、M-04）",
        LargePayload => "Ruler/03_通信与调度规则.md（C-03）",
        _ => "Ruler/00_规则索引.md"
    };

    /// <summary>Part 子类允许的基类型名。</summary>
    public const string PartBaseType = "ModulePart";

    /// <summary>模块总类允许的基类型名。</summary>
    public const string ModuleBaseType = "ModuleBase";

    /// <summary>禁止作为命名空间层级或类型名后缀的泛化词（R-04）。</summary>
    public static readonly string[] ForbiddenSegments =
    {
        "Utils", "Util", "Common", "Helper", "Helpers", "Misc", "Base"
    };

    /// <summary>投递方法名：返回 DeliveryResult，必须被检查。</summary>
    public static readonly string[] DeliveryMethods = { "SendAsync", "PublishAsync" };

    /// <summary>请求方法名：只允许在非执行循环线程调用。</summary>
    public static readonly string[] RequestMethods = { "RequestAsync", "QueryOutcomeAsync" };

    /// <summary>契约族根命名前缀。</summary>
    public const string ContractsPrefix = "MultiStation.Platform.Contracts";

    /// <summary>模块程序集统一前缀。模块目录形如 MultiStation.Line.Station，不允许插入 .Modules 等中间段。</summary>
    public const string ModuleRootPrefix = "MultiStation.";

    /// <summary>基础设施程序集：不属于业务模块，互引不受 R-10 约束。</summary>
    public static readonly string[] InfrastructureAssemblies =
    {
        "MultiStation.Platform.Contracts",
        "MultiStation.Platform.Core",
        "MultiStation.Platform.Config",
        "MultiStation.Platform.Host",
        "MultiStation.Platform.Simulation",
        "MultiStation.Data.Persistence",
        "MultiStation.Data.Telemetry",
        "MultiStation.Ui.Desktop",
        "MultiStation.Ui.Demo",
        "MultiStation.Analyzers"
    };

    /// <summary>判断程序集名是否为业务模块（R-10 的约束对象）。</summary>
    public static bool IsModuleAssembly(string? assemblyName)
    {
        if (assemblyName is null || assemblyName.Length == 0)
        {
            return false;
        }

        if (!assemblyName.StartsWith(ModuleRootPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        // 契约族（含分层契约）不算业务模块
        if (assemblyName.StartsWith(ContractsPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var infrastructure in InfrastructureAssemblies)
        {
            if (string.Equals(assemblyName, infrastructure, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

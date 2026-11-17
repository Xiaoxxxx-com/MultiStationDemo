using Microsoft.CodeAnalysis;

namespace MultiStation.Analyzers;

/// <summary>
/// 全部诊断描述符。严重级别默认 Warning，可由 .editorconfig 提升为 Error。
/// </summary>
public static class DiagnosticDescriptors
{
    private const string CategoryNaming = "MultiStation.Naming";
    private const string CategoryDependency = "MultiStation.Dependency";
    private const string CategoryConcurrency = "MultiStation.Concurrency";
    private const string CategoryStructure = "MultiStation.Structure";

    public static readonly DiagnosticDescriptor NamingConvention = new(
        id: RuleIds.NamingConvention,
        title: "命名空间必须与物理目录一致",
        messageFormat: "类型 '{0}' 的命名空间 '{1}' 与目录预期 '{2}' 不一致，或包含禁用层级名（规则 R-04 / R-05）",
        category: CategoryNaming,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "命名空间 = 程序集名 + 相对子路径（点连接）；层级名不得使用 Utils / Common / Helper / Base。");

    public static readonly DiagnosticDescriptor ModuleReference = new(
        id: RuleIds.ModuleReference,
        title: "模块之间禁止直接引用",
        messageFormat: "模块程序集 '{0}' 引用了其他模块 '{1}'，只允许引用契约族 '{2}'（规则 R-10）",
        category: CategoryDependency,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "任意两个业务模块之间零编译期引用；跨模块交互一律经 Core 消息总线。");

    public static readonly DiagnosticDescriptor BlockingCall = new(
        id: RuleIds.BlockingCall,
        title: "执行循环内禁止阻塞调用",
        messageFormat: "在 '{0}' 中使用了阻塞调用 '{1}'，请改为 await 或下沉到作业池（规则 C-13 / M-11）",
        category: CategoryConcurrency,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: ".Result、.Wait()、GetAwaiter().GetResult()、Thread.Sleep 会占住执行线程，导致优先级调度失效与环等待。");

    public static readonly DiagnosticDescriptor UncheckedDeliveryResult = new(
        id: RuleIds.UncheckedDeliveryResult,
        title: "投递结果必须被检查",
        messageFormat: "调用 '{0}' 时丢弃了 DeliveryResult，必须按 Accepted / Rejected / Replaced / Coalesced 处理（规则 C-12）",
        category: CategoryConcurrency,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "投递一律非阻塞并返回结果；忽略结果表明未处理背压与拒绝场景。");

    public static readonly DiagnosticDescriptor TemplateMethodOverride = new(
        id: RuleIds.TemplateMethodOverride,
        title: "模块总类不得重写模板方法",
        messageFormat: "'{0}' 重写了模板方法 '{1}'，总类骨架固定，子类只能实现 OnStart / OnStop / OnMessageAsync（规则 M-07）",
        category: CategoryStructure,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "模板方法负责校验、去重、分派、兜底与计时，重写会破坏统一行为。");

    public static readonly DiagnosticDescriptor PartStructure = new(
        id: RuleIds.PartStructure,
        title: "Part 子类必须继承 ModulePart",
        messageFormat: "'{0}' 以 Part 命名但未继承 '{1}'，或同时承担多个部分（规则 M-03 / M-04）",
        category: CategoryStructure,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "每个 Part 只承担一个部分，且只能继承 ModulePart 并通过 Owner / Context 交互。");

    public static readonly DiagnosticDescriptor LargePayload = new(
        id: RuleIds.LargePayload,
        title: "大负载必须走共享缓冲句柄",
        messageFormat: "'{0}' 的消息负载包含大于 4KB 的常量，请改走 BufferHandle（规则 C-03）",
        category: CategoryStructure,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "消息体软限 4KB、硬限 64KB；图像与波形必须走数据面。");
}

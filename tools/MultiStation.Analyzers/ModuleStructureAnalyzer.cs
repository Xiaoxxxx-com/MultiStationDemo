using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MultiStation.Analyzers;

/// <summary>
/// 规则 M-07：模块总类不得重写模板方法。
/// 规则 M-03 / M-04：以 Part 命名的类型必须继承 ModulePart。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleStructureAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<string> TemplateMethods =
        ImmutableArray.Create("StartAsync", "StopAsync", "HandleAsync", "ResolvePart");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(
            DiagnosticDescriptors.TemplateMethodOverride,
            DiagnosticDescriptors.PartStructure);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class)
        {
            return;
        }

        var inheritsModuleBase = InheritsFrom(type, RuleIds.ModuleBaseType);
        var inheritsModulePart = InheritsFrom(type, RuleIds.PartBaseType);

        // M-07：继承 ModuleBase 的总类不得声明模板方法
        if (inheritsModuleBase)
        {
            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol method && TemplateMethods.Contains(method.Name))
                {
                    var location = method.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.TemplateMethodOverride,
                        location,
                        type.Name,
                        method.Name));
                }
            }
        }

        // M-03 / M-04：Part 命名必须继承 ModulePart
        if (type.Name.EndsWith("Part", StringComparison.Ordinal) && !inheritsModulePart)
        {
            var location = type.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.PartStructure,
                location,
                type.Name,
                RuleIds.PartBaseType));
        }
    }

    private static bool InheritsFrom(INamedTypeSymbol type, string baseTypeName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (string.Equals(current.Name, baseTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

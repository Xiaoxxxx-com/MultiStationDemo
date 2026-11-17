using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MultiStation.Analyzers;

/// <summary>
/// 规则 R-10：模块程序集之间禁止直接引用，只允许引用契约族。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModuleReferenceAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(DiagnosticDescriptors.ModuleReference);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        var ownName = context.Compilation.AssemblyName;
        if (!RuleIds.IsModuleAssembly(ownName))
        {
            return;
        }

        foreach (var reference in context.Compilation.References)
        {
            var name = reference.Display;
            if (!RuleIds.IsModuleAssembly(name))
            {
                continue;
            }

            if (string.Equals(name, ownName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.ModuleReference,
                Location.None,
                ownName,
                name,
                RuleIds.ContractsPrefix));
        }
    }
}

/// <summary>
/// 规则 C-13 / M-11：执行循环内禁止阻塞调用。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlockingCallAnalyzer : DiagnosticAnalyzer
{
    private static readonly ImmutableArray<string> BlockingMembers =
        ImmutableArray.Create("Result", "Wait", "GetResult");

    private static readonly ImmutableArray<string> BlockingTypes =
        ImmutableArray.Create("Thread", "Task", "ValueTask");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(DiagnosticDescriptors.BlockingCall);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMemberAccess,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeMemberAccess(SyntaxNodeAnalysisContext context)
    {
        var access = (Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax)context.Node;
        var name = access.Name.Identifier.ValueText;
        if (name != "Result")
        {
            return;
        }

        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken)
            .Type?.Name;
        if (typeName is null || !BlockingTypes.Contains(typeName))
        {
            return;
        }

        var owner = FindContainingTypeName(access);
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.BlockingCall,
            access.GetLocation(),
            owner,
            ".Result"));
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax)context.Node;
        var symbol = context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            as IMethodSymbol;
        if (symbol is null)
        {
            return;
        }

        var owner = FindContainingTypeName(invocation);

        if (symbol.Name == "Sleep" && symbol.ContainingType?.Name == "Thread")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.BlockingCall, invocation.GetLocation(), owner, "Thread.Sleep"));
            return;
        }

        if (BlockingMembers.Contains(symbol.Name) && symbol.ContainingType?.Name is "Task" or "ValueTask")
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.BlockingCall, invocation.GetLocation(), owner, $".{symbol.Name}()"));
        }
    }

    private static string FindContainingTypeName(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax type)
            {
                return type.Identifier.ValueText;
            }
        }

        return "<global>";
    }
}

/// <summary>
/// 规则 C-12：SendAsync / PublishAsync 返回 DeliveryResult，不得作为语句直接丢弃。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UncheckedDeliveryResultAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(DiagnosticDescriptors.UncheckedDeliveryResult);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeExpressionStatement,
            Microsoft.CodeAnalysis.CSharp.SyntaxKind.ExpressionStatement);
    }

    private static void AnalyzeExpressionStatement(SyntaxNodeAnalysisContext context)
    {
        var statement = (Microsoft.CodeAnalysis.CSharp.Syntax.ExpressionStatementSyntax)context.Node;

        // 形如 await bus.SendAsync(...);
        if (statement.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.AwaitExpressionSyntax awaitExpression)
        {
            return;
        }

        if (awaitExpression.Expression is not Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation)
        {
            return;
        }

        var methodName = GetMethodName(invocation);
        if (methodName is null || !RuleIds.DeliveryMethods.Contains(methodName))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.UncheckedDeliveryResult,
            invocation.GetLocation(),
            methodName));
    }

    private static string? GetMethodName(Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            Microsoft.CodeAnalysis.CSharp.Syntax.IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null
        };
    }
}

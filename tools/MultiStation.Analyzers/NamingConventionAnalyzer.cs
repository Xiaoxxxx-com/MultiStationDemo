using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace MultiStation.Analyzers;

/// <summary>
/// 规则 R-04 / R-05：命名空间必须与物理目录一致，且不得使用泛化层级名。
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NamingConventionAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(DiagnosticDescriptors.NamingConvention);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeNamedType, SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(SymbolAnalysisContext context)
    {
        var symbol = (INamedTypeSymbol)context.Symbol;
        if (symbol.DeclaringSyntaxReferences.Length == 0)
        {
            return;
        }

        var syntax = symbol.DeclaringSyntaxReferences[0].GetSyntax(context.CancellationToken);
        var tree = syntax.SyntaxTree;
        if (tree.FilePath.Length == 0)
        {
            return;
        }

        var projectDir = TryGetProjectDir(context, tree);
        if (projectDir is null)
        {
            return;
        }

        var relative = TryMakeRelative(projectDir, tree.FilePath);
        if (relative is null)
        {
            return;
        }

        var expected = BuildExpectedNamespace(context.Compilation.AssemblyName, relative);
        var actual = symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        // 生成文件、自动生成的 partial 不参与校验
        if (actual.Length == 0 || IsGenerated(relative))
        {
            return;
        }

        var forbidden = FindForbiddenSegment(actual);
        if (expected == actual && forbidden is null)
        {
            return;
        }

        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource) ?? Location.None;
        context.ReportDiagnostic(Diagnostic.Create(
            DiagnosticDescriptors.NamingConvention,
            location,
            symbol.Name,
            actual,
            forbidden is null ? expected : $"{expected}（禁用层级名 {forbidden}）"));
    }

    private static string BuildExpectedNamespace(string? assemblyName, string relativePath)
    {
        var root = assemblyName ?? "MultiStation";
        var dir = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(dir))
        {
            return root;
        }

        var segments = dir
            .Replace('\\', '/')
            .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
        {
            return root;
        }

        // 子模块目录名本身即命名空间（如 MultiStation.Test.Parts），
        // 若首段已以程序集名开头则不再重复拼接程序集名。
        var joined = string.Join(".", segments);
        if (segments[0].StartsWith(root, StringComparison.Ordinal))
        {
            return joined;
        }

        return root + "." + joined;
    }

    private static string? FindForbiddenSegment(string ns)
    {
        foreach (var segment in ns.Split('.'))
        {
            if (RuleIds.ForbiddenSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return segment;
            }
        }

        return null;
    }

    private static bool IsGenerated(string relativePath)
        => relativePath.Contains(".g.cs", StringComparison.OrdinalIgnoreCase)
           || relativePath.Contains(".designer.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>取相对路径；不在项目目录下时返回 null（netstandard2.0 无 Path.GetRelativePath）。</summary>
    private static string? TryMakeRelative(string root, string path)
    {
        try
        {
            var rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                rootFull += Path.DirectorySeparatorChar;
            }

            var pathFull = Path.GetFullPath(path);
            if (!pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return pathFull.Substring(rootFull.Length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetProjectDir(SymbolAnalysisContext context, SyntaxTree tree)
    {
        // 优先使用 MSBuild 注入的 build_property.ProjectDir（需消费方声明 CompilerVisibleProperty）
        var options = context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree);
        if (options.TryGetValue("build_property.ProjectDir", out var dir) && !string.IsNullOrEmpty(dir))
        {
            return dir;
        }

        // 回退：从源文件向上查找 csproj 所在目录
        return FindProjectDir(tree.FilePath);
    }

    private static string? FindProjectDir(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.EnumerateFiles(dir, "*.csproj").Any())
                {
                    return dir;
                }

                var parent = Path.GetDirectoryName(dir);
                if (parent == dir)
                {
                    break;
                }

                dir = parent;
            }
        }
        catch (Exception)
        {
            // 路径不可访问时放弃校验
        }

        return null;
    }
}

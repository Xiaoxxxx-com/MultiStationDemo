using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiStation.Ui.Desktop;

/// <summary>烧录报告中的单个步骤。</summary>
public sealed record FlashReportStep(
    string Number,
    string Code,
    string Project,
    string Result,
    string Measured,
    string Duration,
    string Time,
    string Remark);

/// <summary>生成烧录报告所需的现场数据。</summary>
public sealed record FlashReportInfo(
    string Model,
    string Sn,
    string CarrierId,
    string ShellCode,
    string Recipe,
    DateTime StartedAt,
    DateTime FinishedAt,
    string Verdict,
    int PassCount,
    int NgCount,
    IReadOnlyList<FlashReportStep> Steps);

/// <summary>
/// 烧录报告规则：目录与文件名支持占位符，按基础信息与结果生成文本报告。
/// 占位符：{Model} {SN} {CarrierID} {ShellCode} {Recipe} {Verdict}
/// {Date} {Time} {DateTime}，也可写 {Date:yyyy-MM-dd} 这类自定义格式。
/// </summary>
public static class FlashReport
{
    public const string DefaultFileNamePattern = "{Model}_{SN}_{DateTime}.txt";

    private static readonly Regex TimeFormatToken = new(
        @"\{(Date|Time|DateTime):([^}]+)\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsEnabled(string? directory) => !string.IsNullOrWhiteSpace(directory);

    /// <summary>按规则写出报告，返回完整路径；目录为空时返回 null。</summary>
    public static string? Write(FlashReportInfo info, string? directoryTemplate, string? fileNamePattern)
    {
        if (!IsEnabled(directoryTemplate))
        {
            return null;
        }

        var now = info.FinishedAt;
        var directory = ExpandTokens(directoryTemplate!.Trim(), info, now);
        var pattern = string.IsNullOrWhiteSpace(fileNamePattern)
            ? DefaultFileNamePattern
            : fileNamePattern.Trim();
        var fileName = SanitizeFileName(ExpandTokens(pattern, info, now));

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, BuildContent(info), Encoding.UTF8);
        return path;
    }

    /// <summary>展开占位符；时间格式非法时抛出 FormatException。</summary>
    public static string ExpandTokens(string template, FlashReportInfo info, DateTime now)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var text = template
            .Replace("{Model}", info.Model, StringComparison.OrdinalIgnoreCase)
            .Replace("{SN}", info.Sn, StringComparison.OrdinalIgnoreCase)
            .Replace("{CarrierID}", info.CarrierId, StringComparison.OrdinalIgnoreCase)
            .Replace("{ShellCode}", info.ShellCode, StringComparison.OrdinalIgnoreCase)
            .Replace("{Recipe}", info.Recipe, StringComparison.OrdinalIgnoreCase)
            .Replace("{Verdict}", info.Verdict, StringComparison.OrdinalIgnoreCase)
            .Replace("{Date}", now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{Time}", now.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{DateTime}", now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        return TimeFormatToken.Replace(text, match =>
        {
            var format = match.Groups[2].Value;
            try
            {
                return now.ToString(format, CultureInfo.InvariantCulture);
            }
            catch (FormatException)
            {
                throw new FormatException($"时间格式无效：{{{match.Groups[1].Value}:{format}}}");
            }
        });
    }

    /// <summary>把文件名中的非法字符替换为下划线。</summary>
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "report.txt" : result;
    }

    /// <summary>生成报告正文。</summary>
    public static string BuildContent(FlashReportInfo info)
    {
        var started = info.StartedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var finished = info.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var seconds = (info.FinishedAt - info.StartedAt).TotalSeconds.ToString("F1", CultureInfo.InvariantCulture);
        var pass = info.PassCount.ToString(CultureInfo.InvariantCulture);
        var ng = info.NgCount.ToString(CultureInfo.InvariantCulture);

        var builder = new StringBuilder();
        builder.AppendLine("多工位测试上位机 · 烧录报告");
        builder.AppendLine(new string('=', 60));
        builder.AppendLine("机种：" + info.Model);
        builder.AppendLine("SN：" + Display(info.Sn));
        builder.AppendLine("CarrierID：" + Display(info.CarrierId));
        builder.AppendLine("壳体码：" + Display(info.ShellCode));
        builder.AppendLine("配方：" + Display(info.Recipe));
        builder.AppendLine("开始时间：" + started);
        builder.AppendLine("结束时间：" + finished);
        builder.AppendLine("耗时：" + seconds + " 秒");
        builder.AppendLine("判定：" + info.Verdict);
        builder.AppendLine("通过：" + pass + "    不良：" + ng);
        builder.AppendLine(new string('-', 60));
        builder.AppendLine("序号\t编号\t烧录步骤\t结果\t实测值\t耗时(s)\t时间\t备注");
        foreach (var step in info.Steps)
        {
            builder.AppendLine(
                step.Number + "\t" + step.Code + "\t" + step.Project + "\t" + step.Result + "\t" +
                Display(step.Measured) + "\t" + step.Duration + "\t" + step.Time + "\t" + step.Remark);
        }

        return builder.ToString();
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "-" : value;
}

using System.IO;
using MultiStation.Ui.Desktop;

namespace MultiStation.Ui.UnitTests;

/// <summary>烧录报告规则：占位符展开、文件名清洗与报告内容生成。</summary>
internal static class ReportTests
{
    public static void Run()
    {
        var finished = new DateTime(2026, 11, 23, 10, 15, 30, DateTimeKind.Local);
        var info = new FlashReportInfo(
            "NP12",
            "1121830A00000002",
            "CAR01",
            "H-001",
            "NP12",
            finished.AddMinutes(-2),
            finished,
            "PASS",
            1,
            0,
            [
                new FlashReportStep(
                    "1", "NP12_1", "AutoProgramFlash1th", "PASS", "-", "12.3", "10:13:40",
                    "外部 AutoLoader 退出码 0 判 PASS"),
            ]);

        var name = FlashReport.ExpandTokens("{Model}_{SN}_{ShellCode}_{DateTime}", info, finished);
        var dateFormat = FlashReport.ExpandTokens("{Date:yyyy-MM-dd}_{Time:HHmmss}_{Verdict}", info, finished);
        var sanitized = FlashReport.SanitizeFileName("NP12:112/3*4?.txt");

        var directory = Path.Combine(Path.GetTempPath(), "multistation-report-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = FlashReport.Write(info, directory, "{Model}_{SN}_{DateTime}.txt");
            var content = path is null ? string.Empty : File.ReadAllText(path);
            Program.Check("RE-01",
                name == "NP12_1121830A00000002_H-001_20261123_101530"
                    && dateFormat == "2026-11-23_101530_PASS"
                    && sanitized == "NP12_112_3_4_.txt"
                    && path is not null && File.Exists(path)
                    && content.Contains("SN：1121830A00000002", StringComparison.Ordinal)
                    && content.Contains("壳体码：H-001", StringComparison.Ordinal)
                    && content.Contains("判定：PASS", StringComparison.Ordinal)
                    && content.Contains("NP12_1", StringComparison.Ordinal),
                $"name={name} date={dateFormat} sanitized={sanitized} path={path}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }
}

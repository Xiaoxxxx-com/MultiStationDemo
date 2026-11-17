using System.Diagnostics;

namespace MultiStation.Platform.Core.Execution;

/// <summary>外部批处理执行结果。</summary>
public sealed record BatResult(
    bool Started,
    int ExitCode,
    bool TimedOut,
    string StandardOutput,
    string StandardError,
    string Message)
{
    /// <summary>判定口径：正常启动、未超时且退出码为 0。</summary>
    public bool Passed => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// 测试序列中的外部批处理执行器。
/// 仅接受 .bat / .cmd：退出码 0 判通过，非 0 判失败，超时强制终止并判失败。
/// </summary>
public static class BatRunner
{
    /// <summary>未配置超时时的默认值（毫秒）。</summary>
    public const int DefaultTimeoutMs = 60_000;

    /// <summary>执行批处理文件。file 支持绝对路径，或相对程序目录（逐级向上查找）。</summary>
    public static async Task<BatResult> RunAsync(
        string file,
        string? arguments,
        string? workingDirectory,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return Fail("未配置批处理文件（FILE=）");
        }

        if (!file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            && !file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return Fail($"仅支持 .bat / .cmd 脚本：{file}");
        }

        var path = ResolvePath(file);
        if (path is null)
        {
            return Fail($"脚本不存在：{file}");
        }

        var effectiveTimeout = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        var workDir = ResolveWorkDir(workingDirectory, path);

        // cmd /d /c ""脚本路径" 参数"：外层引号保证路径含空格时也能正确解析
        var info = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = string.IsNullOrWhiteSpace(arguments)
                ? $"/d /c \"\"{path}\"\""
                : $"/d /c \"\"{path}\" {arguments}\"",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = info };
        try
        {
            if (!process.Start())
            {
                return Fail($"脚本启动失败：{file}");
            }
        }
        catch (Exception ex)
        {
            return Fail($"脚本启动失败：{ex.Message}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            var output = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
            var error = await SafeReadAsync(stderrTask).ConfigureAwait(false);

            return new BatResult(
                true,
                -1,
                !cancellationToken.IsCancellationRequested,
                output,
                error,
                cancellationToken.IsCancellationRequested
                    ? "脚本执行已取消"
                    : $"脚本执行超时（{effectiveTimeout} ms），已强制终止");
        }

        var standardOutput = await SafeReadAsync(stdoutTask).ConfigureAwait(false);
        var standardError = await SafeReadAsync(stderrTask).ConfigureAwait(false);

        return new BatResult(
            true,
            process.ExitCode,
            false,
            standardOutput,
            standardError,
            process.ExitCode == 0 ? "执行成功" : $"退出码 {process.ExitCode}");
    }

    private static BatResult Fail(string message) => new(false, -1, false, string.Empty, string.Empty, message);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // 进程已退出或无权限终止，忽略
        }
    }

    private static async Task<string> SafeReadAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>相对路径从程序目录逐级向上查找，便于开发期引用仓库根目录下的 Script。</summary>
    private static string? ResolvePath(string file)
    {
        if (Path.IsPathRooted(file))
        {
            return File.Exists(file) ? file : null;
        }

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, file);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            probe = probe.Parent;
        }

        return null;
    }

    private static string ResolveWorkDir(string? workingDirectory, string scriptPath)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            var resolved = ResolveDirectory(workingDirectory);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return Path.GetDirectoryName(scriptPath)!;
    }

    /// <summary>相对目录同样从程序目录逐级向上查找。</summary>
    private static string? ResolveDirectory(string directory)
    {
        if (Path.IsPathRooted(directory))
        {
            return Directory.Exists(directory) ? directory : null;
        }

        var probe = new DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            var candidate = Path.Combine(probe.FullName, directory);
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }

            probe = probe.Parent;
        }

        return null;
    }
}

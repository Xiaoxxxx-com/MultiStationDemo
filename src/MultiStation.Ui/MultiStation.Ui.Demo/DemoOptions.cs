namespace MultiStation.Ui.Demo;

internal sealed class DemoOptions
{
    public int Seconds { get; private set; }

    public bool Plain { get; private set; }

    public bool ForceUi { get; private set; }

    public bool ScriptedFaults { get; private set; }

    public string LogDirectory { get; private set; } = Path.Combine(Directory.GetCurrentDirectory(), "logs");

    public bool ShowHelp { get; private set; }

    public static DemoOptions Parse(string[] args)
    {
        var options = new DemoOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--seconds" when i + 1 < args.Length && int.TryParse(args[i + 1], out var seconds):
                    options.Seconds = Math.Max(1, seconds);
                    i++;
                    break;

                case "--plain":
                    options.Plain = true;
                    break;

                case "--ui":
                    options.ForceUi = true;
                    break;

                case "--faults":
                    options.ScriptedFaults = true;
                    break;

                case "--logs" when i + 1 < args.Length:
                    options.LogDirectory = Path.GetFullPath(args[i + 1]);
                    i++;
                    break;

                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("多工位异步测试软件 Demo");
        Console.WriteLine("  --seconds N   运行 N 秒后自动优雅停机（默认手动 Q/ Ctrl+C 退出）");
        Console.WriteLine("  --plain       纯文本输出，不使用 ANSI 看板（适合重定向/CI）");
        Console.WriteLine("  --ui          强制使用 ANSI 看板");
        Console.WriteLine("  --faults      启用脚本化故障：ST3 工艺故障 → 复位 → ST4 相机断线 → PLC 断线");
        Console.WriteLine("  --logs DIR    结构化日志目录（默认 ./logs）");
    }
}

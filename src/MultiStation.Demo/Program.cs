namespace MultiStation.Demo;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = DemoOptions.Parse(args);
        if (options.ShowHelp)
        {
            DemoOptions.PrintHelp();
            return 0;
        }

        var app = new DemoApp(options);
        await app.RunAsync();
        return 0;
    }
}

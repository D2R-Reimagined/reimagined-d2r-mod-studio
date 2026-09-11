using Avalonia;
using Avalonia.Headless;

namespace ModStudio.App;
internal static class Program
{
    public static string[] Arguments { get; private set; } = [];
    [STAThread]
    public static void Main(string[] args)
    {
        Arguments = args;
        var app = AppBuilder.Configure<StudioApplication>().UsePlatformDetect().LogToTrace();
        if (args.Contains("--smoke")) app = app.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
        app.StartWithClassicDesktopLifetime(args);
    }
}

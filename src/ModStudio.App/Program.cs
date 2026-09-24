using Reimagined.Integration;
using Avalonia;
using Avalonia.Headless;

namespace ModStudio.App;
internal static class Program
{
    public static ActivationHost? Integration { get; private set; }
    public static string[] Arguments { get; private set; } = [];
    [STAThread]
    public static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run();
        Arguments = args;
        if (!args.Contains("--smoke") || args.Contains("--companion-only"))
        {
            EditorRequest? startup = null;
            if (!args.Contains("--smoke") && args.FirstOrDefault() is { } root && !root.StartsWith('-') && Directory.Exists(root))
            {
                var project = ModStudio.Core.ModProject.Open(root);
                startup = new EditorRequest(Project: new(project.Id, project.Root));
            }
            if (!ActivationHost.Initialize(CompanionApps.Studio, args, out var integration, startup)) return;
            Integration = integration;
        }
        var app = AppBuilder.Configure<StudioApplication>().UsePlatformDetect().LogToTrace();
        if (args.Contains("--smoke")) app = app.UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
        app.StartWithClassicDesktopLifetime(args);
    }
}

using System.Globalization;
using Avalonia;

namespace Source2MapCompiler;

public static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            // Skia only keeps about 28 MB of textures on the GPU by default, and an 8192x8192 lightmap preview is 512 MB of half
            // floats plus its mipmaps, so without this Skia keeps throwing out everything else to stay under its budget
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 1024L * 1024 * 1024 })
            .LogToTrace();
    }
}

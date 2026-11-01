using System;
using Avalonia;
using JacintoLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (MultipatchInstaller.RunCommand(args) is { } exitCode) return exitCode;
        if (LauncherUpdate.FinishIfRequested(args)) return 0;
        // Headless entry: GoW3Jacinto_Launcher --apply-mode Offline|Steam
        if (args.Length == 2 && args[0] == "--apply-mode")
        {
            try
            {
                var gameDir = GameLayout.ResolveGameDir(AppContext.BaseDirectory) ?? throw new System.IO.DirectoryNotFoundException("Game installation was not detected.");
                NetworkMode.Apply(gameDir, args[1]);
                return 0;
            }
            catch (Exception exception)
            {
                LauncherDiagnostics.Error("--apply-mode " + args[1] + " failed", exception);
                return 2;
            }
        }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Launcher crashed", exception);
            Telemetry.CaptureFatal(exception);
            return 1;
        }
        finally { Telemetry.Stop(); }
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

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
        // One launcher at a time, unless Settings allows more: a second start asks the first to show itself and quits.
        using var single = new System.Threading.Mutex(false, @"Local\GoW3JacintoLauncher.Instance", out var first);
        using var show = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\GoW3JacintoLauncher.Show");
        if (!first && !LauncherConfig.Load(System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).AllowMultipleInstances) { show.Set(); return 0; }
        if (first) new System.Threading.Thread(() => { while (show.WaitOne()) ShowRequested?.Invoke(); }) { IsBackground = true }.Start();
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Launcher crashed", exception);
            Telemetry.CaptureFatal(exception);
            return 1;
        }
        finally { Telemetry.Stop(); }
    }

    /// <summary>Set by the main window: another start of the launcher asked this one to show itself.</summary>
    internal static Action? ShowRequested;

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

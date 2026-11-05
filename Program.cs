using System;
using Avalonia;
using JacintoLauncher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The crash window runs in its own process so it still works when this one is broken.
        if (args.Length == 2 && args[0] == CrashScreen.Arg) return CrashScreen.Show(args[1]);
        // A crash on any other thread ends the process; the player still gets told.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString());
            LauncherDiagnostics.Error("Launcher crashed", exception);
            CrashScreen.Report("Launcher crashed", exception);
        };
        // Before anything else, so a failure in any later step is reported (the settings are read from the file beside the launcher).
        try { Telemetry.Start(LauncherConfig.Load(System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)), null); }
        catch (Exception exception) { LauncherDiagnostics.Info("Error reporting did not start: " + exception.Message); }
        try { return Run(args); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Launcher failed to start", exception);
            CrashScreen.Report("Launcher failed to start", exception);
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        if (Environment.GetEnvironmentVariable("JACINTO_TEST_CRASH") == "1") throw new InvalidOperationException("Test crash (JACINTO_TEST_CRASH=1).");
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
        // Named events exist only on Windows (PlatformNotSupportedException elsewhere, which aborted the launcher on Linux / SteamOS).
        // ponytail: on Linux / macOS a second start just quits; add a file-based signal if "bring forward" is wanted there.
        System.Threading.EventWaitHandle? show = null;
        if (OperatingSystem.IsWindows())
        {
            try { show = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\GoW3JacintoLauncher.Show"); }
            catch (Exception exception) { LauncherDiagnostics.Error("Show event unavailable", exception); }
        }
        if (!first && !LauncherConfig.Load(System.IO.Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)).AllowMultipleInstances)
        {
            LauncherDiagnostics.Info("Another launcher is open; it was asked to show itself.");
            show?.Set();
            return 0;
        }
        if (first && show is not null) new System.Threading.Thread(() => { while (show.WaitOne()) ShowRequested?.Invoke(); }) { IsBackground = true }.Start();
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Launcher crashed", exception);
            CrashScreen.Report("Launcher crashed", exception);
            return 1;
        }
        finally { Telemetry.Stop(); }
    }

    /// <summary>Set by the main window: another start of the launcher asked this one to show itself.</summary>
    internal static Action? ShowRequested;

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}

using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace JacintoLauncher;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // The launcher is a static WPF shell.  Software rendering avoids the
        // intermittent GPU composition artefacts observed over the left panel
        // on this machine; it has no effect on the game renderer.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        base.OnStartup(e);
        // Headless entry for the uninstaller: GoW3Jacinto_Launcher.exe --apply-mode Offline|Steam
        if (e.Args.Length == 2 && e.Args[0] == "--apply-mode")
        {
            try
            {
                var gameDir = GameLayout.ResolveGameDir(AppContext.BaseDirectory) ?? throw new DirectoryNotFoundException("Game installation was not detected.");
                NetworkMode.Apply(gameDir, e.Args[1]);
                Shutdown(0);
            }
            catch (Exception exception)
            {
                LauncherDiagnostics.Error("--apply-mode " + e.Args[1] + " failed", exception);
                Shutdown(2);
            }
            return;
        }
        try { new MainWindow().Show(); }
        catch (Exception exception)
        {
            var log = Path.Combine(Path.GetTempPath(), "GoW3JacintoLauncher-startup-error.txt");
            File.WriteAllText(log, exception.ToString());
            MessageBox.Show(exception.Message + "\n\nDetails: " + log, "Launcher startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }
}

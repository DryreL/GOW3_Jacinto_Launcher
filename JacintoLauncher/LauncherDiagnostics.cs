#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace JacintoLauncher;

/// <summary>
/// Keeps technical details out of the launcher artwork while retaining an
/// inspectable, local record for troubleshooting. No game files are written.
/// </summary>
internal static class LauncherDiagnostics
{
    private static readonly object Sync = new();
    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GOW3JacintoLauncher", "logs");

    /// <summary>Set by Telemetry; stays null in headless use and in the self-tests.</summary>
    internal static Action<string, Exception>? ErrorReported;

    internal static void Info(string message) => Write("INFO", message);
    internal static void Error(string operation, Exception exception)
    {
        Write("ERROR", operation + Environment.NewLine + exception);
        ErrorReported?.Invoke(operation, exception);
    }

    internal static void OpenFolder() { Directory.CreateDirectory(DirectoryPath); Open(DirectoryPath); }

    internal static void Open(string path)
    {
        // Linux has no shell-execute verb for folders and URLs: xdg-open is the equivalent.
        var start = OperatingSystem.IsLinux() ? new ProcessStartInfo("xdg-open", new[] { path }) { UseShellExecute = false } : new ProcessStartInfo(path) { UseShellExecute = true };
        Process.Start(start)?.Dispose();
    }

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var file = Path.Combine(DirectoryPath, "launcher-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");
            lock (Sync)
            {
                File.AppendAllText(file,
                    $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never prevent launching or backing up saves.
        }
    }
}

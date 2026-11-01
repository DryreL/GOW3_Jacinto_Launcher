#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace JacintoLauncher;

/// <summary>
/// Keeps technical details out of the launcher artwork while retaining an
/// inspectable, local record for troubleshooting. No game files are written.
/// </summary>
internal static class LauncherDiagnostics
{
    private static readonly object Sync = new();
    internal static string DirectoryPath => Environment.GetEnvironmentVariable("JACINTO_LOG_DIR") ?? (resolved ??= Resolve());
    private static string? resolved;

    // Beside the launcher, where a player can find them; a folder the launcher cannot write to falls back to the user's own.
    private static string Resolve()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            Directory.CreateDirectory(beside);
            var probe = Path.Combine(beside, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return beside;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "GOW3JacintoLauncher", "logs");
        }
    }

    /// <summary>Set by Telemetry; stays null in headless use and in the self-tests.</summary>
    internal static Action<string, Exception>? ErrorReported;

    internal static void Info(string message) => Write("INFO", message);
    internal static void Error(string operation, Exception exception)
    {
        Write("ERROR", operation + Environment.NewLine + exception);
        ErrorReported?.Invoke(operation, exception);
    }

    internal static string? LatestLog() => Directory.Exists(DirectoryPath) ? new DirectoryInfo(DirectoryPath).EnumerateFiles("*.log").OrderBy(f => f.LastWriteTimeUtc).LastOrDefault()?.FullName : null;

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

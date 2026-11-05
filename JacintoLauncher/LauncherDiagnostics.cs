#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace JacintoLauncher;

/// <summary>Gives memory back while nobody is looking at the launcher.</summary>
internal static class LauncherMemory
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    internal static void Release()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
        if (OperatingSystem.IsWindows()) using (var self = Process.GetCurrentProcess()) SetProcessWorkingSetSize(self.Handle, -1, -1);
    }
}

/// <summary>A refusal with its reason, for something only the player can change, such as closing the game.</summary>
internal sealed class UserActionException : InvalidOperationException
{
    internal UserActionException(string message) : base(message) { }
}

/// <summary>
/// Keeps technical details out of the launcher artwork while retaining an
/// inspectable, local record for troubleshooting. No game files are written.
/// </summary>
internal static class LauncherDiagnostics
{
    private const int VirusBlocked = unchecked((int)0x800700E1);

    /// <summary>Goes to the local log only: what the player or their PC must change is not a launcher bug, and the launcher already says what to do.</summary>
    internal static bool Reportable(Exception exception) =>
        // Win32Exception: antivirus, elevation or a missing file at process start. COMException: the desktop shortcut.
        exception is not (ArgumentException or UserActionException or TimeoutException or System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        && !LooksLikeAntivirus(exception) && !LooksLikePermission(exception);

    // The files antivirus programs quarantine: JacintoNet's Winsock proxy (a Kaspersky "Yephiler" heuristic hit), its helper, the patched game and the launcher.
    private static readonly string[] Flagged = { "wsock32.dll", "steam_api64.dll", "GOW3Helper.dll", "GearGame.exe", "GearsOfWar3.exe", "GoW3Jacinto_Launcher" };

    /// <summary>A file operation an antivirus program most likely stopped: Windows' "contains a virus" code, or access denied / missing on one of the flagged files.</summary>
    internal static bool LooksLikeAntivirus(Exception exception) =>
        exception.HResult == VirusBlocked || exception.InnerException is { } inner && inner.HResult == VirusBlocked
        || exception is IOException or UnauthorizedAccessException && Flagged.Any(name => exception.Message.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The system said no: a folder that is read-only, owned by another user, or protected (Program Files, OneDrive, a Linux SD card). A setting of the player's PC, not a launcher bug.</summary>
    internal static bool LooksLikePermission(Exception exception) =>
        exception is UnauthorizedAccessException || exception is IOException && exception.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

    internal static string PermissionAdvice(Exception exception) =>
        "The launcher is not allowed to change a file or folder: it is read-only, belongs to another user, or is protected by the system (for example Program Files, OneDrive, or a game on an SD card or Windows drive mounted read-only on Linux). "
        + "Move the game or the launcher to a folder you own, such as C:\\Games or your home folder, or fix the folder's permissions, then try again. " + exception.Message;

    /// <summary>What the player is told instead of a bare "access denied", with the code to quote.</summary>
    internal static string AntivirusAdvice(Exception exception) =>
        $"Your antivirus program most likely blocked or removed a launcher or game file (error code 0x{exception.HResult:X8}). The Multipatch files are not harmful: Kaspersky, Avast, AVG and Windows Security sometimes flag the JacintoNet network file (wsock32.dll) by mistake. "
        + "Open your antivirus, restore the file from quarantine or protection history, add the game folder to its exclusions, then try again. " + exception.Message;

    /// <summary>The macOS package is a signed .app: files may not be written into it, and macOS runs a downloaded one from a read-only copy.</summary>
    internal static bool InAppBundle => OperatingSystem.IsMacOS() && AppContext.BaseDirectory.Contains(".app/Contents/", StringComparison.Ordinal);

    private static readonly object Sync = new();
    internal static string DirectoryPath => Environment.GetEnvironmentVariable("JACINTO_LOG_DIR") ?? (resolved ??= Resolve());
    private static string? resolved;

    // Beside the launcher, where a player can find them; a folder the launcher cannot write to falls back to the user's own.
    private static string Resolve()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "logs");
        try
        {
            if (InAppBundle) throw new UnauthorizedAccessException("Inside the app bundle.");
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

    /// <summary>Set by the main window, which shows each error in a window of its own.</summary>
    internal static Action<string, Exception>? ErrorShown;

    internal static void Info(string message) => Write("INFO", message);
    internal static void Error(string operation, Exception exception)
    {
        Write("ERROR", operation + Environment.NewLine + exception);
        ErrorReported?.Invoke(operation, exception);
        // The window says what the likely cause is; the original error stays inside it for Copy details.
        ErrorShown?.Invoke(operation, LooksLikeAntivirus(exception) ? new IOException(AntivirusAdvice(exception), exception) : LooksLikePermission(exception) ? new IOException(PermissionAdvice(exception), exception) : exception);
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

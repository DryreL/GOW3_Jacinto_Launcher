#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace JacintoLauncher;

/// <summary>Locates the single Jacinto installation and its Multipatch data folder.</summary>
internal static class GameLayout
{
    /// <summary>Walks up from the launcher folder (normally &lt;Game&gt;\JacintoLauncher\Launcher).</summary>
    internal static string? ResolveGameDir(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        try
        {
            DirectoryInfo? candidate = new(start);
            for (var depth = 0; candidate is not null && depth < 5; depth++, candidate = candidate.Parent)
                if (File.Exists(Path.Combine(candidate.FullName, "Binaries", "Win64", "GearGame.exe"))) return candidate.FullName;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>
    /// The Multipatch _files folder: tools\multipatch\_files for a full Jacinto
    /// installation, Gears3_Multipatch_Tool\_files for an existing DryreL
    /// installation (where the tool keeps graphics.json and Backups).
    /// </summary>
    internal static string? ToolFilesDir(string gameDir)
    {
        foreach (var relative in new[] { Path.Combine("tools", "multipatch", "_files"), Path.Combine("Gears3_Multipatch_Tool", "_files") })
        {
            var candidate = Path.Combine(gameDir, relative);
            if (File.Exists(Path.Combine(candidate, "graphics.json"))) return candidate;
        }
        return null;
    }

    /// <summary>The Multipatch start script: .bat when patched on Windows, .sh when patched on Linux / SteamOS.</summary>
    internal static string? StartScript(string gameDir) =>
        new[] { "Gears3_Start_Game.bat", "Gears3_Start_Game.sh" }.Select(name => Path.Combine(gameDir, name)).FirstOrDefault(File.Exists);

    /// <summary>On Linux and macOS the game is a Wine process, reported as "GearGame.exe".</summary>
    internal static bool GameRunning()
    {
        // One look at the process list: this runs every second.
        var processes = Process.GetProcesses();
        try { return processes.Any(process => process.ProcessName is "GearGame" or "GearGame.exe" or "GearsOfWar3" or "GearsOfWar3.exe"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

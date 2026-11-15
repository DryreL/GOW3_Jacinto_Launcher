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

    internal const string CreatedScriptTag = "Created by the Jacinto launcher";
    internal static readonly string[] StartScriptNames = { "Gears3_Start_Game.bat", "Gears3_Start_Game_Debug.bat", "Gears3_Start_Game.sh", "Gears3_Start_Game_Debug.sh" };

    /// <summary>
    /// Writes whichever of the four start scripts is missing, with the arguments of the one that exists, so a game patched on
    /// Windows also starts by hand on Linux / SteamOS / macOS and the reverse. Settings keeps them all in step afterwards.
    /// Returns how many were written; files written here carry <see cref="CreatedScriptTag"/> so Uninstall removes them.
    /// </summary>
    internal static int EnsureStartScripts(string gameDir)
    {
        if (StartScript(gameDir) is null) return 0;
        var arguments = MultipatchBridge.LaunchArguments(gameDir);
        var written = 0;
        foreach (var name in StartScriptNames)
        {
            var path = Path.Combine(gameDir, name);
            if (File.Exists(path)) continue;
            var shell = name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);
            var text = name switch
            {
                "Gears3_Start_Game.bat" => BatScript(arguments),
                "Gears3_Start_Game_Debug.bat" => DebugBatScript(arguments),
                "Gears3_Start_Game.sh" => ShScript(arguments),
                _ => DebugShScript(arguments),
            };
            File.WriteAllText(path, shell ? text : text.Replace("\n", "\r\n"));
            if (!OperatingSystem.IsWindows() && shell) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            written++;
        }
        return written;
    }

    /// <summary>Removes the start scripts <see cref="EnsureStartScripts"/> wrote; the Multipatch's own scripts stay.</summary>
    internal static void RemoveCreatedStartScripts(string gameDir)
    {
        foreach (var path in StartScriptNames.Select(name => Path.Combine(gameDir, name)))
            if (File.Exists(path) && File.ReadAllText(path).Contains(CreatedScriptTag)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
    }

    // The same scripts the Multi Patch Tool writes (step 29 and Game Settings), plus the tag line.
    private static string BatScript(string arguments) =>
        "@echo off\nrem " + CreatedScriptTag + "\ntitle Gears of War 3 Jacinto\necho Starting Gears of War 3...\necho Please wait until the game launches.\n" +
        "cd /d \"%~dp0Binaries\\Win64\"\nstart \"\" GearGame.exe " + arguments + "\n:wait\ntimeout /t 1 /nobreak >nul\n" +
        "tasklist /FI \"IMAGENAME eq GearGame.exe\" | find /I \"GearGame.exe\" >nul\nif errorlevel 1 goto wait\n";

    private static string DebugBatScript(string arguments) =>
        "@echo off\nrem " + CreatedScriptTag + "\ntitle Gears of War 3 Jacinto - DEBUG (log)\nrem Reuses resolution / language / window mode from Gears3_Start_Game.bat\n" +
        "set \"ARGS=" + arguments + "\"\n" +
        "for /f \"usebackq tokens=*\" %%L in (`findstr /i /c:\"GearGame.exe -\" \"%~dp0Gears3_Start_Game.bat\"`) do set \"LINE=%%L\"\n" +
        "if defined LINE set \"ARGS=%LINE:*GearGame.exe =%\"\necho Starting GearGame.exe %ARGS% -log -forcelogflush\n" +
        "echo A log console window opens next to the game. Close the game normally when done.\ncd /d \"%~dp0Binaries\\Win64\"\nGearGame.exe %ARGS% -log -forcelogflush\n" +
        "rem Keep a timestamped copy of the log (the game rotates Launch.log on next start)\n" +
        "for /f %%T in ('powershell -NoProfile -Command \"Get-Date -Format yyyyMMdd_HHmmss\"') do set \"TS=%%T\"\n" +
        "if not exist \"%~dp0DebugLogs\" mkdir \"%~dp0DebugLogs\"\ncopy /y \"%~dp0GearGame\\Logs\\Launch.log\" \"%~dp0DebugLogs\\Launch_%TS%.log\" >nul\n" +
        "echo Log saved: DebugLogs\\Launch_%TS%.log\npause\n";

    // Linux / SteamOS / macOS: wine, as the tool's own script. The launcher itself starts the game through umu-run / Proton / wine (GameRunner).
    private static string ShScript(string arguments) =>
        "#!/bin/sh\n# Gears of War 3 Jacinto launcher (Linux / SteamOS / macOS). ARGS is reused by Gears3_Start_Game_Debug.sh.\n# " + CreatedScriptTag + "\n" +
        "ARGS=\"" + arguments + "\"\ncd \"$(dirname \"$0\")/Binaries/Win64\" || exit 1\nif command -v wine >/dev/null 2>&1; then\n    wine GearGame.exe $ARGS \"$@\"\n    rc=$?\n" +
        "    if [ $rc -ne 0 ]; then\n        echo \"If the game stopped with 'DwTriovizCreatePixelShader' (Fatal error), the prefix is missing the DirectX DLLs the game needs.\"\n" +
        "        echo \"Install them:  protontricks <AppID> d3dcompiler_43 d3dx9_43   (or: WINEPREFIX=<prefix> winetricks d3dcompiler_43 d3dx9_43)\"\n    fi\n    exit $rc\nfi\n" +
        "echo \"wine not found. On Steam Deck / Steam: add Binaries/Win64/GearGame.exe as a non-Steam game, force a Proton version\"\n" +
        "echo \"and set the launch options to:  $ARGS\"\n" +
        "echo \"Game stops with 'DwTriovizCreatePixelShader'? Install the DirectX DLLs into that prefix: protontricks <AppID> d3dcompiler_43 d3dx9_43\"\nexit 1\n";

    private static string DebugShScript(string arguments) =>
        "#!/bin/sh\n# Debug launcher (Linux / SteamOS / macOS): starts the game with -log and keeps a timestamped copy of Launch.log in DebugLogs/\n# " + CreatedScriptTag + "\n" +
        "DIR=\"$(cd \"$(dirname \"$0\")\" && pwd)\"\nARGS=$(sed -n 's/^ARGS=\"\\(.*\\)\"$/\\1/p' \"$DIR/Gears3_Start_Game.sh\")\n[ -z \"$ARGS\" ] && ARGS=\"" + arguments + "\"\n" +
        "echo \"Starting GearGame.exe $ARGS -log -forcelogflush\"\ncd \"$DIR/Binaries/Win64\" || exit 1\nif command -v wine >/dev/null 2>&1; then\n" +
        "    wine GearGame.exe $ARGS -log -forcelogflush || {\n        echo \"If the log shows 'DwTriovizCreatePixelShader': install the DirectX DLLs: protontricks <AppID> d3dcompiler_43 d3dx9_43 (or winetricks)\"\n    }\n" +
        "else\n    echo \"wine not found. Add GearGame.exe as a non-Steam game (Proton) with launch options: $ARGS -log -forcelogflush\"\n    exit 1\nfi\n" +
        "mkdir -p \"$DIR/DebugLogs\"\nTS=$(date +%Y%m%d_%H%M%S)\ncp \"$DIR/GearGame/Logs/Launch.log\" \"$DIR/DebugLogs/Launch_$TS.log\" && echo \"Log saved: DebugLogs/Launch_$TS.log\"\n";

    /// <summary>On Linux and macOS the game is a Wine process, reported as "GearGame.exe".</summary>
    internal static bool GameRunning()
    {
        // One look at the process list: this runs every second.
        var processes = Process.GetProcesses();
        try { return processes.Any(process => process.ProcessName is "GearGame" or "GearGame.exe" or "GearsOfWar3" or "GearsOfWar3.exe"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

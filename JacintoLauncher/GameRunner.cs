#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace JacintoLauncher;

/// <summary>
/// Starts GearGame.exe. Windows (and this launcher itself running inside Proton/Wine) starts it directly;
/// a native Linux or macOS launcher goes through a Windows compatibility layer it finds or was told about.
/// </summary>
internal static class GameRunner
{
    internal sealed record Layer(string Kind, string Path);

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string DefaultPrefix => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "GOW3JacintoLauncher", "prefix");
    private static string Prefix(LauncherConfig config) => string.IsNullOrWhiteSpace(config.WinePrefix) ? DefaultPrefix : config.WinePrefix;

    internal static bool NeedsCompatibilityLayer => !OperatingSystem.IsWindows();

    /// <summary>True when this Windows process actually runs inside Wine/Proton, where host processes (Steam) are invisible.</summary>
    internal static bool InsideWine =>
        OperatingSystem.IsWindows() && NativeLibrary.TryLoad("ntdll.dll", out var ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out _);

    /// <summary>The configured runner, else umu-run, else the newest Proton, else wine from PATH. Null when nothing was found.</summary>
    internal static Layer? FindLayer(LauncherConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.WineRunner) && File.Exists(config.WineRunner)) return Classify(config.WineRunner);
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string? OnPath(string name) => path.Select(dir => Path.Combine(dir, name)).FirstOrDefault(File.Exists);
        if (OnPath("umu-run") is { } umu) return new Layer("umu", umu);
        var libraries = new[] { Path.Combine(Home, ".steam", "steam", "steamapps", "common"), Path.Combine(Home, ".local", "share", "Steam", "steamapps", "common"), Path.Combine(Home, ".steam", "root", "compatibilitytools.d"), Path.Combine(Home, ".local", "share", "Steam", "compatibilitytools.d") };
        var proton = libraries.Where(Directory.Exists).SelectMany(Directory.GetDirectories).Where(d => Path.GetFileName(d).Contains("Proton", StringComparison.OrdinalIgnoreCase))
            .Select(d => Path.Combine(d, "proton")).Where(File.Exists).OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        if (proton is not null) return new Layer("proton", proton);
        return (OnPath("wine") ?? OnPath("wine64")) is { } wine ? new Layer("wine", wine) : null;
    }

    private static Layer Classify(string runner) => Path.GetFileName(runner) switch { "umu-run" => new Layer("umu", runner), "proton" => new Layer("proton", runner), _ => new Layer("wine", runner) };

    /// <summary>Where the game keeps "My Documents" for the settings engine. The test override wins.</summary>
    internal static string DocumentsRoot(LauncherConfig? config)
    {
        var testOverride = Environment.GetEnvironmentVariable("JACINTO_SETTINGS_DOCUMENTS");
        if (!string.IsNullOrWhiteSpace(testOverride)) return Path.GetFullPath(testOverride);
        if (!NeedsCompatibilityLayer) return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        config ??= new LauncherConfig();
        var layer = FindLayer(config);
        var drive = layer?.Kind == "wine" ? Path.Combine(Prefix(config), "drive_c") : Path.Combine(Prefix(config), "pfx", "drive_c");
        var user = layer?.Kind == "wine" ? Environment.UserName : "steamuser";
        return Path.Combine(drive, "users", user, "Documents");
    }

    internal static bool IsSteamClientRunning()
    {
        if (InsideWine) return true; // Steam runs on the host, out of sight; JacintoNet reports a missing client itself.
        foreach (var name in new[] { "steam", "steam_osx" })
        {
            var processes = Process.GetProcessesByName(name);
            try { if (processes.Length != 0) return true; }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        return false;
    }

    private static readonly string[] DirectXFiles = { "D3DCOMPILER_43.dll", "d3dx9_43.dll" };
    private const string DirectXDownload = "https://www.microsoft.com/en-us/download/details.aspx?id=35";

    internal static string? MissingDirectX(string gameDir, LauncherConfig? config = null)
    {
        if (InsideWine) return null;
        string system;
        if (OperatingSystem.IsWindows()) system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        else
        {
            config ??= new LauncherConfig();
            var layer = FindLayer(config);
            if (layer is null) return null;
            system = Path.Combine(Prefix(config), layer.Kind == "wine" ? "" : "pfx", "drive_c", "windows", "system32");
            if (!Directory.Exists(system)) return null; // The prefix is created by the first start; nothing can be said yet.
        }
        var binaries = Path.Combine(gameDir, "Binaries", "Win64");
        var missing = DirectXFiles.Where(n => !Exists(system, n) && !Exists(binaries, n)).ToArray();
        return missing.Length == 0 ? null : string.Join(", ", missing);
    }

    private static bool Exists(string directory, string name) =>
        File.Exists(Path.Combine(directory, name)) || (!OperatingSystem.IsWindows() && Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Windows opens Microsoft's DirectX June 2010 page (a third-party installer is never run silently).
    /// Linux and macOS install the two files into the game's prefix with winetricks. Returns false when winetricks is missing.
    /// </summary>
    internal static bool InstallDirectX(LauncherConfig config)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(DirectXDownload) { UseShellExecute = true })?.Dispose();
            return true;
        }
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var layer = FindLayer(config);
        var winetricks = path.Select(dir => Path.Combine(dir, "winetricks")).FirstOrDefault(File.Exists);
        ProcessStartInfo start;
        if (layer?.Kind == "umu")
        {
            start = new ProcessStartInfo(layer.Path) { UseShellExecute = false };
            start.Environment["GAMEID"] = "umu-0"; start.Environment["PROTONPATH"] = "GE-Proton";
            start.ArgumentList.Add("winetricks");
        }
        else
        {
            if (winetricks is null) return false;
            start = new ProcessStartInfo(winetricks) { UseShellExecute = false };
            // Proton keeps its wine next to the "proton" script, under files/ on current versions and dist/ on older ones.
            if (layer?.Kind == "proton")
                foreach (var folder in new[] { "files", "dist" })
                {
                    var wine = Path.Combine(Path.GetDirectoryName(layer.Path)!, folder, "bin", "wine");
                    if (File.Exists(wine)) { start.Environment["WINE"] = wine; break; }
                }
            else if (layer is not null) start.Environment["WINE"] = layer.Path;
        }
        start.Environment["WINEPREFIX"] = layer?.Kind == "wine" ? Prefix(config) : Path.Combine(Prefix(config), "pfx");
        foreach (var argument in new[] { "-q", "d3dcompiler_43", "d3dx9_43" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("winetricks did not start.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException("winetricks exited with " + process.ExitCode + ".");
        return true;
    }

    /// <summary>
    /// A copy of GearGame.exe under another name. Discord matches on the file name alone and lists "geargame.exe" as Gears of War 4.
    /// Refreshed whenever the original changes, for example after a repair.
    /// </summary>
    internal static string GameExecutable(string gameDir, bool create = true)
    {
        var binaries = Path.Combine(Path.GetFullPath(gameDir), "Binaries", "Win64");
        var copy = Path.Combine(binaries, "GearsOfWar3.exe");
        if (!create) return copy;
        var original = new FileInfo(Path.Combine(binaries, "GearGame.exe"));
        var current = new FileInfo(copy);
        if (!current.Exists || current.Length != original.Length || current.LastWriteTimeUtc != original.LastWriteTimeUtc) original.CopyTo(copy, true);
        return copy;
    }

    internal static ProcessStartInfo BuildStart(string gameDir, string arguments, string mode, LauncherConfig config, Layer? layer)
    {
        var binaries = Path.Combine(Path.GetFullPath(gameDir), "Binaries", "Win64");
        var exe = GameExecutable(gameDir);
        if (!NeedsCompatibilityLayer)
        {
            var direct = new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = binaries };
            direct.Environment["DXVK_LOG_LEVEL"] = "none"; // Otherwise it writes logs beside the game.
            return direct;
        }
        if (layer is null) throw new InvalidOperationException("No Windows compatibility layer was found (umu-run, Proton or Wine). Install one, or set it in Settings.");
        var prefix = Prefix(config);
        Directory.CreateDirectory(prefix);
        var start = new ProcessStartInfo(layer.Path) { UseShellExecute = false, WorkingDirectory = binaries };
        var environment = new Dictionary<string, string>();
        // The JacintoNet proxy must win over Wine's builtin wsock32 in Steam mode.
        if (mode == NetworkMode.Steam) environment["WINEDLLOVERRIDES"] = "wsock32=n,b";
        switch (layer.Kind)
        {
            case "umu":
                environment["WINEPREFIX"] = Path.Combine(prefix, "pfx"); environment["GAMEID"] = "umu-0"; environment["PROTONPATH"] = "GE-Proton";
                start.ArgumentList.Add(exe);
                break;
            case "proton":
                environment["STEAM_COMPAT_DATA_PATH"] = prefix;
                environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = new[] { Path.Combine(Home, ".steam", "steam"), Path.Combine(Home, ".local", "share", "Steam") }.FirstOrDefault(Directory.Exists) ?? "";
                start.ArgumentList.Add("run"); start.ArgumentList.Add(exe);
                break;
            default:
                environment["WINEPREFIX"] = prefix;
                start.ArgumentList.Add(exe);
                break;
        }
        foreach (var (name, value) in environment) start.Environment[name] = value;
        foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)) start.ArgumentList.Add(argument);
        return start;
    }
}

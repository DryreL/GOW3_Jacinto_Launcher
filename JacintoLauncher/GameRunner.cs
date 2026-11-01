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
    private static string DefaultPrefix => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GOW3JacintoLauncher", "prefix");
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

    internal static string? MissingDirectX(string gameDir)
    {
        if (!OperatingSystem.IsWindows() || InsideWine) return null;
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var binaries = Path.Combine(gameDir, "Binaries", "Win64");
        var missing = new[] { "D3DCOMPILER_43.dll", "d3dx9_43.dll" }.Where(n => !File.Exists(Path.Combine(system, n)) && !File.Exists(Path.Combine(binaries, n))).ToArray();
        return missing.Length == 0 ? null : string.Join(", ", missing);
    }

    internal static ProcessStartInfo BuildStart(string gameDir, string arguments, string mode, LauncherConfig config, Layer? layer)
    {
        var binaries = Path.Combine(Path.GetFullPath(gameDir), "Binaries", "Win64");
        var exe = Path.Combine(binaries, "GearGame.exe");
        if (!NeedsCompatibilityLayer) return new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = binaries };
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

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JacintoLauncher;

// Integration boundary between the launcher UI and the Multipatch installation.
internal static class MultipatchBridge
{
    internal sealed record ModeStatus(string Headline, string Detail);
    internal sealed record SaveBackupResult(string Directory, int Files, string ManifestPath)
    {
        public override string ToString() => $"Save backup created outside the game: {Directory} ({Files} file(s), manifest: {ManifestPath})";
    }

    /// <summary>Set by the UI so the settings engine and game start see the user's Wine prefix and runner.</summary>
    internal static LauncherConfig? Config;

    private static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GOW3JacintoLauncher");
    private static string SettingsTransactionRoot
    {
        get
        {
            var testOverride = Environment.GetEnvironmentVariable("JACINTO_SETTINGS_TRANSACTION_ROOT");
            return string.IsNullOrWhiteSpace(testOverride) ? Path.Combine(StateRoot, "settings-transactions") : Path.GetFullPath(testOverride);
        }
    }

    internal static ModeStatus BuildModeStatus(bool offlineReady, bool steamReady)
    {
        if (offlineReady && steamReady) return new ModeStatus("Ready to play", "Offline ready • Steam/JacintoNet ready");
        if (offlineReady) return new ModeStatus("Ready to play", "Offline ready • Steam/JacintoNet unavailable");
        if (steamReady) return new ModeStatus("Ready to play", "Offline unavailable • Steam/JacintoNet ready");
        return new ModeStatus("Game installation not found", "Offline unavailable • Steam/JacintoNet unavailable. See Diagnostics.");
    }

    private static SettingsEngine Engine(string gameDir) => new(gameDir, SettingsTransactionRoot, GameRunner.DocumentsRoot(Config));
    private static JsonObject Logged(string action, Func<JsonObject> operation)
    {
        try
        {
            var response = operation();
            LauncherDiagnostics.Info($"Settings action {action} succeeded. Transaction: {response["TransactionId"]?.GetValue<string>() ?? "read-only"}.");
            return response;
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Settings action " + action + " failed", exception);
            throw;
        }
    }

    internal static JsonObject LoadSettingsFromMultipatch(string gameDir) => Logged("GetGraphics", () => Engine(gameDir).GetGraphics());
    internal static JsonObject LoadControlsFromMultipatch(string gameDir) => Logged("GetControls", () => Engine(gameDir).GetControls());
    internal static JsonObject ApplyGraphicsThroughMultipatch(string gameDir, JsonObject request) => Logged("ApplyGraphics", () => Engine(gameDir).ApplyGraphics(request));
    internal static JsonObject ApplyControlsThroughMultipatch(string gameDir, JsonObject bindings, string? sensitivity = null) => Logged("ApplyControls", () => Engine(gameDir).ApplyControls(bindings, sensitivity));
    internal static JsonObject RollbackSettingsThroughMultipatch(string gameDir, string? transactionId = null) => Logged("RollbackGraphics", () => Engine(gameDir).Rollback(transactionId, "Graphics"));
    internal static JsonObject RollbackControlsThroughMultipatch(string gameDir, string? transactionId = null) => Logged("RollbackControls", () => Engine(gameDir).Rollback(transactionId, "Controls"));

    public static SaveBackupResult BackupAllSaves(string gameDir, string? backupRoot = null)
    {
        // Checkpoints live in GearGame\SaveData; the game writes the profile relative to its working
        // directory (Binaries\Win64), i.e. Binaries\GearGame\SaveData. Both are backed up.
        var sources = new[] { ("SaveData", Path.GetFullPath(Path.Combine(gameDir, "GearGame", "SaveData"))), ("Profile", Path.GetFullPath(Path.Combine(gameDir, "Binaries", "GearGame", "SaveData"))) }
            .Where(item => Directory.Exists(item.Item2)).ToArray();
        if (sources.Length == 0) throw new DirectoryNotFoundException("SaveData directory not found: " + Path.Combine(gameDir, "GearGame", "SaveData"));
        var root = backupRoot is null ? Path.Combine(StateRoot, "save-backups") : Path.GetFullPath(backupRoot);
        Directory.CreateDirectory(root);
        var name = "saves-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N");
        var destination = Path.Combine(root, name);
        var partial = Path.Combine(root, ".partial-" + name);
        Directory.CreateDirectory(partial);
        var entries = new List<object>();
        try
        {
            foreach (var (label, source) in sources)
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var inner = Path.GetRelativePath(source, file);
                if (inner.StartsWith("..", StringComparison.Ordinal)) throw new InvalidOperationException("A save path escaped SaveData.");
                var relative = label == "SaveData" ? inner : Path.Combine(label, inner);
                var output = Path.Combine(partial, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output, false);
                var sourceHash = Hash(file);
                if (!string.Equals(sourceHash, Hash(output), StringComparison.Ordinal)) throw new IOException("Save hash mismatch after copy: " + relative);
                entries.Add(new { RelativePath = relative, Length = new FileInfo(file).Length, SHA256 = sourceHash });
            }
            File.WriteAllText(Path.Combine(partial, "manifest.json"), JsonSerializer.Serialize(new { CreatedUtc = DateTime.UtcNow, Sources = sources.Select(item => item.Item2).ToArray(), Files = entries }, new JsonSerializerOptions { WriteIndented = true }));
            Directory.Move(partial, destination);
            return new SaveBackupResult(destination, entries.Count, Path.Combine(destination, "manifest.json"));
        }
        catch (Exception exception) { throw new IOException("Save backup did not complete. Partial data, if any, was retained for inspection at: " + partial, exception); }
    }

    /// <summary>
    /// Read-only preflight for one mode of the single installation: game files,
    /// Multipatch start script/data and whether the network mode can be applied.
    /// </summary>
    internal static string ValidateLaunch(string gameDir, string mode, bool requireSteamClient = true)
    {
        var root = Path.GetFullPath(gameDir);
        var executable = Path.Combine(root, "Binaries", "Win64", "GearGame.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("GearGame.exe not found.", executable);
        var script = GameLayout.StartScript(root) ?? throw new FileNotFoundException("Multipatch launch script not found.", Path.Combine(root, "Gears3_Start_Game.bat"));
        if (GameLayout.ToolFilesDir(root) is null) throw new DirectoryNotFoundException("Multipatch data (graphics.json) was not found in tools\\multipatch\\_files or Gears3_Multipatch_Tool\\_files.");
        if (mode == NetworkMode.Steam && requireSteamClient && !GameRunner.IsSteamClientRunning()) throw new InvalidOperationException("Steam is not running.");
        NetworkMode.Validate(root, mode);
        return script;
    }

    private static readonly Regex LaunchLine = new(@"(?im)^[ \t]*(?:rem[ \t]+)?(?:start[ \t]+""""[ \t]+)?GearGame\.exe[ \t]+(-(?:FULLSCREEN|WINDOWED)[^\r\n]*)");
    private static readonly Regex ShellArguments = new(@"(?m)^ARGS=""(-(?:FULLSCREEN|WINDOWED)[^""\r\n]*)""");
    private static readonly Regex SafeArguments = new(@"^-(FULLSCREEN|WINDOWED) -(NOVSYNC|VSYNC) -ResX=\d+ -ResY=\d+ -language=[A-Za-z]+( -exec=Gears3_Audio\.txt)?$");

    /// <summary>Startup arguments written by the Multipatch (and Game Settings) into its start script.</summary>
    internal static string LaunchArguments(string gameDir)
    {
        var script = GameLayout.StartScript(gameDir) ?? throw new FileNotFoundException("Multipatch launch script not found.");
        var match = (script.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ? ShellArguments : LaunchLine).Match(File.ReadAllText(script));
        if (!match.Success) throw new InvalidDataException("No GearGame.exe line was found in " + Path.GetFileName(script) + ".");
        var arguments = match.Groups[1].Value.Trim();
        if (!SafeArguments.IsMatch(arguments)) throw new InvalidDataException("Unsupported startup arguments in " + Path.GetFileName(script) + ": " + arguments);
        return arguments;
    }

    /// <summary>Applies the network mode and starts GearGame.exe exactly as the Multipatch start script does.</summary>
    internal static void StartGame(string gameDir, string mode, bool backupSaves)
    {
        ValidateLaunch(gameDir, mode);
        var arguments = LaunchArguments(gameDir);
        var config = Config ?? new LauncherConfig();
        var layer = GameRunner.NeedsCompatibilityLayer ? GameRunner.FindLayer(config) : null;
        var start = GameRunner.BuildStart(gameDir, arguments, mode, config, layer);
        NetworkMode.Apply(gameDir, mode);
        if (backupSaves && (Directory.Exists(Path.Combine(gameDir, "GearGame", "SaveData")) || Directory.Exists(Path.Combine(gameDir, "Binaries", "GearGame", "SaveData"))))
            LauncherDiagnostics.Info("Pre-launch " + BackupAllSaves(gameDir));
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("GearGame.exe did not start.");
        LauncherDiagnostics.Info($"GearGame.exe started in {mode} mode (pid {process.Id}, {layer?.Kind ?? "direct"}) with: {arguments}");
    }

    private static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
}

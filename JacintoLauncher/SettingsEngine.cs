#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace JacintoLauncher;

/// <summary>
/// Graphics and controls settings: the C# form of the former tools\MultipatchSettings.ps1, so the
/// launcher no longer spawns PowerShell and the same code runs on every platform. Every change is a
/// journaled transaction: files are snapshotted first, verified afterwards and restored on failure.
/// </summary>
internal sealed class SettingsEngine
{
    private const StringComparison Ci = StringComparison.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    /// <summary>Monitor bounds in physical pixels (the launcher is DPI aware), set by the UI for "native" and "span".</summary>
    internal static Func<(int Left, int Top, int Right, int Bottom, bool Primary)[]>? Screens;

    private readonly string root, txRoot, configDir, filesDir, graphicsPath, keybindsPath, binariesDir, docsConfig, audioExecPath;

    internal SettingsEngine(string gameDir, string transactionRoot, string documentsRoot)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDir));
        txRoot = Path.GetFullPath(transactionRoot);
        configDir = Path.Combine(root, "GearGame", "Config");
        filesDir = GameLayout.ToolFilesDir(root) ?? Path.Combine(root, "tools", "multipatch", "_files");
        graphicsPath = Path.Combine(filesDir, "graphics.json");
        keybindsPath = Path.Combine(filesDir, "keybinds.json");
        binariesDir = Path.Combine(root, "Binaries", "Win64");
        docsConfig = Path.Combine(Path.GetFullPath(documentsRoot), "My Games", "Gears of War 3", "GearGame", "Config");
        // -exec= reads from ..\..\Binaries, not from the folder the game runs in.
        audioExecPath = Path.Combine(root, "Binaries", "Gears3_Audio.txt");
        Directory.CreateDirectory(txRoot);
        RecoverIncompleteTransactions();
    }

    // ---------------------------------------------------------------- file helpers
    internal static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant(); }
    private static bool Same(string? a, string? b) => string.Equals(a, b, Ci);
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Bool(bool value) => value ? "True" : "False";

    private static JsonObject ReadJsonObject(string path, bool optional = false)
    {
        if (!File.Exists(path)) { if (optional) return new JsonObject(); throw new FileNotFoundException("Required file is missing: " + path, path); }
        JsonNode? node;
        try { node = JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)); }
        catch (JsonException exception) { throw new UserActionException($"Invalid JSON at '{path}': {exception.Message} The file was edited or damaged; fix it or restore it from a backup."); }
        return node as JsonObject ?? throw new InvalidDataException($"Expected a JSON object at '{path}'.");
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var temp = path + ".jacinto-" + Guid.NewGuid().ToString("N") + ".tmp";
        var swap = path + ".jacinto-" + Guid.NewGuid().ToString("N") + ".bak";
        var existed = File.Exists(path);
        var attributes = existed ? File.GetAttributes(path) : FileAttributes.Normal;
        try
        {
            if (existed && attributes.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.WriteAllBytes(temp, bytes);
            if (existed) { File.Replace(temp, path, swap); File.SetAttributes(path, attributes); File.Delete(swap); }
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) try { File.Delete(temp); } catch { }
            if (File.Exists(swap)) try { File.Delete(swap); } catch { }
        }
    }
    private static void WriteIfChanged(string path, string before, string after) { if (after != before) WriteAtomic(path, Utf8.GetBytes(after)); }

    /// <summary>Replaces every duplicate key in the first matching section; keeps same-named keys in other sections.</summary>
    internal static string SetIniValue(string text, string section, string key, string value)
    {
        var line = key + "=" + value;
        var sectionEscaped = Regex.Escape(section);
        var keyEscaped = Regex.Escape(key);
        if (Regex.IsMatch(text, @"^\[" + sectionEscaped + @"\]\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            var match = Regex.Match(text, @"(?ms)(^\[" + sectionEscaped + @"\]\r?\n)(.*?)(?=(?m)^\[[^\]\r\n]+\]\r?$|\z)");
            if (match.Success)
            {
                var body = match.Groups[2].Value;
                var keyPattern = "(?m)^" + keyEscaped + @"=[^\r\n]*";
                var newBody = Regex.IsMatch(body, keyPattern, RegexOptions.IgnoreCase) ? Regex.Replace(body, keyPattern, _ => line) : body.TrimEnd() + "\r\n" + line + "\r\n";
                return text[..match.Index] + match.Groups[1].Value + newBody + text[(match.Index + match.Length)..];
            }
        }
        return text.TrimEnd() + "\r\n\r\n[" + section + "]\r\n" + line + "\r\n";
    }

    internal static string? GetIniValue(string text, string section, string key)
    {
        var match = Regex.Match(text, @"(?ms)^\[" + Regex.Escape(section) + @"\]\r?\n(.*?)(?=^\[[^\]\r\n]+\]\r?$|\z)");
        if (!match.Success) return null;
        var value = Regex.Match(match.Groups[1].Value, "(?m)^" + Regex.Escape(key) + @"=([^\r\n]*)");
        return value.Success ? value.Groups[1].Value : null;
    }

    private static string ReplaceLine(string text, string pattern, string value) => Regex.Replace(text, pattern, _ => value);

    // ---------------------------------------------------------------- settings values
    private static string Str(JsonObject o, string name) => o[name] is JsonValue v ? v.TryGetValue<string>(out var s) ? s : v.ToJsonString().Trim('"') : "";
    private static bool? Flag(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static bool On(JsonObject o, string name) => Flag(o, name) == true;
    // Whole numbers written as 20, 20.0 or "20" all count; the Multipatch tool and old launchers wrote floats.
    private static bool TryInt(JsonObject o, string name, out int result)
    {
        result = 0;
        if (o[name] is not JsonValue v || Flag(o, name) is not null) return false;
        if (v.TryGetValue<int>(out result)) return true;
        double d;
        if (v.TryGetValue<double>(out d) || (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)))
        {
            if (d == Math.Floor(d) && Math.Abs(d) <= int.MaxValue) { result = (int)d; return true; }
        }
        return false;
    }
    private static int Num(JsonObject o, string name) => TryInt(o, name, out var i) ? i : 0;
    private static bool IsInt(JsonObject o, string name) => TryInt(o, name, out _);
    private static string[] Disabled(JsonObject o) => o["Disabled"] is JsonArray a ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").ToArray() : Array.Empty<string>();

    private static (int X, int Y) Resolution(JsonObject settings)
    {
        var resolution = Str(settings, "Resolution");
        var fixedSize = Regex.Match(resolution, @"^(\d{3,5})x(\d{3,5})$", RegexOptions.IgnoreCase);
        if (fixedSize.Success) return (int.Parse(fixedSize.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(fixedSize.Groups[2].Value, CultureInfo.InvariantCulture));
        var screens = Screens?.Invoke();
        if (screens is null || screens.Length == 0) throw new InvalidOperationException("The display size is not available. Choose an explicit resolution.");
        if (Same(resolution, "span")) return (screens.Max(s => s.Right) - screens.Min(s => s.Left), screens.Max(s => s.Bottom) - screens.Min(s => s.Top));
        var primary = screens.FirstOrDefault(s => s.Primary, screens[0]);
        return (primary.Right - primary.Left, primary.Bottom - primary.Top);
    }

    private static void Validate(JsonObject s)
    {
        if (!Regex.IsMatch(Str(s, "Resolution"), @"^(native|span|\d{3,5}x\d{3,5})$", RegexOptions.IgnoreCase)) throw new ArgumentException("Unsupported Resolution value.");
        var size = Regex.Match(Str(s, "Resolution"), @"^(\d+)x(\d+)$", RegexOptions.IgnoreCase);
        if (size.Success && (int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) < 640 || int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture) < 480)) throw new ArgumentException("Resolution must be at least 640x480.");
        if (!IsInt(s, "FpsLimit") || Num(s, "FpsLimit") is < 0 or > 500) throw new ArgumentException("FpsLimit must be an integer from 0 to 500 (0 means the Multipatch default).");
        if (!IsInt(s, "RefreshRate") || Num(s, "RefreshRate") is < 0 or > 1000) throw new ArgumentException("RefreshRate must be an integer from 0 to 1000.");
        if (Num(s, "Fov") != 0 && Num(s, "Fov") is < 50 or > 130) throw new ArgumentException("Fov must be 0 (automatic) or 50 to 130.");
        if (!new[] { "off", "fxaa", "msaa2x", "msaa4x" }.Contains(Str(s, "AntiAliasing"), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("AntiAliasing must be off, fxaa, msaa2x or msaa4x.");
        if (!new[] { 1, 2, 4, 8, 16 }.Contains(Num(s, "Anisotropy"))) throw new ArgumentException("Anisotropy must be 1, 2, 4, 8 or 16.");
        foreach (var name in new[] { "Fullscreen", "VSync", "FovAllModes", "AmbientOcclusion", "AudioFilter", "DisableRunShake" }) if (Flag(s, name) is null) throw new ArgumentException(name + " must be a JSON boolean.");
        foreach (var name in new[] { "GraphicsQuality", "ShadowQuality", "TextureQuality", "FoliageQuality" }) if (!new[] { "low", "medium", "high", "ultra" }.Contains(Str(s, name), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException(name + " must be low, medium, high or ultra.");
        if (!Same(Str(s, "DriveType"), "ssd") && !Same(Str(s, "DriveType"), "hdd")) throw new ArgumentException("DriveType must be ssd or hdd.");
        if (s["Disabled"] is not JsonArray) throw new ArgumentException("Disabled must be a JSON array.");
        foreach (var effect in Disabled(s)) if (!Effects.ContainsKey(effect)) throw new ArgumentException("Unsupported disabled effect: " + effect);
        if (!IsInt(s, "AudioChannels") || !new[] { 32, 64, 128 }.Contains(Num(s, "AudioChannels"))) throw new ArgumentException("AudioChannels must be 32, 64 or 128.");
        foreach (var name in new[] { "MasterVolume", "MusicVolume", "EffectsVolume", "DialogueVolume" }) if (!IsInt(s, name) || Num(s, name) is < 0 or > 100) throw new ArgumentException(name + " must be an integer from 0 to 100.");
        if (!new[] { "big", "normal", "default" }.Contains(Str(s, "SubtitleSize"), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("SubtitleSize must be big, normal or default.");
    }

    // Audio / camera options of the Multi Patch Tool Game Settings; defaults are the tool's.
    private static JsonObject AddAudioDefaults(JsonObject s)
    {
        s["AudioFilter"] ??= true; s["AudioChannels"] ??= 32; s["DisableRunShake"] ??= true;
        s["MasterVolume"] ??= 100; s["MusicVolume"] ??= 100; s["EffectsVolume"] ??= 100; s["DialogueVolume"] ??= 100;
        s["SubtitleSize"] ??= "big"; s["HordeBeast"] ??= false;
        return s;
    }

    // Each sound class with its stock volume (SoundClassesAndModes.upk, from the game's ListSoundClassVolumes). A command sets one class only, so all of a group are listed.
    private static readonly (string Setting, (string Class, double Stock)[] Classes)[] VolumeGroups =
    {
        ("MusicVolume", new[] { ("GameMusic", 1.05), ("Music", 1.0), ("OptionMusic", 1.0), ("Stinger", .97), ("UserMusic", 1.0) }),
        ("DialogueVolume", new[] { ("Dialog", 1.0), ("DialogDeafening", 1.1), ("DialogDeafeningSloMo", 1.1), ("DialogLoud", 1.1), ("DialogMedium", 1.1), ("DialogNoDSP", 1.1), ("DialogNoRadio", 1.1), ("DialogSoft", 1.0), ("Chatter", 1.0), ("ChatterEnemy", 1.1), ("ChatterEnemytell", 1.1), ("ChatterFriend", 1.1), ("ChatterMPEndofRound", 1.0), ("MovieVoice", 1.0), ("OptionVoice", 1.0), ("Voice", 1.0) }),
        ("EffectsVolume", new[] { ("Ambient", .9), ("Character", .93), ("CharacterTell", 1.0), ("Cinematic", 1.0), ("EventDeafening", 1.0), ("EventLoud", 1.0), ("EventMedium", 1.0), ("Item", 1.0), ("MovieEffects", 1.0), ("OptionSFX", 1.0), ("SFX", 1.0), ("SoundModeExceptions", 1.0), ("UI", .94), ("Vehicle", .93), ("Weapon", 1.0), ("WeaponBulletEffects", .92), ("WeaponPlayer", .94), ("WeaponShared", .92), ("WeaponTell", .95), ("WeaponTellSuper", 1.0) })
    };

    /// <summary>Console commands the engine runs once at startup through -exec=. Empty when everything is at its stock value.</summary>
    private static string AudioCommands(JsonObject s)
    {
        var commands = new StringBuilder();
        if (!On(s, "AudioFilter")) commands.Append("DisableLPF\r\n");
        // Master scales every group, so the game has one class list to set and no class is set twice.
        var master = Num(s, "MasterVolume") / 100.0;
        foreach (var (setting, classes) in VolumeGroups)
            if (Num(s, setting) != 100 || master != 1)
                foreach (var (name, stock) in classes) commands.Append(CultureInfo.InvariantCulture, $"ModifySoundClass {name} Vol={stock * Num(s, setting) / 100 * master:0.###}\r\n");
        return commands.ToString();
    }

    private static readonly Dictionary<string, string[]> Effects = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MotionBlur"] = new[] { "MotionBlur", "MotionBlurPause" }, ["DepthOfField"] = new[] { "DepthOfField" }, ["Bloom"] = new[] { "Bloom" }, ["LensFlares"] = new[] { "LensFlares" }, ["LightShafts"] = new[] { "bAllowLightShafts" },
        ["Distortion"] = new[] { "Distortion", "FilteredDistortion" }, ["RadialBlur"] = new[] { "AllowRadialBlur" }, ["Subsurface"] = new[] { "AllowSubsurfaceScattering" }, ["SeparateTranslucency"] = new[] { "bAllowSeparateTranslucency" }
    };
    private static readonly (string Section, string Stock)[] RunShake = { ("GearGame.GearPawn", "0.6f"), ("GearGameContent.GearPawn_LocustKantusArmor", "0.45f") };
    private static readonly string[] TextureGroups = { "World", "WorldNormalMap", "Character", "CharacterNormalMap", "Weapon", "WeaponNormalMap", "Vehicle", "VehicleNormalMap" };

    private static string[] QualityRow(JsonObject s) => Str(s, "GraphicsQuality").ToLowerInvariant() switch
    {
        "low" => new[] { "0", "0.800000", "1", "1" }, "medium" => new[] { "1", "1.000000", "0", "0" }, "ultra" => new[] { "2", "1.250000", "-1", "-1" }, _ => new[] { "2", "1.000000", "0", "0" }
    };
    private static string[] ShadowRow(JsonObject s) => Str(s, "ShadowQuality").ToLowerInvariant() switch
    {
        "low" => new[] { "32", "512", "512", "-1", "False" }, "medium" => new[] { "64", "1024", "1024", "0", "True" }, "ultra" => new[] { "128", "2048", "2048", "1", "True" }, _ => new[] { "64", "1120", "1344", "0", "True" }
    };
    internal string? CurrentTextureLod() => File.Exists(graphicsPath) ? TextureLod(AddAudioDefaults(ReadJsonObject(graphicsPath))) : null;
    private static string TextureLod(JsonObject s) => Str(s, "TextureQuality").ToLowerInvariant() switch { "low" => "2", "medium" => "1", _ => "0" };
    private static (string Key, string Hdd, string Ssd)[] StreamingRows => new[]
    {
        ("MinRequestedMipsToConsider", "1", "11"), ("MinTimeToGuaranteeMinMipCount", "12", "2"), ("MaxTimeToGuaranteeMinMipCount", "30", "12"), ("LoadMapTimeLimit", "60.0", "20.0"),
        ("DropMipLevelsLimit", "24", "16"), ("StopIncreasingLimit", "16", "12"), ("StopStreamingLimit", "12", "8")
    };

    // ---------------------------------------------------------------- target files
    private static string[] Existing(params string[] paths) => paths.Where(File.Exists).ToArray();
    private string[] PawnFiles() => new[] { configDir, docsConfig }.SelectMany(d => new[] { "GearPawn.ini", "DefaultPawn.ini", "GearPawnMP.ini", "DefaultPawnMP.ini" }.Select(n => Path.Combine(d, n))).Where(File.Exists).ToArray();
    private string[] EngineFiles() => Existing(Path.Combine(configDir, "GearEngine.ini"), Path.Combine(configDir, "DefaultEngine.ini"), Path.Combine(root, "Engine", "Config", "BaseEngine.ini"), Path.Combine(docsConfig, "GearEngine.ini"));
    private string[] CameraFiles() => Existing(Path.Combine(configDir, "GearCamera.ini"), Path.Combine(configDir, "DefaultCamera.ini"), Path.Combine(docsConfig, "GearCamera.ini"));
    private string[] LaunchFiles() => Existing(Path.Combine(root, "Gears3_Start_Game.bat"), Path.Combine(binariesDir, "StartCampaign.bat"), Path.Combine(root, "Gears3_Start_Game_Debug.bat"), Path.Combine(root, "Gears3_Start_Game.sh"), Path.Combine(root, "Gears3_Start_Game_Debug.sh"));
    private string[] ControlFiles() => Existing(Path.Combine(docsConfig, "GearInput.ini"), Path.Combine(configDir, "GearInput.ini"), Path.Combine(configDir, "DefaultInput.ini"));
    private string[] MenuFiles() => Existing(Path.Combine(configDir, "DefaultUI.ini"), Path.Combine(configDir, "GearUI.ini"), Path.Combine(docsConfig, "GearUI.ini"));
    private string[] FontFiles() => new[] { SubtitleFonts.NormalFile, SubtitleFonts.BigFile }.Select(n => Path.Combine(root, "GearGame", "Content", "Interface", n)).ToArray();

    private string[] AllowedTargets(string kind) => kind == "Graphics"
        ? new[] { graphicsPath }.Concat(EngineFiles()).Concat(CameraFiles()).Concat(LaunchFiles()).Concat(PawnFiles()).Concat(MenuFiles()).Concat(FontFiles()).Append(audioExecPath).ToArray()
        : new[] { graphicsPath, keybindsPath }.Concat(ControlFiles()).ToArray();

    private void AssertPreflight(string kind, bool writing = true)
    {
        if (!File.Exists(Path.Combine(binariesDir, "GearGame.exe"))) throw new UserActionException("Binaries\\Win64\\GearGame.exe is missing from the game folder. Choose the folder that has Binaries and GearGame in it.");
        if (GameLayout.StartScript(root) is null) throw new UserActionException("Gears3_Start_Game.bat is missing from the game folder. Install the Multipatch on the Home screen.");
        if (!File.Exists(graphicsPath)) throw new FileNotFoundException("Multipatch graphics.json is missing.");
        if (writing && GameLayout.GameRunning()) throw new UserActionException("Close GearGame before applying settings.");
        var journalPath = Path.Combine(filesDir, "installation-state.json");
        if (File.Exists(journalPath))
        {
            var status = Str(ReadJsonObject(journalPath), "Status");
            if (!Same(status, "complete")) throw new InvalidOperationException($"Multipatch journal is '{status}'; settings changes are blocked until installation is complete.");
        }
        if (kind == "Graphics" && EngineFiles().Length == 0) throw new FileNotFoundException("No effective engine INI files were found.");
        if (kind == "Controls" && ControlFiles().Length == 0) throw new FileNotFoundException("No supported GearInput.ini or DefaultInput.ini was found.");
    }

    // ---------------------------------------------------------------- transactions
    private string ManifestPath(string id) => Path.Combine(txRoot, id, "transaction.json");
    private void SaveTransaction(JsonObject tx) => File.WriteAllText(ManifestPath(Str(tx, "Id")), tx.ToJsonString(Pretty), Utf8);

    private JsonObject NewTransaction(string kind, IEnumerable<string> targets)
    {
        var id = Guid.NewGuid().ToString("N");
        var snapDir = Path.Combine(txRoot, id, "before");
        Directory.CreateDirectory(snapDir);
        var entries = new JsonArray();
        var i = 0;
        foreach (var path in targets.Select(Path.GetFullPath).Distinct())
        {
            var exists = File.Exists(path);
            var entry = new JsonObject { ["Path"] = path, ["Exists"] = exists, ["Snapshot"] = null, ["BeforeSHA256"] = null, ["AfterSHA256"] = null, ["Attributes"] = null };
            if (exists)
            {
                var snapshot = Path.Combine(snapDir, $"{i++:D3}.bin");
                File.Copy(path, snapshot);
                entry["Snapshot"] = snapshot; entry["BeforeSHA256"] = Hash(path); entry["Attributes"] = (int)File.GetAttributes(path);
            }
            entries.Add(entry);
        }
        var tx = new JsonObject { ["Id"] = id, ["Kind"] = kind, ["GameRoot"] = root, ["CreatedUtc"] = DateTime.UtcNow.ToString("o"), ["Status"] = "applying", ["Entries"] = entries };
        SaveTransaction(tx);
        return tx;
    }

    private static void RestoreSnapshot(JsonObject tx)
    {
        foreach (var entry in tx["Entries"]!.AsArray().Cast<JsonObject>())
        {
            var path = Str(entry, "Path");
            if (On(entry, "Exists"))
            {
                var snapshot = Str(entry, "Snapshot");
                var before = Str(entry, "BeforeSHA256");
                var attributes = (FileAttributes)Num(entry, "Attributes");
                if (!File.Exists(snapshot)) throw new FileNotFoundException("Rollback snapshot is missing: " + snapshot);
                if (!Same(Hash(snapshot), before)) throw new InvalidDataException("Rollback snapshot hash mismatch: " + snapshot);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && Same(Hash(path), before)) { File.SetAttributes(path, attributes); continue; }
                if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
                WriteAtomic(path, File.ReadAllBytes(snapshot));
                File.SetAttributes(path, attributes);
                if (!Same(Hash(path), before)) throw new IOException($"Rollback verification failed for '{path}'.");
            }
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private JsonObject Transact(string kind, IEnumerable<string> targets, Action body)
    {
        using var applyLock = new FileStream(Path.Combine(txRoot, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var tx = NewTransaction(kind, targets);
        try
        {
            body();
            foreach (var entry in tx["Entries"]!.AsArray().Cast<JsonObject>()) if (File.Exists(Str(entry, "Path"))) entry["AfterSHA256"] = Hash(Str(entry, "Path"));
            tx["Status"] = "committed"; tx["CompletedUtc"] = DateTime.UtcNow.ToString("o");
            SaveTransaction(tx);
            return tx;
        }
        catch (Exception failure)
        {
            try { RestoreSnapshot(tx); tx["Status"] = "rolledback"; tx["Failure"] = failure.Message; SaveTransaction(tx); }
            catch (Exception rollback) { throw new IOException($"Apply failed ('{failure.Message}') and automatic rollback also failed ('{rollback.Message}'). Transaction: {Str(tx, "Id")}", failure); }
            throw new InvalidOperationException("Apply failed and was rolled back: " + failure.Message, failure);
        }
    }

    internal JsonObject Rollback(string? id, string? kind)
    {
        var directories = id is { Length: > 0 } ? new[] { Path.Combine(txRoot, id) } : Directory.GetDirectories(txRoot)
            .Where(d => File.Exists(Path.Combine(d, "transaction.json"))).OrderByDescending(d => Str(ReadJsonObject(Path.Combine(d, "transaction.json")), "CreatedUtc"), StringComparer.Ordinal).ToArray();
        foreach (var directory in directories)
        {
            var manifest = Path.Combine(directory, "transaction.json");
            if (!File.Exists(manifest)) continue;
            var tx = ReadJsonObject(manifest);
            if (Str(tx, "Status") != "committed" || !SamePath(Str(tx, "GameRoot"), root) || (kind is not null && Str(tx, "Kind") != kind)) continue;
            var allowed = AllowedTargets(Str(tx, "Kind"));
            foreach (var entry in tx["Entries"]!.AsArray().Cast<JsonObject>())
            {
                var path = Path.GetFullPath(Str(entry, "Path"));
                if (!allowed.Any(a => SamePath(a, path))) throw new InvalidDataException("Rollback manifest contains an out-of-scope path: " + path);
                var after = Str(entry, "AfterSHA256");
                if (On(entry, "Exists"))
                {
                    if (!File.Exists(Str(entry, "Snapshot")) || !Same(Hash(Str(entry, "Snapshot")), Str(entry, "BeforeSHA256"))) throw new InvalidDataException($"Rollback refused because the snapshot for '{path}' is missing or corrupt.");
                    if (after.Length != 0) { if (!File.Exists(path) || !Same(Hash(path), after)) throw new InvalidOperationException($"Rollback refused because '{path}' changed after the transaction."); }
                    else if (File.Exists(path)) throw new InvalidOperationException($"Rollback refused because '{path}' was recreated after the transaction removed it.");
                }
                else if (File.Exists(path) && !Same(Hash(path), after)) throw new InvalidOperationException($"Rollback refused because '{path}' changed after the transaction.");
            }
            RestoreSnapshot(tx);
            tx["Status"] = "rolledback"; tx["RolledBackUtc"] = DateTime.UtcNow.ToString("o");
            SaveTransaction(tx);
            return new JsonObject { ["Success"] = true, ["TransactionId"] = Str(tx, "Id"), ["Status"] = "Previous state restored and hashes verified." };
        }
        throw new InvalidOperationException("No committed settings transaction is available to roll back.");
    }

    private void RecoverIncompleteTransactions()
    {
        var incomplete = Directory.GetDirectories(txRoot).Select(d => Path.Combine(d, "transaction.json")).Where(File.Exists).Select(p => ReadJsonObject(p))
            .Where(tx => Str(tx, "Status") == "applying" && SamePath(Str(tx, "GameRoot"), root)).ToArray();
        if (incomplete.Length == 0) return;
        using var applyLock = new FileStream(Path.Combine(txRoot, "apply.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var tx in incomplete)
        {
            var allowed = AllowedTargets(Str(tx, "Kind"));
            foreach (var entry in tx["Entries"]!.AsArray().Cast<JsonObject>())
            {
                var path = Path.GetFullPath(Str(entry, "Path"));
                if (!allowed.Any(a => SamePath(a, path))) throw new InvalidDataException("Incomplete transaction contains an out-of-scope path: " + path);
                if (On(entry, "Exists") && (!File.Exists(Str(entry, "Snapshot")) || !Same(Hash(Str(entry, "Snapshot")), Str(entry, "BeforeSHA256")))) throw new InvalidDataException($"Cannot recover incomplete settings transaction {Str(tx, "Id")}: a snapshot is missing or corrupt.");
            }
            RestoreSnapshot(tx);
            tx["Status"] = "rolledback"; tx["RecoveredUtc"] = DateTime.UtcNow.ToString("o");
            SaveTransaction(tx);
        }
    }

    // ---------------------------------------------------------------- graphics
    internal JsonObject GetGraphics()
    {
        AssertPreflight("Graphics", false);
        var graphics = AddAudioDefaults(ReadJsonObject(graphicsPath));
        Validate(graphics);
        return new JsonObject { ["Success"] = true, ["Graphics"] = graphics.DeepClone(), ["Applied"] = EffectiveState(graphics) };
    }

    internal JsonObject ApplyGraphics(JsonObject request)
    {
        var merged = ReadJsonObject(graphicsPath);
        foreach (var property in request) merged[property.Key] = property.Value?.DeepClone();
        var settings = AddAudioDefaults(merged);
        Validate(settings);
        AssertPreflight("Graphics");
        var engineFiles = EngineFiles(); var cameraFiles = CameraFiles(); var launchFiles = LaunchFiles(); var pawnFiles = PawnFiles(); var menuFiles = MenuFiles();
        // Only a full installation has the fonts; a folder patched by the old tool keeps what the tool chose.
        var managed = MultipatchInstaller.Managed(root);
        var (normalFont, largeFont) = SubtitleFonts.Sources(Language(settings), Str(settings, "SubtitleSize").ToLowerInvariant(), On(settings, "ForceWarfareFonts"));
        var fontWrites = managed ? MultipatchInstaller.FontWrites(root, normalFont, largeFont) : Array.Empty<(string Target, string Source)>();
        var targets = new[] { graphicsPath }.Concat(engineFiles).Concat(cameraFiles).Concat(launchFiles).Concat(pawnFiles).Concat(menuFiles).Concat(fontWrites.Select(w => w.Target)).Append(audioExecPath);
        var tx = Transact("Graphics", targets, () =>
        {
            MultipatchInstaller.WriteFonts(root, fontWrites);
            var fontName = managed ? SubtitleFonts.FontName(Language(settings), Str(settings, "SubtitleSize").ToLowerInvariant(), On(settings, "ForceWarfareFonts")) : null;
            foreach (var path in engineFiles.Where(p => fontName is not null && !Same(Path.GetFileName(p), "BaseEngine.ini")))
            {
                var text = File.ReadAllText(path);
                WriteIfChanged(path, text, ReplaceLine(text, @"(?m)^SubtitleFontName=[^\r\n]*", "SubtitleFontName=" + fontName));
            }
            // The build ships Horde and Beast switched off in the main menu.
            foreach (var path in menuFiles)
            {
                var text = File.ReadAllText(path);
                WriteIfChanged(path, text, Regex.Replace(text, @"(\(Label=""(?:Horde|Siege)"",ImagePath=""[^""]*"",Next=MAINMENUOP_(?:Horde|Siege),bIsDisabled=)(?:true|false)\)", m => m.Groups[1].Value + (On(settings, "HordeBeast") ? "false" : "true") + ")"));
            }
            var (rx, ry) = Resolution(settings);
            var aspect = (double)rx / ry;
            var fov = Num(settings, "Fov") >= 50 ? Num(settings, "Fov") : aspect >= 2.0 ? 65 : 80;
            foreach (var path in engineFiles) WriteIfChanged(path, File.ReadAllText(path), ApplyEngine(File.ReadAllText(path), settings, rx, ry));
            // Run camera shake: 0 removes it; the game values are 0.6 (everyone) and 0.45 (armored Kantus). Only sections that exist.
            foreach (var path in pawnFiles)
            {
                var text = File.ReadAllText(path); var updated = text;
                foreach (var (section, stock) in RunShake)
                    if (Regex.IsMatch(updated, @"(?m)^\[" + Regex.Escape(section) + @"\]\r?$", RegexOptions.IgnoreCase)) updated = SetIniValue(updated, section, "RoadieRunShakyCamDampeningFactor", On(settings, "DisableRunShake") ? "0.0f" : stock);
                WriteIfChanged(path, text, updated);
            }
            // DisableLPF removes the muffled low-pass filter; the volumes are per sound class. Earlier versions put this file where the engine never looked.
            var audioCommands = AudioCommands(settings);
            if (audioCommands.Length != 0) WriteAtomic(audioExecPath, Encoding.ASCII.GetBytes(audioCommands));
            else if (File.Exists(audioExecPath)) File.Delete(audioExecPath);
            var misplaced = Path.Combine(binariesDir, "Gears3_Audio.txt");
            if (File.Exists(misplaced)) File.Delete(misplaced);
            var iconScale = aspect > 2.0 ? "0.68f" : aspect > 1.8 ? "0.85f" : "1.00f";
            var fovText = fov.ToString(CultureInfo.InvariantCulture);
            foreach (var path in cameraFiles)
            {
                var text = File.ReadAllText(path);
                var updated = ReplaceLine(text, @"(?m)^BaseFOV=[^\r\n]*", "BaseFOV=" + fovText);
                updated = On(settings, "FovAllModes") ? ReplaceLine(updated, @"(?m)^FOVAngle=[^\r\n]*", "FOVAngle=" + fovText) : SetIniValue(updated, "GearGame.GameplayCam_Default", "FOVAngle", fovText);
                WriteIfChanged(path, text, ReplaceLine(updated, @"(?m)^WeaponIconScale=[^\r\n]*", "WeaponIconScale=" + iconScale));
            }
            var startArgs = $"{(On(settings, "Fullscreen") ? "-FULLSCREEN" : "-WINDOWED")} {(On(settings, "VSync") ? "-VSYNC" : "-NOVSYNC")} -ResX={rx} -ResY={ry} -language={Language(settings)}{(audioCommands.Length == 0 ? "" : " -exec=Gears3_Audio.txt")}";
            foreach (var path in launchFiles)
            {
                var text = File.ReadAllText(path);
                var name = Path.GetFileName(path);
                var pattern = Regex.IsMatch(text, @"(?m)^rem GearGame\.exe ", RegexOptions.IgnoreCase) ? @"(?m)(?<=rem GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*"
                    : Same(name, "Gears3_Start_Game.bat") ? @"(?m)(?<=GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*"
                    : Same(name, "StartCampaign.bat") ? @"(?m)(?<=^GearGame\.exe )-(?:FULLSCREEN|WINDOWED)[^\r\n]*"
                    : Same(name, "Gears3_Start_Game.sh") ? @"(?m)(?<=^ARGS="")[^""\r\n]*(?="")"
                    : Same(name, "Gears3_Start_Game_Debug.sh") ? @"(?m)(?<=ARGS="")-(?:FULLSCREEN|WINDOWED)[^""\r\n]*(?="")|(?<=\[ -z ""\$ARGS"" \] && ARGS="")[^""\r\n]*(?="")"
                    : @"(?m)(?<=^set ""ARGS=)[^""\r\n]*(?="")";
                if (!Regex.IsMatch(text, pattern)) throw new InvalidDataException($"Could not locate the Multipatch startup arguments in '{path}'.");
                WriteIfChanged(path, text, Regex.Replace(text, pattern, _ => startArgs));
            }
            var json = ReadJsonObject(graphicsPath);
            foreach (var property in settings) json[property.Key] = property.Value?.DeepClone();
            WriteAtomic(graphicsPath, Utf8.GetBytes(json.ToJsonString(Pretty)));
            if (EffectiveState(json)["IsApplied"]?.GetValue<bool>() != true) throw new InvalidDataException("Post-apply verification found configuration files that do not match the saved settings.");
        });
        return new JsonObject { ["Success"] = true, ["TransactionId"] = Str(tx, "Id"), ["Status"] = "Applied and file values verified; restart game if it was already open.", ["Applied"] = EffectiveState(ReadJsonObject(graphicsPath)) };
    }

    private static string Language(JsonObject settings) => Str(settings, "Language") is { Length: > 0 } language ? language : "INT";

    private static string ApplyEngine(string text, JsonObject s, int rx, int ry)
    {
        var aspect = (double)rx / ry;
        var updated = ReplaceLine(text, @"(?m)^ResX=[^\r\n]*", "ResX=" + rx);
        updated = ReplaceLine(updated, @"(?m)^ResY=[^\r\n]*", "ResY=" + ry);
        updated = ReplaceLine(updated, @"(?m)^AspectRatioAxisConstraint=[^\r\n]*", "AspectRatioAxisConstraint=" + (aspect >= 2.0 ? "AspectRatio_MaintainYFOV" : "AspectRatio_MaintainXFOV"));
        updated = ReplaceLine(updated, @"(?m)^Fullscreen=[^\r\n]*", "Fullscreen=" + Bool(On(s, "Fullscreen")));
        updated = ReplaceLine(updated, @"(?m)^UseVsync=[^\r\n]*", "UseVsync=" + Bool(On(s, "VSync")));
        updated = Regex.Replace(updated, @"(?m)^Language=[a-zA-Z]{3}(?=\r?$)", "Language=" + Language(s));
        updated = Regex.Replace(updated, @"(?m)^LanguageExt=[^\r\n]*", "LanguageExt=" + Language(s));
        string Set(string section, string key, string value) => updated = SetIniValue(updated, section, key, value);
        Set("SystemSettings", "OneFrameThreadLag", "True");
        var refresh = Num(s, "RefreshRate").ToString(CultureInfo.InvariantCulture);
        Set("WinDrv.WindowsClient", "MinAllowableRefreshRate", refresh); Set("WinDrv.WindowsClient", "MaxAllowableRefreshRate", refresh);
        var limited = Num(s, "FpsLimit") > 0;
        var fps = limited ? Num(s, "FpsLimit").ToString(CultureInfo.InvariantCulture) : "300";
        Set("Engine.Engine", "bSmoothFrameRate", Bool(limited)); Set("Engine.Engine", "MinSmoothedFrameRate", "0"); Set("Engine.Engine", "MaxSmoothedFrameRate", fps);
        Set("Engine.GameEngine", "bSmoothFrameRate", Bool(limited)); Set("Engine.GameEngine", "MaxSmoothedFrameRate", fps);
        var q = QualityRow(s);
        Set("SystemSettings", "DetailMode", q[0]); Set("SystemSettings", "MaxDrawDistanceScale", q[1]); Set("SystemSettings", "SkeletalMeshLODBias", q[2]); Set("SystemSettings", "StaticMeshLODBias", q[3]); Set("SystemSettings", "bUseMaxQualityMode", "False");
        var shadow = ShadowRow(s);
        Set("SystemSettings", "DynamicShadows", "True"); Set("SystemSettings", "LightEnvironmentShadows", "True"); Set("SystemSettings", "MinShadowResolution", shadow[0]); Set("SystemSettings", "MaxShadowResolution", shadow[1]);
        Set("SystemSettings", "MaxWholeSceneDominantShadowResolution", shadow[2]); Set("SystemSettings", "ShadowFilterQualityBias", shadow[3]); Set("SystemSettings", "bAllowWholeSceneDominantShadows", shadow[4]);
        var lod = TextureLod(s);
        foreach (var group in TextureGroups) updated = Regex.Replace(updated, @"(?m)^(TEXTUREGROUP_" + group + @"=\([^\r\n]*LODBias=)-?\d+([^\r\n]*\))", m => m.Groups[1].Value + lod + m.Groups[2].Value);
        var foliage = Str(s, "FoliageQuality").ToLowerInvariant();
        Set("SystemSettings", "SpeedTreeLeaves", Bool(foliage != "low")); Set("SystemSettings", "SpeedTreeFronds", Bool(foliage is "high" or "ultra"));
        Set("SystemSettings", "MaxAnisotropy", Num(s, "Anisotropy").ToString(CultureInfo.InvariantCulture));
        var aa = Str(s, "AntiAliasing").ToLowerInvariant();
        // Temporal AA stays off: with it on, the engine asserts as soon as a level draws a reflection probe.
        Set("SystemSettings", "bAllowTemporalAA", Bool(false)); Set("SystemSettings", "bAllowD3D9MSAA", Bool(aa.StartsWith("msaa", StringComparison.Ordinal))); Set("SystemSettings", "MaxMultiSamples", aa switch { "msaa2x" => "2", "msaa4x" => "4", _ => "1" });
        Set("SystemSettings", "AmbientOcclusion", Bool(On(s, "AmbientOcclusion")));
        var disabled = Disabled(s);
        foreach (var (effect, keys) in Effects) foreach (var key in keys) Set("SystemSettings", key, Bool(!disabled.Contains(effect, StringComparer.OrdinalIgnoreCase)));
        var hdd = Same(Str(s, "DriveType"), "hdd");
        Set("SystemSettings", "OnlyStreamInTextures", Bool(hdd)); Set("Engine.Engine", "bEnableMipLevelFading", Bool(hdd));
        foreach (var (key, whenHdd, whenSsd) in StreamingRows) Set("TextureStreaming", key, hdd ? whenHdd : whenSsd);
        // Audio channels (simultaneous sounds; the game ships 32), as the Multi Patch Tool writes them.
        var channels = Num(s, "AudioChannels").ToString(CultureInfo.InvariantCulture);
        foreach (var section in new[] { "XAudio2.XAudio2Device", "ALAudio.ALAudioDevice" }) Set(section, "MaxChannels", channels);
        return updated;
    }

    private JsonObject EffectiveState(JsonObject s)
    {
        var (rx, ry) = Resolution(s);
        var limited = Num(s, "FpsLimit") > 0;
        var aa = Str(s, "AntiAliasing").ToLowerInvariant();
        var fov = (Num(s, "Fov") >= 50 ? Num(s, "Fov") : (double)rx / ry >= 2 ? 65 : 80).ToString(CultureInfo.InvariantCulture);
        var q = QualityRow(s); var shadow = ShadowRow(s); var lod = TextureLod(s);
        var hdd = Same(Str(s, "DriveType"), "hdd");
        var foliage = Str(s, "FoliageQuality").ToLowerInvariant();
        var refresh = Num(s, "RefreshRate").ToString(CultureInfo.InvariantCulture);
        var disabled = Disabled(s);
        var allMatch = true;
        var managed = MultipatchInstaller.Managed(root);
        var fontName = managed ? SubtitleFonts.FontName(Language(s), Str(s, "SubtitleSize").ToLowerInvariant(), On(s, "ForceWarfareFonts")) : null;
        // What the audit shows: the file, the setting, what is there and what should be.
        var problems = new JsonArray();
        void Miss(string file, string key, string? actual, string expected) =>
            problems.Add(new JsonObject { ["File"] = Path.GetRelativePath(root, file), ["Key"] = key, ["Actual"] = actual, ["Expected"] = expected });
        var states = new JsonArray();
        var primaryEngine = File.Exists(Path.Combine(docsConfig, "GearEngine.ini")) ? Path.Combine(docsConfig, "GearEngine.ini") : Path.Combine(configDir, "GearEngine.ini");
        foreach (var path in EngineFiles())
        {
            var text = File.ReadAllText(path);
            var isPrimary = SamePath(path, primaryEngine);
            var fileMatch = true;
            var resX = Regex.Match(text, @"(?m)^ResX=(\d+)"); var resY = Regex.Match(text, @"(?m)^ResY=(\d+)");
            var rows = new (string Key, string? Actual, string Expected)[]
            {
                ("Resolution", resX.Success && resY.Success ? resX.Groups[1].Value + "x" + resY.Groups[1].Value : "", rx + "x" + ry),
                ("Fullscreen", GetIniValue(text, "SystemSettings", "Fullscreen"), Bool(On(s, "Fullscreen"))), ("UseVsync", GetIniValue(text, "SystemSettings", "UseVsync"), Bool(On(s, "VSync"))),
                ("MaxSmoothedFrameRate", GetIniValue(text, "Engine.Engine", "MaxSmoothedFrameRate"), limited ? Num(s, "FpsLimit").ToString(CultureInfo.InvariantCulture) : "300"), ("bSmoothFrameRate", GetIniValue(text, "Engine.Engine", "bSmoothFrameRate"), Bool(limited)),
                ("MaxMultiSamples", GetIniValue(text, "SystemSettings", "MaxMultiSamples"), aa switch { "msaa2x" => "2", "msaa4x" => "4", _ => "1" }), ("bAllowD3D9MSAA", GetIniValue(text, "SystemSettings", "bAllowD3D9MSAA"), Bool(aa.StartsWith("msaa", StringComparison.Ordinal))), ("bAllowTemporalAA", GetIniValue(text, "SystemSettings", "bAllowTemporalAA"), Bool(false)),
                ("MaxAnisotropy", GetIniValue(text, "SystemSettings", "MaxAnisotropy"), Num(s, "Anisotropy").ToString(CultureInfo.InvariantCulture)), ("AmbientOcclusion", GetIniValue(text, "SystemSettings", "AmbientOcclusion"), Bool(On(s, "AmbientOcclusion"))),
                ("MinAllowableRefreshRate", GetIniValue(text, "WinDrv.WindowsClient", "MinAllowableRefreshRate"), refresh), ("MaxAllowableRefreshRate", GetIniValue(text, "WinDrv.WindowsClient", "MaxAllowableRefreshRate"), refresh),
                ("DetailMode", GetIniValue(text, "SystemSettings", "DetailMode"), q[0]), ("MaxDrawDistanceScale", GetIniValue(text, "SystemSettings", "MaxDrawDistanceScale"), q[1]), ("SkeletalMeshLODBias", GetIniValue(text, "SystemSettings", "SkeletalMeshLODBias"), q[2]), ("StaticMeshLODBias", GetIniValue(text, "SystemSettings", "StaticMeshLODBias"), q[3]), ("bUseMaxQualityMode", GetIniValue(text, "SystemSettings", "bUseMaxQualityMode"), "False"),
                ("MinShadowResolution", GetIniValue(text, "SystemSettings", "MinShadowResolution"), shadow[0]), ("MaxShadowResolution", GetIniValue(text, "SystemSettings", "MaxShadowResolution"), shadow[1]), ("MaxWholeSceneDominantShadowResolution", GetIniValue(text, "SystemSettings", "MaxWholeSceneDominantShadowResolution"), shadow[2]), ("ShadowFilterQualityBias", GetIniValue(text, "SystemSettings", "ShadowFilterQualityBias"), shadow[3]), ("bAllowWholeSceneDominantShadows", GetIniValue(text, "SystemSettings", "bAllowWholeSceneDominantShadows"), shadow[4]),
                ("SpeedTreeLeaves", GetIniValue(text, "SystemSettings", "SpeedTreeLeaves"), Bool(foliage != "low")), ("SpeedTreeFronds", GetIniValue(text, "SystemSettings", "SpeedTreeFronds"), Bool(foliage is "high" or "ultra")), ("OnlyStreamInTextures", GetIniValue(text, "SystemSettings", "OnlyStreamInTextures"), Bool(hdd)), ("bEnableMipLevelFading", GetIniValue(text, "Engine.Engine", "bEnableMipLevelFading"), Bool(hdd))
            };
            foreach (var (key, actual, expected) in rows)
            {
                if (actual is null) { if (isPrimary) { allMatch = false; fileMatch = false; Miss(path, key, null, expected); } }
                else if (!Same(actual, expected)) { allMatch = false; fileMatch = false; Miss(path, key, actual, expected); }
            }
            // A secondary file may simply not have these keys until the first Apply.
            foreach (var (key, whenHdd, whenSsd) in StreamingRows) if (GetIniValue(text, "TextureStreaming", key) is var value && (value is not null || isPrimary) && !Same(value, hdd ? whenHdd : whenSsd)) { allMatch = false; Miss(path, key, value, hdd ? whenHdd : whenSsd); }
            foreach (var (effect, keys) in Effects) foreach (var key in keys) if (GetIniValue(text, "SystemSettings", key) is var value && (value is not null || disabled.Contains(effect, StringComparer.OrdinalIgnoreCase)) && !Same(value, Bool(!disabled.Contains(effect, StringComparer.OrdinalIgnoreCase)))) { allMatch = false; Miss(path, key, value, Bool(!disabled.Contains(effect, StringComparer.OrdinalIgnoreCase))); }
            var textureRows = Regex.Matches(text, @"(?m)^TEXTUREGROUP_(?:World|WorldNormalMap|Character|CharacterNormalMap|Weapon|WeaponNormalMap|Vehicle|VehicleNormalMap)=\([^\r\n]*LODBias=-?\d+[^\r\n]*\)");
            foreach (Match row in textureRows) if (Regex.Match(row.Value, @"LODBias=(-?\d+)").Groups[1].Value is var bias && bias != lod) { allMatch = false; fileMatch = false; Miss(path, row.Value.Split('=')[0] + " LODBias", bias, lod); }
            if (isPrimary && textureRows.Count == 0) { allMatch = false; fileMatch = false; Miss(path, "TEXTUREGROUP LODBias", null, lod); }
            foreach (Match language in Regex.Matches(text, @"(?m)^(Language(?:Ext)?)=([a-zA-Z]{3})\r?$")) if (!Same(language.Groups[2].Value, Language(s))) { allMatch = false; fileMatch = false; Miss(path, language.Groups[1].Value, language.Groups[2].Value, Language(s)); }
            if (fontName is not null && !Same(Path.GetFileName(path), "BaseEngine.ini") && Regex.Match(text, @"(?m)^SubtitleFontName=([^\r\n]*)") is { Success: true } font && !Same(font.Groups[1].Value, fontName)) { allMatch = false; fileMatch = false; Miss(path, "SubtitleFontName", font.Groups[1].Value, fontName); }
            states.Add(new JsonObject { ["File"] = path, ["Matches"] = fileMatch, ["TextureGroupsChecked"] = textureRows.Count });
        }
        var primaryCamera = File.Exists(Path.Combine(docsConfig, "GearCamera.ini")) ? Path.Combine(docsConfig, "GearCamera.ini") : Path.Combine(configDir, "GearCamera.ini");
        foreach (var path in CameraFiles())
        {
            var text = File.ReadAllText(path);
            var baseFov = Regex.Match(text, @"(?m)^BaseFOV=([^\r\n]*)");
            var values = On(s, "FovAllModes") ? Regex.Matches(text, @"(?m)^FOVAngle=([^\r\n]*)").Select(m => (string?)m.Groups[1].Value).ToArray() : new[] { GetIniValue(text, "GearGame.GameplayCam_Default", "FOVAngle") };
            var ok = (!baseFov.Success || Same(baseFov.Groups[1].Value, fov)) && values.Length > 0 && values.All(v => Same(v, fov));
            if (!ok && SamePath(path, primaryCamera)) { allMatch = false; Miss(path, "FOV", baseFov.Success ? baseFov.Groups[1].Value : values.FirstOrDefault(), fov); }
            states.Add(new JsonObject { ["File"] = path, ["ExpectedFov"] = fov, ["Matches"] = ok });
        }
        var audioCommands = AudioCommands(s);
        var usesExec = audioCommands.Length != 0;
        foreach (var path in LaunchFiles())
        {
            var text = File.ReadAllText(path);
            var ok = text.Contains($"-ResX={rx} -ResY={ry}", StringComparison.Ordinal) && text.Contains(On(s, "Fullscreen") ? "-FULLSCREEN" : "-WINDOWED", StringComparison.Ordinal)
                && text.Contains(On(s, "VSync") ? "-VSYNC" : "-NOVSYNC", StringComparison.Ordinal) && text.Contains("-exec=Gears3_Audio.txt", StringComparison.Ordinal) == usesExec
                && text.Contains("-language=" + Language(s), StringComparison.OrdinalIgnoreCase);
            if (!ok) { allMatch = false; Miss(path, "start arguments", null, $"-ResX={rx} -ResY={ry} {(On(s, "Fullscreen") ? "-FULLSCREEN" : "-WINDOWED")} {(On(s, "VSync") ? "-VSYNC" : "-NOVSYNC")} -language={Language(s)}{(usesExec ? " -exec=Gears3_Audio.txt" : "")}"); }
            states.Add(new JsonObject { ["File"] = path, ["LaunchArgumentsMatch"] = ok });
        }
        // Audio: channels in every engine INI that has the audio sections, the filter command file, run shake in the pawn INIs.
        var channels = Num(s, "AudioChannels").ToString(CultureInfo.InvariantCulture);
        foreach (var path in EngineFiles())
        {
            var text = File.ReadAllText(path);
            foreach (var section in new[] { "XAudio2.XAudio2Device", "ALAudio.ALAudioDevice" }) if (GetIniValue(text, section, "MaxChannels") is { } value && !Same(value, channels)) { allMatch = false; Miss(path, "MaxChannels", value, channels); }
        }
        var execOk = usesExec ? File.Exists(audioExecPath) && File.ReadAllText(audioExecPath) == audioCommands : !File.Exists(audioExecPath);
        if (!execOk) { allMatch = false; Miss(audioExecPath, "audio commands", null, usesExec ? "the volume and filter commands" : "no file"); }
        foreach (var path in PawnFiles())
        {
            var text = File.ReadAllText(path);
            foreach (var (section, stock) in RunShake) if (GetIniValue(text, section, "RoadieRunShakyCamDampeningFactor") is { } value && !Same(value, On(s, "DisableRunShake") ? "0.0f" : stock)) { allMatch = false; Miss(path, "RoadieRunShakyCamDampeningFactor", value, On(s, "DisableRunShake") ? "0.0f" : stock); }
        }
        var menuDisabled = On(s, "HordeBeast") ? "false" : "true";
        foreach (var path in MenuFiles())
            foreach (Match entry in Regex.Matches(File.ReadAllText(path), @"\(Label=""(Horde|Siege)"",ImagePath=""[^""]*"",Next=MAINMENUOP_(?:Horde|Siege),bIsDisabled=(true|false)\)"))
                if (!Same(entry.Groups[2].Value, menuDisabled)) { allMatch = false; Miss(path, entry.Groups[1].Value + " bIsDisabled", entry.Groups[2].Value, menuDisabled); }
        // The subtitle font files: what Apply would still have to write for this size and language.
        if (managed)
        {
            var (normalFont, largeFont) = SubtitleFonts.Sources(Language(s), Str(s, "SubtitleSize").ToLowerInvariant(), On(s, "ForceWarfareFonts"));
            foreach (var (target, _) in MultipatchInstaller.FontWrites(root, normalFont, largeFont)) { allMatch = false; Miss(target, "subtitle font", null, Str(s, "SubtitleSize")); }
        }
        return new JsonObject { ["IsApplied"] = allMatch, ["Files"] = states, ["Problems"] = problems };
    }

    // ---------------------------------------------------------------- controls
    private static readonly (string Command, string Action, string Default)[] KbActions =
    {
        ("GBA_MoveForward", "Move forward", "W"), ("GBA_MoveBackward", "Move backward", "S"), ("GBA_StrafeLeft", "Strafe left", "A"), ("GBA_StrafeRight", "Strafe right", "D"),
        ("GBA_MoveAction", "Cover / Roll / Sprint / Vault", "SpaceBar"), ("GBA_Crouch", "Crouch / Switch shoulder", "C"), ("GBA_Walk", "Walk", "LeftControl"), ("GBA_Shoot", "Fire", "LeftMouseButton"), ("GBA_Target", "Aim", "RightMouseButton"),
        ("GBA_Melee", "Melee / Execute", "F"), ("GBA_Reload", "Reload / Active Reload", "R"), ("GBA_Zoom", "Zoom / Scope", "T"), ("GBA_Pickup", "Pick up / Swap / Revive", "E"), ("GBA_POI", "Point of interest / Ping", "Q"), ("GBA_Taccom", "Squad commands", "H"),
        ("GBA_Weap1", "Weapon slot 1", "One"), ("GBA_Weap2", "Weapon slot 2", "Two"), ("GBA_Pistols", "Pistol", "Three"), ("GBA_Grenades", "Grenade", "Four"), ("GBA_PrevWeapon", "Previous weapon", "MouseScrollUp"), ("GBA_NextWeapon", "Next weapon", "MouseScrollDown"), ("GBA_Scoreboard", "Scoreboard", "Slash")
    };
    private static readonly string[] ReservedKeys = { "Escape", "F8", "F10" };
    private static readonly string[] SupportedKeys = Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
        .Concat(new[] { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" }).Concat(Enumerable.Range(1, 12).Select(i => "F" + i))
        .Concat(new[] { "LeftShift", "RightShift", "LeftControl", "RightControl", "LeftAlt", "RightAlt", "SpaceBar", "Tab", "CapsLock", "Enter", "BackSpace", "Escape", "LeftMouseButton", "RightMouseButton", "MiddleMouseButton", "ThumbMouseButton", "ThumbMouseButton2", "MouseScrollUp", "MouseScrollDown", "Up", "Down", "Left", "Right", "Home", "End", "PageUp", "PageDown", "Insert", "Delete", "Semicolon", "Equals", "Comma", "Underscore", "Period", "Slash", "Tilde", "LeftBracket", "RightBracket", "Backslash", "Quote" })
        .Concat(new[] { "NumPadZero", "NumPadOne", "NumPadTwo", "NumPadThree", "NumPadFour", "NumPadFive", "NumPadSix", "NumPadSeven", "NumPadEight", "NumPadNine", "Multiply", "Add", "Subtract", "Decimal", "Divide", "NumLock" }).ToArray();
    private const string KbLine = @"(?m)^([ \t]*[.+]?Bindings=\(Name="")([^""]+)("",Command=""(GBA_\w+)[^""]*""\)[^\r\n]*)\r?$";
    private static bool IsGamepadKey(string key) => key.StartsWith("XboxTypeS_", Ci) || key.StartsWith("Gamepad", Ci);

    private static Dictionary<string, List<string>> BindingMap(string text)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, KbLine))
        {
            if (IsGamepadKey(m.Groups[2].Value)) continue;
            if (!map.TryGetValue(m.Groups[4].Value, out var keys)) map[m.Groups[4].Value] = keys = new List<string>();
            keys.Add(m.Groups[2].Value);
        }
        return map;
    }

    private static string SetBindings(string text, IReadOnlyDictionary<string, string> wanted)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return Regex.Replace(text, KbLine, m =>
        {
            if (IsGamepadKey(m.Groups[2].Value) || !seen.Add(m.Groups[4].Value)) return m.Value;
            if (!wanted.TryGetValue(m.Groups[4].Value, out var key)) return m.Value;
            return m.Groups[1].Value + key + m.Groups[3].Value + (m.Value.EndsWith('\r') ? "\r" : "");
        });
    }

    internal JsonObject GetControls()
    {
        AssertPreflight("Controls", false);
        var files = ControlFiles();
        var text = File.ReadAllText(files[0]);
        var active = BindingMap(text);
        var saved = ReadJsonObject(keybindsPath, optional: true);
        var rows = new JsonArray();
        foreach (var (command, action, defaultKey) in KbActions)
        {
            var key = active.TryGetValue(command, out var keys) && keys.Count != 0 ? keys[0] : saved.ContainsKey(command) ? Str(saved, command) : defaultKey;
            rows.Add(new JsonObject { ["Command"] = command, ["Action"] = action, ["Key"] = key, ["Default"] = defaultKey });
        }
        return new JsonObject
        {
            ["Success"] = true, ["Source"] = files[0], ["MouseSensitivity"] = GetIniValue(text, "Engine.PlayerInput", "MouseSensitivity"), ["Actions"] = rows,
            ["SupportedKeys"] = new JsonArray(SupportedKeys.Select(k => (JsonNode?)k).ToArray()), ["ReservedKeys"] = new JsonArray(ReservedKeys.Select(k => (JsonNode?)k).ToArray()), ["PauseAndEscapeEditable"] = false
        };
    }

    /// <summary>For the audit: each saved key that an input file holds differently.</summary>
    internal JsonArray ControlProblems()
    {
        AssertPreflight("Controls", false);
        var saved = ReadJsonObject(keybindsPath, optional: true);
        var problems = new JsonArray();
        foreach (var path in ControlFiles())
        {
            var map = BindingMap(File.ReadAllText(path));
            foreach (var (command, node) in saved)
                if (node is JsonValue value && value.TryGetValue<string>(out var key) && map.TryGetValue(command, out var keys) && keys.Count != 0 && !Same(keys[0], key))
                    problems.Add(new JsonObject { ["File"] = Path.GetRelativePath(root, path), ["Key"] = KbActions.FirstOrDefault(a => Same(a.Command, command)).Action ?? command, ["Actual"] = keys[0], ["Expected"] = key });
        }
        return problems;
    }

    internal JsonObject ApplyControls(JsonObject bindings, string? mouseSensitivity)
    {
        AssertPreflight("Controls");
        var files = ControlFiles();
        string? sensitivity = null;
        if (mouseSensitivity is not null)
        {
            if (!double.TryParse(mouseSensitivity, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || double.IsNaN(value) || value < 1 || value > 200) throw new ArgumentException("Mouse sensitivity must be between 1 and 200 (Multipatch range).");
            sensitivity = value.ToString("0.################", CultureInfo.InvariantCulture);
        }
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (command, node) in bindings)
        {
            var key = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            if (!KbActions.Any(a => Same(a.Command, command))) throw new ArgumentException("Unsupported control command: " + command);
            if (!SupportedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Unsupported UE3 key name: " + key);
            if (ReservedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException(key + " is reserved by the game or Multipatch and cannot be assigned.");
            if (used.TryGetValue(key, out var other)) throw new ArgumentException($"Key conflict: {key} is assigned to both {other} and {command}.");
            used[key] = command; wanted[command] = key;
        }
        if (wanted.Count == 0) throw new ArgumentException("No control bindings were provided.");
        var effective = BindingMap(File.ReadAllText(files[0]));
        foreach (var command in wanted.Keys) if (!effective.TryGetValue(command, out var keys) || keys.Count == 0) throw new InvalidDataException($"Effective input file '{files[0]}' does not contain {command}.");
        void Verify()
        {
            foreach (var path in files)
            {
                var map = BindingMap(File.ReadAllText(path));
                foreach (var (command, key) in wanted) if (map.TryGetValue(command, out var keys) && keys[0] != key) throw new InvalidDataException($"Post-apply verification failed for {command} in '{path}'.");
            }
        }
        var tx = Transact("Controls", new[] { keybindsPath }.Concat(files), () =>
        {
            foreach (var path in files)
            {
                var text = File.ReadAllText(path);
                var updated = SetBindings(text, wanted);
                if (sensitivity is not null) updated = SetIniValue(updated, "Engine.PlayerInput", "MouseSensitivity", sensitivity);
                WriteIfChanged(path, text, updated);
            }
            if (sensitivity is not null) foreach (var path in files) if (GetIniValue(File.ReadAllText(path), "Engine.PlayerInput", "MouseSensitivity") != sensitivity) throw new InvalidDataException("Mouse sensitivity verification failed.");
            var saved = ReadJsonObject(keybindsPath, optional: true);
            foreach (var (command, key) in wanted) saved[command] = key;
            foreach (var (command, _, defaultKey) in KbActions) if (saved.ContainsKey(command) && Same(Str(saved, command), defaultKey)) saved.Remove(command);
            WriteAtomic(keybindsPath, Utf8.GetBytes(saved.ToJsonString(Pretty)));
            Verify();
        });
        Verify();
        return new JsonObject { ["Success"] = true, ["TransactionId"] = Str(tx, "Id"), ["Status"] = "Bindings applied and verified in effective input files." };
    }
}

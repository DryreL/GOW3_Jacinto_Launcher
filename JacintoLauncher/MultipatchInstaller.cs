#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace JacintoLauncher;

/// <summary>
/// Installs, verifies, repairs and removes the Multipatch without PowerShell, on every platform.
/// The patch data is a .jcpatch zip built from a clean Jacinto 1.1.1 and the result of the original
/// PowerShell engine (see BuildPatchset), so the outcome is byte-identical to that engine by construction:
/// every file is checked against its expected SHA-256 before and after writing.
/// </summary>
internal static class MultipatchInstaller
{
    internal const string PatchsetName = "multipatch.jcpatch";
    private const string CleanExe = "BDF0B8D0424766098318846354646BBBC642EB17CB247C549B644159A40ED4F1";
    private const string CleanU = "37935AC5831743FB30B004FBA644295649B1C4FE7DFE507466E77F700655DA12";
    private const string TempTag = ".jacinto-tmp-";
    private const int MinimumFreeBytes = int.MaxValue; // 2 GB, as the PowerShell installer required.

    internal sealed record Entry(string Path, string? Before, string After, string Blob, bool Delta);

    // ---------------------------------------------------------------- layout
    private static string Marker(string game) => Path.Combine(game, ".jacinto-runtime.json");
    private static string LayoutDir(string game) => Path.Combine(game, "JacintoLauncher");
    private static string IdentityPath(string game) => Path.Combine(LayoutDir(game), "State", "distribution-install.json");
    private static string OriginalsDir(string game) => Path.Combine(game, "tools", "jacinto-originals");
    private static string InstalledPatchset(string game) => Path.Combine(LayoutDir(game), PatchsetName);
    private static string Local(string relative) => relative.Replace('\\', Path.DirectorySeparatorChar);

    // Player packages carry the patch data inside the executable. The copy installed into the game never offers it: its folder goes away on uninstall.
    private static bool InstalledCopy(string exeDir) => Path.GetFileName(exeDir) == "Launcher" && Path.GetFileName(Path.GetDirectoryName(exeDir)) == "JacintoLauncher";
    private static Stream? EmbeddedPatchset() => typeof(MultipatchInstaller).Assembly.GetManifestResourceStream(PatchsetName);

    /// <summary>Free space on the filesystem holding the folder. On Linux and macOS the path root is always "/", which on a
    /// Steam Deck is the small system partition, not /home; DriveInfo there reads the mount of the folder itself (issue #1).</summary>
    internal static long FreeBytes(string folder) => new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(folder)! : folder).AvailableFreeSpace;

    internal static bool HasPackagedPatchset(string exeDir)
    {
        if (File.Exists(Path.Combine(exeDir, PatchsetName))) return true;
        if (InstalledCopy(exeDir)) return false;
        using var embedded = EmbeddedPatchset();
        return embedded is not null;
    }

    /// <summary>A file holding the patch data: beside the launcher (development), or a temporary copy for ReleasePatchset to delete.</summary>
    internal static string? FindPackagedPatchset(string exeDir, bool evenInstalled = false)
    {
        var path = Path.Combine(exeDir, PatchsetName);
        if (File.Exists(path)) return path;
        if (InstalledCopy(exeDir) && !evenInstalled) return null;
        using var embedded = EmbeddedPatchset();
        if (embedded is null) return null;
        var temporary = Path.Combine(Path.GetTempPath(), "jacinto-" + Guid.NewGuid().ToString("N") + ".jcpatch");
        using (var output = File.Create(temporary)) embedded.CopyTo(output);
        return temporary;
    }

    internal static void ReleasePatchset(string path, string exeDir)
    {
        if (!string.Equals(Path.GetDirectoryName(path), exeDir, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
    }

    /// <summary>True when a launcher-managed installation exists whose patch data allows verify, repair and uninstall.</summary>
    internal static bool Managed(string game) => File.Exists(InstalledPatchset(game)) && File.Exists(IdentityPath(game));

    private static bool IsText(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ini" or ".int" or ".esm" or ".ita" or ".txt" or ".bat" or ".json";

    // Files a player's copy may legitimately hold in another form, or not at all: JacintoNet comes and goes with the network mode.
    private static bool IsFlexible(string path) => IsText(path) || NetworkMode.JacintoNetFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    // Linux file systems are case-sensitive and a copied game can differ in case from the patch data.
    private static string Resolve(string root, string relative)
    {
        var direct = Path.Combine(root, Local(relative));
        if (OperatingSystem.IsWindows() || File.Exists(direct)) return direct;
        var current = root;
        foreach (var part in Local(relative).Split(Path.DirectorySeparatorChar))
        {
            var next = Path.Combine(current, part);
            if (Directory.Exists(current) && !File.Exists(next) && !Directory.Exists(next))
                next = Directory.EnumerateFileSystemEntries(current).FirstOrDefault(e => string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase)) ?? next;
            current = next;
        }
        return current;
    }

    private static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

    private static void AssertPlain(string path)
    {
        for (var cursor = Path.GetFullPath(path); !string.IsNullOrEmpty(cursor); cursor = Path.GetDirectoryName(cursor)!)
        {
            FileSystemInfo info = Directory.Exists(cursor) ? new DirectoryInfo(cursor) : new FileInfo(cursor);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint) && Path.GetPathRoot(cursor) != cursor && OperatingSystem.IsWindows()) throw new UserActionException("The game folder is inside a linked or cloud folder, such as OneDrive: " + cursor + ". Move the game to a normal folder, such as C:\\Games. No files were changed.");
        }
    }

    private static void ReplaceFile(string temporary, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        File.Move(temporary, target, true);
    }

    private static void CopyVerified(string source, string target, string expected)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + TempTag + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            File.SetAttributes(temporary, FileAttributes.Normal);
            if (!Hash(temporary).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Copy failed verification: " + Path.GetFileName(target));
            ReplaceFile(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // ---------------------------------------------------------------- patch data
    private static (ZipArchive Zip, Entry[] Entries) Open(string patchset)
    {
        var zip = ZipFile.OpenRead(patchset);
        using var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        return (zip, JsonSerializer.Deserialize<Entry[]>(manifest.ReadToEnd())!);
    }

    // A delta is a list of operations: 0 + offset + length copies from the original file, 1 + length + bytes adds new bytes.
    // A copy may come from anywhere in the original: a package that grew by a few bytes shifts everything after them.
    internal static byte[] BuildDelta(byte[] before, byte[] after)
    {
        const int block = 16, minimum = 24; // Shorter equal runs cost more as operations than as literal bytes.
        static ulong Key(byte[] data, int at) => BitConverter.ToUInt64(data, at) * 0x9E3779B97F4A7C15UL ^ BitConverter.ToUInt64(data, at + 8);
        var index = new Dictionary<ulong, int>(before.Length / block + 1);
        for (var i = 0; i + block <= before.Length; i += block) index.TryAdd(Key(before, i), i);
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        void Add(ReadOnlySpan<byte> bytes) { if (bytes.Length > 0) { writer.Write((byte)1); writer.Write((long)bytes.Length); writer.Write(bytes); } }
        int Run(int from, int to) => from < 0 || from >= before.Length ? 0 : before.AsSpan(from).CommonPrefixLength(after.AsSpan(to));
        var position = 0; var pending = 0; var shift = 0;
        while (position < after.Length)
        {
            // The next match usually keeps the last one's shift: the offsets in a package change, the data between them does not.
            var source = position + shift;
            var length = Run(source, position);
            if (length < minimum)
            {
                length = 0;
                if (position + block <= after.Length && index.TryGetValue(Key(after, position), out var found)) { source = found; length = Run(found, position); }
                if (length < minimum) { position++; continue; }
            }
            while (position > pending && source > 0 && after[position - 1] == before[source - 1]) { position--; source--; length++; }
            Add(after.AsSpan(pending, position - pending));
            writer.Write((byte)0); writer.Write((long)source); writer.Write((long)length);
            shift = source - position;
            position += length;
            pending = position;
        }
        Add(after.AsSpan(pending));
        writer.Flush();
        return output.ToArray();
    }

    internal static void ApplyDelta(Stream delta, string before, Stream output)
    {
        using var source = File.OpenRead(before);
        using var reader = new BinaryReader(delta);
        var buffer = new byte[1 << 16];
        while (true)
        {
            int kind;
            try { kind = reader.ReadByte(); } catch (EndOfStreamException) { return; }
            if (kind == 0) source.Position = reader.ReadInt64();
            var remaining = reader.ReadInt64();
            var input = kind == 0 ? source : reader.BaseStream;
            while (remaining > 0)
            {
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read <= 0) throw new EndOfStreamException("Patch data is truncated.");
                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }
    }

    /// <summary>
    /// Build step, not used by players: GoW3Jacinto_Launcher --build-patchset &lt;clean game&gt; &lt;PowerShell-patched game&gt; &lt;output&gt;.
    /// Leaves out what only the PowerShell tool needs: its scripts, its backups and its copies of already installed files.
    /// </summary>
    internal static int BuildPatchset(string clean, string patched, string output, string? fonts = null)
    {
        static bool Skip(string relative)
        {
            var path = relative.Replace('/', '\\');
            if (path.StartsWith("JacintoLauncher\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("tools\\jacinto-originals\\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith("Gears of War 3 Jacinto Multipatch Launcher\\", StringComparison.OrdinalIgnoreCase) || !path.Contains('\\') && path.StartsWith('.')) return true;
            if (!path.StartsWith("tools\\multipatch\\", StringComparison.OrdinalIgnoreCase)) return false;
            var keep = new[] { "_files\\graphics.json", "_files\\keybinds.json", "_files\\wsock32.dll", "_files\\steam_api64.dll", "_files\\GOW3Helper.dll", "readme.txt", "changelog.txt" };
            return !keep.Any(k => path.EndsWith("multipatch\\" + k, StringComparison.OrdinalIgnoreCase));
        }
        var entries = new List<Entry>();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        var written = new HashSet<string>();
        static long Packed(byte[] data)
        {
            using var packed = new MemoryStream();
            using (var deflate = new DeflateStream(packed, CompressionLevel.SmallestSize, true)) deflate.Write(data);
            return packed.Length;
        }
        void Blob(string name, byte[] data)
        {
            if (!written.Add(name)) return;
            using var stream = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            stream.Write(data);
        }
        foreach (var file in Directory.EnumerateFiles(patched, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(patched, file);
            if (Skip(relative)) continue;
            var original = Path.Combine(clean, relative);
            var existed = File.Exists(original);
            var info = new FileInfo(file);
            if (existed && new FileInfo(original).Length == info.Length && Hash(original) == Hash(file)) continue;
            var after = File.ReadAllBytes(file);
            var afterHash = Convert.ToHexString(SHA256.HashData(after));
            if (existed && !IsFlexible(relative))
            {
                var before = File.ReadAllBytes(original);
                var delta = BuildDelta(before, after);
                if (Packed(delta) < Packed(after))
                {
                    var name = "delta/" + afterHash;
                    Blob(name, delta);
                    entries.Add(new Entry(relative.Replace('/', '\\'), Convert.ToHexString(SHA256.HashData(before)), afterHash, name, true));
                    continue;
                }
            }
            var whole = "file/" + afterHash;
            Blob(whole, after);
            entries.Add(new Entry(relative.Replace('/', '\\'), existed ? Hash(original) : null, afterHash, whole, false));
        }
        // Offered by Settings, so they are not part of what an install writes.
        if (fonts is not null)
            foreach (var (name, file) in new[] { ("extra/WarfareFonts_normal.upk", "larger_subtitle_default/WarfareFonts.upk"), ("extra/WarfareFonts_cn.upk", "larger_subtitle_chinese/WarfareFonts.upk"), ("extra/WarfareFonts2x_cn.upk", "larger_subtitle_chinese/WarfareFonts2x.upk") })
                Blob(name, File.ReadAllBytes(Path.Combine(fonts, file)));
        Blob("manifest.json", JsonSerializer.SerializeToUtf8Bytes(entries));
        Console.WriteLine($"{entries.Count} entries, {entries.Count(e => e.Delta)} deltas");
        return 0;
    }

    // ---------------------------------------------------------------- install
    private static void WriteEntry(ZipArchive zip, Entry entry, string game, string? beforeOverride = null)
    {
        var target = Resolve(game, entry.Path);
        AssertPlain(target);
        if (File.Exists(target) && Hash(target).Equals(entry.After, StringComparison.OrdinalIgnoreCase)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + TempTag + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = File.Create(temporary))
            using (var blob = zip.GetEntry(entry.Blob)!.Open())
            {
                if (entry.Delta) ApplyDelta(blob, beforeOverride ?? target, output);
                else blob.CopyTo(output);
            }
            if (!Hash(temporary).Equals(entry.After, StringComparison.OrdinalIgnoreCase)) throw new IOException("Patched file failed verification: " + entry.Path);
            ReplaceFile(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>Read-only: why this game cannot take the patch. Text files may differ; they are replaced after a safety copy.</summary>
    internal static List<string> Preflight(string game, Entry[] entries)
    {
        var problems = new List<string>();
        foreach (var entry in entries.Where(e => e.Before is not null && !IsFlexible(e.Path)))
        {
            var target = Resolve(game, entry.Path);
            if (!File.Exists(target)) problems.Add("Missing: " + entry.Path);
            else if (!Hash(target).Equals(entry.Before, StringComparison.OrdinalIgnoreCase)) problems.Add("Not the original Jacinto 1.1.1 file: " + entry.Path);
        }
        return problems;
    }

    private static string Scenario(string game)
    {
        var hasFull = Directory.Exists(Path.Combine(game, "tools", "multipatch"));
        var hasDryrel = Directory.Exists(Path.Combine(game, "Gears3_Multipatch_Tool"));
        if (hasDryrel && !hasFull) return "existing-multipatch";
        // The file check before patching names any file that is not the original 1.1.1 one, so the folder needs no version name.
        if (!hasFull && !hasDryrel && MissingGameFile(game) is null) return "clean";
        return "unsupported";
    }

    /// <summary>The file that shows a game folder is incomplete, as a badly extracted download or an antivirus leaves it; null when it is there.</summary>
    internal static string? MissingGameFile(string game) => File.Exists(NetworkMode.ScriptPath(game)) ? null : Path.GetRelativePath(game, NetworkMode.ScriptPath(game));

    // A launcher left open in the game folder holds its own file, which both restoring and installing must replace.
    private static void AssertLauncherFree(string game)
    {
        var installed = Path.Combine(LayoutDir(game), "Launcher", Path.GetFileName(Environment.ProcessPath!));
        if (!File.Exists(installed) || string.Equals(Path.GetFullPath(installed), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
        try { using var _ = new FileStream(installed, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw new UserActionException("Close the launcher that is open from the game folder, then try again. No files were changed."); }
    }

    /// <summary>Installs from the package next to the running launcher. Returns the scenario; throws with the reason otherwise.</summary>
    internal static string Install(string game, string patchset, string launcherSource, Action<string> log, bool createShortcut = true)
    {
        game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game));
        AssertPlain(game);
        using var mutex = new Mutex(false, "Local\\JacintoInstall-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToLowerInvariant()))));
        if (!mutex.WaitOne(0)) throw new UserActionException("Another installation is already running for this game.");
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game before installing.");
        AssertLauncherFree(game);
        if (File.Exists(IdentityPath(game))) throw new UserActionException("The Jacinto launcher is already installed here. Use Uninstall Multipatch on the Home screen first to reinstall.");
        if (File.Exists(Marker(game)))
        {
            log("A previous installation did not finish. Restoring the original files first...");
            Restore(game, patchset, log);
        }
        var scenario = Scenario(game);
        if (scenario == "unsupported") throw new UserActionException("This game is neither a clean Jacinto 1.1.1 nor a game patched by the Gears3 Multi Patch Tool. Jacinto 1.1.2 (Horde and multiplayer) is not supported. No files were changed.");
        if (scenario == "existing-multipatch") { InstallLauncherOnly(game, launcherSource, log, createShortcut); return scenario; }

        var (zip, entries) = Open(patchset);
        using (zip)
        {
            log("Checking the game files...");
            var problems = Preflight(game, entries);
            if (problems.Count != 0) throw new UserActionException($"{problems.Count} game file(s) cannot be patched; nothing was changed. First: {problems[0]}");
            if (FreeBytes(game) < MinimumFreeBytes) throw new UserActionException("At least 2 GB of free space is required on the game drive.");
            try
            {
                File.WriteAllText(Marker(game), JsonSerializer.Serialize(new { Schema = 3, Mode = "Single", Source = game, StartedUtc = DateTime.UtcNow.ToString("o") }));
                log("Making safety copies...");
                var originals = new List<object>();
                foreach (var entry in entries.Where(e => e.Before is not null))
                {
                    var source = Resolve(game, entry.Path);
                    if (!File.Exists(source)) continue;
                    var hash = Hash(source);
                    CopyVerified(source, Path.Combine(OriginalsDir(game), Path.GetRelativePath(game, source)), hash);
                    originals.Add(new { Path = Path.GetRelativePath(game, source), SHA256 = hash });
                }
                // Not in the patch data, but the player name changes it.
                var sera = PlayerIdentity.SeraIni(game);
                if (File.Exists(sera))
                {
                    var hash = Hash(sera);
                    CopyVerified(sera, Path.Combine(OriginalsDir(game), Path.GetRelativePath(game, sera)), hash);
                    originals.Add(new { Path = Path.GetRelativePath(game, sera), SHA256 = hash });
                }
                File.WriteAllText(Path.Combine(OriginalsDir(game), "originals.json"), JsonSerializer.Serialize(originals, new JsonSerializerOptions { WriteIndented = true }));
                var done = 0;
                foreach (var entry in entries)
                {
                    log($"[{++done}/{entries.Length}] {FixCatalog.Describe(entry.Path)}  ({Path.GetFileName(entry.Path.Replace('\\', '/'))})");
                    WriteEntry(zip, entry, game);
                }
                log("Installing the launcher...");
                InstallLauncherFiles(game, launcherSource);
                File.Copy(patchset, InstalledPatchset(game), true);
                ApplyDisplay(game, log);
                ApplyIntro(game);
                MultipatchBridge.ApplyHdCaps(game);
                var shortcut = createShortcut ? CreateShortcut(game) : null;
                WriteIdentity(game, "full", NetworkMode.Steam, shortcut, null);
            }
            catch (Exception exception)
            {
                LauncherDiagnostics.Error("Multipatch installation failed; restoring the original files", exception);
                log("Installation failed. Restoring the original files...");
                try { Restore(game, patchset, log); }
                catch (Exception recovery) { LauncherDiagnostics.Error("Automatic recovery failed", recovery); throw new IOException(exception.Message + " Recovery also failed; the safety copies were kept in tools\\jacinto-originals.", exception); }
                throw;
            }
        }
        return scenario;
    }

    // The patch data skips the intro. The build's own check compares against that, so it runs with no settings.
    private static void ApplyIntro(string game)
    {
        if (MultipatchBridge.Config is { } config) IntroSettings.Apply(new[] { game }, config.SkipIntro, () => { });
    }

    // ---------------------------------------------------------------- input profile
    private const string HudPath = "GearGame\\Content\\Interface\\Warfare_HUD.upk";
    private static string HudOriginal(string game) => Path.Combine(OriginalsDir(game), Local(HudPath));

    /// <summary>Only a full installation has what the switch needs: the patch data and the original icons.</summary>
    internal static bool CanSwitchInput(string game) => Managed(game) && File.Exists(HudOriginal(game));
    internal static bool UsesGamepad(string game) => InputProfile.IsGamepad(File.ReadAllBytes(NetworkMode.ScriptPath(game)));

    /// <summary>The old installer's "Keyboard or Gamepad?" question, as a switch that needs no reinstall. It keeps the current network mode.</summary>
    internal static void SetInput(string game, bool gamepad)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game first.");
        var script = NetworkMode.ScriptPath(game);
        var data = File.ReadAllBytes(script);
        if (gamepad) InputProfile.ToGamepad(data); else InputProfile.ToKeyboard(data);
        var temporary = script + TempTag + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temporary, data); ReplaceFile(temporary, script); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // Time-based turn acceleration is tuned for a stick: it multiplies the large values a mouse sends. The patch data has it off.
        var documents = Path.Combine(GameRunner.DocumentsRoot(MultipatchBridge.Config), "My Games", "Gears of War 3", "GearGame", "Config", "GearInput.ini");
        foreach (var ini in new[] { Path.Combine(game, "GearGame", "Config", "GearInput.ini"), Path.Combine(game, "GearGame", "Config", "DefaultInput.ini"), documents }.Where(File.Exists))
        {
            var text = File.ReadAllText(ini);
            var updated = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^bViewAccelEnabled=[^\r\n]*", "bViewAccelEnabled=" + (gamepad ? "TRUE" : "FALSE"));
            if (updated != text) File.WriteAllText(ini, updated);
        }
        var target = Resolve(game, HudPath);
        var (zip, entries) = Open(InstalledPatchset(game));
        using (zip) WriteEntry(zip, entries.First(e => e.Path.Equals(HudPath, StringComparison.OrdinalIgnoreCase)), game, HudOriginal(game));
        if (!gamepad) return;
        // The pad icons are in the original file. It takes the keyboard file's GUID, or a keyboard player and a gamepad player cannot join each other.
        // Only the pictures differ: names, objects and imports are the same, so both games number the objects alike.
        var icons = File.ReadAllBytes(HudOriginal(game));
        InputProfile.CopyGuid(File.ReadAllBytes(target), icons);
        var padHud = target + TempTag + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(padHud, icons); ReplaceFile(padHud, target); }
        finally { if (File.Exists(padHud)) File.Delete(padHud); }
    }

    /// <summary>Gamepad installations from before v0.3.9 have the original HUD file with its own GUID, so keyboard players could not join them.</summary>
    internal static void ShareHudGuid(string game)
    {
        if (CanSwitchInput(game) && Hash(Resolve(game, HudPath)).Equals(Hash(HudOriginal(game)), StringComparison.OrdinalIgnoreCase) && UsesGamepad(game)) SetInput(game, true);
    }

    // ---------------------------------------------------------------- HUD scaling
    // The old installer's question: the one call in GearGame.exe that runs the scaling code. The original bytes come from the safety copy.
    private const long HudHook = 0x00C150A8;
    private static readonly byte[] HudOn = Convert.FromHexString("E8D3A1B80290");
    private static string ExePath(string game) => Path.Combine(game, "Binaries", "Win64", "GearGame.exe");
    private static string ExeOriginal(string game) => Path.Combine(OriginalsDir(game), "Binaries", "Win64", "GearGame.exe");

    private static byte[] ReadAt(string path, long offset, int count)
    {
        using var stream = File.OpenRead(path);
        stream.Position = offset;
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static bool CanSwitchHud(string game) => Managed(game) && File.Exists(ExeOriginal(game));
    internal static bool UsesHudScaling(string game) => ReadAt(ExePath(game), HudHook, HudOn.Length).AsSpan().SequenceEqual(HudOn);

    internal static void SetHudScaling(string game, bool on)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game first.");
        var bytes = on ? HudOn : ReadAt(ExeOriginal(game), HudHook, HudOn.Length);
        using var stream = new FileStream(ExePath(game), FileMode.Open, FileAccess.Write, FileShare.None);
        stream.Position = HudHook;
        stream.Write(bytes);
    }

    // ---------------------------------------------------------------- subtitle fonts
    private static string FontPath(string game, string file) => Path.Combine(game, "GearGame", "Content", "Interface", file);

    /// <summary>The files that must change, each with where its new content comes from. Files already right are left out, so a settings transaction stays small.</summary>
    internal static (string Target, string Source)[] FontWrites(string game, string? normal, string? large)
    {
        var writes = new List<(string, string)>();
        if (!File.Exists(InstalledPatchset(game))) return Array.Empty<(string, string)>();
        var (zip, entries) = Open(InstalledPatchset(game));
        using (zip)
        {
            string? Expected(string source, string file) => source == "patch" ? entries.FirstOrDefault(e => Path.GetFileName(e.Path).Equals(file, StringComparison.OrdinalIgnoreCase))?.After
                : source == "original" ? (File.Exists(Path.Combine(OriginalsDir(game), Path.GetRelativePath(game, FontPath(game, file)))) ? Hash(Path.Combine(OriginalsDir(game), Path.GetRelativePath(game, FontPath(game, file)))) : null)
                : BlobHash(zip, source);
            foreach (var (file, source) in new[] { (SubtitleFonts.NormalFile, normal), (SubtitleFonts.BigFile, large) })
            {
                if (source is null) continue;
                var target = FontPath(game, file);
                var expected = Expected(source, file);
                if (expected is null) continue; // "original": the file was never replaced.
                if (!File.Exists(target) || !Hash(target).Equals(expected, StringComparison.OrdinalIgnoreCase)) writes.Add((target, source));
            }
        }
        return writes.ToArray();
    }

    private static string? BlobHash(ZipArchive zip, string name)
    {
        if (zip.GetEntry(name) is not { } entry) return null;
        using var stream = entry.Open();
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void WriteFonts(string game, (string Target, string Source)[] writes)
    {
        if (writes.Length == 0) return;
        var (zip, entries) = Open(InstalledPatchset(game));
        using (zip)
            foreach (var (target, source) in writes)
            {
                var relative = Path.GetRelativePath(game, target);
                if (source == "original") { var copy = Path.Combine(OriginalsDir(game), relative); CopyVerified(copy, target, Hash(copy)); continue; }
                if (source == "patch") { WriteEntry(zip, entries.First(e => e.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)), game); continue; }
                KeepOriginal(game, relative);
                var temporary = target + TempTag + Guid.NewGuid().ToString("N");
                try
                {
                    using (var output = File.Create(temporary))
                    using (var blob = zip.GetEntry(source)!.Open()) blob.CopyTo(output);
                    if (!Hash(temporary).Equals(BlobHash(zip, source), StringComparison.OrdinalIgnoreCase)) throw new IOException("Font failed verification: " + source);
                    ReplaceFile(temporary, target);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
    }

    // WarfareFonts.upk is not in the patch data, so the installer has no safety copy of it until a font first replaces it. Uninstall restores it from here.
    private static void KeepOriginal(string game, string relative)
    {
        var source = Path.Combine(game, relative);
        if (!File.Exists(source)) return;
        var index = Path.Combine(OriginalsDir(game), "originals.json");
        var list = JsonNode.Parse(File.ReadAllText(index))!.AsArray();
        if (list.Any(n => n!["Path"]!.GetValue<string>().Equals(relative, StringComparison.OrdinalIgnoreCase))) return;
        var hash = Hash(source);
        CopyVerified(source, Path.Combine(OriginalsDir(game), relative), hash);
        list.Add(new JsonObject { ["Path"] = relative, ["SHA256"] = hash });
        File.WriteAllText(index, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------------------------------------------------------------- upgrade
    /// <summary>True when this launcher carries other patch data than the game has, or the game still has an older package's layout.</summary>
    internal static bool UpgradeAvailable(string game, string exeDir)
    {
        if (!File.Exists(IdentityPath(game)) || !File.Exists(Path.Combine(OriginalsDir(game), "originals.json"))) return false;
        if (JsonNode.Parse(File.ReadAllText(IdentityPath(game)))?["Kind"]?.GetValue<string>() != "full") return false;
        var patchset = FindPackagedPatchset(exeDir, true);
        if (patchset is null) return false;
        try { return !File.Exists(InstalledPatchset(game)) || Hash(patchset) != Hash(InstalledPatchset(game)); }
        finally { ReleasePatchset(patchset, exeDir); }
    }

    /// <summary>
    /// Moves an installed game to this launcher's patch data, keeping the player's settings. Every step checks what it writes
    /// and the old patch data is replaced last, so a run that stops is simply run again at the next start.
    /// </summary>
    internal static void Upgrade(string game, string exeDir, Action<string> log)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game before updating the Multipatch.");
        var patchset = FindPackagedPatchset(exeDir, true) ?? throw new FileNotFoundException("The launcher package holds no patch data.");
        try
        {
            var offline = NetworkMode.Read(game).Describe() == NetworkMode.Offline;
            var gamepad = UsesGamepad(game);
            var hud = UsesHudScaling(game);
            var index = Path.Combine(OriginalsDir(game), "originals.json");
            var originals = JsonNode.Parse(File.ReadAllText(index))!.AsArray().Select(n => (Path: n!["Path"]!.GetValue<string>(), Hash: n["SHA256"]!.GetValue<string>())).ToList();
            var previous = Array.Empty<Entry>();
            if (File.Exists(InstalledPatchset(game))) { var (old, oldEntries) = Open(InstalledPatchset(game)); old.Dispose(); previous = oldEntries; }
            var (zip, entries) = Open(patchset);
            using (zip)
            {
                bool Wanted(string path) => entries.Any(e => string.Equals(Path.GetRelativePath(game, Resolve(game, e.Path)), path, StringComparison.OrdinalIgnoreCase));
                foreach (var copy in originals.Where(c => !Wanted(c.Path) && !c.Path.Equals(Path.GetRelativePath(game, PlayerIdentity.SeraIni(game)), StringComparison.OrdinalIgnoreCase)))
                    CopyVerified(Path.Combine(OriginalsDir(game), copy.Path), Path.Combine(game, copy.Path), copy.Hash);
                foreach (var gone in previous.Where(p => p.Before is null && !entries.Any(e => e.Path.Equals(p.Path, StringComparison.OrdinalIgnoreCase))))
                    if (File.Exists(Resolve(game, gone.Path))) File.Delete(Resolve(game, gone.Path));
                // A file first touched by this version is still the original: it gets its safety copy now.
                foreach (var entry in entries.Where(e => e.Before is not null))
                {
                    var relative = Path.GetRelativePath(game, Resolve(game, entry.Path));
                    if (originals.Exists(c => c.Path.Equals(relative, StringComparison.OrdinalIgnoreCase)) || !File.Exists(Path.Combine(game, relative))) continue;
                    var hash = Hash(Path.Combine(game, relative));
                    CopyVerified(Path.Combine(game, relative), Path.Combine(OriginalsDir(game), relative), hash);
                    originals.Add((relative, hash));
                }
                File.WriteAllText(index, JsonSerializer.Serialize(originals.Select(c => new { c.Path, SHA256 = c.Hash }), new JsonSerializerOptions { WriteIndented = true }));
                var done = 0;
                foreach (var entry in entries)
                {
                    var name = Path.GetFileName(entry.Path.Replace('\\', '/'));
                    log($"[{++done}/{entries.Length}] {FixCatalog.Describe(entry.Path)}  ({name})");
                    if (name is "graphics.json" or "keybinds.json" && File.Exists(Resolve(game, entry.Path))) continue; // The player's settings.
                    var original = entry.Before is null ? null : Path.Combine(OriginalsDir(game), Path.GetRelativePath(game, Resolve(game, entry.Path)));
                    WriteEntry(zip, entry, game, original);
                }
            }
            log("Applying your settings again...");
            var files = GameLayout.ToolFilesDir(game)!;
            MultipatchBridge.ApplyGraphicsThroughMultipatch(game, JsonNode.Parse(File.ReadAllText(Path.Combine(files, "graphics.json")))!.AsObject());
            var keys = Path.Combine(files, "keybinds.json");
            if (File.Exists(keys) && JsonNode.Parse(File.ReadAllText(keys))?.AsObject() is { Count: > 0 } saved) MultipatchBridge.ApplyControlsThroughMultipatch(game, saved);
            ApplyIntro(game);
            var oldLayout = !File.Exists(InstalledPatchset(game));
            File.Copy(patchset, InstalledPatchset(game), true);
            // Again now that the new patch data is in place: the subtitle fonts are read from it.
            MultipatchBridge.ApplyGraphicsThroughMultipatch(game, JsonNode.Parse(File.ReadAllText(Path.Combine(files, "graphics.json")))!.AsObject());
            if (!hud) SetHudScaling(game, false);
            if (gamepad) SetInput(game, true);
            // The new patch data may hold a newer JacintoNet than the copy parked for Offline; that copy no longer counts.
            foreach (var name in NetworkMode.JacintoNetFiles)
            {
                var installed = Path.Combine(game, "Binaries", "Win64", name);
                var parked = Path.Combine(NetworkMode.ParkingDir(game), name);
                if (File.Exists(installed) && File.Exists(parked) && entries.Any(e => Resolve(game, e.Path) == installed && e.After.Equals(Hash(installed), StringComparison.OrdinalIgnoreCase))) File.Delete(parked);
            }
            if (offline) NetworkMode.Apply(game, NetworkMode.Offline);
            MultipatchBridge.ApplyHdCaps(game);
            if (oldLayout)
            {
                var identity = JsonNode.Parse(File.ReadAllText(IdentityPath(game)))!;
                if (!InstalledCopy(exeDir)) InstallLauncherFiles(game, exeDir);
                // Its uninstaller would not know the newer fixes; Uninstall on Home replaces it.
                foreach (var name in new[] { "Uninstall.bat", "Uninstall-Jacinto.ps1", Path.Combine("Launcher", "GoW3Jacinto_Launcher.dll"), Path.Combine("Launcher", "GoW3Jacinto_Launcher.deps.json"), Path.Combine("Launcher", "GoW3Jacinto_Launcher.runtimeconfig.json") })
                    if (File.Exists(Path.Combine(LayoutDir(game), name))) File.Delete(Path.Combine(LayoutDir(game), name));
                if (Directory.Exists(Path.Combine(LayoutDir(game), "Patcher"))) Directory.Delete(Path.Combine(LayoutDir(game), "Patcher"), true);
                WriteIdentity(game, "full", NetworkMode.Steam, identity["Shortcut"]?.GetValue<string>(), null);
            }
            log("Multipatch updated.");
        }
        finally { ReleasePatchset(patchset, exeDir); }
    }

    /// <summary>Not a game the Multipatch can use: neither clean Jacinto 1.1.1 nor anything patched. Players often try the 1.1.2 multiplayer build.</summary>
    internal static bool NamedOneOneTwo(string game) => System.Text.RegularExpressions.Regex.IsMatch(game, @"(?<![\d.])1[._ ]1[._ ]2(?!\d)") && !File.Exists(IdentityPath(game)) && !File.Exists(Marker(game));

    // The patch data records the display of the PC that built it; this PC may differ.
    private static void ApplyDisplay(string game, Action<string> log)
    {
        var screens = SettingsEngine.Screens?.Invoke();
        var primary = screens?.FirstOrDefault(s => s.Primary);
        if (primary is null || primary.Value.Right <= primary.Value.Left) return;
        var native = $"{primary.Value.Right - primary.Value.Left}x{primary.Value.Bottom - primary.Value.Top}";
        var graphics = Path.Combine(game, "tools", "multipatch", "_files", "graphics.json");
        var current = JsonNode.Parse(File.ReadAllText(graphics))!.AsObject();
        if (string.Equals(current["Resolution"]?.GetValue<string>(), native, StringComparison.OrdinalIgnoreCase)) return;
        log("Display: " + native);
        current["Resolution"] = native;
        MultipatchBridge.ApplyGraphicsThroughMultipatch(game, current);
    }

    private static void InstallLauncherFiles(string game, string launcherSource)
    {
        var destination = Path.Combine(LayoutDir(game), "Launcher");
        Directory.CreateDirectory(Path.Combine(LayoutDir(game), "State"));
        // A single-file launcher may have been opened from any folder (Downloads, say): only it and its settings go along.
#pragma warning disable IL3000 // An empty location is exactly what is being asked.
        var singleFile = string.IsNullOrEmpty(typeof(MultipatchInstaller).Assembly.Location);
#pragma warning restore IL3000
        var files = singleFile ? new[] { Environment.ProcessPath!, Path.Combine(launcherSource, "GoW3Jacinto_Launcher.json"), Path.Combine(launcherSource, "languages.csv") }.Where(File.Exists)
            : Directory.EnumerateFiles(launcherSource, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != PatchsetName);
        foreach (var file in files)
        {
            var target = Path.Combine(destination, Path.GetRelativePath(launcherSource, file));
            if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) continue; // Already running from the game folder.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, true);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }
    }

    private static string? CreateShortcut(string game)
    {
        var executable = Path.Combine(LayoutDir(game), "Launcher", Path.GetFileName(Environment.ProcessPath!));
        var desktop = Environment.GetEnvironmentVariable("JACINTO_TEST_DESKTOP") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(desktop)) return null;
        Directory.CreateDirectory(desktop);
        try { return DesktopShortcut.Create(desktop, executable, "Gears of War 3 Jacinto Launcher"); }
        catch (Exception exception) { LauncherDiagnostics.Error("Desktop shortcut failed", exception); return null; } // Not worth failing the installation.
    }

    private static void WriteIdentity(string game, string kind, string originalMode, string? shortcut, string? createdGraphics) =>
        File.WriteAllText(IdentityPath(game), JsonSerializer.Serialize(new { Schema = 3, Kind = kind, GameDir = game, OriginalMode = originalMode, Shortcut = shortcut, CreatedGraphicsJsonSHA256 = createdGraphics, InstalledUtc = DateTime.UtcNow.ToString("o") }, new JsonSerializerOptions { WriteIndented = true }));

    // Launcher next to a complete Multi Patch Tool installation; its files are not modified.
    private static void InstallLauncherOnly(string game, string launcherSource, Action<string> log, bool createShortcut)
    {
        log("Multi Patch Tool installation found. Only the launcher will be installed.");
        var files = Path.Combine(game, "Gears3_Multipatch_Tool", "_files");
        var script = GameLayout.StartScript(game);
        var problems = new List<string>();
        if (script is null) problems.Add("Gears3_Start_Game is missing.");
        if (!Directory.Exists(Path.Combine(files, "Backups")) || !Directory.EnumerateFiles(Path.Combine(files, "Backups"), "*", SearchOption.AllDirectories).Any()) problems.Add("The Multipatch backups folder is empty.");
        if (Hash(Path.Combine(game, "Binaries", "Win64", "GearGame.exe")) == CleanExe) problems.Add("GearGame.exe is still unpatched.");
        string? mode = null;
        try { mode = NetworkMode.Read(game).Describe(); } catch (InvalidDataException) { problems.Add("Unsupported Multipatch version (Campaign setting not found)."); }
        if (problems.Count != 0) throw new UserActionException("Launcher-only installation refused. No files were changed. " + string.Join(" ", problems));
        string? createdGraphics = null;
        var graphics = Path.Combine(files, "graphics.json");
        if (!File.Exists(graphics))
        {
            var match = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(script!), @"GearGame\.exe -(FULLSCREEN|WINDOWED) -(NOVSYNC|VSYNC) -ResX=(\d+) -ResY=(\d+) -language=([A-Za-z]{3})");
            var defaults = new JsonObject { ["PlayerName"] = "", ["Language"] = "INT", ["Resolution"] = "native", ["Fullscreen"] = true, ["VSync"] = false };
            if (match.Success) { defaults["Fullscreen"] = match.Groups[1].Value == "FULLSCREEN"; defaults["VSync"] = match.Groups[2].Value == "VSYNC"; defaults["Resolution"] = $"{match.Groups[3].Value}x{match.Groups[4].Value}"; defaults["Language"] = match.Groups[5].Value.ToUpperInvariant(); }
            try { File.WriteAllText(graphics, defaults.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
            catch (UnauthorizedAccessException) { throw new UserActionException("Windows does not let the launcher write to the game folder, as in Program Files. Move the game to a folder such as C:\\Games, or start the launcher as administrator. No files were changed."); }
            createdGraphics = Hash(graphics);
        }
        InstallLauncherFiles(game, launcherSource);
        WriteIdentity(game, "launcher-only", mode!, createShortcut ? CreateShortcut(game) : null, createdGraphics);
    }

    // ---------------------------------------------------------------- restore
    // Only files named by the patch data are removed, so saves and anything else the player added are never touched.
    private static void Restore(string game, string patchset, Action<string> log)
    {
        var (zip, entries) = Open(patchset);
        using (zip)
        {
            var index = Path.Combine(OriginalsDir(game), "originals.json");
            var copies = File.Exists(index) ? JsonNode.Parse(File.ReadAllText(index))!.AsArray().Select(n => (Path: n!["Path"]!.GetValue<string>(), Hash: n["SHA256"]!.GetValue<string>())).ToArray() : Array.Empty<(string Path, string Hash)>();
            foreach (var copy in copies)
            {
                var source = Path.Combine(OriginalsDir(game), copy.Path);
                if (!File.Exists(source) || Hash(source) != copy.Hash) throw new IOException("Safety copy failed verification: " + copy.Path);
            }
            log($"Restoring {copies.Length} original file(s)...");
            foreach (var copy in copies)
            {
                var target = Path.Combine(game, copy.Path);
                if (File.Exists(target) && Hash(target) == copy.Hash) continue;
                CopyVerified(Path.Combine(OriginalsDir(game), copy.Path), target, copy.Hash);
            }
            // originals.json is written before any game file changes. Without it, nothing with an original was touched.
            var kept = new HashSet<string>(copies.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries.Where(e => e.Before is null || (File.Exists(index) && !kept.Contains(Path.GetRelativePath(game, Resolve(game, e.Path))))))
            {
                var target = Resolve(game, entry.Path);
                if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
            }
            foreach (var entry in entries)
            {
                var directory = Path.GetDirectoryName(Resolve(game, entry.Path));
                if (directory is not null && Directory.Exists(directory))
                    foreach (var leftover in Directory.EnumerateFiles(directory, "*" + TempTag + "*")) File.Delete(leftover);
            }
            if (copies.Length != 0 && (Hash(Path.Combine(game, "Binaries", "Win64", "GearGame.exe")) != CleanExe || Hash(NetworkMode.ScriptPath(game)) != CleanU)) throw new IOException("GearGame.exe or GearGame.u is still not the original Jacinto 1.1.1 file; the safety copies were kept.");
        }
        var tools = Path.Combine(game, "tools");
        foreach (var directory in new[] { Path.Combine(tools, "multipatch"), OriginalsDir(game) }) if (Directory.Exists(directory)) { AssertPlain(directory); DeleteTree(directory); }
        if (Directory.Exists(tools) && !Directory.EnumerateFileSystemEntries(tools).Any()) Directory.Delete(tools);
        if (Directory.Exists(LayoutDir(game)) && !File.Exists(IdentityPath(game))) RemoveLayoutExceptRunning(game);
        if (File.Exists(Marker(game))) File.Delete(Marker(game));
        MultipatchBridge.ApplyHdCaps(game); // The originals may predate an HD pack that is still installed.
    }

    private static void RemoveLayoutExceptRunning(string game)
    {
        var running = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (running.StartsWith(LayoutDir(game), StringComparison.OrdinalIgnoreCase)) return;
        DeleteTree(LayoutDir(game));
    }

    // Files from a read-only download refuse a plain recursive delete.
    internal static void DeleteTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(entry, File.GetAttributes(entry) & ~FileAttributes.ReadOnly);
        File.SetAttributes(directory, File.GetAttributes(directory) & ~FileAttributes.ReadOnly);
        Directory.Delete(directory, true);
    }

    /// <summary>Returns the game to clean Jacinto 1.1.1 (full install) or to the state the launcher found (launcher-only).</summary>
    internal static void Uninstall(string game, Action<string> log)
    {
        game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game));
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game before uninstalling.");
        var identity = JsonNode.Parse(File.ReadAllText(IdentityPath(game)))!;
        if (identity["Schema"]?.GetValue<int>() != 3) throw new UserActionException("This installation was made by an older package. Use its JacintoLauncher\\Uninstall.bat.");
        var kind = identity["Kind"]!.GetValue<string>();
        if (kind == "full") Restore(game, InstalledPatchset(game), log);
        else
        {
            var mode = identity["OriginalMode"]?.GetValue<string>();
            if (mode is NetworkMode.Steam or NetworkMode.Offline or "LAN") NetworkMode.Apply(game, mode == "LAN" ? NetworkMode.Steam : mode);
            // LAN is listen without JacintoNet: remove only the DLLs the switch above restored.
            if (mode == "LAN")
                foreach (var name in NetworkMode.JacintoNetFiles)
                {
                    var installed = Path.Combine(game, "Binaries", "Win64", name);
                    var source = NetworkMode.SteamSource(game, name);
                    if (File.Exists(installed) && source is not null && Hash(source) == Hash(installed)) File.Delete(installed);
                }
            var created = identity["CreatedGraphicsJsonSHA256"]?.GetValue<string>();
            var graphics = Path.Combine(game, "Gears3_Multipatch_Tool", "_files", "graphics.json");
            if (created is not null && File.Exists(graphics) && Hash(graphics) == created) File.Delete(graphics);
        }
        var shortcut = identity["Shortcut"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(shortcut) && File.Exists(shortcut)) File.Delete(shortcut);
        Dxvk.Remove(game);
        var audioCommands = Path.Combine(game, "Binaries", "Gears3_Audio.txt");
        if (File.Exists(audioCommands)) File.Delete(audioCommands);
        var discordName = GameRunner.GameExecutable(game, create: false);
        if (File.Exists(discordName)) File.Delete(discordName);
        File.Delete(IdentityPath(game));
        foreach (var file in Directory.EnumerateFiles(LayoutDir(game))) File.Delete(file);
        ScheduleLayoutRemoval(game);
        log("Uninstall complete.");
    }

    // The running launcher lives inside the folder and Windows keeps its file locked, so what it holds stays until a launcher
    // started from anywhere else removes it (RemoveLeftoverLayout). A hidden shell that waits and deletes would do it sooner,
    // but antivirus engines read exactly that as malware removing itself.
    private static void ScheduleLayoutRemoval(string game)
    {
        var layout = LayoutDir(game);
        var running = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        if (!running.StartsWith(layout, StringComparison.OrdinalIgnoreCase)) { DeleteTree(layout); return; }
        foreach (var file in Directory.EnumerateFiles(layout, "*", SearchOption.AllDirectories))
            try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } // The running launcher.
    }

    /// <summary>The JacintoLauncher folder an uninstall had to leave because the launcher in it was running. Removed at the next start from elsewhere.</summary>
    internal static void RemoveLeftoverLayout(string game)
    {
        var layout = LayoutDir(game);
        if (!Directory.Exists(layout) || File.Exists(IdentityPath(game)) || File.Exists(Marker(game))) return;
        if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).StartsWith(layout, StringComparison.OrdinalIgnoreCase)) return;
        try { DeleteTree(layout); LauncherDiagnostics.Info("Removed the launcher folder an uninstall left behind: " + layout); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("Leftover launcher folder not removed yet: " + exception.Message); }
    }

    // ---------------------------------------------------------------- verify / repair
    // GearGame.u legitimately differs by the Campaign URL in Offline mode, and text files by the player's settings.
    internal static List<Entry> Verify(string game, Action<int, int>? progress = null)
    {
        var (zip, entries) = Open(InstalledPatchset(game));
        using (zip)
        {
            var binaries = entries.Where(e => !IsText(e.Path)).ToArray();
            var bad = new List<Entry>();
            for (var i = 0; i < binaries.Length; i++)
            {
                progress?.Invoke(i + 1, binaries.Length);
                var entry = binaries[i];
                var target = Resolve(game, entry.Path);
                if (!File.Exists(target)) { if (!NetworkMode.JacintoNetFiles.Contains(Path.GetFileName(target), StringComparer.OrdinalIgnoreCase)) bad.Add(entry); continue; }
                var actual = Path.GetFileName(target).Equals("GearGame.u", StringComparison.OrdinalIgnoreCase) ? NetworkMode.HashAsSteam(target) : Hash(target);
                // Same file, other choice: the gamepad profile keeps the original icons, Traditional Chinese has its own font, and HUD scaling off leaves one call out.
                var other = entry.Path.Equals(HudPath, StringComparison.OrdinalIgnoreCase) ? entry.Before : Path.GetFileName(target).Equals(SubtitleFonts.BigFile, StringComparison.OrdinalIgnoreCase) ? BlobHash(zip, "extra/WarfareFonts2x_cn.upk") : null;
                if (entry.Path.Equals(HudPath, StringComparison.OrdinalIgnoreCase) && File.Exists(HudOriginal(game)))
                {
                    // The gamepad icons carry the keyboard file's GUID; see SetInput.
                    var hud = File.ReadAllBytes(target);
                    InputProfile.CopyGuid(File.ReadAllBytes(HudOriginal(game)), hud);
                    if (Convert.ToHexString(SHA256.HashData(hud)).Equals(entry.Before, StringComparison.OrdinalIgnoreCase)) actual = entry.Before!;
                }
                if (Path.GetFileName(target).Equals("GearGame.exe", StringComparison.OrdinalIgnoreCase) && !UsesHudScaling(game))
                {
                    var exe = File.ReadAllBytes(target);
                    HudOn.CopyTo(exe, HudHook);
                    actual = Convert.ToHexString(SHA256.HashData(exe));
                }
                if (!actual.Equals(entry.After, StringComparison.OrdinalIgnoreCase) && !actual.Equals(other, StringComparison.OrdinalIgnoreCase)) bad.Add(entry);
            }
            return bad;
        }
    }

    /// <summary>Rewrites damaged files from the safety copies and the patch data, keeping the current network mode.</summary>
    internal static void Repair(string game, IEnumerable<Entry> damaged, Action<string> log)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game before repairing.");
        var offline = NetworkMode.Read(game).Describe() == NetworkMode.Offline;
        var gamepad = UsesGamepad(game);
        var hud = UsesHudScaling(game);
        var (zip, _) = Open(InstalledPatchset(game));
        using (zip)
            foreach (var entry in damaged)
            {
                log("Repairing " + Path.GetFileName(entry.Path));
                var original = entry.Before is null ? null : Path.Combine(OriginalsDir(game), Path.GetRelativePath(game, Resolve(game, entry.Path)));
                if (entry.Delta && (original is null || !File.Exists(original))) throw new FileNotFoundException("The safety copy needed for the repair is missing.", original);
                var target = Resolve(game, entry.Path);
                if (File.Exists(target)) { File.SetAttributes(target, FileAttributes.Normal); File.Delete(target); }
                WriteEntry(zip, entry, game, original);
            }
        if (!hud && CanSwitchHud(game)) SetHudScaling(game, false);
        if (gamepad && CanSwitchInput(game)) SetInput(game, true);
        if (offline) NetworkMode.Apply(game, NetworkMode.Offline);
    }

    /// <summary>Headless forms for build scripts and tests. Null when <paramref name="args"/> is not one of them.</summary>
    internal static int? RunCommand(string[] args)
    {
        try
        {
            if (args.Length >= 4 && args[0] == "--build-patchset") return BuildPatchset(args[1], args[2], args[3], args.Length > 4 ? args[4] : null);
            if (args.Length == 0) return null;
            var exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var game = args.Length > 1 ? args[1] : GameLayout.ResolveGameDir(exeDir);
            if (game is null) throw new DirectoryNotFoundException("Game installation was not detected.");
            switch (args[0])
            {
                case "--install":
                    var patchset = FindPackagedPatchset(exeDir) ?? throw new FileNotFoundException(PatchsetName + " was not found next to the launcher.");
                    try { Console.WriteLine(Install(game, patchset, exeDir, Console.WriteLine)); }
                    finally { ReleasePatchset(patchset, exeDir); }
                    return 0;
                case "--uninstall": Uninstall(game, Console.WriteLine); return 0;
                case "--upgrade": Upgrade(game, exeDir, Console.WriteLine); return 0;
                case "--input": SetInput(game, args[2] == "gamepad"); return 0;
                case "--hud": SetHudScaling(game, args[2] == "on"); return 0;
                case "--verify":
                    var bad = Verify(game);
                    foreach (var entry in bad) Console.WriteLine("Damaged: " + entry.Path);
                    Console.WriteLine($"{bad.Count} damaged file(s).");
                    return bad.Count == 0 ? 0 : 3;
                case "--repair": Repair(game, Verify(game), Console.WriteLine); return 0;
                default: return null;
            }
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error(args[0] + " failed", exception);
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }
}

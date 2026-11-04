#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace JacintoLauncher;

/// <summary>The optional Vulkan renderer: DXVK's d3d9.dll beside the game. It changes no game file and goes away with one delete.</summary>
internal static class Dxvk
{
    internal const string Version = "3.1.1";
    private const string Download = "https://github.com/doitsujin/dxvk/releases/download/v" + Version + "/dxvk-" + Version + ".tar.gz";
    private const string ArchiveHash = "40565B4A724AADC4433FA4E010B4B23916D9B1F1BAEEE64E17186DB94F54E608";
    private const string DllHash = "C126316F7478EAFF1AD8087702EDA5930F2400187B9C8B3592219C34F6632780";

    private static string Dll(string gameDir) => Path.Combine(gameDir, "Binaries", "Win64", "d3d9.dll");
    private static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }

    internal static bool Installed(string gameDir) => File.Exists(Dll(gameDir)) && Hash(Dll(gameDir)) == DllHash;

    /// <summary>Proton and Wine already run the game through DXVK, so only Windows needs this.</summary>
    internal static bool Offered => OperatingSystem.IsWindows() && !GameRunner.InsideWine;

    internal static async Task InstallAsync(string gameDir)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game first.");
        var target = Dll(gameDir);
        if (File.Exists(target)) { if (Hash(target) == DllHash) return; throw new UserActionException("Another d3d9.dll is already in the game folder; it was left alone."); }
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GoW3JacintoLauncher");
        var archive = await client.GetByteArrayAsync(Download);
        if (Convert.ToHexString(SHA256.HashData(archive)) != ArchiveHash) throw new InvalidDataException("The DXVK download failed verification.");
        Extract(archive, target);
    }

    internal static void Extract(byte[] archive, string target)
    {
        using var gzip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.Name != "dxvk-" + Version + "/x64/d3d9.dll" || entry.DataStream is null) continue;
            var temporary = target + ".jacinto-tmp";
            using (var output = File.Create(temporary)) entry.DataStream.CopyTo(output);
            if (Hash(temporary) != DllHash) { File.Delete(temporary); throw new InvalidDataException("The DXVK library failed verification."); }
            File.Move(temporary, target);
            return;
        }
        throw new InvalidDataException("The DXVK download holds no 64-bit library.");
    }

    /// <summary>Only our own build is removed: any other d3d9.dll belongs to the player.</summary>
    internal static void Remove(string gameDir)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game first.");
        if (Installed(gameDir)) File.Delete(Dll(gameDir));
    }
}

/// <summary>
/// Borderless is the game's fullscreen mode running on DXVK, which draws into a plain monitor-sized window instead of taking the display.
/// DXVK keeps that window above all others, so the launcher lowers it and Alt-Tab can show them. Taking the frame off the
/// game's own window does not work: the engine shrinks a framed window to the work area and puts the frame back on any resize.
/// </summary>
internal static class BorderlessWindow
{
    private const int GwlExStyle = -20;
    private const long Topmost = 0x00000008;
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoActivate = 0x0010;
    private static readonly nint NotTopmost = -2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetLong(nint window, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    /// <summary>Runs for as long as the game does: the engine may recreate its window.</summary>
    internal static void Watch(int processId)
    {
        if (!Dxvk.Offered) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                while (!process.HasExited)
                {
                    process.Refresh();
                    var window = process.MainWindowHandle;
                    if (window != 0 && (GetLong(window, GwlExStyle) & Topmost) != 0) SetWindowPos(window, NotTopmost, 0, 0, 0, 0, NoSize | NoMove | NoActivate);
                    await Task.Delay(1000);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { LauncherDiagnostics.Info("Borderless window stopped: " + exception.Message); }
        });
    }
}

/// <summary>Game languages: the build's own and the community packs on Nexus Mods.</summary>
internal static class GameLanguages
{
    internal sealed record Item(string Code, string Name, string? ModUrl, bool ReplacesEnglish = false);

    internal static readonly Item[] Known =
    {
        new("INT", "English", null), new("ESM", "Español", null), new("ITA", "Italiano", null),
        new("TUR", "Türkçe (community)", "https://www.nexusmods.com/gearsofwar3/mods/26"),
        new("INT", "Português - Brasil (community)", "https://www.nexusmods.com/gearsofwar3/mods/24", true),
        new("CHN", "繁體中文 (community)", "https://www.nexusmods.com/gearsofwar3/mods/31"),
        new("RUS", "Русский (community)", "https://www.nexusmods.com/gearsofwar3/mods/33")
    };

    internal static bool Installed(string gameDir, string code) => Directory.Exists(Path.Combine(gameDir, "GearGame", "Localization", code));

    /// <summary>A typed language code as the game writes it; the engine takes three letters.</summary>
    internal static string Code(string? typed) => new string((typed ?? "").Trim().ToUpperInvariant().Where(char.IsAsciiLetter).ToArray());

    /// <summary>The known languages, then any other pack the player put in GearGame\Localization.</summary>
    internal static Item[] All(string? gameDir)
    {
        var list = new System.Collections.Generic.List<Item>(Known);
        var folder = gameDir is null ? null : Path.Combine(gameDir, "GearGame", "Localization");
        if (folder is not null && Directory.Exists(folder))
            foreach (var directory in Directory.GetDirectories(folder))
            {
                var code = Path.GetFileName(directory).ToUpperInvariant();
                if (code.Length == 3 && !list.Exists(l => l.Code == code)) list.Add(new Item(code, code, null));
            }
        return list.ToArray();
    }
}

/// <summary>
/// Who the game's menus answer to. The patch data holds the keyboard form; the gamepad form undoes these same-size edits to GearGame.u
/// (ESC / Enter / E / G / R instead of the pad's buttons), and the original Warfare_HUD.upk brings the pad icons back.
/// </summary>
internal static class InputProfile
{
    private static readonly (byte[] Pad, byte[] Keys)[] Swaps = new[]
    {
        ("07000000546573744D6500", "0700000045736361706500"), ("2113BD000000000000", "212BB0000000000000"), ("2123BD000000000000", "21B04A000000000000"), ("2124BD000000000000", "21E744000000000000"),
        ("2119BD0000000000001B11330000000000001DFFFFFFFF", "21A7520000000000001B11330000000000001DFFFFFFFF"), ("211EBD0000000000001B113300000000000026", "21D9950000000000001B113300000000000026"),
        ("2119BD000000000000161B0F3300000000000028", "21A752000000000000161B0F3300000000000028"), ("211EBD000000000000161B0F3300000000000027", "21D995000000000000161B0F3300000000000027")
    }.Select(p => (Convert.FromHexString(p.Item1), Convert.FromHexString(p.Item2))).ToArray();
    // The game's own script has this one Enter-key reference; it was never a pad button.
    private static readonly byte[] StockEnterTail = Convert.FromHexString("1616180D00812D01DB29");

    private static void Swap(byte[] data, byte[] from, byte[] to, bool keepStock)
    {
        for (var at = 0; ; at += from.Length)
        {
            var found = data.AsSpan(at).IndexOf(from);
            if (found < 0) return;
            at += found;
            if (keepStock && data.AsSpan(at + from.Length).StartsWith(StockEnterTail)) continue;
            Buffer.BlockCopy(to, 0, data, at, to.Length);
        }
    }

    internal static void ToKeyboard(byte[] data) { foreach (var (pad, keys) in Swaps) Swap(data, pad, keys, false); }
    internal static void ToGamepad(byte[] data) { foreach (var (pad, keys) in Swaps) Swap(data, keys, pad, true); }
    internal static bool IsGamepad(byte[] data) => data.AsSpan().IndexOf(Swaps[0].Pad) >= 0;

    // A UE3 package header: tag, version, header size, folder name, flags, seven table fields, the GUID table and the thumbnail table, then the GUID.
    private static int GuidOffset(byte[] package) { var folder = BitConverter.ToInt32(package, 12); return 16 + (folder < 0 ? -folder * 2 : folder) + 4 + 7 * 4 + 4 * 4; }

    /// <summary>Gives <paramref name="package"/> the GUID of <paramref name="from"/>. The game refuses to join a host whose loaded packages have other GUIDs.</summary>
    internal static void CopyGuid(byte[] from, byte[] package) => from.AsSpan(GuidOffset(from), 16).CopyTo(package.AsSpan(GuidOffset(package)));
}

/// <summary>Which subtitle font files and font name a language and size need. CHN has its own glyphs; other non-Latin packs keep their own font.</summary>
internal static class SubtitleFonts
{
    internal const string Stock = "WarfareFonts.Fonts.WarfareFonts_Euro20", Big = "WarfareFonts2x.Fonts.WarfareFonts_Euro20";
    internal const string NormalFile = "WarfareFonts.upk", BigFile = "WarfareFonts2x.upk";
    private static readonly string[] OwnFont = { "RUS", "ZHO", "CHI", "CHT", "JPN", "JAP", "KOR", "ARA", "PER", "FAR", "HEB", "THA" };

    internal static bool KeepsOwnFont(string language, bool force) => !force && OwnFont.Contains(language.ToUpperInvariant());
    internal static string FontName(string language, string size, bool force) => !KeepsOwnFont(language, force) && size == "big" ? Big : Stock;

    /// <summary>Patch data entries for WarfareFonts.upk and WarfareFonts2x.upk. "original" is the file the game had; null leaves the file alone.</summary>
    internal static (string? Normal, string? Large) Sources(string language, string size, bool force)
    {
        if (language.Equals("CHN", StringComparison.OrdinalIgnoreCase)) return ("extra/WarfareFonts_cn.upk", "extra/WarfareFonts2x_cn.upk");
        if (KeepsOwnFont(language, force)) return (null, "patch");
        // The game loads both packages, so big and normal both need the tool's WarfareFonts.upk beside WarfareFonts2x.upk; the font name picks the size.
        return (size == "default" ? "original" : "extra/WarfareFonts_normal.upk", "patch");
    }
}

/// <summary>
/// The developer hotkeys of the Jacinto build (god, fly, slomo, weapon keys ...) live in GearInput.ini and DefaultInput.ini.
/// Off puts a ";" before each of those lines, so the game skips them and On gives back exactly what was there.
/// </summary>
internal static class CheatKeys
{
    private static readonly System.Text.RegularExpressions.Regex Line = new(
        @"(?im)^(;?)([ \t]*[.+]?Bindings=\(Name=""([^""]+)"",Command=""(?:god|badass|kill enemy|kill ai|loaded|debugai|debugaicycle|invis|show paths|show cover|show collision|show dynamicshadows|togglehandik|slomo|fly|ghost|walk|stat fps|stat engine|stat d3d|playersonly|servertravel|viewmode|camera|adjustfreecam\w*|switchtoweaponclass|togglephysicsmode|freezerendering|shot)(?=[""\s\\])[^\r\n]*?)(\r?)$");

    internal static string[] Files(string game, string? documents)
    {
        var config = Path.Combine(game, "GearGame", "Config");
        var files = new[] { Path.Combine(config, "GearInput.ini"), Path.Combine(config, "DefaultInput.ini") }.ToList();
        if (documents is not null) files.Add(Path.Combine(documents, "My Games", "Gears of War 3", "GearGame", "Config", "GearInput.ini"));
        return files.Where(File.Exists).ToArray();
    }

    /// <summary>True when the hotkeys work, false when they are switched off, null when the input files hold none.</summary>
    internal static bool? On(string game, string? documents)
    {
        var lines = Files(game, documents).SelectMany(f => Line.Matches(File.ReadAllText(f))).ToArray();
        return lines.Length == 0 ? null : lines.Any(m => m.Groups[1].Value.Length == 0);
    }

    /// <summary>The keys the switched-on hotkeys use, such as G and NumPadZero.</summary>
    internal static HashSet<string> Keys(string game, string? documents) =>
        Files(game, documents).SelectMany(f => Line.Matches(File.ReadAllText(f))).Where(m => m.Groups[1].Value.Length == 0).Select(m => m.Groups[3].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    internal static void Set(string game, string? documents, bool on)
    {
        if (GameLayout.GameRunning()) throw new UserActionException("Close the game before changing the cheat hotkeys.");
        foreach (var path in Files(game, documents))
        {
            // Byte for byte: the same BOM and encoding go back, and a file without a BOM stays without one.
            var bytes = File.ReadAllBytes(path);
            var (encoding, skip) = bytes is [0xFF, 0xFE, ..] ? (System.Text.Encoding.Unicode, 2) : bytes is [0xEF, 0xBB, 0xBF, ..] ? (System.Text.Encoding.UTF8, 3) : (System.Text.Encoding.Latin1, 0);
            var text = encoding.GetString(bytes, skip, bytes.Length - skip);
            var changed = Line.Replace(text, m => (on ? "" : ";") + m.Groups[2].Value + m.Groups[4].Value);
            if (changed == text) continue;
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.WriteAllBytes(path, bytes[..skip].Concat(encoding.GetBytes(changed)).ToArray());
        }
    }
}

/// <summary>Every size the main display offers, DSR and VSR sizes above the panel's own included.</summary>
internal static class DisplayModes
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields, PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string? device, int mode, ref DevMode devMode);

    internal static (int X, int Y)[] Primary()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<(int, int)>();
        var modes = new HashSet<(int, int)>();
        var mode = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
        try { for (var i = 0; EnumDisplaySettingsW(null, i, ref mode); i++) if (mode.PelsWidth >= 640 && mode.PelsHeight >= 480) modes.Add((mode.PelsWidth, mode.PelsHeight)); }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { }
        return modes.ToArray();
    }
}

/// <summary>Last checks before the game starts.</summary>
internal static class PreLaunchCheck
{
    internal const string HdMoviesUrl = "https://www.nexusmods.com/gearsofwar3/mods/29";
    private const long MovieLimit = 4L << 30;

    /// <summary>Movies over 4 GB come from the HD Movies Pack; the game crashes while playing the final one.</summary>
    internal static string[] OversizeMovies(string gameDir)
    {
        var movies = Path.Combine(gameDir, "GearGame", "Movies");
        if (!Directory.Exists(movies)) return Array.Empty<string>();
        var options = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        return new DirectoryInfo(movies).GetFiles("*.bik", options).Where(f => f.Length > MovieLimit).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JacintoLauncher;

/// <summary>Desktop shortcut for the launcher; one code path for the installer and the Settings page.</summary>
internal static class DesktopShortcut
{
    // Typed COM on purpose: `dynamic` COM crashes the process (not an exception) in the trimmed build.
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    // Our own copy: trimming removes unused methods from the framework's interface, which moves Save to another COM slot.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string path, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string path, [MarshalAs(UnmanagedType.Bool)] bool remember);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void Unused1(); void Unused2(); void Unused3(); void Unused4();
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void Unused5();
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void Unused6(); void Unused7(); void Unused8(); void Unused9(); void Unused10(); void Unused11(); void Unused12();
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void Unused13(); void Unused14();
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    internal static string Create(string desktop, string executable, string title)
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(desktop, title + ".lnk");
            var link = (IShellLinkW)new ShellLink();
            link.SetPath(executable);
            link.SetWorkingDirectory(Path.GetDirectoryName(executable)!);
            link.SetIconLocation(executable, 0);
            link.SetDescription(title);
            ((IPersistFile)link).Save(path, true);
            if (!File.Exists(path)) throw new IOException("The shortcut was not written.");
            return path;
        }
        var entry = Path.Combine(desktop, "gears-of-war-3-jacinto.desktop");
        File.WriteAllText(entry, $"[Desktop Entry]\nType=Application\nName={title}\nExec=\"{executable}\"\nPath={Path.GetDirectoryName(executable)}\nTerminal=false\nCategories=Game;\n");
        File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return entry;
    }
}

/// <summary>
/// Adds the launcher to the Steam library by editing userdata\&lt;id&gt;\config\shortcuts.vdf (binary KeyValues).
/// Steam rewrites this file from memory, so it must be closed while the file changes; a .bak copy of the old file is kept.
/// </summary>
internal static class SteamShortcut
{
    private const byte Map = 0, Text = 1, Int = 2, End = 8;
    internal const string AppName = "Gears of War 3 Jacinto";

    internal static string? SteamRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            return !string.IsNullOrEmpty(fromRegistry) && Directory.Exists(fromRegistry) ? fromRegistry.Replace('/', Path.DirectorySeparatorChar) : null;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.Combine(home, ".steam", "steam"), Path.Combine(home, ".local", "share", "Steam"), Path.Combine(home, "Library", "Application Support", "Steam") }.FirstOrDefault(Directory.Exists);
    }

    internal static bool MustRestart => GameRunner.IsSteamClientRunning() && !GameRunner.InsideWine;

    /// <summary>Closes Steam if it is running, adds the entry and starts Steam again. If the entry fails, Steam still comes back.</summary>
    internal static int AddRestarting(string executable, string? iconPath)
    {
        if (!MustRestart) return Add(executable, iconPath);
        Run("-shutdown");
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (GameRunner.IsSteamClientRunning())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Steam did not close.");
            System.Threading.Thread.Sleep(500);
        }
        try { return Add(executable, iconPath); }
        finally { Run(); }
    }

    private static void Run(params string[] arguments)
    {
        var start = OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open", new[] { "-a", "Steam", "--args" }.Concat(arguments))
            : new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? Path.Combine(SteamRoot() ?? throw new DirectoryNotFoundException("Steam was not found."), "steam.exe") : "steam", arguments);
        start.UseShellExecute = false;
        System.Diagnostics.Process.Start(start)?.Dispose();
    }

    /// <summary>Adds or refreshes the entry for every Steam account on this PC. Returns the number of accounts changed.</summary>
    internal static int Add(string executable, string? iconPath = null)
    {
        var root = SteamRoot() ?? throw new DirectoryNotFoundException("Steam was not found.");
        var accounts = Path.Combine(root, "userdata");
        var directories = Directory.Exists(accounts) ? Directory.GetDirectories(accounts).Where(d => Path.GetFileName(d).All(char.IsDigit) && Path.GetFileName(d) != "0").ToArray() : Array.Empty<string>();
        if (directories.Length == 0) throw new DirectoryNotFoundException("No Steam account was found on this PC. Sign in to Steam once.");
        foreach (var account in directories)
        {
            var config = Path.Combine(account, "config");
            Directory.CreateDirectory(config);
            var path = Path.Combine(config, "shortcuts.vdf");
            var existing = File.Exists(path) ? File.ReadAllBytes(path) : null;
            var result = Merge(existing, executable, iconPath);
            if (existing is not null) File.WriteAllBytes(path + ".bak", existing);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, result);
            File.Move(temporary, path, true);
        }
        TurnOffSteamInput(AppId("\"" + executable + "\"", AppName));
        return directories.Length;
    }

    // JacintoNet starts Steam as Spacewar, the SDK's test app; the library entry is the other way in.
    internal const uint JacintoNetApp = 480;

    /// <summary>
    /// Steam Input turns the gamepad into its own layout, so it starts off for the game. A player who changed it in Steam keeps that choice.
    /// Steam keeps the file in memory and writes it back on exit, so this does nothing while Steam runs.
    /// </summary>
    internal static void TurnOffSteamInput(uint? shortcutApp = null)
    {
        if (GameRunner.IsSteamClientRunning() || SteamRoot() is not { } root || !Directory.Exists(Path.Combine(root, "userdata"))) return;
        var apps = shortcutApp is { } shortcut ? new[] { JacintoNetApp, shortcut } : new[] { JacintoNetApp };
        foreach (var path in Directory.GetDirectories(Path.Combine(root, "userdata")).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists))
        {
            var text = File.ReadAllText(path);
            var updated = WithoutSteamInput(text, apps);
            if (updated == text) continue;
            File.WriteAllText(path + ".bak", text);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, updated);
            File.Move(temporary, path, true);
        }
    }

    /// <summary>localconfig.vdf with UseSteamControllerConfig "0" (off) for each app that has no value yet. Per-app options are under "apps", one tab in.</summary>
    internal static string WithoutSteamInput(string text, IEnumerable<uint> apps)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        const string AppsBlock = "(?mi)^\t\"apps\"\r?\n\t[{]\r?\n";
        var block = Regex.Match(text, AppsBlock);
        if (!block.Success)
        {
            var last = text.LastIndexOf('}');
            if (last < 0) throw new InvalidDataException("localconfig.vdf has an unknown format; it was left untouched.");
            text = text[..last] + "\t\"apps\"" + nl + "\t{" + nl + "\t}" + nl + text[last..];
            block = Regex.Match(text, AppsBlock);
        }
        var start = block.Index + block.Length;
        var line = "\t\t\t\"UseSteamControllerConfig\"\t\t\"0\"" + nl;
        foreach (var app in apps)
        {
            var end = start + Regex.Match(text[start..], "(?m)^\t[}]").Index;
            var own = Regex.Match(text[start..end], "(?m)^\t\t\"" + app + "\"\r?\n\t\t[{]\r?\n");
            if (!own.Success) { text = text.Insert(start, "\t\t\"" + app + "\"" + nl + "\t\t{" + nl + line + "\t\t}" + nl); continue; }
            var bodyStart = start + own.Index + own.Length;
            var bodyEnd = bodyStart + Regex.Match(text[bodyStart..], "(?m)^\t\t[}]").Index;
            if (!text[bodyStart..bodyEnd].Contains("\"UseSteamControllerConfig\"", StringComparison.OrdinalIgnoreCase)) text = text.Insert(bodyStart, line);
        }
        return text;
    }

    /// <summary>Non-Steam entries carry a CRC-based id; Steam computes it from the exe and name when it is absent or stale.</summary>
    internal static uint AppId(string quotedExe, string name)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in Encoding.UTF8.GetBytes(quotedExe + name))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc | 0x80000000;
    }

    // ponytail: keeps existing entries as raw bytes; only top-level entries are split, which is all this needs.
    internal static byte[] Merge(byte[]? existing, string executable, string? iconPath)
    {
        var quoted = "\"" + executable + "\"";
        var entries = new List<byte[]>();
        if (existing is not null && existing.Length > 0)
        {
            var at = 0;
            if (existing[at++] != Map || ReadCString(existing, ref at) != "shortcuts") throw new InvalidDataException("shortcuts.vdf has an unknown format; it was left untouched.");
            while (existing[at] == Map)
            {
                at++;
                ReadCString(existing, ref at); // Index, renumbered below.
                var bodyStart = at;
                SkipMap(existing, ref at);
                var body = existing[bodyStart..at];
                if (!Mentions(body, AppName)) entries.Add(body);
            }
            if (existing[at] != End) throw new InvalidDataException("shortcuts.vdf has an unknown format; it was left untouched.");
        }
        entries.Add(BuildEntry(quoted, iconPath));
        using var output = new MemoryStream();
        output.WriteByte(Map); WriteCString(output, "shortcuts");
        for (var i = 0; i < entries.Count; i++)
        {
            output.WriteByte(Map); WriteCString(output, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            output.Write(entries[i]);
        }
        output.WriteByte(End); output.WriteByte(End);
        return output.ToArray();
    }

    private static byte[] BuildEntry(string quotedExe, string? iconPath)
    {
        using var body = new MemoryStream();
        void Str(string key, string value) { body.WriteByte(Text); WriteCString(body, key); WriteCString(body, value); }
        void Num(string key, uint value) { body.WriteByte(Int); WriteCString(body, key); body.Write(BitConverter.GetBytes(value)); }
        Num("appid", AppId(quotedExe, AppName));
        Str("AppName", AppName);
        Str("Exe", quotedExe);
        Str("StartDir", "\"" + Path.GetDirectoryName(quotedExe.Trim('"')) + "\"");
        Str("icon", iconPath ?? "");
        Str("ShortcutPath", "");
        Str("LaunchOptions", "");
        Num("IsHidden", 0); Num("AllowDesktopConfig", 1); Num("AllowOverlay", 1); Num("OpenVR", 0); Num("Devkit", 0);
        Str("DevkitGameID", "");
        Num("DevkitOverrideAppID", 0); Num("LastPlayTime", 0);
        Str("FlatpakAppID", "");
        body.WriteByte(Map); WriteCString(body, "tags"); body.WriteByte(End);
        body.WriteByte(End);
        return body.ToArray();
    }

    private static bool Mentions(byte[] body, string name) => body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(name + "\0")) >= 0;

    private static string ReadCString(byte[] data, ref int at)
    {
        var end = Array.IndexOf(data, (byte)0, at);
        if (end < 0) throw new InvalidDataException("shortcuts.vdf is truncated; it was left untouched.");
        var value = Encoding.UTF8.GetString(data, at, end - at);
        at = end + 1;
        return value;
    }

    private static void WriteCString(Stream output, string value) { output.Write(Encoding.UTF8.GetBytes(value)); output.WriteByte(0); }

    private static void SkipMap(byte[] data, ref int at)
    {
        while (true)
        {
            var type = data[at++];
            if (type == End) return;
            ReadCString(data, ref at);
            if (type == Text) ReadCString(data, ref at);
            else if (type == Int) at += 4;
            else if (type == Map) SkipMap(data, ref at);
            else throw new InvalidDataException("shortcuts.vdf has an unknown format; it was left untouched.");
        }
    }
}

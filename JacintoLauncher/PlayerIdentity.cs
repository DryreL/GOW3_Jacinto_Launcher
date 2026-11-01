#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JacintoLauncher;

/// <summary>
/// Writes the player name where the game reads it: sera.ini (campaign, pause menu), [DefaultPlayer] Name (multiplayer and co-op)
/// and the localized "not signed in" text, which the build fills with "Jacinto 1.1.1" and the main menu's party panel shows.
/// </summary>
internal static class PlayerIdentity
{
    internal static string SeraIni(string game) => Path.Combine(game, "Binaries", "Win64", "sera.ini");

    // URL option separators are dropped and the name keeps 12 characters.
    internal static string GameName(string name)
    {
        var clean = Regex.Replace(name, "[?=#\"\\r\\n]", "").Trim();
        if (clean.Length == 0) clean = "Player";
        return clean.Length > 12 ? clean[..12] : clean;
    }

    internal static void Apply(string game, string name, string? documents)
    {
        var full = Regex.Replace(name, "[\\r\\n]", "").Trim();
        var short_ = GameName(name);
        Rewrite(SeraIni(game), text => Regex.Replace(text, @"(?m)^player_name=[^\r\n]*", m => "player_name=" + full));
        var config = Path.Combine(game, "GearGame", "Config");
        var inis = new List<string> { Path.Combine(config, "DefaultGame.ini"), Path.Combine(config, "GearGame.ini") };
        if (documents is not null) inis.Add(Path.Combine(documents, "My Games", "Gears of War 3", "GearGame", "Config", "GearGame.ini"));
        foreach (var ini in inis)
            Rewrite(ini, text => Regex.Replace(text, @"(?ms)(^\[DefaultPlayer\]\r?\n(?:(?!\[)[^\r\n]*\r?\n)*?Name=)[^\r\n]*", m => m.Groups[1].Value + short_));
        var localization = Path.Combine(game, "GearGame", "Localization");
        if (!Directory.Exists(localization)) return;
        foreach (var file in Directory.EnumerateFiles(localization, "*", SearchOption.AllDirectories).Where(f => Path.GetFileNameWithoutExtension(f).Equals("GearGame", StringComparison.OrdinalIgnoreCase) && !f.Contains(".bak", StringComparison.OrdinalIgnoreCase)))
            Rewrite(file, text => Regex.Replace(text, @"(?m)^(NotSignedIn(?:String)?=)[^\r\n]*", m => m.Groups[1].Value + short_));
    }

    // Keeps each file's encoding: the localization files are UTF-16 or plain 8-bit. Unchanged files are not touched.
    private static void Rewrite(string path, Func<string, string> change)
    {
        if (!File.Exists(path)) return;
        var bytes = File.ReadAllBytes(path);
        Encoding encoding;
        int skip = 0;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = Encoding.Unicode; skip = 2; }
        else if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { encoding = new UTF8Encoding(false); skip = 3; }
        else
        {
            encoding = new UTF8Encoding(false, true);
            try { encoding.GetString(bytes); } catch (ArgumentException) { encoding = Encoding.Latin1; }
        }
        var before = encoding.GetString(bytes, skip, bytes.Length - skip);
        var after = change(before);
        if (after == before) return;
        var body = encoding.GetBytes(after);
        var output = new byte[skip + body.Length];
        Array.Copy(bytes, output, skip);
        body.CopyTo(output, skip);
        File.WriteAllBytes(path, output);
    }
}

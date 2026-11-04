#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
namespace JacintoLauncher;
internal static class IntroSettings
{
    private const string Marker = ";JacintoSkipIntro:";
    internal static string Transform(string text, bool enabled)
    {
        bool movieSection = false;
        return Regex.Replace(text, @"[^\r\n]+", match =>
        {
            string line = match.Value;
            if (Regex.IsMatch(line, @"^\s*\[.*\]\s*$"))
                movieSection = line.Trim().Equals("[FullScreenMovie]", StringComparison.OrdinalIgnoreCase);
            if (!movieSection) return line;
            if (!enabled && line.StartsWith(Marker, StringComparison.Ordinal)) return line.Substring(Marker.Length);
            if (enabled && Regex.IsMatch(line, @"^\s*\+?StartupMovies\s*=\s*Open_Logos\s*$", RegexOptions.IgnoreCase))
                return Marker + line;
            return line;
        });
    }
    internal static void Apply(string[] directories, bool enabled, Action persist)
    {
        if (GameLayout.GameRunning())
            throw new UserActionException("Close the game before changing Skip Intro.");
        var paths = directories.SelectMany(d => new[] { Path.Combine(d,"GearGame","Config","GearEngine.ini"), Path.Combine(d,"GearGame","Config","DefaultEngine.ini") }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var before = new Dictionary<string, byte[]>();
        foreach (var path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required engine configuration is missing.", path);
            before[path] = File.ReadAllBytes(path);
        }
        try
        {
            foreach (var path in paths)
            {
                var bytes = before[path];
                // Preserve UTF-8 BOM and all line endings. Refuse unsupported encoding.
                if (bytes.Length >= 2 && ((bytes[0] == 255 && bytes[1] == 254) || (bytes[0] == 254 && bytes[1] == 255)))
                    throw new InvalidDataException("UTF-16 INI requires explicit encoding support: " + path);
                var encoding = new UTF8Encoding(false, true);
                var text = encoding.GetString(bytes);
                var changed = Transform(text, enabled);
                if (changed != text) Write(path, encoding.GetBytes(changed));
            }
            persist();
        }
        catch
        {
            foreach (var item in before) if (!File.ReadAllBytes(item.Key).SequenceEqual(item.Value)) Write(item.Key, item.Value);
            throw;
        }
    }
    private static void Write(string path, byte[] bytes)
    {
        var temp = path + ".intro-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllBytes(temp, bytes); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JacintoLauncher;

/// <summary>
/// The HD Textures Pack rule, same as its HD_TexturePack_Caps.ps1: a size cap is raised only for texture groups whose
/// stock textures the installed phases made safe; raising any other crashes the game (Offset != INDEX_NONE).
/// Phase 1 unlocks Character, Weapon and Vehicle; Phase 1 + 2 also World. Runs after install and before every launch,
/// so the pack and the Multipatch can be installed or removed in any order.
/// </summary>
internal static class HdTextureCaps
{
    private const int RaisedSize = 4096;
    private static readonly Dictionary<string, (int Size, int Bias)> Stock = new()
    {
        ["World"] = (1024, 0), ["WorldNormalMap"] = (1024, 0), ["WorldSpecular"] = (512, 1),
        ["Character"] = (1024, 0), ["CharacterNormalMap"] = (1024, 0), ["CharacterSpecular"] = (512, 0),
        ["Weapon"] = (1024, 0), ["WeaponNormalMap"] = (1024, 0), ["WeaponSpecular"] = (512, 0),
        ["Vehicle"] = (1024, 0), ["VehicleNormalMap"] = (1024, 0), ["VehicleSpecular"] = (512, 1),
    };

    private static bool PhaseInstalled(string game, int phase)
    {
        var marker = Path.Combine(game, "GearGame", "Content", $"HD_TexturePack_Phase{phase}.version");
        return File.Exists(marker) && Version.TryParse(File.ReadLines(marker).FirstOrDefault()?.Trim(), out var version) && version >= new Version(1, 3);
    }

    /// <param name="qualityLodBias">LODBias of the launcher's Texture Quality setting for non-specular groups; null when unknown.</param>
    internal static void Apply(string game, string documentsRoot, string? qualityLodBias)
    {
        var phase1 = PhaseInstalled(game, 1);
        var world = phase1 && PhaseInstalled(game, 2);
        bool Safe(string group) => group.StartsWith("World", StringComparison.Ordinal) ? world : phase1;
        var config = Path.Combine(game, "GearGame", "Config");
        foreach (var (path, isBase) in new[] { (Path.Combine(config, "GearEngine.ini"), false), (Path.Combine(config, "DefaultEngine.ini"), false), (Path.Combine(game, "Engine", "Config", "BaseEngine.ini"), true), (Path.Combine(documentsRoot, "My Games", "Gears of War 3", "GearGame", "Config", "GearEngine.ini"), false) })
        {
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path, Encoding.Latin1); // Byte-preserving: only the matched numbers may change.
            var parts = Regex.Split(text, "(\r?\n)");
            var inSystem = false;
            for (var i = 0; i < parts.Length; i += 2)
            {
                if (Regex.IsMatch(parts[i], @"^\s*\[")) inSystem = parts[i].Trim() == "[SystemSettings]";
                var match = inSystem ? Regex.Match(parts[i], @"^TEXTUREGROUP_(\w+)=") : Match.Empty;
                if (!match.Success || !Stock.TryGetValue(match.Groups[1].Value, out var stock)) continue;
                var group = match.Groups[1].Value;
                var safe = Safe(group);
                if (isBase && !safe) continue; // BaseEngine's own stock values are higher and overridden by DefaultEngine.
                var quality = group.EndsWith("Specular", StringComparison.Ordinal) ? null : qualityLodBias;
                var bias = quality ?? (safe ? "0" : stock.Bias.ToString());
                parts[i] = Regex.Replace(parts[i], @"LODBias=-?\d+", "LODBias=" + bias);
                parts[i] = Regex.Replace(parts[i], @"MaxLODSize=(\d+)", m => "MaxLODSize=" + (safe ? (int.Parse(m.Groups[1].Value) <= stock.Size ? RaisedSize : int.Parse(m.Groups[1].Value)) : stock.Size));
            }
            var updated = string.Concat(parts);
            if (updated == text) continue;
            var temporary = path + ".caps-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, updated, Encoding.Latin1);
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Move(temporary, path, true);
        }
    }
}

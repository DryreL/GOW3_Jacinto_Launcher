#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace JacintoLauncher;

/// <summary>
/// Checks that the installation really holds what the player chose: the patched files, every game setting, the keys,
/// the network mode and the launcher itself. Read-only; each finding says what is wrong and where.
/// </summary>
internal static class InstallAudit
{
    /// <summary>One line of the audit. <see cref="Problem"/> is null when the check passed.</summary>
    internal sealed record Finding(string Area, string Item, string? Problem);

    internal static List<Finding> Run(string game)
    {
        var findings = new List<Finding>();
        // One check failing to run is itself a finding; the others still run.
        void Check(string area, Action check)
        {
            try { check(); }
            catch (Exception exception) { findings.Add(new Finding(area, L.T("The check could not run."), exception.Message)); }
        }

        Check(L.T("Game files"), () =>
        {
            if (!MultipatchInstaller.Managed(game)) { findings.Add(new Finding(L.T("Game files"), L.T("Not checked: the old Multi Patch Tool patched this game, so the launcher has no patch data to compare with."), null)); return; }
            var damaged = MultipatchInstaller.Verify(game);
            foreach (var entry in damaged)
                findings.Add(new Finding(L.T("Game files"), entry.Path, File.Exists(Path.Combine(game, entry.Path.Replace('\\', Path.DirectorySeparatorChar))) ? L.T("Differs from the Multipatch file. Repair in Settings rewrites it.") : L.T("Missing. Repair in Settings puts it back.")));
            if (damaged.Count == 0) findings.Add(new Finding(L.T("Game files"), L.T("Patched files"), null));
        });

        Check(L.T("Game settings"), () => Settings(L.T("Game settings"), MultipatchBridge.LoadSettingsFromMultipatch(game)["Applied"]?["Problems"]?.AsArray(), L.T("Every setting is in the configuration files.")));
        Check(L.T("Controls"), () => Settings(L.T("Controls"), MultipatchBridge.ControlProblems(game), L.T("Every key is in the input files.")));

        Check(L.T("Network mode"), () =>
        {
            var mode = NetworkMode.Read(game).Describe();
            findings.Add(new Finding(L.T("Network mode"), L.T(mode == NetworkMode.Steam ? "Steam co-op" : mode == NetworkMode.Offline ? "Offline" : mode),
                mode is NetworkMode.Steam or NetworkMode.Offline ? null : L.T("The campaign setting and the JacintoNet files do not agree. Press Play Offline or Play on Steam once to set them again.")));
        });

        Check(L.T("Launcher"), () =>
        {
            var launcher = Path.Combine(game, "JacintoLauncher", "Launcher");
            var found = Directory.Exists(launcher) && Directory.EnumerateFiles(launcher).Any(f => Path.GetFileName(f).StartsWith("GoW3Jacinto_", StringComparison.OrdinalIgnoreCase));
            findings.Add(new Finding(L.T("Launcher"), L.T("Launcher in the game folder"), found ? null : L.T("Not found in JacintoLauncher\\Launcher. Your antivirus may have taken it.")));
        });
        return findings;

        void Settings(string area, JsonArray? problems, string allGood)
        {
            foreach (var problem in problems?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            {
                var actual = problem["Actual"]?.GetValue<string>();
                findings.Add(new Finding(area, problem["Key"]?.GetValue<string>() + " (" + problem["File"]?.GetValue<string>() + ")",
                    actual is null ? L.F("Not set; should be {0}.", problem["Expected"]?.GetValue<string>() ?? "") : L.F("Is {0}; should be {1}.", actual, problem["Expected"]?.GetValue<string>() ?? "")));
            }
            if (problems is null || problems.Count == 0) findings.Add(new Finding(area, allGood, null));
        }
    }
}

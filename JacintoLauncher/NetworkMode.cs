#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace JacintoLauncher;

/// <summary>
/// Switches one Multipatch installation between Offline and Steam/JacintoNet.
/// The two modes differ only in the Campaign URL inside GearGame.u
/// ("?listen" / "?nolist", located with the Multipatch step 30 pattern) and in
/// the presence of the JacintoNet proxy DLLs in Binaries\Win64.
/// </summary>
internal static class NetworkMode
{
    internal const string Offline = "Offline";
    internal const string Steam = "Steam";
    internal static readonly string[] JacintoNetFiles = { "wsock32.dll", "steam_api64.dll" };

    // Same bytes as Multipatch step 30 ($ListenOld / $ListenNew).
    private static readonly byte[] ListenUrl = Convert.FromHexString("1f3f6c6f6164636865636b706f696e7400160ed0e9ffff614200d0a500001f3f6c697374656e0016");
    private static readonly byte[] NoListUrl = Convert.FromHexString("1f3f6c6f6164636865636b706f696e7400160ed0e9ffff614200d0a500001f3f6e6f6c6973740016");

    internal sealed record State(bool Listen, bool NoList, bool[] DllsInstalled)
    {
        internal string Describe() =>
            Listen && DllsInstalled.All(x => x) ? Steam :
            NoList && DllsInstalled.All(x => !x) ? Offline :
            Listen && DllsInstalled.All(x => !x) ? "LAN" : "Mixed";
    }

    internal static string ScriptPath(string gameDir) => Path.Combine(gameDir, "GearGame", "Script", "GearGame.u");
    private static string BinariesDir(string gameDir) => Path.Combine(gameDir, "Binaries", "Win64");

    /// <summary>JacintoNet DLLs parked while Offline is active; never deleted.</summary>
    internal static string ParkingDir(string gameDir) => Path.Combine(gameDir, "JacintoLauncher", "State", "JacintoNet");

    internal static State Read(string gameDir)
    {
        var bytes = File.ReadAllBytes(ScriptPath(gameDir));
        var listen = Count(bytes, ListenUrl);
        var nolist = Count(bytes, NoListUrl);
        if (listen + nolist != 1)
            throw new InvalidDataException($"The Campaign network setting was not found exactly once in GearGame.u (listen={listen}, nolist={nolist}). This GearGame.u is not a supported Multipatch build.");
        return new State(listen == 1, nolist == 1, JacintoNetFiles.Select(name => File.Exists(Path.Combine(BinariesDir(gameDir), name))).ToArray());
    }

    /// <summary>Source of a JacintoNet DLL for Steam mode: parked copy first, then the Multipatch payload.</summary>
    internal static string? SteamSource(string gameDir, string name)
    {
        var parked = Path.Combine(ParkingDir(gameDir), name);
        if (File.Exists(parked)) return parked;
        var files = GameLayout.ToolFilesDir(gameDir);
        var payload = files is null ? null : Path.Combine(files, name);
        return payload is not null && File.Exists(payload) ? payload : null;
    }

    /// <summary>Read-only check that <paramref name="mode"/> can be applied; throws with the reason otherwise.</summary>
    internal static void Validate(string gameDir, string mode)
    {
        var state = Read(gameDir);
        if (mode == Steam)
        {
            for (var i = 0; i < JacintoNetFiles.Length; i++)
                if (!state.DllsInstalled[i] && SteamSource(gameDir, JacintoNetFiles[i]) is null)
                    throw new FileNotFoundException("JacintoNet file not available for Steam mode: " + JacintoNetFiles[i]);
        }
        else if (mode == Offline)
        {
            foreach (var name in JacintoNetFiles)
            {
                var installed = Path.Combine(BinariesDir(gameDir), name);
                var parked = Path.Combine(ParkingDir(gameDir), name);
                if (File.Exists(installed) && File.Exists(parked) && Hash(installed) != Hash(parked))
                    throw new InvalidDataException("Binaries\\Win64\\" + name + " differs from the parked JacintoNet copy. It was left untouched.");
            }
        }
        else throw new ArgumentException("Unknown network mode: " + mode);
    }

    /// <summary>Drives the installation to <paramref name="mode"/>. Idempotent; refuses while the game runs.</summary>
    internal static void Apply(string gameDir, string mode)
    {
        if (GameLayout.GameRunning()) throw new InvalidOperationException("Close the game before changing the network mode.");
        Validate(gameDir, mode);
        var binaries = BinariesDir(gameDir);
        if (mode == Steam)
        {
            foreach (var name in JacintoNetFiles)
            {
                var installed = Path.Combine(binaries, name);
                if (File.Exists(installed)) continue;
                CopyVerified(SteamSource(gameDir, name)!, installed);
            }
            SetCampaignUrl(gameDir, listen: true);
        }
        else
        {
            SetCampaignUrl(gameDir, listen: false);
            Directory.CreateDirectory(ParkingDir(gameDir));
            foreach (var name in JacintoNetFiles)
            {
                var installed = Path.Combine(binaries, name);
                if (!File.Exists(installed)) continue;
                var parked = Path.Combine(ParkingDir(gameDir), name);
                if (!File.Exists(parked)) CopyVerified(installed, parked);
                if (Hash(installed) != Hash(parked)) throw new InvalidDataException("Parked JacintoNet copy does not match " + name + ".");
                File.Delete(installed);
            }
        }
        var result = Read(gameDir).Describe();
        if (result != mode) throw new InvalidOperationException($"Network mode switch did not complete (now {result}).");
        LauncherDiagnostics.Info("Network mode set to " + mode + ".");
    }

    private static void SetCampaignUrl(string gameDir, bool listen)
    {
        var path = ScriptPath(gameDir);
        var bytes = File.ReadAllBytes(path);
        var from = listen ? NoListUrl : ListenUrl;
        var to = listen ? ListenUrl : NoListUrl;
        var index = IndexOf(bytes, from);
        if (index < 0) return;
        Buffer.BlockCopy(to, 0, bytes, index, to.Length);
        var temporary = path + ".mode-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (Count(File.ReadAllBytes(temporary), to) != 1) throw new IOException("GearGame.u verification failed after writing the network mode.");
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void CopyVerified(string source, string destination)
    {
        var temporary = destination + ".copy-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary);
            if (Hash(source) != Hash(temporary)) throw new IOException("Copy verification failed: " + Path.GetFileName(destination));
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    private static int Count(byte[] haystack, byte[] needle)
    {
        var count = 0;
        var span = haystack.AsSpan();
        for (var at = span.IndexOf(needle); at >= 0; at = span.IndexOf(needle))
        {
            count++;
            span = span[(at + needle.Length)..];
        }
        return count;
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}

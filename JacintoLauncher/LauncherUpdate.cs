#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace JacintoLauncher;

/// <summary>
/// Updates through GitHub Releases. A release carries Gears3_Multipatch_Launcher_v&lt;version&gt;_&lt;platform&gt;.zip and SHA256SUMS.txt, and multipatch.jcpatch with its .sha256 when the fixes changed.
/// The update runs from a new folder next to the old one and replaces it, so nothing has to overwrite itself.
/// </summary>
internal static class LauncherUpdate
{
    internal const string Repository = "DryreL/GOW3_Jacinto_Launcher";
    internal sealed record Release(Version Version, string Notes, string? AssetUrl, string? SumsUrl, string AssetName, string? PatchUrl = null, string? PatchSumUrl = null);

    private static string Platform => (OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux") + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "_arm64" : "_x64");
    internal static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("GoW3JacintoLauncher/" + Current);
        return client;
    }

    internal static Release? Parse(string json)
    {
        var node = JsonNode.Parse(json)!;
        var tag = node["tag_name"]?.GetValue<string>();
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var version) || node["draft"]?.GetValue<bool>() == true || node["prerelease"]?.GetValue<bool>() == true) return null;
        var name = "Gears3_Multipatch_Launcher_" + tag + "_" + Platform + ".zip";
        string? Asset(string wanted) => node["assets"]?.AsArray().FirstOrDefault(a => a?["name"]?.GetValue<string>() == wanted)?["browser_download_url"]?.GetValue<string>();
        return new Release(version, node["body"]?.GetValue<string>() ?? "", Asset(name), Asset("SHA256SUMS.txt"), name, Asset(MultipatchInstaller.PatchsetName), Asset(MultipatchInstaller.PatchsetName + ".sha256"));
    }

    /// <summary>The latest published release, or null when offline or rate limited. Never throws: an update check must not disturb the launcher.</summary>
    internal static async Task<Release?> LatestAsync()
    {
        try
        {
            using var client = Client();
            return Parse(await client.GetStringAsync(Environment.GetEnvironmentVariable("JACINTO_RELEASES_URL") ?? $"https://api.github.com/repos/{Repository}/releases/latest"));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LauncherDiagnostics.Info("Update check skipped: " + exception.Message);
            return null;
        }
    }

    internal static bool IsNewer(Release release) => release.Version > Current && release.AssetUrl is not null && release.SumsUrl is not null;

    /// <summary>Downloads, checks the SHA-256 and starts the new launcher, which finishes the job after this process exits.</summary>
    internal static async Task DownloadAndStartAsync(Release release, string exeDir, Action<int> progress)
    {
        var parent = Path.GetDirectoryName(exeDir)!;
        var incoming = Path.Combine(parent, Path.GetFileName(exeDir) + ".update");
        var archive = incoming + ".zip";
        using var client = Client();
        client.Timeout = TimeSpan.FromMinutes(20);
        var sums = await client.GetStringAsync(release.SumsUrl);
        var expected = sums.Split('\n').Select(line => line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).FirstOrDefault(p => p.Length == 2 && p[1].TrimStart('*') == release.AssetName)?[0]
            ?? throw new InvalidDataException("The release has no checksum for " + release.AssetName + ".");
        using (var response = await client.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(archive);
            var buffer = new byte[1 << 16];
            long done = 0;
            for (int read; (read = await input.ReadAsync(buffer)) > 0;)
            {
                await output.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress((int)(done * 100 / total));
            }
        }
        string actual;
        await using (var stream = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) { File.Delete(archive); throw new InvalidDataException("The downloaded update failed its SHA-256 check."); }
        if (Directory.Exists(incoming)) Directory.Delete(incoming, true);
        ZipFile.ExtractToDirectory(archive, incoming);
        File.Delete(archive);
        // A package zip has one folder around the launcher; the update must be the launcher folder itself.
        // Found by its package name: the running copy may have been renamed (for example "GoW3Jacinto_Launcher v0.3.8.exe").
        var executable = new[] { PackageExe, Path.GetFileName(Environment.ProcessPath!) }
            .Select(name => Directory.EnumerateFiles(incoming, name, SearchOption.AllDirectories).FirstOrDefault()).FirstOrDefault(found => found is not null)
            ?? throw new InvalidDataException("The update holds no launcher.");
        if (Path.GetDirectoryName(executable) is { } inner && !string.Equals(inner, incoming, StringComparison.OrdinalIgnoreCase))
        {
            var lifted = incoming + ".lift";
            Directory.Move(inner, lifted);
            Directory.Delete(incoming, true);
            Directory.Move(lifted, incoming);
            executable = Path.Combine(incoming, Path.GetFileName(executable));
        }
        if (release.PatchUrl is not null && release.PatchSumUrl is not null)
        {
            var patch = await client.GetByteArrayAsync(release.PatchUrl);
            var sum = (await client.GetStringAsync(release.PatchSumUrl)).Split(' ', '\n', '\r')[0];
            if (!Convert.ToHexString(SHA256.HashData(patch)).Equals(sum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded patch data failed its SHA-256 check.");
            await File.WriteAllBytesAsync(Path.Combine(incoming, MultipatchInstaller.PatchsetName), patch);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = incoming };
        foreach (var argument in new[] { "--finish-update", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), Environment.ProcessPath! }) start.ArgumentList.Add(argument);
        Process.Start(start)?.Dispose();
    }

    /// <summary>Runs in the new copy: waits for the old launcher, swaps the folders (keeping the user's config) and restarts from the final path.</summary>
    internal static bool FinishIfRequested(string[] args)
    {
        if (args.Length == 3 && args[0] == "--finish-update") { FinishSwap(int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture), args[2]); return true; }
        if (args.Length == 2 && args[0] == "--cleanup-update") { Cleanup(args[1]); return false; }
        return false;
    }

    private static string PackageExe => "GoW3Jacinto_Launcher" + (OperatingSystem.IsWindows() ? ".exe" : "");

    // Only the package's own files are replaced, never the folder: the launcher may sit on the desktop or in Downloads
    // next to the user's files. The old launcher keeps its file name. "oldLauncher" is the old executable; launchers
    // up to v0.3.8 pass their folder instead.
    private static void FinishSwap(int oldProcess, string oldLauncher)
    {
        try
        {
            try { using var process = Process.GetProcessById(oldProcess); process.WaitForExit(30000); } catch (ArgumentException) { }
            if (Directory.Exists(oldLauncher)) oldLauncher = Path.Combine(oldLauncher, PackageExe);
            var target = Path.GetDirectoryName(oldLauncher)!;
            var source = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var backup = oldLauncher + ".old";
            if (File.Exists(oldLauncher))
                for (var attempt = 0; ; attempt++)
                {
                    try { File.Move(oldLauncher, backup, true); break; }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(500); } // An antivirus scan can hold the file briefly.
                }
            try
            {
                File.Copy(Environment.ProcessPath!, oldLauncher, true);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(oldLauncher, File.GetUnixFileMode(Environment.ProcessPath!));
            }
            catch
            {
                if (File.Exists(backup)) File.Move(backup, oldLauncher, true);
                throw;
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(file, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "GoW3Jacinto_Launcher.json") continue;
                var destination = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, true);
            }
            var start = new ProcessStartInfo(oldLauncher) { UseShellExecute = false, WorkingDirectory = target };
            start.ArgumentList.Add("--cleanup-update"); start.ArgumentList.Add(source);
            Process.Start(start)?.Dispose();
        }
        catch (Exception exception) { LauncherDiagnostics.Error("Launcher update could not be finished", exception); }
    }

    private static void Cleanup(string updateFolder)
    {
        try
        {
            Thread.Sleep(1000);
            if (Directory.Exists(updateFolder) && updateFolder.EndsWith(".update", StringComparison.OrdinalIgnoreCase)) Directory.Delete(updateFolder, true);
            if (File.Exists(Environment.ProcessPath + ".old")) File.Delete(Environment.ProcessPath + ".old");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("Update leftovers kept: " + exception.Message); }
    }
}

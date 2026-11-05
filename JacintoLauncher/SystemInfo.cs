#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace JacintoLauncher;

/// <summary>
/// What an error report says about the PC: operating system, processor, memory and graphics card. No name, no address, no serial.
/// Every lookup may fail on some system; a failure leaves that line empty and never throws.
/// </summary>
internal sealed record SystemInfo(string OsName, string OsVersion, string Cpu, int Cores, long RamBytes, string? Gpu, string? GpuVendor, long GpuMemoryMb, string? GpuDriver, string? OtherGpus)
{
    private static readonly Lazy<SystemInfo> Current = new(Read);
    internal static SystemInfo Get() => Current.Value;

    private static T Safe<T>(Func<T> read, T fallback) { try { return read(); } catch (Exception) { return fallback; } }

    private static SystemInfo Read()
    {
        var (os, version) = Safe(OperatingSystemName, ("Unknown", Environment.OSVersion.VersionString));
        var gpus = Safe(Gpus, Array.Empty<Card>());
        var main = gpus.OrderByDescending(g => g.MemoryMb).FirstOrDefault();
        var ram = Safe(() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 0L);
        return new SystemInfo(os, version, Safe(CpuName, ""), Environment.ProcessorCount, ram, main?.Name, main?.Vendor, main?.MemoryMb ?? 0, main?.Driver,
            gpus.Length > 1 ? string.Join("; ", gpus.Where(g => g != main).Select(g => g.Name)) : null);
    }

    private sealed record Card(string Name, string? Vendor, long MemoryMb, string? Driver);

    // ---------------------------------------------------------------- operating system
    private static (string Name, string Version) OperatingSystemName()
    {
        if (OperatingSystem.IsWindows())
        {
            var version = Environment.OSVersion.Version;
            var name = GameRunner.InsideWine ? "Proton/Wine (Windows build)" : version.Build >= 22000 ? "Windows 11" : "Windows 10";
            return (name, version.ToString());
        }
        if (OperatingSystem.IsMacOS()) return ("macOS", Run("sw_vers", "-productVersion").Trim() is { Length: > 0 } mac ? mac : Environment.OSVersion.Version.ToString());
        var release = File.Exists("/etc/os-release") ? File.ReadAllLines("/etc/os-release") : Array.Empty<string>();
        string Field(string key) => release.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..].Trim('"') ?? "";
        var pretty = Field("PRETTY_NAME");
        // SteamOS (the Steam Deck) reports itself as ID=steamos.
        var distro = Field("ID") == "steamos" ? "SteamOS" : pretty.Length != 0 ? pretty : "Linux";
        return (distro, Environment.OSVersion.Version + (Field("VERSION_ID") is { Length: > 0 } id ? " (" + id + ")" : ""));
    }

    // ---------------------------------------------------------------- processor
    private static string CpuName()
    {
        if (OperatingSystem.IsWindows())
            return (Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null) as string ?? "").Trim();
        if (OperatingSystem.IsMacOS()) return Run("sysctl", "-n", "machdep.cpu.brand_string").Trim();
        return File.Exists("/proc/cpuinfo") ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "" : "";
    }

    // ---------------------------------------------------------------- graphics
    private static Card[] Gpus()
    {
        if (OperatingSystem.IsWindows()) return WindowsGpus();
        if (OperatingSystem.IsMacOS())
        {
            var chip = Regex.Match(Run("system_profiler", "SPDisplaysDataType"), @"Chipset Model:\s*(.+)");
            return chip.Success ? new[] { new Card(chip.Groups[1].Value.Trim(), "Apple/AMD", 0, null) } : Array.Empty<Card>();
        }
        var found = Run("lspci", "-mm").Split('\n')
            .Select(line => Regex.Match(line, "\"(?:VGA compatible controller|3D controller|Display controller)\" \"([^\"]*)\" \"([^\"]*)\""))
            .Where(m => m.Success).Select(m => new Card(m.Groups[2].Value, m.Groups[1].Value, 0, null)).ToArray();
        return found.Length != 0 ? found : SysfsGpus();
    }

    // No lspci (the Steam Deck image may lack it): the kernel still lists the PCI vendor of each card.
    private static Card[] SysfsGpus() =>
        !Directory.Exists("/sys/class/drm") ? Array.Empty<Card>()
        : Directory.GetDirectories("/sys/class/drm", "card?").Select(card => Path.Combine(card, "device", "vendor")).Where(File.Exists).Select(path => File.ReadAllText(path).Trim())
            .Select(id => new Card("PCI device (" + id + ")", id switch { "0x1002" => "AMD", "0x10de" => "NVIDIA", "0x8086" => "Intel", _ => id }, 0, null)).ToArray();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Card[] WindowsGpus()
    {
        using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
        if (classKey is null) return Array.Empty<Card>();
        var list = new System.Collections.Generic.List<Card>();
        foreach (var name in classKey.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var key = classKey.OpenSubKey(name);
            if (key?.GetValue("DriverDesc") is not string description || description.Contains("Basic Display", StringComparison.OrdinalIgnoreCase)) continue;
            var memory = key.GetValue("HardwareInformation.qwMemorySize") switch { long l => l, byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0), int i => (long)(uint)i, _ => 0L };
            list.Add(new Card(description, key.GetValue("ProviderName") as string, memory / (1024 * 1024), key.GetValue("DriverVersion") as string));
        }
        return list.ToArray();
    }

    /// <summary>Output of a small system tool, or empty when it is missing or slow.</summary>
    private static string Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(4000)) { try { process.Kill(); } catch (InvalidOperationException) { } return ""; }
            return output.GetAwaiter().GetResult();
        }
        catch (System.ComponentModel.Win32Exception) { return ""; } // The tool is not installed.
    }
}

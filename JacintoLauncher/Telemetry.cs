#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Sentry;

namespace JacintoLauncher;

/// <summary>
/// Anonymous launcher and game crash reports (Sentry) and usage events (Aptabase). Both are off without a key, when the
/// user opts out in Settings, or with DO_NOT_TRACK=1. Local paths never leave the machine.
/// </summary>
internal static class Telemetry
{
    private static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
    private static readonly string SessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + Random.Shared.Next(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture);
    private static HttpClient? aptabase;
    private static HttpClient? presence;
    private static bool enabled;
    private static IDisposable? sentry;
    private static string? gameDir;
    private static string? playerName;

    private static string Key(string name, string environment) =>
        Environment.GetEnvironmentVariable(environment) is { Length: > 0 } value ? value
        : Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value ?? "";

    internal static void Start(LauncherConfig config, string? game)
    {
        // Called first thing at startup (so no crash is missed) and again when the window knows the game folder or the settings change:
        // what already runs keeps running, only the scrubbed names change.
        gameDir = game;
        playerName = config.PlayerName;
        enabled = config.TelemetryEnabled && Environment.GetEnvironmentVariable("DO_NOT_TRACK") != "1";
        if (!enabled) { Stop(); return; }
        _ = System.Threading.Tasks.Task.Run(SystemInfo.Get); // Looked up now, so a crash report does not wait for it.
        var dsn = Key("SentryDsn", "JACINTO_SENTRY_DSN");
        if (dsn.Length != 0 && sentry is null)
        {
            sentry = SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.Release = "gow3-jacinto-launcher@" + Version;
                options.IsGlobalModeEnabled = true;
                options.AutoSessionTracking = true;
                options.SendDefaultPii = false;
                options.ServerName = "";
                options.SetBeforeSend(evt =>
                {
                    if (evt.Message?.Formatted is { } formatted) evt.Message = Scrub(formatted);
                    foreach (var exception in evt.SentryExceptions ?? Enumerable.Empty<Sentry.Protocol.SentryException>()) exception.Value = exception.Value is null ? null : Scrub(exception.Value);
                    Describe(evt);
                    return evt;
                });
            });
            // Log-only failures become reports too.
            LauncherDiagnostics.ErrorReported = (operation, exception) =>
            {
                if (!LauncherDiagnostics.Reportable(exception)) return;
                SentrySdk.CaptureException(exception, scope => scope.SetTag("operation", operation.Split(' ')[0]));
            };
        }
        var key = Key("AptabaseKey", "JACINTO_APTABASE_KEY");
        var host = key.Split('-') is [_, var region, _] ? region switch { "EU" => "https://eu.aptabase.com", "US" => "https://us.aptabase.com", _ => null } : null;
        if (host is not null && aptabase is null)
        {
            aptabase = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("JACINTO_APTABASE_URL") ?? host), Timeout = TimeSpan.FromSeconds(10) };
            aptabase.DefaultRequestHeaders.Add("App-Key", key);
        }
    }

    /// <summary>Operating system, processor, memory and graphics card on every event, so "Unknown OS" never shows. Nothing that names the player or the PC.</summary>
    private static void Describe(SentryEvent evt)
    {
        try
        {
            var info = SystemInfo.Get();
            evt.Contexts.OperatingSystem.Name = info.OsName;
            evt.Contexts.OperatingSystem.Version = info.OsVersion;
            evt.Contexts.Device.Architecture = RuntimeInformation.OSArchitecture.ToString();
            evt.Contexts.Device.ProcessorCount = info.Cores;
            evt.Contexts.Device.CpuDescription = info.Cpu;
            if (info.RamBytes > 0) evt.Contexts.Device.MemorySize = info.RamBytes;
            if (info.Gpu is not null)
            {
                evt.Contexts.Gpu.Name = info.Gpu; evt.Contexts.Gpu.VendorName = info.GpuVendor; evt.Contexts.Gpu.Version = info.GpuDriver;
                if (info.GpuMemoryMb > 0) evt.Contexts.Gpu.MemorySize = (int)Math.Min(int.MaxValue, info.GpuMemoryMb);
            }
            evt.SetTag("os", info.OsName);
            evt.SetTag("ram_gb", Math.Round(info.RamBytes / 1073741824.0).ToString(CultureInfo.InvariantCulture));
            if (info.Gpu is not null) evt.SetTag("gpu", info.Gpu);
            if (info.OtherGpus is not null) evt.SetExtra("other_gpus", info.OtherGpus);
            evt.SetTag("wine", GameRunner.InsideWine ? "yes" : "no");
        }
        catch (Exception) { } // A report without hardware lines is still a report.
    }

    internal static void Stop()
    {
        LauncherDiagnostics.ErrorReported = null;
        sentry?.Dispose(); sentry = null;
        aptabase?.Dispose(); aptabase = null;
    }

    internal static void CaptureFatal(Exception exception) { if (sentry is not null) { SentrySdk.CaptureException(exception); SentrySdk.Flush(TimeSpan.FromSeconds(3)); } }

    internal static bool CrashReportsOn => sentry is not null;

    /// <summary>
    /// A launcher crash, sent now (before the process dies). True only when reports are on, the server answers and the event was handed over;
    /// false means the player has to report it by hand.
    /// </summary>
    internal static bool SendCrash(LauncherConfig config, Exception exception)
    {
        if (sentry is null) Start(config, gameDir);
        if (sentry is null) return false;
        var uri = new Uri(Key("SentryDsn", "JACINTO_SENTRY_DSN"));
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var answer = probe.GetAsync(uri.Scheme + "://" + uri.Host, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        }
        catch (Exception probeFailure) when (probeFailure is HttpRequestException or TaskCanceledException) { return false; }
        SentrySdk.CaptureException(exception);
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        return true;
    }

    /// <summary>A game crash found by GameCrash, reported apart from launcher errors so the two never group together.</summary>
    internal static void CaptureGameCrash(GameCrash.Report report)
    {
        if (sentry is null) return;
        var crash = new SentryEvent { Message = Scrub(report.Title), Level = SentryLevel.Fatal };
        crash.SetFingerprint(new[] { "game-crash", report.Fingerprint });
        SentrySdk.CaptureEvent(crash, scope =>
        {
            scope.SetTag("source", "game");
            if (report.ExceptionCode is not null) { scope.SetTag("exception_code", report.ExceptionCode); scope.SetExtra("exception_address", report.ExceptionAddress); }
            scope.SetExtra("log_tail", Scrub(report.LogTail));
        });
    }

    /// <summary>
    /// Launchers open now, or null when unknown. Each call also says this one is open, so it follows the same opt-out as the rest.
    /// Aptabase has no read API for an app key, so this asks a small server of our own (tools/presence_server.py).
    /// </summary>
    internal static async Task<(int Online, int? Total)?> OnlineAsync()
    {
        var url = Key("PresenceUrl", "JACINTO_PRESENCE_URL");
        if (url.Length == 0 || !enabled) return null;
        try
        {
            presence ??= new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var answer = await presence.GetFromJsonAsync<Dictionary<string, int>>(url + "?session=" + SessionId);
            return answer is not null && answer.TryGetValue("online", out var online) ? (online, answer.TryGetValue("total", out var total) ? total : null) : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException or InvalidOperationException) { return null; }
    }

    /// <summary>Takes this launcher off the count as it closes. Waits at most 2 seconds; if that fails, the server drops it after 90.</summary>
    internal static void LeaveOnline()
    {
        var url = Key("PresenceUrl", "JACINTO_PRESENCE_URL");
        if (url.Length == 0 || !enabled || presence is null) return;
        var client = presence;
        Task.Run(async () =>
        {
            try { using var _ = await client.GetAsync(url + "?session=" + SessionId + "&leave=1"); }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException) { }
        }).Wait(TimeSpan.FromSeconds(2));
    }

    internal static void Track(string name, string prop, object value) => Track(name, new Dictionary<string, object> { [prop] = value });

    /// <summary>Fire and forget; analytics must never slow down or break the launcher.</summary>
    internal static void Track(string name, Dictionary<string, object>? props = null)
    {
        var client = aptabase;
        if (client is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await client.PostAsJsonAsync("/api/v0/event", new
                {
                    timestamp = DateTime.UtcNow.ToString("o"), sessionId = SessionId, eventName = name, props,
                    systemProps = new
                    {
#if DEBUG
                        isDebug = true,
#else
                        isDebug = false,
#endif
                        osName = OperatingSystem.IsWindows() ? (GameRunner.InsideWine ? "Proton/Wine" : "Windows") : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                        osVersion = Environment.OSVersion.Version.ToString(), locale = CultureInfo.CurrentUICulture.Name, appVersion = Version, appBuildNumber = Version,
                        sdkVersion = "jacinto-launcher@" + Version, deviceModel = RuntimeInformation.OSArchitecture.ToString()
                    }
                });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or ObjectDisposedException) { }
        });
    }

    internal static string Scrub(string text)
    {
        foreach (var (value, label) in new[] { (gameDir, "<game>"), (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>"), (Environment.UserName, "<user>"), (Environment.MachineName, "<pc>"), (playerName, "<player>") })
            if (!string.IsNullOrEmpty(value)) text = text.Replace(value, label, StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"\b7656119\d{10}\b", "<steamid>");
        text = Regex.Replace(text, @"\b\d{1,3}(\.\d{1,3}){3}\b", "<ip>");
        text = Regex.Replace(text, @"'[A-Za-z]:\\[^']*'", "'<path>'"); // Quoted paths may hold spaces, and a folder name after one.
        return Regex.Replace(text, @"[A-Za-z]:\\[^\s""']+", "<path>");
    }
}

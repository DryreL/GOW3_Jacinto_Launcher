#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Sentry;

namespace JacintoLauncher;

/// <summary>
/// Anonymous crash reports (Sentry) and usage events (Aptabase). Both are off without a key, when the
/// user opts out in Settings, or with DO_NOT_TRACK=1. Local paths never leave the machine.
/// </summary>
internal static class Telemetry
{
    private static readonly string Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0";
    private static readonly string SessionId = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) + Random.Shared.Next(10_000_000, 99_999_999).ToString(CultureInfo.InvariantCulture);
    private static HttpClient? aptabase;
    private static IDisposable? sentry;
    private static string? gameDir;

    private static string Key(string name, string environment) =>
        Environment.GetEnvironmentVariable(environment) is { Length: > 0 } value ? value
        : Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == name)?.Value ?? "";

    internal static void Start(LauncherConfig config, string? game)
    {
        Stop();
        gameDir = game;
        if (!config.TelemetryEnabled || Environment.GetEnvironmentVariable("DO_NOT_TRACK") == "1") return;
        var dsn = Key("SentryDsn", "JACINTO_SENTRY_DSN");
        if (dsn.Length != 0)
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
                    return evt;
                });
            });
            // Log-only failures become reports too; refused input is the user's, not a bug.
            LauncherDiagnostics.ErrorReported = (operation, exception) =>
            {
                if (exception is ArgumentException) return;
                SentrySdk.CaptureException(exception, scope => scope.SetTag("operation", operation.Split(' ')[0]));
            };
        }
        var key = Key("AptabaseKey", "JACINTO_APTABASE_KEY");
        var host = key.Split('-') is [_, var region, _] ? region switch { "EU" => "https://eu.aptabase.com", "US" => "https://us.aptabase.com", _ => null } : null;
        if (host is not null)
        {
            aptabase = new HttpClient { BaseAddress = new Uri(host), Timeout = TimeSpan.FromSeconds(10) };
            aptabase.DefaultRequestHeaders.Add("App-Key", key);
        }
    }

    internal static void Stop()
    {
        LauncherDiagnostics.ErrorReported = null;
        sentry?.Dispose(); sentry = null;
        aptabase?.Dispose(); aptabase = null;
    }

    internal static void CaptureFatal(Exception exception) { if (sentry is not null) { SentrySdk.CaptureException(exception); SentrySdk.Flush(TimeSpan.FromSeconds(3)); } }

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

    private static string Scrub(string text)
    {
        foreach (var (value, label) in new[] { (gameDir, "<game>"), (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>"), (Environment.UserName, "<user>") })
            if (!string.IsNullOrEmpty(value)) text = text.Replace(value, label, StringComparison.OrdinalIgnoreCase);
        return text;
    }
}

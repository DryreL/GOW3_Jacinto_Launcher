#nullable enable
using System.IO;
using System.Text.Json;

namespace JacintoLauncher;

public class LauncherConfig
{
	public string PlayerName { get; set; } = "Player";


	public string? InstallDirOverride { get; set; }

	// A legacy InstallDirOverride can still point to a single installation, but
	// an isolated Offline installation and a JacintoNet/Steam installation must
	// never be treated as interchangeable.
	public string? OfflineInstallDirOverride { get; set; }
	public string? SteamInstallDirOverride { get; set; }
	public string PreferredConfigurationMode { get; set; } = "Offline";

	// The patch data skips the intro; the installer turns it back on unless this is set.
	public bool SkipIntro { get; set; }
	public bool SidebarCollapsed { get; set; }
	public bool HideSetupGuide { get; set; }

	public bool BackupSaveOnLaunch { get; set; } = true;

	// The close button sends the launcher to the system tray, so the online count keeps it.
	public bool CloseToTray { get; set; } = true;

	// Off by default: a second start brings the open launcher forward instead.
	public bool AllowMultipleInstances { get; set; }

	// Launcher interface language (L.Languages code); empty = Windows display language. Does not change the game.
	public string? LauncherLanguage { get; set; }

	// LauncherTheme.Options id.
	public string Theme { get; set; } = "blue";

	// auto (follows the system), light or dark.
	public string ThemeMode { get; set; } = "auto";

	// The pre-launch warning about movies over 4 GB (HD Movies Pack).
	public bool HideHdMoviesWarning { get; set; }

	// Game crash logs and dumps up to this time were already looked at; null = first run, older crashes are not sent.
	public System.DateTime? GameCrashCheckedUtc { get; set; }

	// The JacintoNet build the player was last told about; null = none yet, so the first one is not news.
	public string? JacintoNetSeen { get; set; }

	// Anonymous crash reports (Sentry) and usage events (Aptabase). The notice is shown once.
	public bool TelemetryEnabled { get; set; } = true;
	public bool TelemetryNoticeShown { get; set; }

	// Native Linux / macOS only: umu-run, proton or wine path and prefix. Empty = detected / default.
	public string? WineRunner { get; set; }
	public string? WinePrefix { get; set; }


	// A folder Windows does not let the launcher write to, such as Program Files, keeps the settings in the user's own folder instead.
	private static string Fallback(string exeDir) => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "GOW3JacintoLauncher",
		"config-" + System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(exeDir).ToUpperInvariant())))[..12] + ".json");

	private static string PathOf(string exeDir)
	{
		var beside = Path.Combine(exeDir, "GoW3Jacinto_Launcher.json");
		var fallback = Fallback(exeDir);
		return File.Exists(fallback) && (!File.Exists(beside) || File.GetLastWriteTimeUtc(fallback) > File.GetLastWriteTimeUtc(beside)) ? fallback : beside;
	}

	public static LauncherConfig Load(string exeDir)
	{
		try
		{
			string path = PathOf(exeDir);
			if (File.Exists(path))
			{
				return JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path)) ?? new LauncherConfig();
			}
		}
		catch (System.Exception exception)
		{
			LauncherDiagnostics.Info("Launcher settings file not read, defaults are used: " + exception.Message);
		}
		return new LauncherConfig();
	}

	internal static string FilePath(string exeDir) => PathOf(exeDir);

	/// <summary>Takes the values of a file changed outside the launcher. True when any value is new; the launcher's own saves change nothing. A file that does not parse throws and leaves every value as it was.</summary>
	public bool Reload(string exeDir)
	{
		string path = PathOf(exeDir);
		if (!File.Exists(path)) return false;
		LauncherConfig? fresh = JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path));
		if (fresh is null || JsonSerializer.Serialize(fresh) == JsonSerializer.Serialize(this)) return false;
		foreach (var property in typeof(LauncherConfig).GetProperties()) property.SetValue(this, property.GetValue(fresh));
		return true;
	}

	public void Save(string exeDir)
	{
		JsonSerializerOptions options = new JsonSerializerOptions
		{
			WriteIndented = true
		};
		string destination = PathOf(exeDir);
		try { Write(destination, JsonSerializer.Serialize(this, options)); }
		catch (System.UnauthorizedAccessException) when (destination != Fallback(exeDir))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Fallback(exeDir))!);
			Write(Fallback(exeDir), JsonSerializer.Serialize(this, options));
		}
	}

	private static void Write(string destination, string json)
	{
		string temporary = destination + ".tmp-" + System.Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, json);
			File.Move(temporary, destination, true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}
}

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

	// Anonymous crash reports (Sentry) and usage events (Aptabase). The notice is shown once.
	public bool TelemetryEnabled { get; set; } = true;
	public bool TelemetryNoticeShown { get; set; }

	// Native Linux / macOS only: umu-run, proton or wine path and prefix. Empty = detected / default.
	public string? WineRunner { get; set; }
	public string? WinePrefix { get; set; }


	private static string PathOf(string exeDir)
	{
		return Path.Combine(exeDir, "GoW3Jacinto_Launcher.json");
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
		catch
		{
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
		string temporary = destination + ".tmp-" + System.Guid.NewGuid().ToString("N");
		try
		{
			File.WriteAllText(temporary, JsonSerializer.Serialize(this, options));
			File.Move(temporary, destination, true);
		}
		finally
		{
			if (File.Exists(temporary)) File.Delete(temporary);
		}
	}
}

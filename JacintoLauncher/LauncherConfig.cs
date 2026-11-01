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

	public bool SkipIntro { get; set; }

	public bool BackupSaveOnLaunch { get; set; } = true;

	// Launcher interface language (L.Languages code); empty = Windows display language. Does not change the game.
	public string? LauncherLanguage { get; set; }

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

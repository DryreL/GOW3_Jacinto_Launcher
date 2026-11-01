using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace JacintoLauncher;

public class OptionsWindow : Window, IComponentConnector
{
	private const string Unlimited = "Unlimited";

	private static readonly string[] FixedFps = new string[8] { "30", "60", "75", "90", "120", "144", "165", "240" };

	private static readonly string[] Resolutions = new string[7] { "1280x720", "1600x900", "1920x1080", "2560x1080", "2560x1440", "3440x1440", "3840x2160" };

	private static readonly string[] FovSections = new string[7] { "GearGame.GearGameplayCameraMode", "GearGame.GearCameraMode", "GearGame.GameplayCam_Default", "GearGame.GameplayCam_Cover", "GearGame.GameplayCam_RoadieRun", "GearGame.GameplayCam_RoadieRunStab", "GearGame.GameplayCam_DBNO" };

	private static readonly (string Cmd, string Nome)[] Acoes = new(string, string)[23]
	{
		("GBA_MoveForward", "Move forward"),
		("GBA_MoveBackward", "Move backward"),
		("GBA_StrafeLeft", "Strafe left"),
		("GBA_StrafeRight", "Strafe right"),
		("GBA_MoveAction", "Cover / roadie run / vault"),
		("GBA_Crouch", "Crouch"),
		("GBA_Walk", "Walk"),
		("GBA_Shoot", "Fire"),
		("GBA_Target", "Aim"),
		("GBA_Melee", "Melee"),
		("GBA_Reload", "Reload"),
		("GBA_Zoom", "Zoom"),
		("GBA_Pickup", "Pick up / interact"),
		("GBA_POI", "Point of interest"),
		("GBA_Taccom", "Tac-Com"),
		("GBA_Weap1", "Weapon slot 1"),
		("GBA_Weap2", "Weapon slot 2"),
		("GBA_Pistols", "Pistol"),
		("GBA_Grenades", "Grenades"),
		("GBA_PrevWeapon", "Previous weapon"),
		("GBA_NextWeapon", "Next weapon"),
		("GBA_Scoreboard", "Scoreboard"),
		("GBA_Pause", "Pause")
	};

	private static readonly Regex RxBind = new Regex("Bindings=\\(\\s*Name\\s*=\\s*\"(?<k>[^\"]*)\"\\s*,\\s*Command\\s*=\\s*\"(?<c>[^\"]*)\"", RegexOptions.IgnoreCase);

	private readonly string gameDir;

	private readonly string exeDir;

	private readonly LauncherConfig cfg;

	private readonly string engineIni;

	private readonly string cameraIni;

	private readonly string defCameraIni;

	private readonly string inputIni;

	private readonly ObservableCollection<BindRow> binds = new ObservableCollection<BindRow>();

	private double lookUpMagnitude = 250.0;

	internal TabControl Tabs;

	internal ComboBox ResCombo;

	internal CheckBox FullscreenCheck;

	internal CheckBox VsyncCheck;

	internal ComboBox FpsCombo;

	internal TextBlock FpsWarning;

	internal Slider FovSlider;

	internal TextBlock FovValue;

	internal ComboBox MsaaCombo;

	internal ComboBox AnisoCombo;

	internal CheckBox ShadowsCheck;

	internal CheckBox AoCheck;

	internal CheckBox BloomCheck;

	internal CheckBox LightShaftCheck;

	internal CheckBox DofCheck;

	internal CheckBox MotionBlurCheck;

	internal CheckBox LensFlareCheck;

	internal CheckBox SubsCheck;

	internal Slider SensSlider;

	internal TextBlock SensValue;

	internal Slider AimSensSlider;

	internal TextBlock AimSensValue;

	internal CheckBox SmoothingCheck;

	internal CheckBox InvertCheck;

	internal ListBox BindsList;

	internal TextBox PlayerNameBox;

	internal CheckBox BackupCheck;

	internal TextBlock LaunchPathText;

	internal TextBlock IniPathText;

	internal TextBlock SaveStatus;

	private bool _contentLoaded;

	public OptionsWindow(string gameDir, string exeDir, LauncherConfig cfg, int tab)
	{
		InitializeComponent();
		this.gameDir = gameDir;
		this.exeDir = exeDir;
		this.cfg = cfg;
		engineIni = FindIni("GearEngine.ini");
		cameraIni = FindIni("GearCamera.ini");
		defCameraIni = FindIni("DefaultCamera.ini");
		inputIni = FindIni("GearInput.ini");
		BindsList.ItemsSource = binds;
		LoadVideo();
		LoadControls();
		LoadLauncher();
		Tabs.SelectedIndex = Math.Max(0, Math.Min(2, tab));
	}

	private string FindIni(string name)
	{
		string text = Path.Combine(gameDir, "GearGame", "Config", name);
		if (!File.Exists(text))
		{
			return null;
		}
		return text;
	}

	private void LoadVideo()
	{
		string[] resolutions = Resolutions;
		foreach (string newItem in resolutions)
		{
			ResCombo.Items.Add(newItem);
		}
		resolutions = FixedFps;
		foreach (string text in resolutions)
		{
			FpsCombo.Items.Add(text + " FPS");
		}
		FpsCombo.Items.Add("Unlimited");
		resolutions = new string[5] { "Off", "FXAA", "2x", "4x", "8x" };
		foreach (string newItem2 in resolutions)
		{
			MsaaCombo.Items.Add(newItem2);
		}
		resolutions = new string[5] { "Trilinear", "2x", "4x", "8x", "16x" };
		foreach (string newItem3 in resolutions)
		{
			AnisoCombo.Items.Add(newItem3);
		}
		if (engineIni == null)
		{
			SaveStatus.Text = "GearEngine.ini not found";
			Control[] array = new Control[7] { ResCombo, FpsCombo, MsaaCombo, AnisoCombo, FullscreenCheck, VsyncCheck, SubsCheck };
			for (int i = 0; i < array.Length; i++)
			{
				array[i].IsEnabled = false;
			}
			return;
		}
		string text2 = Read(engineIni, "SystemSettings", "ResX");
		string text3 = Read(engineIni, "SystemSettings", "ResY");
		if (text2 != null && text3 != null)
		{
			string text4 = text2 + "x" + text3;
			if (!ResCombo.Items.Contains(text4))
			{
				ResCombo.Items.Insert(0, text4);
			}
			ResCombo.SelectedItem = text4;
		}
		FullscreenCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "Fullscreen"), padrao: true);
		VsyncCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "UseVsync"), padrao: false);
		SubsCheck.IsChecked = IsOn(Read(engineIni, "Engine.Engine", "bSubtitlesEnabled"), padrao: true);
		ShadowsCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "DynamicShadows"), padrao: true);
		AoCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "AmbientOcclusion"), padrao: true);
		BloomCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "Bloom"), padrao: true);
		LightShaftCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "bAllowLightShafts"), padrao: true);
		DofCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "DepthOfField"), padrao: true);
		MotionBlurCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "MotionBlur"), padrao: true);
		LensFlareCheck.IsChecked = IsOn(Read(engineIni, "SystemSettings", "LensFlares"), padrao: true);
		bool flag = IsOn(Read(engineIni, "SystemSettings", "bAllowD3D9MSAA"), padrao: false);
		int num = ParseInt(Read(engineIni, "SystemSettings", "MaxMultiSamples"), 1);
		MsaaCombo.SelectedItem = ((!flag || num <= 1) ? "Off" : (num + "x"));
		if (MsaaCombo.SelectedItem == null)
		{
			MsaaCombo.SelectedItem = "Off";
		}
		int num2 = ParseInt(Read(engineIni, "SystemSettings", "MaxAnisotropy"), 1);
		AnisoCombo.SelectedItem = ((num2 <= 1) ? "Trilinear" : (num2 + "x"));
		if (AnisoCombo.SelectedItem == null)
		{
			AnisoCombo.SelectedItem = "Trilinear";
		}
		if (!IsOn(Read(engineIni, "Engine.Engine", "bSmoothFrameRate"), padrao: true))
		{
			FpsCombo.SelectedItem = "Unlimited";
		}
		else
		{
			string text5 = ParseFps(Read(engineIni, "Engine.Engine", "MaxSmoothedFrameRate")) + " FPS";
			if (!FpsCombo.Items.Contains(text5))
			{
				FpsCombo.Items.Insert(0, text5);
			}
			FpsCombo.SelectedItem = text5;
		}
		double val = 110.0;
		if (cameraIni != null)
		{
			val = ParseDouble(Read(cameraIni, "GearGame.GameplayCam_Default", "FOVAngle"), 110.0);
		}
		FovSlider.Value = Math.Max(FovSlider.Minimum, Math.Min(FovSlider.Maximum, val));
		FovValue.Text = ((int)FovSlider.Value).ToString(CultureInfo.InvariantCulture);
		LoadMultipatchGraphics();
	}

	private void LoadMultipatchGraphics()
	{
		if (!MultipatchBridge.TryLoadGraphics(gameDir, out var g)) return;
		string resolution = g["Resolution"]?.GetValue<string>();
		if (!string.IsNullOrEmpty(resolution)) { if (!ResCombo.Items.Contains(resolution)) ResCombo.Items.Insert(0, resolution); ResCombo.SelectedItem = resolution; }
		if (g["Fullscreen"] != null) FullscreenCheck.IsChecked = g["Fullscreen"].GetValue<bool>();
		if (g["VSync"] != null) VsyncCheck.IsChecked = g["VSync"].GetValue<bool>();
		if (g["FpsLimit"] != null) { int fps = g["FpsLimit"].GetValue<int>(); FpsCombo.SelectedItem = fps == 0 ? "Unlimited" : fps + " FPS"; }
		if (g["Fov"] != null) { int fov = g["Fov"].GetValue<int>(); if (fov != 0) FovSlider.Value = Math.Max(FovSlider.Minimum, Math.Min(FovSlider.Maximum, fov)); FovValue.Text = ((int)FovSlider.Value).ToString(CultureInfo.InvariantCulture); }
		string aa = g["AntiAliasing"]?.GetValue<string>();
		if (aa != null) MsaaCombo.SelectedItem = aa switch { "fxaa" => "FXAA", "msaa2x" => "2x", "msaa4x" => "4x", _ => "Off" };
	}

	private void FpsCombo_Changed(object sender, SelectionChangedEventArgs e)
	{
		if (FpsWarning != null)
		{
			string text = FpsCombo.SelectedItem as string;
			if (text == "Unlimited")
			{
				FpsWarning.Text = "Unlimited is unstable on this build: the physics of this 2011 engine is tied to the frame rate. It was tested twice and behaved differently each time (past 300 FPS with stutter once, locked at 40 the other). Prefer a fixed number that matches your monitor.";
				FpsWarning.Visibility = Visibility.Visible;
			}
			else if (text != null && ParseInt(text.Replace(" FPS", ""), 60) > 165)
			{
				FpsWarning.Text = "Very high caps can also get unstable on this engine. If the game stutters, drop to 144 or 165.";
				FpsWarning.Visibility = Visibility.Visible;
			}
			else
			{
				FpsWarning.Visibility = Visibility.Collapsed;
			}
		}
	}

	private void FovSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (FovValue != null)
		{
			FovValue.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture);
		}
	}

	private void LoadControls()
	{
		if (inputIni == null)
		{
			Slider sensSlider = SensSlider;
			bool isEnabled = (AimSensSlider.IsEnabled = false);
			sensSlider.IsEnabled = isEnabled;
			CheckBox smoothingCheck = SmoothingCheck;
			isEnabled = (InvertCheck.IsEnabled = false);
			smoothingCheck.IsEnabled = isEnabled;
			binds.Add(new BindRow
			{
				Action = "GearInput.ini not found",
				Key = ""
			});
		}
		else
		{
			SensSlider.Value = Clamp(ParseDouble(Read(inputIni, "Engine.PlayerInput", "MouseSensitivity"), 7.0), SensSlider.Minimum, SensSlider.Maximum);
			SensValue.Text = SensSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
			AimSensSlider.Value = Clamp(ParseDouble(Read(inputIni, "GearGame.GearPlayerInput", "TargetingModeViewScalePct"), 0.455), AimSensSlider.Minimum, AimSensSlider.Maximum);
			AimSensValue.Text = AimSensSlider.Value.ToString("0.000", CultureInfo.InvariantCulture);
			SmoothingCheck.IsChecked = IsOn(Read(inputIni, "Engine.PlayerInput", "bEnableMouseSmoothing"), padrao: true);
			double num = ParseDouble(Read(inputIni, "Engine.PlayerInput", "LookUpScale"), -250.0);
			lookUpMagnitude = ((Math.Abs(num) < 1.0) ? 250.0 : Math.Abs(num));
			InvertCheck.IsChecked = num > 0.0;
			CarregarBinds();
		}
	}

	private void CarregarBinds()
	{
		binds.Clear();
		List<(string, string)> list = new List<(string, string)>();
		bool flag = false;
		string[] array = File.ReadAllLines(inputIni);
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith("["))
			{
				flag = text.Equals("[Engine.PlayerInput]", StringComparison.OrdinalIgnoreCase);
			}
			else if (flag && !text.StartsWith(";"))
			{
				Match match = RxBind.Match(text);
				if (match.Success)
				{
					list.Add((match.Groups["k"].Value, match.Groups["c"].Value));
				}
			}
		}
		(string, string)[] acoes = Acoes;
		for (int i = 0; i < acoes.Length; i++)
		{
			(string, string) tuple = acoes[i];
			foreach (var item in list)
			{
				if (!item.Item1.StartsWith("XboxTypeS_", StringComparison.OrdinalIgnoreCase) && item.Item2.StartsWith(tuple.Item1, StringComparison.OrdinalIgnoreCase))
				{
					ObservableCollection<BindRow> observableCollection = binds;
					BindRow obj = new BindRow
					{
						Action = tuple.Item2,
						Command = item.Item2
					};
					(obj.Key, _) = item;
					(obj.KeyOriginal, _) = item;
					observableCollection.Add(obj);
				}
			}
		}
	}

	private void SensSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (SensValue != null)
		{
			SensValue.Text = e.NewValue.ToString("0.0", CultureInfo.InvariantCulture);
		}
	}

	private void AimSensSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
	{
		if (AimSensValue != null)
		{
			AimSensValue.Text = e.NewValue.ToString("0.000", CultureInfo.InvariantCulture);
		}
	}

	private void BindsList_DoubleClick(object sender, MouseButtonEventArgs e)
	{
		TrocarTecla();
	}

	private void ChangeKey_Click(object sender, RoutedEventArgs e)
	{
		TrocarTecla();
	}

	private void TrocarTecla()
	{
		BindRow row = BindsList.SelectedItem as BindRow;
		if (row == null || row.Command == null)
		{
			MessageBox.Show(this, "Pick an action from the list first.", "Change key", MessageBoxButton.OK, MessageBoxImage.Asterisk);
			return;
		}
		string nova = KeyCapture.Ask(this, row.Action);
		if (nova != null && !(nova == row.Key))
		{
			BindRow bindRow = binds.FirstOrDefault((BindRow b) => b != row && string.Equals(b.Key, nova, StringComparison.OrdinalIgnoreCase));
			if (bindRow != null)
			{
				MessageBox.Show(this, nova + " is already used by \"" + bindRow.Action + "\". Free that one first.", "Key already in use", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			row.Key = nova;
			SaveStatus.Text = "Key changes are written when you press Save";
		}
	}

	private void OpenInput_Click(object sender, RoutedEventArgs e)
	{
		Abrir(Path.Combine(gameDir, "GearGame", "Config", "GearInput.ini"));
	}

	private void LoadLauncher()
	{
		PlayerNameBox.Text = cfg.PlayerName;
		BackupCheck.IsChecked = cfg.BackupSaveOnLaunch;
		string path = Path.Combine(gameDir, "Binaries", "Win64", "play.bat");
		LaunchPathText.Text = (File.Exists(path) ? "Binaries\\Win64\\play.bat  (found)" : "Binaries\\Win64\\GearGame.exe  (play.bat not found, starting the game directly)");
		IniPathText.Text = gameDir + ((engineIni == null) ? "   (no GearEngine.ini)" : "");
	}

	private void OpenGameFolder_Click(object sender, RoutedEventArgs e)
	{
		Abrir(gameDir);
	}

	private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
	{
		Abrir(Path.Combine(gameDir, "GearGame", "Config"));
	}

	private void OpenSaveFolder_Click(object sender, RoutedEventArgs e)
	{
		Abrir(Path.Combine(gameDir, "GearGame", "SaveData"));
	}

	private void Abrir(string path)
	{
		try
		{
			if (!File.Exists(path) && !Directory.Exists(path))
			{
				MessageBox.Show(this, path, "Not found", MessageBoxButton.OK, MessageBoxImage.Exclamation);
				return;
			}
			Process.Start(new ProcessStartInfo(path)
			{
				UseShellExecute = true
			});
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, "Could not open", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void Save_Click(object sender, RoutedEventArgs e)
	{
		try
		{
			cfg.PlayerName = (string.IsNullOrWhiteSpace(PlayerNameBox.Text) ? "Player" : PlayerNameBox.Text.Trim());
			cfg.BackupSaveOnLaunch = BackupCheck.IsChecked.GetValueOrDefault();
			cfg.Save(exeDir);
			SalvarPreferenciasMultipatch();
			SaveStatus.Text = "Graphics preferences saved to graphics.json. Run Multipatch Game Settings to apply them to INIs; control changes were not written.";
			base.DialogResult = true;
			Close();
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.ToString(), "Could not save", MessageBoxButton.OK, MessageBoxImage.Hand);
		}
	}

	private void SalvarPreferenciasMultipatch()
	{
		string resolution = ResCombo.SelectedItem as string ?? "1280x720";
		string fpsText = FpsCombo.SelectedItem as string ?? "60 FPS";
		int fps = fpsText == "Unlimited" ? 0 : ParseInt(fpsText.Replace(" FPS", ""), 60);
		string aa = (MsaaCombo.SelectedItem as string) switch { "FXAA" => "fxaa", "2x" => "msaa2x", "4x" => "msaa4x", _ => "off" };
		MultipatchBridge.SaveGraphics(gameDir, resolution, FullscreenCheck.IsChecked.GetValueOrDefault(), VsyncCheck.IsChecked.GetValueOrDefault(), fps, (int)Math.Round(FovSlider.Value), aa);
	}

	private void SalvarVideo()
	{
		if (engineIni == null)
		{
			return;
		}
		Backup(engineIni);
		string text = ResCombo.SelectedItem as string;
		if (!string.IsNullOrEmpty(text))
		{
			string[] array = text.Split('x');
			if (array.Length == 2)
			{
				IniPatch.Write(engineIni, "SystemSettings", "ResX", array[0]);
				IniPatch.Write(engineIni, "SystemSettings", "ResY", array[1]);
			}
		}
		W(engineIni, "SystemSettings", "Fullscreen", FullscreenCheck.IsChecked);
		W(engineIni, "SystemSettings", "UseVsync", VsyncCheck.IsChecked);
		W(engineIni, "SystemSettings", "DynamicShadows", ShadowsCheck.IsChecked);
		W(engineIni, "SystemSettings", "AmbientOcclusion", AoCheck.IsChecked);
		W(engineIni, "SystemSettings", "Bloom", BloomCheck.IsChecked);
		W(engineIni, "SystemSettings", "bAllowLightShafts", LightShaftCheck.IsChecked);
		W(engineIni, "SystemSettings", "DepthOfField", DofCheck.IsChecked);
		W(engineIni, "SystemSettings", "MotionBlur", MotionBlurCheck.IsChecked);
		W(engineIni, "SystemSettings", "LensFlares", LensFlareCheck.IsChecked);
		W(engineIni, "Engine.Engine", "bSubtitlesEnabled", SubsCheck.IsChecked);
		string text2 = MsaaCombo.SelectedItem as string;
		if (text2 == "Off")
		{
			IniPatch.Write(engineIni, "SystemSettings", "bAllowD3D9MSAA", "False");
			IniPatch.Write(engineIni, "SystemSettings", "MaxMultiSamples", "1");
		}
		else if (!string.IsNullOrEmpty(text2))
		{
			IniPatch.Write(engineIni, "SystemSettings", "bAllowD3D9MSAA", "True");
			IniPatch.Write(engineIni, "SystemSettings", "MaxMultiSamples", text2.TrimEnd('x'));
		}
		string text3 = AnisoCombo.SelectedItem as string;
		if (text3 == "Trilinear")
		{
			IniPatch.Write(engineIni, "SystemSettings", "MaxAnisotropy", "1");
		}
		else if (!string.IsNullOrEmpty(text3))
		{
			IniPatch.Write(engineIni, "SystemSettings", "MaxAnisotropy", text3.TrimEnd('x'));
		}
		string text4 = FpsCombo.SelectedItem as string;
		if (text4 == "Unlimited")
		{
			IniPatch.Write(engineIni, "Engine.Engine", "bSmoothFrameRate", "FALSE");
		}
		else if (!string.IsNullOrEmpty(text4))
		{
			IniPatch.Write(engineIni, "Engine.Engine", "bSmoothFrameRate", "TRUE");
			IniPatch.Write(engineIni, "Engine.Engine", "MaxSmoothedFrameRate", text4.Replace(" FPS", "").Trim());
		}
	}

	private void SalvarFov()
	{
		string value = ((int)Math.Round(FovSlider.Value)).ToString(CultureInfo.InvariantCulture);
		string[] array = new string[2] { cameraIni, defCameraIni };
		foreach (string text in array)
		{
			if (text == null)
			{
				continue;
			}
			Backup(text);
			string[] fovSections = FovSections;
			foreach (string text2 in fovSections)
			{
				if (Read(text, text2, "FOVAngle") != null)
				{
					IniPatch.Write(text, text2, "FOVAngle", value);
				}
			}
		}
	}

	private void SalvarControles()
	{
		if (inputIni == null)
		{
			return;
		}
		Backup(inputIni);
		IniPatch.Write(inputIni, "Engine.PlayerInput", "MouseSensitivity", SensSlider.Value.ToString("0.0", CultureInfo.InvariantCulture));
		IniPatch.Write(inputIni, "GearGame.GearPlayerInput", "TargetingModeViewScalePct", AimSensSlider.Value.ToString("0.000", CultureInfo.InvariantCulture));
		IniPatch.Write(inputIni, "Engine.PlayerInput", "bEnableMouseSmoothing", SmoothingCheck.IsChecked.GetValueOrDefault() ? "true" : "false");
		double a = (double)(InvertCheck.IsChecked.GetValueOrDefault() ? 1 : (-1)) * lookUpMagnitude;
		IniPatch.Write(inputIni, "Engine.PlayerInput", "LookUpScale", ((int)Math.Round(a)).ToString(CultureInfo.InvariantCulture));
		foreach (BindRow bind in binds)
		{
			if (bind.Command != null && !(bind.Key == bind.KeyOriginal))
			{
				TrocarBindNoIni(inputIni, bind.KeyOriginal, bind.Command, bind.Key);
				bind.KeyOriginal = bind.Key;
			}
		}
	}

	private static void TrocarBindNoIni(string ini, string teclaAntiga, string comando, string teclaNova)
	{
		string[] array = File.ReadAllLines(ini);
		bool flag = false;
		for (int i = 0; i < array.Length; i++)
		{
			string text = array[i].Trim();
			if (text.StartsWith("["))
			{
				flag = text.Equals("[Engine.PlayerInput]", StringComparison.OrdinalIgnoreCase);
			}
			else if (flag && !text.StartsWith(";"))
			{
				Match match = RxBind.Match(array[i]);
				if (match.Success && string.Equals(match.Groups["k"].Value, teclaAntiga, StringComparison.Ordinal) && string.Equals(match.Groups["c"].Value, comando, StringComparison.Ordinal))
				{
					Group group = match.Groups["k"];
					array[i] = array[i].Substring(0, group.Index) + teclaNova + array[i].Substring(group.Index + group.Length);
					File.WriteAllLines(ini, array);
					break;
				}
			}
		}
	}

	private static string Read(string ini, string sec, string key)
	{
		return IniPatch.Read(ini, sec, key);
	}

	private static void W(string ini, string sec, string key, bool? on)
	{
		IniPatch.Write(ini, sec, key, on.GetValueOrDefault() ? "True" : "False");
	}

	private static void Backup(string ini)
	{
		string text = ini + ".bak_launcher";
		if (!File.Exists(text))
		{
			File.Copy(ini, text);
		}
	}

	private static bool IsOn(string v, bool padrao)
	{
		if (string.IsNullOrWhiteSpace(v))
		{
			return padrao;
		}
		v = v.Trim();
		if (v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1")
		{
			return true;
		}
		if (v.Equals("False", StringComparison.OrdinalIgnoreCase) || v == "0")
		{
			return false;
		}
		return padrao;
	}

	private static int ParseInt(string v, int padrao)
	{
		if (!int.TryParse((v ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
		{
			return padrao;
		}
		return result;
	}

	private static double ParseDouble(string v, double padrao)
	{
		if (!double.TryParse((v ?? "").Trim().TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			return padrao;
		}
		return result;
	}

	private static string ParseFps(string bruto)
	{
		return ((int)Math.Round(ParseDouble(bruto, 120.0))).ToString(CultureInfo.InvariantCulture);
	}

	private static double Clamp(double v, double lo, double hi)
	{
		return Math.Max(lo, Math.Min(hi, v));
	}

	private void Cancel_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != 0)
		{
			return;
		}
		try
		{
			DragMove();
		}
		catch
		{
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.29.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/GoW3Jacinto_Launcher;component/optionswindow.xaml", UriKind.Relative);
			Application.LoadComponent(this, resourceLocator);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.29.0")]
	[EditorBrowsable(EditorBrowsableState.Never)]
	void IComponentConnector.Connect(int connectionId, object target)
	{
		switch (connectionId)
		{
		case 1:
			((Grid)target).MouseLeftButtonDown += TitleBar_Drag;
			break;
		case 2:
			((Button)target).Click += Cancel_Click;
			break;
		case 3:
			Tabs = (TabControl)target;
			break;
		case 4:
			ResCombo = (ComboBox)target;
			break;
		case 5:
			FullscreenCheck = (CheckBox)target;
			break;
		case 6:
			VsyncCheck = (CheckBox)target;
			break;
		case 7:
			FpsCombo = (ComboBox)target;
			FpsCombo.SelectionChanged += FpsCombo_Changed;
			break;
		case 8:
			FpsWarning = (TextBlock)target;
			break;
		case 9:
			FovSlider = (Slider)target;
			FovSlider.ValueChanged += FovSlider_Changed;
			break;
		case 10:
			FovValue = (TextBlock)target;
			break;
		case 11:
			MsaaCombo = (ComboBox)target;
			break;
		case 12:
			AnisoCombo = (ComboBox)target;
			break;
		case 13:
			ShadowsCheck = (CheckBox)target;
			break;
		case 14:
			AoCheck = (CheckBox)target;
			break;
		case 15:
			BloomCheck = (CheckBox)target;
			break;
		case 16:
			LightShaftCheck = (CheckBox)target;
			break;
		case 17:
			DofCheck = (CheckBox)target;
			break;
		case 18:
			MotionBlurCheck = (CheckBox)target;
			break;
		case 19:
			LensFlareCheck = (CheckBox)target;
			break;
		case 20:
			SubsCheck = (CheckBox)target;
			break;
		case 21:
			SensSlider = (Slider)target;
			SensSlider.ValueChanged += SensSlider_Changed;
			break;
		case 22:
			SensValue = (TextBlock)target;
			break;
		case 23:
			AimSensSlider = (Slider)target;
			AimSensSlider.ValueChanged += AimSensSlider_Changed;
			break;
		case 24:
			AimSensValue = (TextBlock)target;
			break;
		case 25:
			SmoothingCheck = (CheckBox)target;
			break;
		case 26:
			InvertCheck = (CheckBox)target;
			break;
		case 27:
			BindsList = (ListBox)target;
			BindsList.MouseDoubleClick += BindsList_DoubleClick;
			break;
		case 28:
			((Button)target).Click += ChangeKey_Click;
			break;
		case 29:
			((Button)target).Click += OpenInput_Click;
			break;
		case 30:
			PlayerNameBox = (TextBox)target;
			break;
		case 31:
			BackupCheck = (CheckBox)target;
			break;
		case 32:
			LaunchPathText = (TextBlock)target;
			break;
		case 33:
			((Button)target).Click += OpenGameFolder_Click;
			break;
		case 34:
			((Button)target).Click += OpenConfigFolder_Click;
			break;
		case 35:
			((Button)target).Click += OpenSaveFolder_Click;
			break;
		case 36:
			IniPathText = (TextBlock)target;
			break;
		case 37:
			SaveStatus = (TextBlock)target;
			break;
		case 38:
			((Button)target).Click += Cancel_Click;
			break;
		case 39:
			((Button)target).Click += Save_Click;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}

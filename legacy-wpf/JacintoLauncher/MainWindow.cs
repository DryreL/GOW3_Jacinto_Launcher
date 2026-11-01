using System;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;

namespace JacintoLauncher;

public class MainWindow : Window, IComponentConnector
{
	private readonly string exeDir;

	private LauncherConfig cfg;

	private string gameDir;

	private Rect boundsAntesDeEncher;

	private bool cheio;

	internal TextBlock PlayerNameText;

	internal Button PlayOfflineButton;

	private Button PlaySteamButton;

	internal TextBlock StatusText;

	internal TextBlock PathText;

	internal TextBlock VersionText;

	private bool _contentLoaded;

	private string GameExe
	{
		get
		{
			if (gameDir != null)
			{
				return Path.Combine(gameDir, "Binaries", "Win64", "GearGame.exe");
			}
			return null;
		}
	}

	public MainWindow()
	{
		InitializeComponent();
		exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
		cfg = LauncherConfig.Load(exeDir);
		gameDir = ResolveGameDir(cfg.InstallDirOverride) ?? ResolveGameDir(Environment.GetEnvironmentVariable("JACINTO_GAME_DIR")) ?? ResolveGameDir(exeDir);
		InstallSteamButton();
		PlayerNameText.Text = (string.IsNullOrWhiteSpace(cfg.PlayerName) ? "Player" : cfg.PlayerName);
		Version version = Assembly.GetExecutingAssembly().GetName().Version;
		VersionText.Text = ((version == null) ? "v1.0" : ("v" + version.Major + "." + version.Minor));
		AtualizarEstado();
	}

	private static string ResolveGameDir(string start)
	{
		if (string.IsNullOrWhiteSpace(start))
		{
			return null;
		}
		DirectoryInfo directoryInfo;
		try
		{
			directoryInfo = new DirectoryInfo(start);
		}
		catch
		{
			return null;
		}
		for (int i = 0; i < 4; i++)
		{
			if (directoryInfo == null)
			{
				break;
			}
			if (File.Exists(Path.Combine(directoryInfo.FullName, "Binaries", "Win64", "GearGame.exe")))
			{
				return directoryInfo.FullName;
			}
			directoryInfo = directoryInfo.Parent;
		}
		return null;
	}

	private void AtualizarEstado()
	{
		if (gameDir == null)
		{
			PlayOfflineButton.IsEnabled = false;
			if (PlaySteamButton != null) PlaySteamButton.IsEnabled = false;
			StatusText.Text = "Game build not found";
			PathText.Text = "Set JACINTO_GAME_DIR or InstallDirOverride to the folder containing Binaries\\Win64\\GearGame.exe. Current launcher folder: " + exeDir;
		}
		else
		{
			PlayOfflineButton.IsEnabled = true;
			if (PlaySteamButton != null) PlaySteamButton.IsEnabled = false;
			StatusText.Text = "Ready to play";
			PathText.Text = gameDir + "  |  Steam/JacintoNet is not validated yet.";
		}
	}

	private void PlayOffline_Click(object sender, RoutedEventArgs e)
	{
		if (gameDir == null)
		{
			return;
		}
		PlayOfflineButton.IsEnabled = false;
		try
		{
			if (cfg.BackupSaveOnLaunch) PathText.Text = BackupSave();
			string script = Path.Combine(gameDir, "Gears3_Start_Game.bat");
			if (!File.Exists(script)) throw new FileNotFoundException("Multipatch launch script not found. Run the Multipatch installation first.", script);
			Process.Start(new ProcessStartInfo(script) { WorkingDirectory = gameDir, UseShellExecute = true });
			StatusText.Text = "Offline launch script started";
		}
		catch (Exception ex)
		{
			MessageBox.Show(this, ex.Message, "Could not start the game", MessageBoxButton.OK, MessageBoxImage.Hand);
			AtualizarEstado();
		}
		finally
		{
			PlayOfflineButton.IsEnabled = gameDir != null;
		}
	}

	private string BackupSave()
	{
		return MultipatchBridge.BackupAllSaves(gameDir);
	}

	private void InstallSteamButton()
	{
		if (PlayOfflineButton?.Parent is not Panel host) return;
		PlaySteamButton = new Button { Content = "Play on Steam", Style = PlayOfflineButton.Style, Width = PlayOfflineButton.Width, Height = PlayOfflineButton.Height, HorizontalAlignment = PlayOfflineButton.HorizontalAlignment, Margin = PlayOfflineButton.Margin, IsEnabled = false, ToolTip = "Steam/JacintoNet has not been implemented and validated." };
		host.Children.Insert(Math.Min(host.Children.IndexOf(PlayOfflineButton) + 1, host.Children.Count), PlaySteamButton);
	}

	private void GameSettings_Click(object sender, RoutedEventArgs e)
	{
		AbrirOpcoes(0);
	}

	private void Controls_Click(object sender, RoutedEventArgs e)
	{
		AbrirOpcoes(1);
	}

	private void Settings_Click(object sender, RoutedEventArgs e)
	{
		AbrirOpcoes(2);
	}

	private void AbrirOpcoes(int aba)
	{
		if (gameDir == null)
		{
			MessageBox.Show(this, "Without the game build there is nothing to read or write settings from.", "Game build not found", MessageBoxButton.OK, MessageBoxImage.Exclamation);
			return;
		}
		OptionsWindow optionsWindow = new OptionsWindow(gameDir, exeDir, cfg, aba);
		optionsWindow.Owner = this;
		optionsWindow.ShowDialog();
		cfg = LauncherConfig.Load(exeDir);
		PlayerNameText.Text = (string.IsNullOrWhiteSpace(cfg.PlayerName) ? "Player" : cfg.PlayerName);
		AtualizarEstado();
	}

	private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
	{
		if (e.ChangedButton != 0)
		{
			return;
		}
		if (e.ClickCount == 2)
		{
			Maximize_Click(sender, e);
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

	private void Minimize_Click(object sender, RoutedEventArgs e)
	{
		base.WindowState = WindowState.Minimized;
	}

	private void Maximize_Click(object sender, RoutedEventArgs e)
	{
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0030: Unknown result type (might be due to invalid IL or missing references)
		if (!cheio)
		{
			boundsAntesDeEncher = new Rect(base.Left, base.Top, base.Width, base.Height);
			Rect workArea = SystemParameters.WorkArea;
			base.Left = workArea.Left;
			base.Top = workArea.Top;
			base.Width = workArea.Width;
			base.Height = workArea.Height;
			cheio = true;
		}
		else
		{
			base.Left = boundsAntesDeEncher.Left;
			base.Top = boundsAntesDeEncher.Top;
			base.Width = boundsAntesDeEncher.Width;
			base.Height = boundsAntesDeEncher.Height;
			cheio = false;
		}
	}

	private void Close_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "8.0.29.0")]
	public void InitializeComponent()
	{
		if (!_contentLoaded)
		{
			_contentLoaded = true;
			Uri resourceLocator = new Uri("/GoW3Jacinto_Launcher;component/mainwindow.xaml", UriKind.Relative);
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
			((Button)target).Click += Minimize_Click;
			break;
		case 3:
			((Button)target).Click += Maximize_Click;
			break;
		case 4:
			((Button)target).Click += Close_Click;
			break;
		case 5:
			PlayerNameText = (TextBlock)target;
			break;
		case 6:
			PlayOfflineButton = (Button)target;
			PlayOfflineButton.Click += PlayOffline_Click;
			break;
		case 7:
			((Button)target).Click += GameSettings_Click;
			break;
		case 8:
			((Button)target).Click += Controls_Click;
			break;
		case 9:
			((Button)target).Click += Settings_Click;
			break;
		case 10:
			((Button)target).Click += Close_Click;
			break;
		case 11:
			StatusText = (TextBlock)target;
			break;
		case 12:
			PathText = (TextBlock)target;
			break;
		case 13:
			VersionText = (TextBlock)target;
			break;
		default:
			_contentLoaded = true;
			break;
		}
	}
}

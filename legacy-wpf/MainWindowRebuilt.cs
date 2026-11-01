#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Input;

namespace JacintoLauncher;

public partial class MainWindow : Window
{
    private readonly LaunchLifecycle lifecycle = new();
    private readonly System.Windows.Threading.DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool lastBusy;
    private static bool GameRunning()
    {
        var processes = Process.GetProcessesByName("GearGame");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    private void MonitorGame()
    {
        try
        {
            var busy = lifecycle.Observe(GameRunning(), DateTime.UtcNow);
            if (busy != lastBusy) { lastBusy = busy; UpdateState(); }
        }
        catch (Exception exception) { LauncherDiagnostics.Error("Game process monitoring failed", exception); PlayOfflineButton.IsEnabled = PlaySteamButton.IsEnabled = false; }
    }
    private void BeginLaunch()
    {
        if (lifecycle.Observe(GameRunning(), DateTime.UtcNow)) throw new InvalidOperationException("The game is running or starting.");
        lifecycle.Begin(DateTime.UtcNow);
        lastBusy = true;
        PlayOfflineButton.IsEnabled = PlaySteamButton.IsEnabled = false;
    }
    private readonly string exeDir;
    private readonly LauncherConfig cfg;
    private readonly string? gameDir;

    public MainWindow()
    {
        InitializeComponent();
        exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        cfg = LauncherConfig.Load(exeDir) ?? new LauncherConfig();
        L.Initialize(cfg.LauncherLanguage);
        L.Apply(this);
        gameDir = GameLayout.ResolveGameDir(exeDir);
        PlayerNameText.Text = string.IsNullOrWhiteSpace(cfg.PlayerName) ? L.T("Player") : cfg.PlayerName;
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "v1.0" : $"v{version.Major}.{version.Minor}";
        LauncherDiagnostics.Info("Launcher started. Game directory: " + (gameDir ?? "<not found>"));
        UpdateState();
        monitor.Tick += (_, _) => MonitorGame();
        monitor.Start();
        Closed += (_, _) => monitor.Stop();
        Activated += (_, _) => { MonitorGame(); UpdateState(); };
    }

    private bool Ready(string mode)
    {
        if (gameDir is null) return false;
        try { MultipatchBridge.ValidateLaunch(gameDir, mode); return true; }
        catch (Exception exception) { LauncherDiagnostics.Info(mode + " preflight unavailable: " + exception.Message); return false; }
    }

    private void UpdateState()
    {
        PlayerNameText.Text = string.IsNullOrWhiteSpace(cfg.PlayerName) ? L.T("Player") : cfg.PlayerName;
        var offlineReady = Ready(NetworkMode.Offline);
        var steamReady = Ready(NetworkMode.Steam);
        var busy = lifecycle.Observe(GameRunning(), DateTime.UtcNow);
        PlayOfflineButton.IsEnabled = offlineReady && !busy;
        PlaySteamButton.IsEnabled = steamReady && !busy;
        PlaySteamButton.Content = L.T("Play on Steam");
        PlaySteamButton.ToolTip = steamReady
            ? L.T("Enables JacintoNet (Steam co-op) and starts the game. Steam must be running.")
            : L.T("Steam/JacintoNet is not ready (is Steam running?). See Diagnostics for details.");
        var status = MultipatchBridge.BuildModeStatus(offlineReady, steamReady);
        StatusText.Text = L.T(status.Headline);
        PathText.Text = L.T(status.Detail);
    }

    private void PlayOffline_Click(object sender, RoutedEventArgs e) => Launch(NetworkMode.Offline, "Offline");
    private void PlaySteam_Click(object sender, RoutedEventArgs e) => Launch(NetworkMode.Steam, "Steam/JacintoNet");

    /// <summary>Called after the launcher language changes.</summary>
    internal void RefreshLanguage() { L.Apply(this); UpdateState(); }

    private void Launch(string mode, string label)
    {
        try
        {
            BeginLaunch();
            if (gameDir is null) throw new InvalidOperationException("Game installation was not detected.");
            MultipatchBridge.StartGame(gameDir, mode, cfg.BackupSaveOnLaunch);
            StatusText.Text = L.T("Launching game...");
            PathText.Text = L.T(label + " launch started");
        }
        catch (Exception exception)
        {
            lifecycle.Failed();
            LauncherDiagnostics.Error("Play " + label + " failed", exception);
            UpdateState();
            StatusText.Text = L.T("An error occurred. See Diagnostics.");
            PathText.Text = L.T("The game was not started.");
        }
    }

    private void GameSettings_Click(object sender, RoutedEventArgs e) => new GameSettingsWindow(gameDir) { Owner = this }.ShowDialog();
    private void Controls_Click(object sender, RoutedEventArgs e) => new ControlsWindow(gameDir) { Owner = this }.ShowDialog();
    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow(gameDir, cfg, exeDir, RefreshLanguage) { Owner = this }.ShowDialog();
    private void TitleBar_Drag(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Quit_Click(object sender, RoutedEventArgs e) => Close();
}



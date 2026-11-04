#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace JacintoLauncher;

public partial class MainWindow : Window
{
    private readonly LaunchLifecycle lifecycle = new();
    private readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string exeDir;
    private readonly LauncherConfig cfg;
    private string? gameDir;
    private readonly DispatcherTimer padTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer onlineTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(30) }; // GitHub allows 60 anonymous calls an hour.
    private TaskCompletionSource<bool>? movieAnswer;
    private readonly PadMapper padMapper = new();
    private readonly GamepadReader padReader = new();
    private LauncherUpdate.Release? release;
    private Version? promptedVersion;
    private TrayIcon? tray;
    private bool exiting, closedToTray;
    private ToggleButton? shownNav;
    private bool hiddenInTray;
    private bool lastBusy, offlineReady, steamReady, checking, installing, uninstalled, background;
    private TaskCompletionSource<bool>? confirmAnswer;
    private string? installedLauncher;
    private string? launcherStamp, gameStamp;
    private IInputElement? guideReturn;
    private bool padTracked;

    public MainWindow()
    {
        exeDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        cfg = LauncherConfig.Load(exeDir) ?? new LauncherConfig();
        LauncherTheme.Apply(cfg.Theme, cfg.ThemeMode);
        InitializeComponent();
        LauncherTheme.Changed += ApplyHomeArt;
        ApplyHomeArt();
        StartAd();
        MultipatchBridge.Config = cfg;
        SettingsEngine.Screens = () => Screens.All.Select(s => (s.Bounds.X, s.Bounds.Y, s.Bounds.Right, s.Bounds.Bottom, s.IsPrimary)).ToArray();
        L.Initialize(cfg.LauncherLanguage);
        gameDir = GameLayout.ResolveGameDir(cfg.InstallDirOverride) ?? GameLayout.ResolveGameDir(exeDir);
        Telemetry.Start(cfg, gameDir);
        Telemetry.Track("app_started", new Dictionary<string, object> { ["language"] = L.Current });
        L.Apply(this);
        VersionText.Text = "v" + LauncherUpdate.Current.ToString(3);
        NewBadge.IsVisible = ReleasedRecently();
        TelemetryNotice.IsVisible = cfg.TelemetryEnabled && !cfg.TelemetryNoticeShown;
        LauncherDiagnostics.Info("Launcher started. Game directory: " + (gameDir ?? "<not found>"));
        if (gameDir is { } leftover) _ = Task.Run(() => MultipatchInstaller.RemoveLeftoverLayout(leftover));
        ShowPage(HomeNav);
        ApplySidebar();
        _ = RefreshStateAsync();
        monitor.Tick += (_, _) => { MonitorGame(); if (!background) WatchSettingsFiles(); };
        monitor.Start();
        padTimer.Tick += (_, _) => PollGamepad();
        padTimer.Start();
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);
        WarnIfWrongBuild();
        if (!cfg.HideSetupGuide) Opened += (_, _) => ShowGuide();
        _ = UpgradeIfNeededAsync();
        _ = CheckForUpdateAsync();
        ReportGameCrash();
        _ = Task.Run(() => { try { SteamShortcut.TurnOffSteamInput(); } catch (Exception exception) { LauncherDiagnostics.Info("Steam Input was not turned off: " + exception.Message); } });
        onlineTimer.Tick += (_, _) => _ = RefreshOnlineAsync();
        onlineTimer.Start();
        _ = RefreshOnlineAsync();
        updateTimer.Tick += (_, _) => _ = CheckForUpdateAsync();
        updateTimer.Start();
        Closing += (_, args) =>
        {
            if (exiting || !cfg.CloseToTray) return;
            args.Cancel = true;
            closedToTray = true;
            HideToTray();
        };
        Program.ShowRequested = () => Dispatcher.UIThread.Post(RestoreFromTray);
        LauncherDiagnostics.ErrorShown = (operation, exception) => Dispatcher.UIThread.Post(() => ShowError(operation, exception));
        Closed += (_, _) => { LauncherDiagnostics.ErrorShown = null; Telemetry.LeaveOnline(); monitor.Stop(); padTimer.Stop(); onlineTimer.Stop(); updateTimer.Stop(); tray?.Dispose(); padReader.Dispose(); };
        Activated += (_, _) => { padTimer.Start(); MonitorGame(); _ = RefreshStateAsync(); };
        Deactivated += (_, _) => padTimer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) SetBackground(WindowState == WindowState.Minimized);
    }

    // Minimized or in the tray nobody sees the window: the big picture goes, and what is left is handed back to the system.
    private void SetBackground(bool on)
    {
        if (background == on) return;
        background = on;
        monitor.Interval = TimeSpan.FromSeconds(on ? 5 : 1);
        if (on) adTimer.Stop(); else if (AdBanner.IsVisible && adCodec?.FrameCount > 1) adTimer.Start();
        if (!on) { ApplyHomeArt(); return; }
        (HomeArt.Source as IDisposable)?.Dispose();
        HomeArt.Source = null;
        _ = Task.Run(LauncherMemory.Release);
    }

    private void MonitorGame()
    {
        try
        {
            var busy = lifecycle.Observe(GameLayout.GameRunning(), DateTime.UtcNow);
            if (busy != lastBusy)
            {
                lastBusy = busy;
                ApplyButtons();
                if (!busy) { DiscordPresence.Stop(); ReportGameCrash(); if (!closedToTray) RestoreFromTray(); }
            }
        }
        catch (Exception exception) { LauncherDiagnostics.Error("Game process monitoring failed", exception); PlayOfflineButton.IsEnabled = PlaySteamButton.IsEnabled = false; }
    }

    // At start too: the launcher may have been closed while the game was running.
    private void ReportGameCrash()
    {
        var directory = gameDir;
        if (directory is null || lifecycle.Busy) return;
        var since = cfg.GameCrashCheckedUtc;
        _ = Task.Run(() =>
        {
            try
            {
                var report = GameCrash.Find(directory, since ?? DateTime.UtcNow, out var seen);
                if (report is not null) { LauncherDiagnostics.Info("Game crash found: " + report.Title); Telemetry.CaptureGameCrash(report); }
                Dispatcher.UIThread.Post(() =>
                {
                    cfg.GameCrashCheckedUtc = seen;
                    try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("Game crash check skipped: " + exception.Message); }
        });
    }

    private static string Stamp(IEnumerable<string> files) => string.Join('|', files.Select(file => File.GetLastWriteTimeUtc(file).Ticks));

    // The settings the game reads. A folder that does not exist simply adds nothing.
    private IEnumerable<string> GameSettingFiles()
    {
        if (gameDir is null) yield break;
        if (GameLayout.ToolFilesDir(gameDir) is { } files) { yield return Path.Combine(files, "graphics.json"); yield return Path.Combine(files, "keybinds.json"); }
        foreach (var folder in new[] { Path.Combine(gameDir, "GearGame", "Config"), Path.Combine(GameRunner.DocumentsRoot(cfg), "My Games", "Gears of War 3", "GearGame", "Config") })
            if (Directory.Exists(folder)) foreach (var ini in Directory.EnumerateFiles(folder, "*.ini")) yield return ini;
    }

    // Asked every second instead of a file system watcher: it behaves the same on Windows, Linux, macOS and on Wine or network drives, needs no folder to exist, and sees editors that save by replacing the file.
    private void WatchSettingsFiles()
    {
        try
        {
            var launcher = Stamp(new[] { LauncherConfig.FilePath(exeDir) });
            if (launcherStamp is not null && launcher != launcherStamp) { LauncherFileChanged(); }
            launcherStamp = launcher; // Not reached when the file is locked, so it is read again next time.
            var game = Stamp(GameSettingFiles());
            if (gameStamp is not null && game != gameStamp) { (PageHost.Content as SettingsPage)?.GameFilesChanged(); _ = RefreshStateAsync(); }
            gameStamp = game;
        }
        catch (System.Text.Json.JsonException exception) { launcherStamp = Stamp(new[] { LauncherConfig.FilePath(exeDir) }); LauncherDiagnostics.Info("Launcher settings file not read, the previous values stay: " + exception.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("Settings files not read yet: " + exception.Message); }
    }

    private void LauncherFileChanged()
    {
        var (intro, language) = (cfg.SkipIntro, L.Current);
        if (!cfg.Reload(exeDir)) return;
        LauncherDiagnostics.Info("Launcher settings file changed outside the launcher.");
        LauncherTheme.Apply(cfg.Theme, cfg.ThemeMode);
        L.Initialize(cfg.LauncherLanguage);
        var game = GameLayout.ResolveGameDir(cfg.InstallDirOverride) ?? GameLayout.ResolveGameDir(exeDir);
        var moved = game != gameDir;
        gameDir = game;
        if (cfg.SkipIntro != intro && gameDir is not null && !GameLayout.GameRunning()) IntroSettings.Apply(new[] { gameDir }, cfg.SkipIntro, () => { });
        OnSettingsChanged();
        TelemetryNotice.IsVisible = cfg.TelemetryEnabled && !cfg.TelemetryNoticeShown;
        if (moved) { WarnIfWrongBuild(); _ = RefreshStateAsync(); _ = UpgradeIfNeededAsync(); }
        // Pages hold no state of their own, but Settings may hold edits, so it updates in place.
        if (PageHost.Content is SettingsPage settings && !moved) settings.LauncherFileChanged();
        else if (PageHost.IsVisible && (moved || language != L.Current)) ShowPage(new[] { SettingsNav, ChangelogNav }.Concat(Pages).First(p => p.IsChecked == true));
    }

    // The free Aptabase plan counts every event, so only what says how the launcher is used: start, launches, installs, updates, applied settings and each page once a session.
    private readonly HashSet<string> viewedPages = new(StringComparer.Ordinal);

    // ---- store banner: an animated WebP made from the banner video. Avalonia shows only an image's first frame, so Skia decodes
    // one frame per tick into the same bitmap: memory stays at one frame, and nothing runs while the banner is hidden or minimized.
    private const string AdUrl = "https://dryrel.com/artworks/store/";
    private SkiaSharp.SKCodecFrameInfo[] adFrameInfo = Array.Empty<SkiaSharp.SKCodecFrameInfo>();
    private readonly DispatcherTimer adTimer = new();
    private SkiaSharp.SKCodec? adCodec;
    private SkiaSharp.SKImageInfo adInfo;
    private Avalonia.Media.Imaging.WriteableBitmap? adBitmap;
    private int adFrame;

    private void StartAd()
    {
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(InfoUi.Resource("assets/ads/dryrel-banner.webp"));
            var memory = new MemoryStream(); stream.CopyTo(memory); memory.Position = 0;
            adCodec = SkiaSharp.SKCodec.Create(memory) ?? throw new InvalidDataException("The banner could not be decoded.");
            var info = new SkiaSharp.SKImageInfo(adCodec.Info.Width, adCodec.Info.Height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
            adInfo = info;
            adBitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(info.Width, info.Height), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            adFrameInfo = adCodec.FrameInfo;
            AdImage.Source = adBitmap;
            ShowAdFrame(0);
            if (adCodec.FrameCount < 2) return;
            adTimer.Tick += (_, _) => ShowAdFrame((adFrame + 1) % adCodec.FrameCount);
            adTimer.Start();
        }
        catch (Exception exception) { AdBanner.IsVisible = false; LauncherDiagnostics.Info("Store banner not shown: " + exception.Message); }
    }

    private void ShowAdFrame(int index)
    {
        if (adCodec is null || adBitmap is null) return;
        var frames = adFrameInfo;
        // A frame may draw only a part on top of the one before it, which the bitmap still holds.
        var prior = frames.Length == 0 ? -1 : frames[index].RequiredFrame;
        using (var target = adBitmap.Lock()) // Decoded straight into the shown bitmap: no copy.
            adCodec.GetPixels(adInfo, target.Address, target.RowBytes, new SkiaSharp.SKCodecOptions(index, prior == index - 1 ? prior : -1));
        adFrame = index;
        AdImage.InvalidateVisual();
        adTimer.Interval = TimeSpan.FromMilliseconds(frames.Length == 0 ? 33 : Math.Max(16, frames[index].Duration));
    }

    // UTM tags let the store's analytics count the visitors this launcher sends.
    private async void Ad_Pressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is not null) return; // The X.
        Telemetry.Track("ad_click", "banner", "artworks_store");
        await Launcher.LaunchUriAsync(new Uri(AdUrl + "?utm_source=gow3_jacinto_launcher&utm_medium=launcher&utm_campaign=home_banner&utm_content=v" + LauncherUpdate.Current.ToString(3)));
    }

    private void AdClose_Click(object? sender, RoutedEventArgs e)
    {
        // Only until the window is opened again: the choice is never saved.
        AdBanner.IsVisible = false;
        adTimer.Stop();
        Telemetry.Track("ad_closed");
    }

    private void ApplyHomeArt()
    {
        // The Multi Patch Tool art is wide with its text in the middle, so it must not be cropped.
        var multipatch = LauncherTheme.Current == "blue";
        using var art = Avalonia.Platform.AssetLoader.Open(InfoUi.Resource("assets/" + (multipatch ? "multipatch.png" : "bg.jpg")));
        (HomeArt.Source as IDisposable)?.Dispose();
        HomeArt.Source = new Avalonia.Media.Imaging.Bitmap(art);
        HomeArt.Stretch = multipatch ? Avalonia.Media.Stretch.Uniform : Avalonia.Media.Stretch.UniformToFill;
        HomeArtPanel.VerticalAlignment = multipatch ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;
    }

    private bool Ready(string mode)
    {
        if (gameDir is null) return false;
        try { MultipatchBridge.ValidateLaunch(gameDir, mode); return true; }
        catch (Exception exception) { LauncherDiagnostics.Info(mode + " preflight unavailable: " + exception.Message); return false; }
    }

    // The preflight reads GearGame.u (34 MB); doing it off the UI thread keeps window activation instant.
    private async Task RefreshStateAsync()
    {
        if (checking) return;
        checking = true;
        try
        {
            var offline = await Task.Run(() => Ready(NetworkMode.Offline));
            var steam = await Task.Run(() => Ready(NetworkMode.Steam));
            offlineReady = offline; steamReady = steam;
            ApplyButtons();
        }
        finally { checking = false; }
    }

    private void ApplyButtons()
    {
        var busy = lifecycle.Busy || lastBusy;
        PlayerNameText.Text = string.IsNullOrWhiteSpace(cfg.PlayerName) ? L.T("Player") : PlayerIdentity.GameName(cfg.PlayerName);
        PlayOfflineButton.IsEnabled = offlineReady && !busy;
        PlaySteamButton.IsEnabled = steamReady && !busy;
        ToolTip.SetTip(PlaySteamButton, cfg.SidebarCollapsed ? L.T("Play on Steam") : steamReady
            ? L.T("Enables JacintoNet (Steam co-op) and starts the game. Steam must be running.")
            : L.T("Steam/JacintoNet is not ready (is Steam running?). See Diagnostics for details."));
        var patched = gameDir is not null && GameLayout.StartScript(gameDir) is not null && GameLayout.ToolFilesDir(gameDir) is not null;
        SettingsNav.IsEnabled = patched && !installing && !busy;
        ToolTip.SetTip(SettingsNav, !patched ? L.T("Install the Multipatch on the Home screen to unlock Settings.") : busy ? L.T("Close the game to change settings.") : cfg.SidebarCollapsed ? L.T("Settings") : null);
        if (!SettingsNav.IsEnabled && SettingsNav.IsChecked == true) ShowPage(HomeNav);
        if (installing) return;
        var status = MultipatchBridge.BuildModeStatus(offlineReady, steamReady);
        StatusText.Text = L.T(gameDir is not null && !patched ? "Multipatch is not installed" : status.Headline);
        PathText.Text = gameDir is not null && !patched ? L.T("Install it once to enable the fixes and both play modes.") : offlineReady || steamReady ? "" : L.T(status.Detail);
        OfflineReadyText.Text = L.T(offlineReady ? "Offline ready" : "Offline unavailable") + (offlineReady ? "  ✓" : "  ✗");
        SteamReadyText.Text = L.T(steamReady ? "Steam ready" : "Steam unavailable") + (steamReady ? "  ✓" : "  ✗");
        OfflineReadyText.Opacity = offlineReady ? 1 : .6; SteamReadyText.Opacity = steamReady ? 1 : .6;
        OfflineReadyText.IsVisible = SteamReadyText.IsVisible = patched && !uninstalled;
        ToolTip.SetTip(SteamReadyText, steamReady ? null : L.T("Steam/JacintoNet is not ready (is Steam running?). See Diagnostics for details."));
        InstallButton.IsVisible = gameDir is not null && !patched && MultipatchInstaller.HasPackagedPatchset(exeDir);
        UninstallButton.IsVisible = gameDir is not null && MultipatchInstaller.Managed(gameDir) && !uninstalled;
        ChooseFolderButton.IsVisible = !patched;
        InputChoicePanel.IsVisible = InstallButton.IsVisible;
        SetupPanel.IsVisible = gameDir is null || InstallButton.IsVisible;
        ReadyRow.IsVisible = OfflineReadyText.IsVisible || UninstallButton.IsVisible;
        if (uninstalled) { StatusText.Text = L.T("Original files restored. Your saves were kept. Close the launcher to finish."); PathText.Text = ""; }
        PathText.IsVisible = !string.IsNullOrEmpty(PathText.Text);
        ApplyWarnings();
    }

    private void ApplyWarnings()
    {
        var missing = gameDir is null ? null : GameRunner.MissingDirectX(gameDir, cfg);
        var noLayer = GameRunner.NeedsCompatibilityLayer && GameRunner.FindLayer(cfg) is null;
        WarningText.Text = noLayer ? L.T("No Windows compatibility layer found. Install Proton, umu-launcher or Wine, or set it in Settings.")
            : missing is not null ? L.F("Missing {0}. Install the DirectX June 2010 runtime, or the game stops at startup.", missing) : "";
        WarningText.IsVisible = WarningText.Text.Length != 0;
        DirectXButton.IsVisible = !noLayer && missing is not null;
    }

    private void PlayOffline_Click(object? sender, RoutedEventArgs e) => _ = LaunchAsync(NetworkMode.Offline, "Offline");
    private void PlaySteam_Click(object? sender, RoutedEventArgs e) => _ = LaunchAsync(NetworkMode.Steam, "Steam/JacintoNet");

    private async Task LaunchAsync(string mode, string label)
    {
        ShowPage(HomeNav);
        try
        {
            if (gameDir is null) throw new InvalidOperationException("Game installation was not detected.");
            if (lifecycle.Observe(GameLayout.GameRunning(), DateTime.UtcNow)) throw new UserActionException("The game is running or starting.");
            if (!await ConfirmMoviesAsync()) return;
            lifecycle.Begin(DateTime.UtcNow);
            lastBusy = true;
            ApplyButtons();
            StatusText.Text = L.T("Launching game...");
            PathText.Text = L.T(label + " launch started");
            var directory = gameDir; var backup = cfg.BackupSaveOnLaunch;
            await Task.Run(() => MultipatchBridge.StartGame(directory, mode, backup));
            Telemetry.Track("game_launch", new Dictionary<string, object> { ["mode"] = mode });
            HideToTray();
        }
        catch (Exception exception)
        {
            lifecycle.Failed();
            lastBusy = false;
            LauncherDiagnostics.Error("Play " + label + " failed", exception);
            ApplyButtons();
            StatusText.Text = L.T("An error occurred. See Diagnostics.");
            PathText.Text = L.T("The game was not started.");
        }
    }

    // While the game runs the launcher waits in the tray, and comes back when the game closes.
    private void HideToTray()
    {
        try
        {
            if (tray is null)
            {
                using var icon = Avalonia.Platform.AssetLoader.Open(InfoUi.Resource("assets/icon256.png"));
                var open = new NativeMenuItem(L.T("Open launcher"));
                open.Click += (_, _) => RestoreFromTray();
                var exit = new NativeMenuItem(L.T("Exit"));
                exit.Click += (_, _) => { exiting = true; Close(); };
                var offline = new NativeMenuItem(L.T("Play Offline"));
                offline.Click += (_, _) => { if (PlayOfflineButton.IsEnabled) _ = LaunchAsync(NetworkMode.Offline, "Offline"); else RestoreFromTray(); };
                var steam = new NativeMenuItem(L.T("Play on Steam"));
                steam.Click += (_, _) => { if (PlaySteamButton.IsEnabled) _ = LaunchAsync(NetworkMode.Steam, "Steam/JacintoNet"); else RestoreFromTray(); };
                tray = new TrayIcon { Icon = new WindowIcon(icon), ToolTipText = Title, Menu = new NativeMenu { open, new NativeMenuItemSeparator(), offline, steam, new NativeMenuItemSeparator(), exit } };
                tray.Clicked += (_, _) => RestoreFromTray();
                TrayIcon.SetIcons(Avalonia.Application.Current!, new TrayIcons { tray });
            }
            tray.IsVisible = true;
            hiddenInTray = true;
            Hide();
            SetBackground(true);
        }
        catch (Exception exception)
        {
            // A desktop without a tray: the window stays reachable on the task bar.
            LauncherDiagnostics.Info("System tray is not available: " + exception.Message);
            WindowState = WindowState.Minimized;
        }
    }

    private void RestoreFromTray()
    {
        closedToTray = false;
        if (!AdBanner.IsVisible && adCodec is not null) { AdBanner.IsVisible = true; if (adCodec?.FrameCount > 1) adTimer.Start(); }
        if (tray is not null) tray.IsVisible = false;
        if (hiddenInTray) { hiddenInTray = false; Show(); }
        SetBackground(false);
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private async Task<bool> ConfirmMoviesAsync()
    {
        var movies = cfg.HideHdMoviesWarning ? Array.Empty<string>() : PreLaunchCheck.OversizeMovies(gameDir!);
        if (movies.Length == 0) return true;
        MovieWarningText.Text = L.F("{0} is larger than 4 GB, so you have the HD Movies Pack. Known issue: the game crashes while playing the final movie. Replace Tower_Outro.bik with the original file before you reach the end of the campaign.", string.Join(", ", movies));
        MovieHideCheck.IsChecked = false;
        MovieOverlay.IsVisible = true;
        MoviePlayButton.Focus();
        movieAnswer = new TaskCompletionSource<bool>();
        var play = await movieAnswer.Task;
        MovieOverlay.IsVisible = false;
        if (play && MovieHideCheck.IsChecked == true)
        {
            cfg.HideHdMoviesWarning = true;
            try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
        }
        return play;
    }

    private void MoviePlay_Click(object? sender, RoutedEventArgs e) => movieAnswer?.TrySetResult(true);
    private void MovieCancel_Click(object? sender, RoutedEventArgs e) => movieAnswer?.TrySetResult(false);
    private async void MoviePage_Click(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(PreLaunchCheck.HdMoviesUrl));

    private async Task RefreshOnlineAsync()
    {
        var online = await Telemetry.OnlineAsync();
        OnlineText.IsVisible = online is not null;
        if (online is { } count) OnlineText.Text = L.F("{0} online", count.Online) + (count.Total is { } total ? "  |  " + L.F("{0} total", total) : "");
    }

    private void Nav_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton button) return;
        if (button == shownNav) { button.IsChecked = true; return; } // Already open: a second click keeps the page and its unsaved edits.
        ShowPage(button);
    }

    // Pages are rebuilt on each visit, so they always show the files' current values.
    private void ShowPage(ToggleButton selected)
    {
        shownNav = selected;
        if (viewedPages.Add(selected.Name!)) Telemetry.Track("page_view", "page", selected.Name!.Replace("Nav", ""));
        foreach (var nav in new[] { HomeNav, FeaturesNav, RecommendedNav, CheatsNav, AboutNav, FaqNav, CreditsNav, SettingsNav, ChangelogNav }) nav.IsChecked = nav == selected;
        PageHost.Content = selected == SettingsNav ? new SettingsPage(gameDir, () => new SettingsView(gameDir, cfg, exeDir, OnSettingsChanged, PickGameFolderAsync, ShowGuide))
            : selected == FeaturesNav ? new FeaturesPage()
            : selected == RecommendedNav ? new RecommendedPage()
            : selected == CheatsNav ? new CheatsPage(gameDir)
            : selected == AboutNav ? new AboutPage(gameDir, cfg, CheckForUpdateNowAsync)
            : selected == CreditsNav ? new TextPage("CREDITS", "assets/credits.txt")
            : selected == FaqNav ? new TextPage("FAQ", "assets/faq.txt")
            : selected == ChangelogNav ? new TextPage("CHANGELOG", "changelog.txt")
            : null;
        PageHost.IsVisible = PageHost.Content is not null;
        HomePage.IsVisible = PageHost.Content is null;
        if (HomePage.IsVisible) { Motion.Enter(HomePage); Motion.Settle(HomeArt); }
        else if (PageHost.Content is not SettingsPage) Motion.Open(PageHost, selected.Name!); // Settings deals in its own tabs.
    }

    private void OnSettingsChanged()
    {
        L.Apply(this);
        Telemetry.Start(cfg, gameDir);
        TelemetryNotice.IsVisible = false;
        ApplySidebar();
    }

    private void ShowGuide()
    {
        guideReturn = FocusManager?.GetFocusedElement();
        GuideHideCheck.IsChecked = true;
        GuideOverlay.IsVisible = true;
        GuideOk.Focus(NavigationMethod.Directional);
    }

    private void GuideOk_Click(object? sender, RoutedEventArgs e)
    {
        GuideOverlay.IsVisible = false;
        (guideReturn ?? HomeNav).Focus();
        cfg.HideSetupGuide = GuideHideCheck.IsChecked == true;
        try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
    }

    private void Browse_Click(object? sender, RoutedEventArgs e) => _ = PickGameFolderAsync();

    private async Task<string?> PickGameFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.T("Choose game folder"), AllowMultiple = false });
        var picked = folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        if (picked is null) return null;
        var found = GameLayout.ResolveGameDir(picked);
        if (found is null) { StatusText.Text = L.T("GearGame.exe was not found in that folder."); return null; }
        gameDir = found;
        cfg.InstallDirOverride = found;
        try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
        WarnIfWrongBuild();
        await RefreshStateAsync();
        _ = UpgradeIfNeededAsync();
        return found;
    }

    private void WarnIfWrongBuild()
    {
        if (gameDir is null) return;
        try
        {
            if (MultipatchInstaller.MissingGameFile(gameDir) is { } missing) ShowIncompleteGame(missing);
            else if (MultipatchInstaller.NamedOneOneTwo(gameDir)) ShowWrongBuild();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("Game version check skipped: " + exception.Message); }
    }

    private void ShowWrongBuild() => ShowGameProblem(L.T("WRONG GAME VERSION"), L.T("This looks like Project Jacinto v1.1.2. The Multipatch must be installed on v1.1.1: v1.1.1 is the campaign build, while v1.1.2 is a different build with Horde and multiplayer. Choose a v1.1.1 folder."));
    private void ShowIncompleteGame(string missing) => ShowGameProblem(L.T("GAME FILES ARE MISSING"), L.F("{0} is missing, so this game folder is incomplete. Extract the whole game again, allow it in your antivirus, then choose its folder.", missing));
    private void ShowGameProblem(string title, string text) { WrongBuildTitle.Text = title; WrongBuildText.Text = text; WrongBuildOverlay.IsVisible = true; WrongBuildOk.Focus(); }
    private void WrongBuild_Click(object? sender, RoutedEventArgs e) => WrongBuildOverlay.IsVisible = false;

    // A launcher update can bring new fixes; the game takes them at the next start, with its settings kept.
    private async Task UpgradeIfNeededAsync()
    {
        var directory = gameDir;
        if (directory is null) return;
        try
        {
            if (!await Task.Run(() => MultipatchInstaller.UpgradeAvailable(directory, exeDir))) { NoticeJacintoNet(directory); return; }
            if (GameLayout.GameRunning()) return; // Asked again at the next start.
            KeepOldPlayerName(directory);
            installing = true;
            ApplyButtons();
            InstallLog.Text = "";
            InstallLogScroll.IsVisible = true;
            StatusText.Text = L.T("Updating the Multipatch...");
            PathText.Text = L.T("Your settings and saves are kept.");
            void Log(string line) => Dispatcher.UIThread.Post(() => { InstallLog.Text += (InstallLog.Text?.Length > 0 ? "\n" : "") + line; InstallLogScroll.ScrollToEnd(); });
            await Task.Run(() => MultipatchInstaller.Upgrade(directory, exeDir, Log));
            installing = false;
            InstallLogScroll.IsVisible = false;
            Telemetry.Track("multipatch_update", new Dictionary<string, object> { ["result"] = "ok" });
            await RefreshStateAsync();
            NoticeJacintoNet(directory);
            await CheckForUpdateAsync(); // A check that ran during this update did not ask.
        }
        catch (Exception exception)
        {
            installing = false;
            LauncherDiagnostics.Error("Multipatch update failed", exception);
            Telemetry.Track("multipatch_update", new Dictionary<string, object> { ["result"] = "failed" });
            ApplyButtons();
            StatusText.Text = L.T("The Multipatch could not be updated. See Diagnostics.");
            PathText.Text = exception.Message;
        }
    }

    // Once per JacintoNet build: co-op partners need the same one. The first build a player has is not news.
    private void NoticeJacintoNet(string game)
    {
        try
        {
            if (NetworkMode.PayloadHash(game) is not { } current || current == cfg.JacintoNetSeen) return;
            var updated = NetworkMode.Updated(cfg.JacintoNetSeen, current);
            cfg.JacintoNetSeen = current;
            cfg.Save(exeDir);
            if (updated) ShowGameProblem(L.T("JACINTONET WAS UPDATED"), L.T("The part of the Multipatch that makes Steam co-op work has a new version. If you play co-op, your friend must update too, or you may not be able to play together."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Info("JacintoNet version check skipped: " + exception.Message); }
    }

    // An old package kept the name in its launcher's settings, which this launcher's replace.
    private void KeepOldPlayerName(string game)
    {
        if (cfg.PlayerName != new LauncherConfig().PlayerName) return;
        var old = LauncherConfig.Load(Path.Combine(game, "JacintoLauncher", "Launcher"))?.PlayerName;
        if (string.IsNullOrWhiteSpace(old) || old == cfg.PlayerName) return;
        cfg.PlayerName = old;
        try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
        ApplyButtons();
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (gameDir is null) return;
        try
        {
            if (MultipatchInstaller.MissingGameFile(gameDir) is { } missing) { ShowIncompleteGame(missing); return; }
            if (MultipatchInstaller.NamedOneOneTwo(gameDir)) { ShowWrongBuild(); return; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LauncherDiagnostics.Info("Game version check failed: " + exception.Message);
            StatusText.Text = L.T("Installation did not complete.");
            PathText.Text = exception.Message;
            PathText.IsVisible = true;
            return;
        }
        var directory = gameDir;
        var gamepad = InputChoiceBox.SelectedIndex == 1;
        var subtitle = new[] { "big", "normal", "default" }[Math.Max(0, SubtitleChoiceBox.SelectedIndex)];
        var hud = HudChoiceCheck.IsChecked == true;
        installing = true;
        ApplyButtons();
        InstallButton.IsEnabled = false;
        InstallLog.Text = "";
        InstallLogScroll.IsVisible = true;
        StatusText.Text = L.T("Installing Multipatch...");
        PathText.Text = L.T("This takes a few minutes. Keep the launcher open.");
        void Log(string line) => Dispatcher.UIThread.Post(() => { InstallLog.Text += (InstallLog.Text?.Length > 0 ? "\n" : "") + line; InstallLogScroll.ScrollToEnd(); });
        try
        {
            await Task.Run(() =>
            {
                var patchset = MultipatchInstaller.FindPackagedPatchset(exeDir) ?? throw new FileNotFoundException("The launcher package holds no patch data.");
                try
                {
                    MultipatchInstaller.Install(directory, patchset, exeDir, Log);
                    if (gamepad && MultipatchInstaller.CanSwitchInput(directory)) MultipatchInstaller.SetInput(directory, true);
                    if (!hud && MultipatchInstaller.CanSwitchHud(directory)) MultipatchInstaller.SetHudScaling(directory, false);
                    if (subtitle != "big" && MultipatchInstaller.Managed(directory)) MultipatchBridge.ApplyGraphicsThroughMultipatch(directory, new System.Text.Json.Nodes.JsonObject { ["SubtitleSize"] = subtitle });
                }
                finally { MultipatchInstaller.ReleasePatchset(patchset, exeDir); }
            });
            installing = false;
            Telemetry.Track("multipatch_install", new Dictionary<string, object> { ["result"] = "ok" });
            installedLauncher = Path.Combine(directory, "JacintoLauncher", "Launcher", Path.GetFileName(Environment.ProcessPath!));
            // The list stays on screen until the player moves on; it scrolls by too fast to read otherwise.
            StatusText.Text = L.T("Installation complete.");
            PathText.Text = L.T("Continue opens the launcher from the game folder. You can then delete this download.");
            InstallButton.IsVisible = ChooseFolderButton.IsVisible = InputChoicePanel.IsVisible = false;
            ContinueButton.IsVisible = true;
            await ShowAuditAsync();
        }
        catch (Exception exception)
        {
            installing = false;
            LauncherDiagnostics.Error("Multipatch installation failed", exception);
            Telemetry.Track("multipatch_install", new Dictionary<string, object> { ["result"] = "failed" });
            ApplyButtons();
            StatusText.Text = L.T("Installation did not complete.");
            PathText.Text = exception.Message;
        }
        finally { InstallButton.IsEnabled = true; }
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        if (installedLauncher is null) return;
        try { Process.Start(new ProcessStartInfo(installedLauncher) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(installedLauncher)! })?.Dispose(); }
        catch (System.ComponentModel.Win32Exception exception)
        {
            // Usually an antivirus that took the copy. The game is installed; this launcher stays open and works.
            LauncherDiagnostics.Error("Installed launcher did not start", exception);
            PathText.Text = L.T("The launcher in the game folder could not be started. Check your antivirus, then open it from JacintoLauncher\\Launcher.");
            return;
        }
        exiting = true;
        Close();
    }

    private const string PassIcon = "M9 16.17 4.83 12l-1.42 1.41L9 19 21 7l-1.41-1.41z";
    private const string FailIcon = "M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z";
    private IInputElement? auditReturn;

    /// <summary>Runs the installation audit and shows each check, with the reason for any that failed.</summary>
    internal async Task ShowAuditAsync()
    {
        if (gameDir is not { } directory) return;
        auditReturn = FocusManager?.GetFocusedElement();
        AuditList.Children.Clear();
        AuditSummary.Text = L.T("Checking the installation...");
        AuditOverlay.IsVisible = true;
        AuditClose.Focus(NavigationMethod.Directional);
        var findings = await Task.Run(() => InstallAudit.Run(directory));
        var failed = findings.Count(f => f.Problem is not null);
        AuditSummary.Text = failed == 0 ? L.T("Everything was applied as chosen.") : L.F("{0} problem(s) found. Each one says what is wrong and where.", failed);
        foreach (var area in findings.GroupBy(f => f.Area))
        {
            AuditList.Children.Add(new TextBlock { Text = area.Key.ToUpperInvariant(), FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), Foreground = (Avalonia.Media.IBrush)this.FindResource("MutedBrush")! });
            foreach (var finding in area)
            {
                var ok = finding.Problem is null;
                var text = new StackPanel { Children = { new TextBlock { Text = finding.Item, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontWeight = Avalonia.Media.FontWeight.SemiBold } } };
                if (!ok) text.Children.Add(new TextBlock { Text = finding.Problem, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Classes = { "error" } });
                var icon = new PathIcon { Width = 14, Height = 14, Margin = new Thickness(0, 3, 10, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Data = Avalonia.Media.StreamGeometry.Parse(ok ? PassIcon : FailIcon) };
                icon.Classes.Set("error", !ok);
                DockPanel.SetDock(icon, Dock.Left);
                AuditList.Children.Add(new DockPanel { Children = { icon, text } });
            }
        }
        LauncherDiagnostics.Info($"Installation audit: {findings.Count} check(s), {failed} problem(s). " + string.Join(" | ", findings.Where(f => f.Problem is not null).Select(f => f.Item + ": " + f.Problem)));
    }

    private void AuditClose_Click(object? sender, RoutedEventArgs e) { AuditOverlay.IsVisible = false; auditReturn?.Focus(); }

    /// <summary>Every launcher error opens this, with the details to copy and the log folder. Refusals the player can fix already say so where they happen.</summary>
    private void ShowError(string operation, Exception exception)
    {
        if (exception is UserActionException) return;
        var details = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {operation}{Environment.NewLine}{exception}";
        if (ErrorOverlay.IsVisible) { ErrorDetails.Text = details + Environment.NewLine + Environment.NewLine + ErrorDetails.Text; return; } // One action can log twice; keep both.
        errorReturn = FocusManager?.GetFocusedElement();
        ErrorSummary.Text = L.T(operation) + ": " + exception.Message;
        ErrorDetails.Text = $"v{LauncherUpdate.Current.ToString(3)}  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}{Environment.NewLine}{details}";
        ErrorOverlay.IsVisible = true;
        ErrorClose.Focus(NavigationMethod.Directional);
    }

    private IInputElement? errorReturn;
    private void ErrorClose_Click(object? sender, RoutedEventArgs e) { ErrorOverlay.IsVisible = false; errorReturn?.Focus(); }
    private void ErrorLogs_Click(object? sender, RoutedEventArgs e) => LauncherDiagnostics.OpenFolder();

    private async void ErrorCopy_Click(object? sender, RoutedEventArgs e)
    {
        if (Clipboard is not { } clipboard) return;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, ErrorSummary.Text + Environment.NewLine + Environment.NewLine + ErrorDetails.Text);
        ErrorCopy.Content = L.T("Copied");
    }

    /// <summary>Asks in the window, like every other overlay. False when Cancel or Esc is pressed.</summary>
    internal async Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        ConfirmTitle.Text = title; ConfirmText.Text = text; ConfirmYes.Content = yes;
        var returnTo = FocusManager?.GetFocusedElement();
        ConfirmOverlay.IsVisible = true;
        ConfirmNo.Focus(NavigationMethod.Directional);
        confirmAnswer = new TaskCompletionSource<bool>();
        var answer = await confirmAnswer.Task;
        ConfirmOverlay.IsVisible = false;
        returnTo?.Focus();
        return answer;
    }

    private void ConfirmYes_Click(object? sender, RoutedEventArgs e) => confirmAnswer?.TrySetResult(true);
    private void ConfirmNo_Click(object? sender, RoutedEventArgs e) => confirmAnswer?.TrySetResult(false);

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        if (gameDir is null) return;
        if (!await ConfirmAsync(L.T("UNINSTALL MULTIPATCH"), L.T("This restores the original game files and removes the launcher from the game folder. Your saves are kept."), L.T("Uninstall Multipatch"))) return;
        UninstallButton.IsEnabled = false;
        var directory = gameDir;
        InstallLog.Text = "";
        InstallLogScroll.IsVisible = true;
        void Log(string line) => Dispatcher.UIThread.Post(() => { InstallLog.Text += (InstallLog.Text?.Length > 0 ? "\n" : "") + line; InstallLogScroll.ScrollToEnd(); });
        try
        {
            await Task.Run(() => MultipatchInstaller.Uninstall(directory, Log));
            uninstalled = true;
            Telemetry.Track("multipatch_uninstall", "result", "ok");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Uninstall failed", exception);
            Telemetry.Track("multipatch_uninstall", "result", "failed");
            StatusText.Text = L.T("An error occurred. See Diagnostics.");
            PathText.Text = exception.Message;
        }
        UninstallButton.IsEnabled = true;
        ApplyButtons();
    }

    private void Version_Click(object? sender, RoutedEventArgs e) => ShowPage(ChangelogNav);

    private void Logo_Tapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<Button>(true) is null) ShowPage(AboutNav);
    }

    private async void DirectX_Click(object? sender, RoutedEventArgs e)
    {
        DirectXButton.IsEnabled = false;
        StatusText.Text = L.T("Installing DirectX components...");
        try
        {
            var installed = await Task.Run(() => GameRunner.InstallDirectX(cfg));
            StatusText.Text = L.T(installed ? "DirectX components were installed." : "winetricks was not found. Install winetricks, then try again.");
            if (OperatingSystem.IsWindows()) StatusText.Text = "";
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("DirectX installation failed", exception);
            StatusText.Text = L.T("An error occurred. See Diagnostics.");
        }
        finally { DirectXButton.IsEnabled = true; if (!OperatingSystem.IsWindows()) ApplyWarnings(); }
    }

    // The latest release's notes double as the news panel; offline, the card simply stays hidden.
    private async Task CheckForUpdateAsync()
    {
        release = await LauncherUpdate.LatestAsync();
        if (release is null) return;
        ShowRelease(release);
        // Asked once per version. During an install it waits for the next check.
        if (!LauncherUpdate.IsNewer(release) || release.Version == promptedVersion || installing) return;
        promptedVersion = release.Version;
        ShowUpdatePrompt();
    }

    private async Task<string> CheckForUpdateNowAsync()
    {
        release = await LauncherUpdate.LatestAsync();
        if (release is null) return L.T("No published version was found, or GitHub could not be reached.");
        ShowRelease(release);
        return LauncherUpdate.IsNewer(release) ? L.F("Version v{0} is available. Update it on Home.", release.Version.ToString(3)) : L.F("You have the latest version (v{0}).", LauncherUpdate.Current.ToString(3));
    }

    /// <summary>True on the day this version came out and the day after, by its date in the bundled changelog.</summary>
    private static bool ReleasedRecently()
    {
        try
        {
            using var reader = new StreamReader(Avalonia.Platform.AssetLoader.Open(InfoUi.Resource("changelog.txt")));
            var match = System.Text.RegularExpressions.Regex.Match(reader.ReadToEnd(), @"(?m)^v" + System.Text.RegularExpressions.Regex.Escape(LauncherUpdate.Current.ToString(3)) + @" \((\d{1,2} \w{3} \d{4})");
            return match.Success && DateTime.TryParseExact(match.Groups[1].Value, "d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var released)
                && (DateTime.Now.Date - released.Date).TotalDays is >= 0 and <= 1;
        }
        catch (Exception exception) when (exception is IOException or FileNotFoundException) { return false; }
    }

    private void ShowRelease(LauncherUpdate.Release release)
    {
        var newer = LauncherUpdate.IsNewer(release);
        NewsTitle.Text = (newer ? L.T("UPDATE AVAILABLE") : L.T("WHAT'S NEW")) + "  ·  v" + release.Version;
        NewsText.Text = release.Notes.Trim();
        UpdateButton.IsVisible = newer;
        VersionText.Text = "v" + LauncherUpdate.Current.ToString(3) + (newer ? " *" : "");
        NewsCard.IsVisible = newer || NewsText.Text.Length != 0;
    }

    private void Update_Click(object? sender, RoutedEventArgs e) => ShowUpdatePrompt();

    private void ShowUpdatePrompt()
    {
        if (release is null) return;
        UpdatePromptText.Text = L.F("Version v{0} is available. Do you want to update now?", release.Version.ToString(3));
        UpdateGameRunningText.IsVisible = false;
        UpdateOverlay.IsVisible = true;
        UpdateYesButton.Focus();
    }

    private void UpdateNo_Click(object? sender, RoutedEventArgs e) => UpdateOverlay.IsVisible = false;

    private void UpdateYes_Click(object? sender, RoutedEventArgs e)
    {
        if (GameLayout.GameRunning()) { UpdateGameRunningText.IsVisible = true; return; }
        UpdateOverlay.IsVisible = false;
        ShowPage(HomeNav);
        _ = StartUpdateAsync();
    }

    private async Task StartUpdateAsync()
    {
        if (release is null) return;
        UpdateButton.IsEnabled = false;
        try
        {
            await LauncherUpdate.DownloadAndStartAsync(release, exeDir, percent => Dispatcher.UIThread.Post(() => NewsTitle.Text = L.F("Downloading update... {0}%", percent)));
            Telemetry.Track("launcher_update", new Dictionary<string, object> { ["version"] = release.Version.ToString() });
            exiting = true;
            Close();
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Launcher update failed", exception);
            NewsTitle.Text = L.T("The update could not be installed. See Diagnostics.");
            UpdateButton.IsEnabled = true;
        }
    }

    // Pad navigation only runs while the launcher has focus, so input meant for the game is ignored.
    private void PollGamepad()
    {
        if (!IsActive) return;
        foreach (var action in padMapper.Step(padReader.Read(), (int)padTimer.Interval.TotalMilliseconds)) HandlePad(action);
    }

    // The pad presses the same keys as the keyboard, so both always behave alike.
    private void HandlePad(PadAction action)
    {
        if (!padTracked) { padTracked = true; Telemetry.Track("gamepad_used"); }
        var key = action switch
        {
            PadAction.Next => Key.Down, PadAction.Previous => Key.Up, PadAction.Left => Key.Left, PadAction.Right => Key.Right,
            PadAction.Accept => Key.Enter, PadAction.Back => Key.Escape, PadAction.PageNext => Key.E, _ => Key.Q
        };
        var focused = FocusManager?.GetFocusedElement() as InputElement ?? HomeNav;
        focused.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, Source = focused });
    }

    private Control? OpenOverlay => new Control[] { ErrorOverlay, ConfirmOverlay, AuditOverlay, NameOverlay, WrongBuildOverlay, UpdateOverlay, MovieOverlay, GuideOverlay }.FirstOrDefault(o => o.IsVisible);

    // Everything the focus can land on, in reading order. A list counts as one stop: it moves inside itself.
    private static List<Control> Stops(Control root)
    {
        var stops = new List<Control>();
        void Walk(Avalonia.Visual parent)
        {
            foreach (var child in parent.GetVisualChildren())
            {
                if (child is not Control control || !control.IsEffectivelyVisible || !control.IsEffectivelyEnabled || child is ScrollBar) continue;
                if (control is ListBox) { stops.Add(control); continue; }
                if (control is ComboBox or TextBox or Slider or Button) { if (control.Focusable) stops.Add(control); continue; }
                Walk(child);
            }
        }
        Walk(root);
        return stops;
    }

    private static int StopOf(List<Control> stops, Avalonia.Visual? focused)
    {
        for (var at = focused; at is not null; at = at.GetVisualParent())
            if (at is Control control && stops.IndexOf(control) is >= 0 and var index) return index;
        return -1;
    }

    private static void FocusStop(Control stop)
    {
        if (stop is ListBox { ItemCount: > 0 } list)
        {
            if (list.SelectedIndex < 0) list.SelectedIndex = 0;
            list.ScrollIntoView(list.SelectedIndex);
            stop = list.ContainerFromIndex(list.SelectedIndex) ?? stop;
        }
        stop.Focus(NavigationMethod.Directional);
        stop.BringIntoView();
    }

    // Text pages have nothing to focus: the arrows scroll them, as they do past the last stop of any page.
    private static bool ScrollPage(Control region, int step)
    {
        if (region.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.IsEffectivelyVisible && v.Extent.Height > v.Viewport.Height) is not { } viewer) return false;
        viewer.Offset = viewer.Offset.WithY(viewer.Offset.Y + step * 80);
        return true;
    }

    private bool MoveVertical(Avalonia.Visual? focused, int step)
    {
        var region = OpenOverlay ?? (focused is not null && Sidebar.IsVisualAncestorOf(focused) ? Sidebar : ContentArea);
        var stops = Stops(region);
        var index = StopOf(stops, focused);
        // Inside a list the list moves, until its end is reached.
        if (focused is ListBoxItem item && item.FindAncestorOfType<ListBox>() is { } list)
        {
            var at = list.IndexFromContainer(item);
            if (at + step >= 0 && at + step < list.ItemCount) return false;
        }
        var next = index < 0 ? (stops.Count == 0 ? -1 : 0) : index + step;
        // Up and down go to the next row: the others on this one are reached with left and right.
        while (index >= 0 && next >= 0 && next < stops.Count && SameRow(stops[index], stops[next])) next += step;
        if (next >= 0 && next < stops.Count)
        {
            while (next > 0 && SameRow(stops[next], stops[next - 1])) next--;
            FocusStop(PageHost.Content is SettingsPage settings && settings.IsTab(stops[next]) ? settings.CurrentTab : stops[next]);
            return true;
        }
        return ScrollPage(region, step);
    }

    private void MoveHorizontal(Avalonia.Visual? focused, int step, KeyEventArgs e)
    {
        if (focused is TextBox or Slider) return; // They use these keys themselves.
        e.Handled = true;
        if (OpenOverlay is { } overlay)
        {
            var inside = Stops(overlay);
            var at = StopOf(inside, focused) + step;
            if (at >= 0 && at < inside.Count) FocusStop(inside[at]);
            return;
        }
        if (focused is not null && Sidebar.IsVisualAncestorOf(focused))
        {
            if (step < 0) return;
            var first = Stops(ContentArea).FirstOrDefault();
            if (first is not null) FocusStop(first);
            else if (ContentArea.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(v => v.IsEffectivelyVisible) is { } viewer) { viewer.Focusable = true; viewer.Focus(NavigationMethod.Directional); }
            return;
        }
        if (PageHost.Content is SettingsPage settings && settings.IsTab(focused) && settings.TurnTab(step)) return;
        var stops = Stops(ContentArea);
        var index = StopOf(stops, focused);
        // A neighbour on the same row, such as the next button; otherwise left goes back to the menu.
        if (index >= 0 && index + step >= 0 && index + step < stops.Count && SameRow(stops[index], stops[index + step])) { FocusStop(stops[index + step]); return; }
        if (step < 0) (Pages.FirstOrDefault(p => p.IsChecked == true) ?? HomeNav).Focus(NavigationMethod.Directional);
    }

    private bool SameRow(Control one, Control two) =>
        one.TranslatePoint(default, this) is { } a && two.TranslatePoint(default, this) is { } b && a.Y < b.Y + two.Bounds.Height && b.Y < a.Y + one.Bounds.Height;

    private ToggleButton[] Pages => new[] { HomeNav, FeaturesNav, RecommendedNav, CheatsNav, AboutNav, FaqNav, CreditsNav, SettingsNav };

    private void TurnPage(int step)
    {
        var pages = Pages.Where(p => p.IsEnabled).ToArray();
        var current = Array.FindIndex(pages, p => p.IsChecked == true);
        var page = pages[(current + step + pages.Length) % pages.Length];
        ShowPage(page);
        page.Focus(NavigationMethod.Tab);
    }

    // The same moves as the pad. Keys that a control needs for itself (typing, sliders, an open dropdown, a key capture) stay with it.
    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager?.GetFocusedElement() as Avalonia.Visual;
        // An editable dropdown keeps the focus in its text box.
        var dropdown = focused as ComboBox ?? (focused as TextBox)?.FindAncestorOfType<ComboBox>();
        if (PageHost.Content is SettingsPage { Capturing: true } || focused is ComboBoxItem || dropdown is { IsDropDownOpen: true }) return;
        if (dropdown is not null && e.Key is Key.Enter || focused is ComboBox && e.Key == Key.Space) { dropdown!.IsDropDownOpen = true; e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.PageDown or Key.PageUp) { TurnPage(e.Key == Key.PageDown ? 1 : -1); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Q or Key.E && focused is not TextBox && OpenOverlay is null)
        {
            var step = e.Key == Key.E ? 1 : -1;
            if (PageHost.Content is SettingsPage settings) settings.TurnTab(step); else TurnPage(step);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up) e.Handled = MoveVertical(focused, e.Key == Key.Down ? 1 : -1);
        else if (e.Key is Key.Left or Key.Right) MoveHorizontal(focused, e.Key == Key.Right ? 1 : -1, e);
        else if (e.Key == Key.Escape)
        {
            if (ErrorOverlay.IsVisible) ErrorClose_Click(sender, e);
            else if (ConfirmOverlay.IsVisible) confirmAnswer?.TrySetResult(false);
            else if (UpdateOverlay.IsVisible) UpdateOverlay.IsVisible = false;
            else if (AuditOverlay.IsVisible) AuditClose_Click(sender, e);
            else if (WrongBuildOverlay.IsVisible) WrongBuildOverlay.IsVisible = false;
            else if (MovieOverlay.IsVisible) movieAnswer?.TrySetResult(false);
            else if (NameOverlay.IsVisible) NameCancel_Click(sender, e);
            else if (GuideOverlay.IsVisible) GuideOk_Click(sender, e);
            else if (PageHost.IsVisible) { ShowPage(HomeNav); HomeNav.Focus(NavigationMethod.Directional); }
            else return;
            e.Handled = true;
        }
    }

    private void Collapse_Click(object? sender, RoutedEventArgs e)
    {
        cfg.SidebarCollapsed = !cfg.SidebarCollapsed;
        ApplySidebar();
        try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
    }

    // Folded, the icons alone say nothing, so each one names itself on hover.
    private void ApplySidebar()
    {
        var collapsed = cfg.SidebarCollapsed;
        Sidebar.Classes.Set("collapsed", collapsed);
        var label = L.T(collapsed ? "Expand menu" : "Collapse menu");
        ToolTip.SetTip(CollapseButton, label);
        Avalonia.Automation.AutomationProperties.SetName(CollapseButton, collapsed ? "Expand menu" : "Collapse menu");
        foreach (var nav in Pages.Where(n => n != SettingsNav).Append<Button>(PlayOfflineButton))
        {
            ToolTip.SetTip(nav, collapsed ? L.T(Avalonia.Automation.AutomationProperties.GetName(nav) ?? "") : null);
            ToolTip.SetShowDelay(nav, 150);
        }
        ToolTip.SetShowDelay(PlaySteamButton, 150);
        ToolTip.SetShowDelay(SettingsNav, 150);
        ApplyButtons();
    }

    private void EditName_Click(object? sender, RoutedEventArgs e)
    {
        NameBox.Text = cfg.PlayerName;
        NameOverlay.IsVisible = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void NameSave_Click(object? sender, RoutedEventArgs e) => SaveName();
    private void NameCancel_Click(object? sender, RoutedEventArgs e) { NameOverlay.IsVisible = false; EditNameButton.Focus(); }

    private void NameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SaveName(); e.Handled = true; }
        else if (e.Key == Key.Escape) { NameCancel_Click(sender, e); e.Handled = true; }
    }

    private void SaveName()
    {
        cfg.PlayerName = string.IsNullOrWhiteSpace(NameBox.Text) ? "Player" : NameBox.Text.Trim();
        // While the game runs its files stay untouched; the next launch writes the name anyway.
        if (gameDir is not null && !GameLayout.GameRunning())
            try { PlayerIdentity.Apply(gameDir, cfg.PlayerName, GameRunner.DocumentsRoot(cfg)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { LauncherDiagnostics.Error("Player name could not be written to the game", exception); }
        NameOverlay.IsVisible = false;
        EditNameButton.Focus();
        ApplyButtons();
        Telemetry.Start(cfg, gameDir); // The new name must be scrubbed from reports too.
        try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
    }

    private void TelemetryNotice_Click(object? sender, RoutedEventArgs e)
    {
        TelemetryNotice.IsVisible = false;
        cfg.TelemetryNoticeShown = true;
        try { cfg.Save(exeDir); } catch (Exception exception) { LauncherDiagnostics.Error("Settings save failed", exception); }
    }

}

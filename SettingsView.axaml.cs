#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace JacintoLauncher;

public partial class SettingsView : UserControl
{
    private string? gameDir;
    private readonly LauncherConfig config;
    private readonly string executableDirectory;
    private readonly Action refresh;
    private readonly Func<Task<string?>> pickFolder;
    private readonly Action showGuide;
    private MultipatchBridge.SaveBackup[] backups = Array.Empty<MultipatchBridge.SaveBackup>();
    private System.Collections.Generic.List<MultipatchInstaller.Entry> damaged = new();
    private readonly GameLanguages.Item[] gameLanguages;

    internal ComboBox GameLanguage => GameLanguageBox;
    internal TextBox CustomLanguage => CustomLanguageBox;
    private bool CustomGameLanguage => GameLanguageBox.SelectedIndex == gameLanguages.Length;
    internal GameLanguages.Item? SelectedGameLanguage => CustomGameLanguage ? new GameLanguages.Item(GameLanguages.Code(CustomLanguageBox.Text), GameLanguages.Code(CustomLanguageBox.Text), null)
        : GameLanguageBox.SelectedIndex >= 0 ? gameLanguages[GameLanguageBox.SelectedIndex] : null;

    public SettingsView() : this(null, new LauncherConfig(), "", () => { }, () => Task.FromResult<string?>(null), () => { }) { }

    public SettingsView(string? gameDir, LauncherConfig config, string executableDirectory, Action refresh, Func<Task<string?>> pickFolder, Action showGuide)
    {
        InitializeComponent();
        this.gameDir = gameDir;
        this.config = config;
        this.executableDirectory = executableDirectory;
        this.refresh = refresh;
        this.pickFolder = pickFolder;
        this.showGuide = showGuide;
        ThemeHost.Content = new ThemePicker(config, executableDirectory);
        L.Apply(this);
        LanguageBox.ItemsSource = L.Languages.Select(l => l.NativeName).ToArray();
        gameLanguages = GameLanguages.All(gameDir);
        GameLanguageBox.ItemsSource = gameLanguages.Select(l => l.Name).Append(L.T("Custom")).ToArray();
        GameLanguageBox.SelectionChanged += (_, _) => ShowGameLanguageNote();
        CustomLanguageBox.TextChanged += (_, _) => ShowGameLanguageNote();
        Reload();
        GamePathText.Text = gameDir ?? L.T("The installation is detected automatically.");
        ShortcutButton.IsVisible = OperatingSystem.IsWindows() || OperatingSystem.IsLinux();
        CompatibilityCard.IsVisible = GameRunner.NeedsCompatibilityLayer;
        if (GameRunner.NeedsCompatibilityLayer)
        {
            var layer = GameRunner.FindLayer(config);
            LayerText.Text = layer is null ? L.T("No Windows compatibility layer found. Install Proton, umu-launcher or Wine, or set it in Settings.") : L.F("Using {0}: {1}", layer.Kind, layer.Path);
        }
        SteamButton.IsVisible = SteamShortcut.SteamRoot() is not null;
        ApplyManagedState();
        LoadBackups();
        LanguageBox.SelectionChanged += Language_Changed;
    }

    // Shows the launcher's own settings, at the start and after the file was changed by hand.
    internal void Reload()
    {
        ThemeModeBox.SelectionChanged -= ThemeMode_Changed;
        ThemeModeBox.SelectedIndex = Math.Max(0, Array.IndexOf(ThemeModes, config.ThemeMode));
        ThemeModeBox.SelectionChanged += ThemeMode_Changed;
        LanguageBox.SelectedIndex = Array.FindIndex(L.Languages, l => l.Code == L.Current);
        SkipIntroCheck.IsChecked = config.SkipIntro;
        BackupCheck.IsChecked = config.BackupSaveOnLaunch;
        CloseToTrayCheck.IsChecked = config.CloseToTray;
        MultipleInstancesCheck.IsChecked = config.AllowMultipleInstances;
        TelemetryCheck.IsChecked = config.TelemetryEnabled;
        RunnerBox.Text = config.WineRunner;
        PrefixBox.Text = config.WinePrefix;
    }

    private void Guide_Click(object? sender, RoutedEventArgs e) => showGuide();

    private static readonly string[] ThemeModes = { "auto", "light", "dark" };

    private void ThemeMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        config.ThemeMode = ThemeModes[Math.Max(0, ThemeModeBox.SelectedIndex)];
        LauncherTheme.Apply(LauncherTheme.Current, config.ThemeMode);
        Save("Settings saved.");
    }

    // A code the list does not know is a custom one.
    internal void SelectGameLanguage(string code)
    {
        var known = Array.FindIndex(gameLanguages, l => l.Code == code);
        if (known < 0) CustomLanguageBox.Text = code;
        GameLanguageBox.SelectedIndex = known >= 0 ? known : gameLanguages.Length;
    }

    // Community packs are downloaded by the player: Nexus Mods gives direct downloads through its API to Premium members only.
    private void ShowGameLanguageNote()
    {
        CustomLanguageRow.IsVisible = CustomGameLanguage;
        if (CustomGameLanguage)
        {
            var code = GameLanguages.Code(CustomLanguageBox.Text);
            LanguageModButton.IsVisible = false;
            GameLanguageNote.IsVisible = true;
            GameLanguageNote.Classes.Set("error", code.Length == 3 && gameDir is not null && !GameLanguages.Installed(gameDir, code));
            GameLanguageNote.Text = code.Length != 3 ? L.T("Type the game's three-letter language code, such as FRA or POL.")
                : gameDir is not null && !GameLanguages.Installed(gameDir, code) ? L.F("This game has no {0} language: {1} is missing. Install the language pack first.", code, System.IO.Path.Combine("GearGame", "Localization", code))
                : L.F("{0} is installed. Press Apply settings to use it.", code);
            return;
        }
        GameLanguageNote.Classes.Remove("error");
        var item = SelectedGameLanguage;
        LanguageModButton.IsVisible = item?.ModUrl is not null;
        var missing = item is not null && gameDir is not null && !GameLanguages.Installed(gameDir, item.Code);
        GameLanguageNote.IsVisible = item?.ModUrl is not null;
        GameLanguageNote.Text = item is null ? "" : item.ReplacesEnglish ? L.T("This pack replaces the English files. Install it from Nexus Mods, then install the Multipatch again so the fixes for the text stay.")
            : missing ? L.T("This pack is not installed. Download it from Nexus Mods and put it in the game folder, then press Apply settings.") : L.T("Community translation. Press Apply settings to use it. The launcher sets up the subtitle font for this language.");
    }

    private async void LanguageMod_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedGameLanguage?.ModUrl is { } url && TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri(url));
    }

    private void Language_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedIndex < 0 || L.Languages[LanguageBox.SelectedIndex].Code == L.Current) return;
        try
        {
            L.Set(L.Languages[LanguageBox.SelectedIndex].Code);
            config.LauncherLanguage = L.Current;
            config.Save(executableDirectory);
            L.Apply(this);
            refresh();
            GamePathText.Text = L.T("Launcher language changed.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Launcher language change failed", exception);
            GamePathText.Text = L.T("Settings could not be saved. See Diagnostics.");
        }
    }

    private void SkipIntro_Click(object? sender, RoutedEventArgs e)
    {
        var previous = config.SkipIntro;
        try
        {
            if (gameDir is null) throw new InvalidOperationException("Game installation was not detected.");
            var enabled = SkipIntroCheck.IsChecked == true;
            IntroSettings.Apply(new[] { gameDir }, enabled, () => { config.SkipIntro = enabled; config.Save(executableDirectory); });
            GamePathText.Text = L.T("Skip Intro updated. Applies on the next game launch.");
        }
        catch (Exception exception)
        {
            config.SkipIntro = previous;
            SkipIntroCheck.IsChecked = previous;
            LauncherDiagnostics.Error("Skip Intro failed", exception);
            GamePathText.Text = L.T("Skip Intro was not changed. ") + exception.Message;
        }
    }

    private void Save(string doneMessage)
    {
        try
        {
            config.Save(executableDirectory);
            GamePathText.Text = L.T(doneMessage);
            refresh();
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Settings save failed", exception);
            GamePathText.Text = L.T("Settings could not be saved. See Diagnostics.");
        }
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        config.WineRunner = string.IsNullOrWhiteSpace(RunnerBox.Text) ? null : RunnerBox.Text.Trim();
        config.WinePrefix = string.IsNullOrWhiteSpace(PrefixBox.Text) ? null : PrefixBox.Text.Trim();
        Save("Settings saved.");
    }

    private void Backup_Toggle(object? sender, RoutedEventArgs e) { config.BackupSaveOnLaunch = BackupCheck.IsChecked == true; Save("Settings saved."); }
    private void MultipleInstances_Click(object? sender, RoutedEventArgs e) { config.AllowMultipleInstances = MultipleInstancesCheck.IsChecked == true; Save("Settings saved."); }
    private void CloseToTray_Click(object? sender, RoutedEventArgs e) { config.CloseToTray = CloseToTrayCheck.IsChecked == true; Save("Settings saved."); }

    private void Telemetry_Click(object? sender, RoutedEventArgs e)
    {
        config.TelemetryEnabled = TelemetryCheck.IsChecked == true;
        config.TelemetryNoticeShown = true;
        Save("Settings saved.");
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var found = await pickFolder();
        if (found is not null) { gameDir = found; GamePathText.Text = found; }
    }

    private void Shortcut_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Launcher executable was not detected.");
            DesktopShortcut.Create(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.Create), executable, "Gears of War 3 - Jacinto");
            GamePathText.Text = L.T("Desktop shortcut created or updated.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Desktop shortcut failed", exception);
            GamePathText.Text = L.T("Shortcut could not be created. See Diagnostics.");
        }
    }

    private void Backup_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (GameLayout.GameRunning()) throw new UserActionException("Close the game before backing up saves.");
            if (gameDir is null) throw new InvalidOperationException("Game installation was not detected.");
            LauncherDiagnostics.Info("Save backup completed: " + MultipatchBridge.BackupAllSaves(gameDir));
            LoadBackups();
            BackupStatusText.Text = L.T("Saves backed up. Offline and Steam use the same saves.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Save backup failed", exception);
            BackupStatusText.Text = L.T("An error occurred. See Diagnostics.");
        }
    }

    private void ApplyManagedState()
    {
        var managed = gameDir is not null && MultipatchInstaller.Managed(gameDir);
        VerifyButton.IsVisible = managed;
        AuditButton.IsVisible = gameDir is not null && GameLayout.ToolFilesDir(gameDir) is not null;
        RepairButton.IsVisible = RepairButton.IsVisible && managed;
    }

    private void LoadBackups()
    {
        backups = MultipatchBridge.ListSaveBackups();
        BackupBox.ItemsSource = backups.Select(b => L.F("{0} ({1} file(s))", b.CreatedUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture), b.Files)).ToArray();
        BackupBox.SelectedIndex = backups.Length == 0 ? -1 : 0;
        BackupBox.PlaceholderText = L.T("No save backups yet.");
        RestoreButton.IsEnabled = backups.Length != 0;
    }

    // Steam reads its library once at start and rewrites the file from memory, so it has to restart to take the new entry.
    private async void Steam_Click(object? sender, RoutedEventArgs e)
    {
        var running = SteamShortcut.MustRestart;
        if (running && GameLayout.GameRunning()) { GamePathText.Text = L.T("Close the game first."); return; }
        if (running && (TopLevel.GetTopLevel(this) is not MainWindow window || !await window.ConfirmAsync(L.T("ADD TO STEAM"), L.T("Steam reads its library only when it starts, so it will close and reopen."), L.T("Add to Steam")))) return;
        SteamButton.IsEnabled = false;
        var executable = Environment.ProcessPath;
        try
        {
            if (executable is null) throw new InvalidOperationException("Launcher executable was not detected.");
            if (running) GamePathText.Text = L.T("Closing Steam...");
            var count = await Task.Run(() => SteamShortcut.AddRestarting(executable, executable));
            LauncherDiagnostics.Info($"Steam shortcut written for {count} account(s).");
            GamePathText.Text = L.T(running ? "Added to Steam. Steam is starting again." : "Added to Steam. Start Steam to see it in your library.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Add to Steam failed", exception);
            GamePathText.Text = exception is TimeoutException ? L.T("Steam did not close. Close it yourself, then try again.") : L.T("An error occurred. See Diagnostics.");
        }
        SteamButton.IsEnabled = true;
    }

    private async void Audit_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is MainWindow window) await window.ShowAuditAsync();
    }

    private async void Verify_Click(object? sender, RoutedEventArgs e)
    {
        if (gameDir is null) return;
        VerifyButton.IsEnabled = false;
        var directory = gameDir;
        try
        {
            damaged = await Task.Run(() => MultipatchInstaller.Verify(directory, (done, total) => Dispatcher.UIThread.Post(() => GamePathText.Text = L.F("Verifying... {0}/{1}", done, total))));
            await Task.Delay(50); // Lets the queued progress text finish before the result replaces it.
            RepairButton.IsVisible = damaged.Count != 0;
            GamePathText.Text = damaged.Count == 0 ? L.T("All game files are intact.") : L.F("{0} damaged file(s) found. First: {1}", damaged.Count, damaged[0].Path);
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Verification failed", exception);
            GamePathText.Text = L.T("An error occurred. See Diagnostics.");
        }
        finally { VerifyButton.IsEnabled = true; }
    }

    private async void Repair_Click(object? sender, RoutedEventArgs e)
    {
        if (gameDir is null) return;
        RepairButton.IsEnabled = false;
        var directory = gameDir; var files = damaged;
        try
        {
            damaged = await Task.Run(() => { MultipatchInstaller.Repair(directory, files, line => Dispatcher.UIThread.Post(() => GamePathText.Text = line)); return MultipatchInstaller.Verify(directory); });
            await Task.Delay(50);
            if (damaged.Count != 0) throw new IOException(damaged.Count + " file(s) are still damaged after the repair.");
            RepairButton.IsVisible = false;
            GamePathText.Text = L.T("Repair complete. All game files are intact.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Repair failed", exception);
            GamePathText.Text = L.T("An error occurred. See Diagnostics.");
        }
        finally { RepairButton.IsEnabled = true; }
    }

    private async void Restore_Click(object? sender, RoutedEventArgs e)
    {
        if (gameDir is null || BackupBox.SelectedIndex < 0) return;
        if (TopLevel.GetTopLevel(this) is not MainWindow window || !await window.ConfirmAsync(L.T("RESTORE BACKUP"), L.T("Your current saves are backed up first, then this backup replaces them."), L.T("Restore Selected Backup"))) return;
        try
        {
            MultipatchBridge.RestoreSaves(gameDir, backups[BackupBox.SelectedIndex].Directory);
            LoadBackups();
            BackupStatusText.Text = L.T("Saves restored. The previous saves were backed up first.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Save restore failed", exception);
            BackupStatusText.Text = L.T("An error occurred. See Diagnostics.");
        }
    }

}

#nullable enable
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace JacintoLauncher;

public partial class SettingsWindow : Window
{
    private readonly string? gameDir;
    private readonly LauncherConfig config;
    private readonly string executableDirectory;
    private readonly Action refresh;

    public SettingsWindow(string? gameDir, LauncherConfig config, string executableDirectory, Action refresh)
    {
        InitializeComponent();
        this.gameDir = gameDir;
        this.config = config;
        this.executableDirectory = executableDirectory;
        this.refresh = refresh;
        L.Apply(this);
        LanguageBox.ItemsSource = L.Languages;
        LanguageBox.DisplayMemberPath = nameof(L.Language.NativeName);
        LanguageBox.SelectedItem = L.Languages.First(l => l.Code == L.Current);
        PlayerNameBox.Text = config.PlayerName;
        SkipIntroCheck.IsChecked = config.SkipIntro;
        GamePathText.Text = L.T("The installation is detected automatically.");
        LanguageBox.SelectionChanged += Language_Changed;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not L.Language language || language.Code == L.Current) return;
        try
        {
            L.Set(language.Code);
            config.LauncherLanguage = language.Code;
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

    private void SkipIntro_Click(object sender, RoutedEventArgs e)
    {
        var previous = config.SkipIntro;
        try
        {
            if (gameDir is null) throw new InvalidOperationException("Game installation was not detected.");
            var enabled = SkipIntroCheck.IsChecked == true;
            IntroSettings.Apply(new[] { gameDir }, enabled, () =>
            {
                config.SkipIntro = enabled;
                config.Save(executableDirectory);
            });
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

    private void SavePaths_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            config.PlayerName = string.IsNullOrWhiteSpace(PlayerNameBox.Text) ? "Player" : PlayerNameBox.Text.Trim();
            config.Save(executableDirectory);
            GamePathText.Text = L.T("Settings saved.");
            refresh();
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Settings save failed", exception);
            GamePathText.Text = L.T("Settings could not be saved. See Diagnostics.");
        }
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = System.Environment.ProcessPath ?? throw new InvalidOperationException("Launcher executable was not detected.");
            if (!string.Equals(System.IO.Path.GetFileName(executable), "GoW3Jacinto_Launcher.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Run the packaged launcher executable to create its shortcut.");
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut support is unavailable.");
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(System.IO.Path.Combine(desktop, "Gears of War 3 - Jacinto.lnk"));
            try
            {
                shortcut.TargetPath = executable;
                shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(executable);
                shortcut.IconLocation = executable + ",0";
                shortcut.Description = "Gears of War 3: Jacinto";
                shortcut.Save();
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            }
            GamePathText.Text = L.T("Desktop shortcut created or updated.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Desktop shortcut failed", exception);
            GamePathText.Text = L.T("Shortcut could not be created. See Diagnostics.");
        }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (System.Diagnostics.Process.GetProcessesByName("GearGame").Length != 0)
                throw new InvalidOperationException("Close the game before backing up saves.");
            if (gameDir is null) throw new InvalidOperationException("Game installation was not detected.");
            LauncherDiagnostics.Info("Save backup completed: " + MultipatchBridge.BackupAllSaves(gameDir));
            BackupStatusText.Text = L.T("Saves backed up. Offline and Steam use the same saves.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Save backup failed", exception);
            BackupStatusText.Text = L.T("An error occurred. See Diagnostics.");
        }
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e)
    {
        try { LauncherDiagnostics.OpenFolder(); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not open diagnostics folder", exception);
            MessageBox.Show(this, L.T("An error occurred. See Diagnostics."), L.T("Diagnostics"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

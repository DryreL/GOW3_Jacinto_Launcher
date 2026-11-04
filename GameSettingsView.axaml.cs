#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace JacintoLauncher;

public partial class GameSettingsView : UserControl
{
    private readonly string? gameDir;
    private readonly ChangeMarks marks = new();
    private readonly CheckBox runShake, hordeBeast;
    private readonly SettingsView? languages;
    private string? loaded;

    public GameSettingsView() : this(null, null) { }

    public GameSettingsView(string? gameDir, SettingsView? launcherSettings)
    {
        InitializeComponent();
        LauncherSettings.Content = launcherSettings;
        runShake = launcherSettings?.RunShakeCheck ?? new CheckBox();
        hordeBeast = launcherSettings?.HordeBeastCheck ?? new CheckBox();
        L.Apply(this);
        this.gameDir = gameDir;
        this.languages = launcherSettings;
        if (!Dxvk.Offered) DisplayModeBox.Items.RemoveAt(2);
        DxvkPanel.IsVisible = Dxvk.Offered;
        FovSlider.PropertyChanged += (_, change) => { if (change.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) UpdateFov(); };
        FovAutoCheck.IsCheckedChanged += (_, _) => { FovSlider.IsEnabled = FovAutoCheck.IsChecked != true; UpdateFov(); };
        ResolutionBox.SelectionChanged += (_, _) => ShowCustomResolution();
        foreach (Control option in new Control[] { ResolutionBox, CustomWidthBox, CustomHeightBox, DisplayModeBox, FpsBox, RefreshBox, FovSlider, SubtitleBox, VsyncCheck, FovAllModesCheck, AaBox, AnisotropyBox, GraphicsQualityBox, ShadowQualityBox, TextureQualityBox, FoliageQualityBox, DriveTypeBox, AmbientOcclusionCheck, AudioChannelsBox, AudioFilterCheck, MasterSlider, MusicSlider, EffectsSlider, DialogueSlider }) marks.Add(option);
        marks.Add(FovAutoCheck, FovSlider.Parent!.Parent is Grid fovRow ? fovRow.Children.OfType<TextBlock>().First() : null);
        foreach (var (_, check) in Effects()) marks.Add(check);
        if (launcherSettings is not null) { marks.Add(launcherSettings.GameLanguage); marks.Add(launcherSettings.CustomLanguage); marks.Add(runShake); marks.Add(hordeBeast); }
        marks.Changed += () => RevertButton.IsEnabled = marks.Dirty;
        if (gameDir is null)
        {
            Fail(L.T("Game installation not found. Settings are unavailable."));
            Form.IsEnabled = ApplyButton.IsEnabled = false;
            return;
        }
        try { LoadCurrent(); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch Game Settings", exception);
            Fail(L.T("Multipatch settings could not be read. See Diagnostics."));
            Form.IsEnabled = ApplyButton.IsEnabled = false;
        }
    }

    internal void ShowSection(string section)
    {
        LauncherSettings.IsVisible = section == "general";
        SectionNote.IsVisible = section != "general";
        DisplayCard.IsVisible = section == "display";
        GraphicsCard.IsVisible = section == "graphics";
        AudioCard.IsVisible = section == "audio";
        Scroller.Offset = default;
    }

    // Zero means "leave it to the game"; the box shows that as a word: no frame rate limit, the monitor's own refresh rate.
    private static string LimitText(int value, string zero) => value == 0 ? L.T(zero) : value.ToString(CultureInfo.InvariantCulture);
    private static int? LimitValue(string? text, string zero)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed == L.T(zero) || trimmed == zero) return 0;
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private void UpdateFov() => FovValue.Text = FovAutoCheck.IsChecked == true ? "" : ((int)FovSlider.Value).ToString(CultureInfo.InvariantCulture);

    private static string Snapshot(JsonObject response) => response["Graphics"]?.ToJsonString() + response["Applied"]?["IsApplied"]?.ToJsonString();

    // The file was changed outside the launcher. Unsaved edits give way to it; the launcher's own Apply reads back the same values and changes nothing.
    internal void Reload()
    {
        if (gameDir is null || !Form.IsEnabled || !ApplyButton.IsEnabled) return;
        try
        {
            if (Snapshot(MultipatchBridge.LoadSettingsFromMultipatch(gameDir)) == loaded) return;
            LoadCurrent();
            StatusText.Classes.Remove("error");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch Game Settings", exception);
            loaded = null; // Half an edit: the next save of the file is read again.
            Fail(L.T("Multipatch settings could not be read. See Diagnostics."));
        }
    }

    internal void ReloadLauncher() => languages?.Reload();

    private void LoadCurrent()
    {
        var response = MultipatchBridge.LoadSettingsFromMultipatch(gameDir!);
        loaded = Snapshot(response);
        var settings = response["Graphics"]?.AsObject() ?? throw new InvalidOperationException("Multipatch returned no graphics settings.");
        marks.Batch(() => Fill(settings));
        marks.Capture();
        if (response["Applied"]?["IsApplied"]?.GetValue<bool>() != true) LauncherDiagnostics.Info("Settings differ from the files: " + response["Applied"]?.ToJsonString());
        StatusText.Text = response["Applied"]?["IsApplied"]?.GetValue<bool>() == true
            ? L.T("Saved values match the checked configuration files. Offline and Steam share these settings.")
            : L.T("Saved values differ from the checked configuration files. Apply rewrites them.");
    }

    // A missing value shows its default, so an empty object is Reset to Defaults.
    private void Fill(JsonObject settings)
    {
        FillResolutions(GetString(settings, "Resolution", "native"));
        DisplayModeBox.SelectedIndex = !GetBool(settings, "Fullscreen", true) ? 1 : GetBool(settings, "Borderless", false) && DisplayModeBox.ItemCount > 2 && Dxvk.Installed(gameDir!) ? 2 : 0;
        languages?.SelectGameLanguage(GetString(settings, "Language", "INT"));
        DxvkCheck.IsChecked = Dxvk.Installed(gameDir!);
        UpdateBorderlessLock();
        SubtitleRow.IsVisible = MultipatchInstaller.Managed(gameDir!);
        SubtitleBox.SelectedIndex = Math.Max(0, Array.IndexOf(SubtitleSizes, GetString(settings, "SubtitleSize", "big")));
        HudPanel.IsVisible = MultipatchInstaller.CanSwitchHud(gameDir!);
        if (HudPanel.IsVisible) HudCheck.IsChecked = MultipatchInstaller.UsesHudScaling(gameDir!);
        hordeBeast.IsChecked = GetBool(settings, "HordeBeast", false);
        VsyncCheck.IsChecked = GetBool(settings, "VSync", false);
        FpsBox.Text = LimitText(GetInt(settings, "FpsLimit", 0), "Unlimited");
        RefreshBox.Text = LimitText(GetInt(settings, "RefreshRate", 0), "Default");
        var fov = GetInt(settings, "Fov", 0);
        FovAutoCheck.IsChecked = fov == 0;
        FovSlider.Value = fov == 0 ? 80 : Math.Clamp(fov, 50, 130);
        FovSlider.IsEnabled = fov != 0;
        UpdateFov();
        FovAllModesCheck.IsChecked = GetBool(settings, "FovAllModes", false);
        AaBox.SelectedIndex = GetString(settings, "AntiAliasing", "off") switch { "msaa2x" => 1, "msaa4x" => 2, _ => 0 };
        AnisotropyBox.SelectedIndex = GetInt(settings, "Anisotropy", 16) switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };
        GraphicsQualityBox.SelectedIndex = QualityIndex(GetString(settings, "GraphicsQuality", "high"));
        ShadowQualityBox.SelectedIndex = QualityIndex(GetString(settings, "ShadowQuality", "high"));
        TextureQualityBox.SelectedIndex = QualityIndex(GetString(settings, "TextureQuality", "high"));
        FoliageQualityBox.SelectedIndex = QualityIndex(GetString(settings, "FoliageQuality", "high"));
        DriveTypeBox.SelectedIndex = GetString(settings, "DriveType", "ssd") == "hdd" ? 1 : 0;
        AmbientOcclusionCheck.IsChecked = GetBool(settings, "AmbientOcclusion", true);
        var disabled = settings["Disabled"] as JsonArray ?? new JsonArray();
        foreach (var (id, check) in Effects()) check.IsChecked = !disabled.Any(node => node?.GetValue<string>() == id);
        AudioChannelsBox.SelectedIndex = GetInt(settings, "AudioChannels", 32) switch { 64 => 1, 128 => 2, _ => 0 };
        AudioFilterCheck.IsChecked = GetBool(settings, "AudioFilter", true);
        MasterSlider.Value = GetInt(settings, "MasterVolume", 100); MusicSlider.Value = GetInt(settings, "MusicVolume", 100); EffectsSlider.Value = GetInt(settings, "EffectsVolume", 100); DialogueSlider.Value = GetInt(settings, "DialogueVolume", 100);
        runShake.IsChecked = !GetBool(settings, "DisableRunShake", true);
        UpdateChannelLock();
    }

    private static readonly (int X, int Y)[] CommonResolutions =
    {
        (1280, 720), (1280, 800), (1366, 768), (1600, 900), (1680, 1050), (1920, 1080), (1920, 1200), (2560, 1080), (2560, 1440), (2560, 1600), (3440, 1440), (3840, 1600), (3840, 2160), (5120, 1440), (5120, 2160), (5760, 1080), (7680, 1440), (7680, 4320)
    };
    private string? nativeResolution, largestResolution;

    // Only what the main display can show. NVIDIA Surround and AMD Eyefinity report one wide display, so they list their full size.
    // DSR (NVIDIA) and VSR (AMD) add sizes above the panel's own; those are listed too, and are the largest a custom size may be.
    private void FillResolutions(string stored)
    {
        var screens = SettingsEngine.Screens?.Invoke() ?? Array.Empty<(int Left, int Top, int Right, int Bottom, bool Primary)>();
        (int X, int Y)? native = null;
        if (screens.Length != 0) { var main = screens.FirstOrDefault(s => s.Primary, screens[0]); native = (main.Right - main.Left, main.Bottom - main.Top); }
        nativeResolution = native is { } size ? Text(size) : null;
        var above = native is { } panel ? DisplayModes.Primary().Where(m => m.X > panel.X || m.Y > panel.Y).ToList() : new List<(int X, int Y)>();
        largestResolution = above.Count != 0 ? Text(above.MaxBy(m => (long)m.X * m.Y)) : nativeResolution;
        var limit = largestResolution?.Split('x') is [var lx, var ly] ? (X: int.Parse(lx, CultureInfo.InvariantCulture), Y: int.Parse(ly, CultureInfo.InvariantCulture)) : native;
        var n = limit;
        var fits = CommonResolutions.Where(r => n is not { } l || (r.X <= l.X && r.Y <= l.Y)).Concat(above).Distinct().ToList();
        if (native is { } own && !fits.Contains(own)) fits.Add(own);
        var saved = System.Text.RegularExpressions.Regex.Match(stored, @"^(\d{3,5})x(\d{3,5})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (saved.Success)
        {
            var value = (X: int.Parse(saved.Groups[1].Value, CultureInfo.InvariantCulture), Y: int.Parse(saved.Groups[2].Value, CultureInfo.InvariantCulture));
            if (n is not { } l || (value.X <= l.X && value.Y <= l.Y)) { if (!fits.Contains(value)) fits.Add(value); stored = Text(value); }
            else stored = nativeResolution!;
        }
        else stored = nativeResolution ?? "1920x1080";
        ResolutionBox.ItemsSource = fits.OrderBy(r => r.X).ThenBy(r => r.Y).Select(Text).Append(L.T("Custom")).ToArray();
        ResolutionBox.SelectedItem = stored;
        static string Text((int X, int Y) r) => r.X.ToString(CultureInfo.InvariantCulture) + "x" + r.Y.ToString(CultureInfo.InvariantCulture);
    }

    private bool CustomResolution => ResolutionBox.SelectedItem as string == L.T("Custom");

    // Custom opens two boxes, filled with the last size chosen from the list.
    private void ShowCustomResolution()
    {
        CustomResolutionRow.IsVisible = CustomResolution;
        if (CustomResolution || ResolutionBox.SelectedItem is not string size || size.Split('x') is not [var x, var y]) return;
        CustomWidthBox.Text = x; CustomHeightBox.Text = y;
    }

    /// <summary>The typed size, or why the game cannot use it: under 640x480, or larger than the main display.</summary>
    private string CustomSize()
    {
        if (!int.TryParse(CustomWidthBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var x) || !int.TryParse(CustomHeightBox.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y))
            throw new ArgumentException(L.T("Type the width and height of the custom resolution as whole numbers."));
        if (x < 640 || y < 480) throw new ArgumentException(L.F("{0}x{1} is not supported: the game needs at least 640x480.", x, y));
        if (largestResolution?.Split('x') is [var nx, var ny] && (x > int.Parse(nx, CultureInfo.InvariantCulture) || y > int.Parse(ny, CultureInfo.InvariantCulture)))
            throw new ArgumentException(L.F("{0}x{1} is not supported: it is larger than your main display ({2}). Turn on DSR (NVIDIA) or VSR (AMD) in the graphics driver for bigger sizes.", x, y, largestResolution));
        return x.ToString(CultureInfo.InvariantCulture) + "x" + y.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly string[] SubtitleSizes = { "big", "normal", "default" };

    private IEnumerable<(string Id, CheckBox Check)> Effects() => new[]
    {
        ("MotionBlur", MotionBlurCheck), ("DepthOfField", DepthOfFieldCheck), ("Bloom", BloomCheck), ("LensFlares", LensFlaresCheck), ("LightShafts", LightShaftsCheck),
        ("Distortion", DistortionCheck), ("RadialBlur", RadialBlurCheck), ("Subsurface", SubsurfaceCheck), ("SeparateTranslucency", SeparateTranslucencyCheck)
    };

    private JsonObject BuildRequest()
    {
        var resolution = CustomResolution ? CustomSize() : ResolutionBox.SelectedItem as string ?? "native";
        if (LimitValue(FpsBox.Text, "Unlimited") is not { } fps) throw new ArgumentException("FPS limit must be an integer from 0 to 500.");
        if (LimitValue(RefreshBox.Text, "Default") is not { } refresh) throw new ArgumentException("Refresh rate must be an integer from 0 to 1000.");
        var disabled = new JsonArray();
        foreach (var (id, check) in Effects()) if (check.IsChecked != true) disabled.Add(id);
        var language = languages?.SelectedGameLanguage?.Code ?? "INT";
        if (language.Length != 3) throw new ArgumentException(L.T("Type the game's three-letter language code, such as FRA or POL."));
        if (!GameLanguages.Installed(gameDir!, language)) throw new ArgumentException(languages?.SelectedGameLanguage?.Name == language ? L.F("This game has no {0} language: {1} is missing. Install the language pack first.", language, System.IO.Path.Combine("GearGame", "Localization", language)) : L.T("This language pack is not installed. Download it from Nexus Mods first."));
        return new JsonObject
        {
            ["Resolution"] = DisplayModeBox.SelectedIndex == 2 || resolution == nativeResolution ? "native" : resolution, ["Language"] = language, ["Fullscreen"] = DisplayModeBox.SelectedIndex != 1, ["Borderless"] = DisplayModeBox.SelectedIndex == 2, ["VSync"] = VsyncCheck.IsChecked == true, ["FpsLimit"] = fps, ["RefreshRate"] = refresh,
            ["Fov"] = FovAutoCheck.IsChecked == true ? 0 : (int)FovSlider.Value, ["FovAllModes"] = FovAllModesCheck.IsChecked == true,
            ["AntiAliasing"] = AaBox.SelectedIndex switch { 1 => "msaa2x", 2 => "msaa4x", _ => "off" },
            ["Anisotropy"] = AnisotropyBox.SelectedIndex switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 16 },
            ["GraphicsQuality"] = QualityValue(GraphicsQualityBox), ["ShadowQuality"] = QualityValue(ShadowQualityBox), ["TextureQuality"] = QualityValue(TextureQualityBox), ["FoliageQuality"] = QualityValue(FoliageQualityBox),
            ["DriveType"] = DriveTypeBox.SelectedIndex == 1 ? "hdd" : "ssd", ["AmbientOcclusion"] = AmbientOcclusionCheck.IsChecked == true, ["Disabled"] = disabled,
            ["AudioChannels"] = AudioChannelsBox.SelectedIndex switch { 1 => 64, 2 => 128, _ => 32 }, ["AudioFilter"] = AudioFilterCheck.IsChecked == true, ["MasterVolume"] = (int)MasterSlider.Value, ["MusicVolume"] = (int)MusicSlider.Value, ["EffectsVolume"] = (int)EffectsSlider.Value, ["DialogueVolume"] = (int)DialogueSlider.Value, ["DisableRunShake"] = runShake.IsChecked != true, ["SubtitleSize"] = SubtitleSizes[Math.Max(0, SubtitleBox.SelectedIndex)], ["HordeBeast"] = hordeBeast.IsChecked == true
        };
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        StatusText.Classes.Remove("error");
        try
        {
            var request = BuildRequest();
            var directory = gameDir!;
            var response = await Task.Run(() => MultipatchBridge.ApplyGraphicsThroughMultipatch(directory, request));
            LoadCurrent();
            StatusText.Text = L.T("Settings saved and verified. Restart the game.");
            Telemetry.Track("settings_applied", new Dictionary<string, object> { ["kind"] = "graphics" });
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Game Settings apply failed", exception);
            Fail(L.T("Settings were not applied. ") + exception.Message);
        }
        finally { ApplyButton.IsEnabled = true; }
    }

    // Borderless runs on DXVK, so it opens only with it.
    private void UpdateBorderlessLock()
    {
        BorderlessItem.IsEnabled = DxvkCheck.IsChecked == true;
        ToolTip.SetTip(BorderlessItem, BorderlessItem.IsEnabled ? null : L.T("Turn on the Vulkan renderer (DXVK) below to use Borderless window."));
    }

    // 64 and 128 audio channels do not work through the Vulkan renderer. The game start turns a saved value back to 32 as well.
    private void UpdateChannelLock()
    {
        var vulkan = DxvkCheck.IsChecked == true;
        foreach (var item in AudioChannelsBox.Items.OfType<ComboBoxItem>().Skip(1))
        {
            item.IsEnabled = !vulkan;
            ToolTip.SetTip(item, vulkan ? L.T("Not available with the Vulkan renderer (DXVK).") : null);
        }
        if (vulkan && AudioChannelsBox.SelectedIndex > 0) AudioChannelsBox.SelectedIndex = 0;
    }

    private async void Dxvk_Click(object? sender, RoutedEventArgs e)
    {
        var enable = DxvkCheck.IsChecked == true;
        DxvkCheck.IsEnabled = false;
        StatusText.Classes.Remove("error");
        var directory = gameDir!;
        try
        {
            if (enable) { StatusText.Text = L.T("Downloading DXVK..."); await Dxvk.InstallAsync(directory); }
            else
            {
                Dxvk.Remove(directory);
                if (DisplayModeBox.SelectedIndex == 2) DisplayModeBox.SelectedIndex = 0; // Borderless needs it.
            }
            UpdateBorderlessLock();
            UpdateChannelLock();
            StatusText.Text = L.T(enable ? "Vulkan renderer on. It applies the next time the game starts." : "Vulkan renderer off. The game uses DirectX 9 again.");
            Telemetry.Track("dxvk", new Dictionary<string, object> { ["enabled"] = enable });
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("DXVK change failed", exception);
            DxvkCheck.IsChecked = Dxvk.Installed(directory);
            Fail(exception is System.Net.Http.HttpRequestException or TaskCanceledException ? L.T("DXVK could not be downloaded. Check your connection.") : L.T("An error occurred. See Diagnostics."));
        }
        finally { DxvkCheck.IsEnabled = true; }
    }

    private async void Hud_Click(object? sender, RoutedEventArgs e)
    {
        var on = HudCheck.IsChecked == true;
        var directory = gameDir!;
        HudCheck.IsEnabled = false;
        StatusText.Classes.Remove("error");
        try
        {
            await Task.Run(() => MultipatchInstaller.SetHudScaling(directory, on));
            StatusText.Text = L.T("Saved. It applies the next time the game starts.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("HUD scaling change failed", exception);
            HudCheck.IsChecked = MultipatchInstaller.UsesHudScaling(directory);
            Fail(exception is InvalidOperationException ? L.T(exception.Message) : L.T("An error occurred. See Diagnostics."));
        }
        finally { HudCheck.IsEnabled = true; }
    }

    private async void Revert_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || !await window.ConfirmAsync(L.T("REVERT SETTINGS"), L.T("Your unsaved changes will be lost, and the saved settings come back."), L.T("Revert settings"))) return;
        try
        {
            LoadCurrent();
            StatusText.Classes.Remove("error");
            StatusText.Text = L.T("Unsaved changes were reverted.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch Game Settings", exception);
            Fail(L.T("Multipatch settings could not be read. See Diagnostics."));
        }
    }

    // The game language and the storage drive are facts about this PC, so they stay. Nothing is written until Apply.
    private async void Reset_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not MainWindow window || !await window.ConfirmAsync(L.T("RESET TO DEFAULTS"), L.T("Every option in General, Display, Graphics and Audio goes back to its default. Nothing is saved until you press Apply settings."), L.T("Reset to Defaults"))) return;
        var language = languages?.GameLanguage.SelectedIndex ?? -1;
        var drive = DriveTypeBox.SelectedIndex;
        marks.Batch(() =>
        {
            Fill(new JsonObject());
            if (languages is not null) languages.GameLanguage.SelectedIndex = language;
            DriveTypeBox.SelectedIndex = drive;
        });
        StatusText.Classes.Remove("error");
        StatusText.Text = L.T("Defaults selected. Press Apply settings to save them.");
    }

    private void Fail(string message) { StatusText.Classes.Add("error"); StatusText.Text = message; }
    private static string GetString(JsonObject obj, string name, string fallback) => obj[name]?.GetValue<string>() ?? fallback;
    private static int GetInt(JsonObject obj, string name, int fallback) => obj[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
    private static bool GetBool(JsonObject obj, string name, bool fallback) => obj[name] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
    private static int QualityIndex(string value) => value.ToLowerInvariant() switch { "low" => 0, "medium" => 1, "ultra" => 3, _ => 2 };
    private static string QualityValue(ComboBox box) => new[] { "low", "medium", "high", "ultra" }[Math.Clamp(box.SelectedIndex, 0, 3)];
}

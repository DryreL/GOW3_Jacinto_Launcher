#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace JacintoLauncher;

public partial class GameSettingsWindow : Window
{
    private readonly string[] gameDirs;
    private readonly List<(string Directory, string Transaction)> transactions = new();

    public GameSettingsWindow(params string?[] directories)
    {
        InitializeComponent();
        L.Apply(this);
        gameDirs = directories.Where(directory => !string.IsNullOrWhiteSpace(directory)).Select(directory => directory!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (gameDirs.Length == 0)
        {
            StatusText.Text = L.T("Game installation not found. Settings are unavailable.");
            SetEnabled(false);
            return;
        }
        try { LoadCurrent(); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch Game Settings", exception);
            StatusText.Text = L.T("Multipatch settings could not be read. See Diagnostics.");
            SetEnabled(false);
        }
    }

    private void LoadCurrent()
    {
        var response = MultipatchBridge.LoadSettingsFromMultipatch(gameDirs[0]);
        var settings = response["Graphics"]?.AsObject()
            ?? throw new InvalidOperationException("Multipatch returned no graphics settings.");
        ResolutionBox.Text = GetString(settings, "Resolution", "native");
        DisplayModeBox.SelectedIndex = GetBool(settings, "Fullscreen", true) ? 0 : 1;
        VsyncCheck.IsChecked = GetBool(settings, "VSync", false);
        FpsBox.Text = GetInt(settings, "FpsLimit", 0).ToString(CultureInfo.InvariantCulture);
        RefreshBox.Text = GetInt(settings, "RefreshRate", 0).ToString(CultureInfo.InvariantCulture);
        FovBox.Text = GetInt(settings, "Fov", 0).ToString(CultureInfo.InvariantCulture);
        FovAllModesCheck.IsChecked = GetBool(settings, "FovAllModes", false);
        AaBox.SelectedIndex = GetString(settings, "AntiAliasing", "off") switch { "fxaa" => 1, "msaa2x" => 2, "msaa4x" => 3, _ => 0 };
        AnisotropyBox.SelectedIndex = GetInt(settings, "Anisotropy", 16) switch { 1 => 0, 2 => 1, 4 => 2, 8 => 3, _ => 4 };
        GraphicsQualityBox.SelectedIndex = QualityIndex(GetString(settings, "GraphicsQuality", "high"));
        ShadowQualityBox.SelectedIndex = QualityIndex(GetString(settings, "ShadowQuality", "high"));
        TextureQualityBox.SelectedIndex = QualityIndex(GetString(settings, "TextureQuality", "high"));
        FoliageQualityBox.SelectedIndex = QualityIndex(GetString(settings, "FoliageQuality", "high"));
        DriveTypeBox.SelectedIndex = GetString(settings, "DriveType", "ssd") == "hdd" ? 1 : 0;
        AmbientOcclusionCheck.IsChecked = GetBool(settings, "AmbientOcclusion", true);
        var disabled = settings["Disabled"] as JsonArray ?? new JsonArray();
        MotionBlurCheck.IsChecked = !IsDisabled(disabled, "MotionBlur");
        DepthOfFieldCheck.IsChecked = !IsDisabled(disabled, "DepthOfField");
        BloomCheck.IsChecked = !IsDisabled(disabled, "Bloom");
        LensFlaresCheck.IsChecked = !IsDisabled(disabled, "LensFlares");
        LightShaftsCheck.IsChecked = !IsDisabled(disabled, "LightShafts");
        DistortionCheck.IsChecked = !IsDisabled(disabled, "Distortion");
        RadialBlurCheck.IsChecked = !IsDisabled(disabled, "RadialBlur");
        SubsurfaceCheck.IsChecked = !IsDisabled(disabled, "Subsurface");
        SeparateTranslucencyCheck.IsChecked = !IsDisabled(disabled, "SeparateTranslucency");
        AudioChannelsBox.SelectedIndex = GetInt(settings, "AudioChannels", 32) switch { 64 => 1, 128 => 2, _ => 0 };
        AudioFilterCheck.IsChecked = GetBool(settings, "AudioFilter", true);
        RunShakeCheck.IsChecked = !GetBool(settings, "DisableRunShake", true);
        var responses = new[] { response }.Concat(gameDirs.Skip(1).Select(MultipatchBridge.LoadSettingsFromMultipatch)).ToArray();
        var isApplied = responses.All(item => item["Applied"]?["IsApplied"]?.GetValue<bool>() == true);
        var sameValues = responses.All(item => settings.All(property =>
            item["Graphics"]?[property.Key]?.ToJsonString() == property.Value?.ToJsonString()));
        StatusText.Text = isApplied && sameValues
            ? L.T("Saved values match the checked configuration files. Offline and Steam share these settings.")
            : L.T("Saved values differ from the checked configuration files. Apply rewrites them.");

    }

    private JsonObject BuildRequest()
    {
        if (string.IsNullOrWhiteSpace(ResolutionBox.Text)) throw new ArgumentException("Enter a resolution, such as 2560x1440 or native.");
        if (!int.TryParse(FpsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps)) throw new ArgumentException("FPS limit must be an integer from 0 to 500.");
        if (!int.TryParse(RefreshBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var refresh)) throw new ArgumentException("Refresh rate must be an integer from 0 to 1000.");
        if (!int.TryParse(FovBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fov)) throw new ArgumentException("FOV must be 0 (automatic) or an integer from 50 to 130.");
        var disabled = new JsonArray();
        AddDisabled(disabled, "MotionBlur", MotionBlurCheck);
        AddDisabled(disabled, "DepthOfField", DepthOfFieldCheck);
        AddDisabled(disabled, "Bloom", BloomCheck);
        AddDisabled(disabled, "LensFlares", LensFlaresCheck);
        AddDisabled(disabled, "LightShafts", LightShaftsCheck);
        AddDisabled(disabled, "Distortion", DistortionCheck);
        AddDisabled(disabled, "RadialBlur", RadialBlurCheck);
        AddDisabled(disabled, "Subsurface", SubsurfaceCheck);
        AddDisabled(disabled, "SeparateTranslucency", SeparateTranslucencyCheck);
        var aa = AaBox.SelectedIndex switch { 1 => "fxaa", 2 => "msaa2x", 3 => "msaa4x", _ => "off" };
        var aniso = AnisotropyBox.SelectedIndex switch { 0 => 1, 1 => 2, 2 => 4, 3 => 8, _ => 16 };
        return new JsonObject
        {
            ["Resolution"] = ResolutionBox.Text.Trim(),
            ["Fullscreen"] = DisplayModeBox.SelectedIndex == 0,
            ["VSync"] = VsyncCheck.IsChecked == true,
            ["FpsLimit"] = fps,
            ["RefreshRate"] = refresh,
            ["Fov"] = fov,
            ["FovAllModes"] = FovAllModesCheck.IsChecked == true,
            ["AntiAliasing"] = aa,
            ["Anisotropy"] = aniso,
            ["GraphicsQuality"] = QualityValue(GraphicsQualityBox),
            ["ShadowQuality"] = QualityValue(ShadowQualityBox),
            ["TextureQuality"] = QualityValue(TextureQualityBox),
            ["FoliageQuality"] = QualityValue(FoliageQualityBox),
            ["DriveType"] = DriveTypeBox.SelectedIndex == 1 ? "hdd" : "ssd",
            ["AmbientOcclusion"] = AmbientOcclusionCheck.IsChecked == true,
            ["Disabled"] = disabled,
            ["AudioChannels"] = AudioChannelsBox.SelectedIndex switch { 1 => 64, 2 => 128, _ => 32 },
            ["AudioFilter"] = AudioFilterCheck.IsChecked == true,
            ["DisableRunShake"] = RunShakeCheck.IsChecked != true
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var request = BuildRequest();
            transactions.Clear();
            foreach (var directory in gameDirs)
            {
                var response = MultipatchBridge.ApplyGraphicsThroughMultipatch(directory, request);
                var id = response["TransactionId"]?.GetValue<string>() ?? throw new InvalidOperationException("Multipatch did not return a graphics transaction ID.");
                transactions.Add((directory, id));
            }
            LauncherDiagnostics.Info("Settings saved and verified. Transactions: " + string.Join(", ", transactions.Select(item => item.Transaction)));
            LoadCurrent();
            StatusText.Text = L.T("Settings saved and verified. Restart the game.");
        }
        catch (Exception exception)
        {
            RollbackAppliedTransactions();
            LauncherDiagnostics.Error("Game Settings apply failed", exception);
            StatusText.Text = L.T("Settings were not applied. ") + exception.Message;
        }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (gameDirs.Length == 0) return;
        if (MessageBox.Show(this, L.T("Restore the previous graphics settings transaction?"), L.T("Rollback graphics settings"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            if (transactions.Count == 0) throw new InvalidOperationException("No graphics change was made during this window session.");
            RollbackAppliedTransactions();
            StatusText.Text = L.T("The latest graphics change was restored.");
            LoadCurrent();
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Graphics settings rollback failed", exception);
            StatusText.Text = L.T("Rollback was not completed. ") + exception.Message;
        }
    }

    private void RollbackAppliedTransactions()
    {
        var failures = new List<string>();
        foreach (var item in transactions.AsEnumerable().Reverse())
        {
            try { _ = MultipatchBridge.RollbackSettingsThroughMultipatch(item.Directory, item.Transaction); }
            catch (Exception exception) { failures.Add(item.Directory + ": " + exception.Message); }
        }
        transactions.Clear();
        if (failures.Count != 0) throw new InvalidOperationException("Rollback of one or more already-applied runtimes failed: " + string.Join(" | ", failures));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void SetEnabled(bool enabled)
    {
        foreach (var child in ((Grid)Content).Children) if (child is FrameworkElement element && element != StatusText) element.IsEnabled = enabled;
    }
    private static string GetString(JsonObject obj, string name, string fallback) => obj[name]?.GetValue<string>() ?? fallback;
    private static int GetInt(JsonObject obj, string name, int fallback) => obj[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;
    private static bool GetBool(JsonObject obj, string name, bool fallback) => obj[name] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : fallback;
    private static int QualityIndex(string value) => value.ToLowerInvariant() switch { "low" => 0, "medium" => 1, "ultra" => 3, _ => 2 };
    private static string QualityValue(ComboBox box) => new[] { "low", "medium", "high", "ultra" }[Math.Clamp(box.SelectedIndex, 0, 3)];
    private static bool IsDisabled(JsonArray array, string id) { foreach (var node in array) if (node?.GetValue<string>() == id) return true; return false; }
    private static void AddDisabled(JsonArray array, string id, CheckBox check) { if (check.IsChecked != true) array.Add(id); }
}

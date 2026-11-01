#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace JacintoLauncher;

public partial class ControlsView : UserControl
{
    private readonly string? gameDir;
    private readonly ObservableCollection<ControlBindingRow> rows = new();
    private string[] supportedKeys = Array.Empty<string>();
    private string[] reservedKeys = Array.Empty<string>();
    private string? lastTransaction;
    private double loadedSensitivity;
    private ControlBindingRow? capturing;
    private string? loaded;

    public ControlsView() : this(null) { }

    public ControlsView(string? gameDir)
    {
        InitializeComponent();
        L.Apply(this);
        this.gameDir = gameDir;
        BindingsList.ItemsSource = rows;
        DiagramHost.Content = new GamepadDiagram();
        LoadInput();
        SensitivitySlider.PropertyChanged += (_, change) => { if (change.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) SensitivityValue.Text = ((int)SensitivitySlider.Value).ToString(CultureInfo.InvariantCulture); };
        CaptureOverlay.AddHandler(KeyDownEvent, Capture_KeyDown, RoutingStrategies.Tunnel);
        BindingsList.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) { Change_Click(BindingsList, e); e.Handled = true; } }, RoutingStrategies.Tunnel);
        CaptureOverlay.AddHandler(PointerPressedEvent, Capture_Pointer, RoutingStrategies.Tunnel);
        CaptureOverlay.AddHandler(PointerWheelChangedEvent, Capture_Wheel, RoutingStrategies.Tunnel);
        if (gameDir is null)
        {
            Fail(L.T("Game installation not found. Controls are unavailable."));
            IsEnabled = false;
            return;
        }
        try { LoadCurrent(); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch controls", exception);
            Fail(L.T("Controls could not be read. See Diagnostics."));
            IsEnabled = false;
        }
    }

    internal bool Capturing => capturing is not null;

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        var gamepad = sender == GamepadTab;
        KeyboardTab.IsChecked = !gamepad; GamepadTab.IsChecked = gamepad;
        KeyboardCard.IsVisible = SensitivityRow.IsVisible = KeyboardButtons.IsVisible = !gamepad;
        GamepadCard.IsVisible = gamepad;
    }

    // The old installer's "Keyboard or Gamepad?" question. A folder patched by the old tool keeps the answer it gave there.
    private void LoadInput()
    {
        InputPanel.IsVisible = gameDir is not null && MultipatchInstaller.CanSwitchInput(gameDir);
        if (!InputPanel.IsVisible) return;
        InputBox.SelectedIndex = MultipatchInstaller.UsesGamepad(gameDir!) ? 1 : 0;
        ShowInputNote();
        InputBox.SelectionChanged += Input_Changed;
    }

    private void ShowInputNote() => InputNote.Text = L.T(InputBox.SelectedIndex == 1
        ? "Menus use the gamepad's buttons and the game shows gamepad icons. ESC and Enter do not work in some menus."
        : "ESC and Enter work in every menu and the game shows keyboard icons. The gamepad's B button does not work in some menus.");

    private async void Input_Changed(object? sender, SelectionChangedEventArgs e)
    {
        var gamepad = InputBox.SelectedIndex == 1;
        var directory = gameDir!;
        InputBox.IsEnabled = false;
        StatusText.Classes.Remove("error");
        try
        {
            await Task.Run(() => MultipatchInstaller.SetInput(directory, gamepad));
            ShowInputNote();
            StatusText.Text = L.T("Saved. It applies the next time the game starts.");
            Telemetry.Track("settings_applied", new Dictionary<string, object> { ["kind"] = gamepad ? "input_gamepad" : "input_keyboard" });
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Input choice failed", exception);
            InputBox.SelectionChanged -= Input_Changed;
            InputBox.SelectedIndex = MultipatchInstaller.UsesGamepad(directory) ? 1 : 0;
            InputBox.SelectionChanged += Input_Changed;
            Fail(exception is InvalidOperationException ? L.T(exception.Message) : L.T("An error occurred. See Diagnostics."));
        }
        InputBox.IsEnabled = true;
    }

    private static string Snapshot(JsonObject response) => response["Actions"]?.ToJsonString() + response["MouseSensitivity"]?.ToJsonString();

    // The file was changed outside the launcher. Unsaved edits give way to it; a key capture or an Apply in progress goes first.
    internal void Reload()
    {
        if (gameDir is null || !IsEnabled || Capturing || !ApplyButton.IsEnabled || !InputBox.IsEnabled) return;
        try
        {
            if (InputPanel.IsVisible)
            {
                InputBox.SelectionChanged -= Input_Changed;
                InputBox.SelectedIndex = MultipatchInstaller.UsesGamepad(gameDir) ? 1 : 0;
                InputBox.SelectionChanged += Input_Changed;
                ShowInputNote();
            }
            if (Snapshot(MultipatchBridge.LoadControlsFromMultipatch(gameDir)) == loaded) return;
            var selected = BindingsList.SelectedIndex;
            LoadCurrent();
            StatusText.Classes.Remove("error");
            BindingsList.SelectedIndex = Math.Min(selected, rows.Count - 1);
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch controls", exception);
            loaded = null; // Half an edit: the next save of the file is read again.
            Fail(L.T("Controls could not be read. See Diagnostics."));
        }
    }

    private void LoadCurrent()
    {
        var response = MultipatchBridge.LoadControlsFromMultipatch(gameDir!);
        loaded = Snapshot(response);
        var sensitivity = response["MouseSensitivity"]?.GetValue<string>();
        // No value in the file: show the game default, and write only if the slider is moved.
        loadedSensitivity = double.TryParse(sensitivity, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(Math.Round(parsed), 1, 200) : 60;
        SensitivitySlider.Value = loadedSensitivity;
        SensitivityValue.Text = ((int)loadedSensitivity).ToString(CultureInfo.InvariantCulture);
        supportedKeys = ReadStrings(response["SupportedKeys"] as JsonArray);
        reservedKeys = ReadStrings(response["ReservedKeys"] as JsonArray);
        rows.Clear();
        if (response["Actions"] is not JsonArray actions) throw new InvalidOperationException("Multipatch returned no keyboard actions.");
        foreach (var action in actions.OfType<JsonObject>())
            rows.Add(new ControlBindingRow(action["Command"]?.GetValue<string>() ?? "", L.T(action["Action"]?.GetValue<string>() ?? ""), action["Key"]?.GetValue<string>() ?? "", action["Default"]?.GetValue<string>() ?? ""));
        StatusText.Text = L.F("Loaded {0} bindings. Apply writes and verifies them; gamepad bindings are preserved.", rows.Count);
    }

    private void List_DoubleTapped(object? sender, TappedEventArgs e) => BeginCapture();
    private void Change_Click(object? sender, RoutedEventArgs e) => BeginCapture();

    private void BeginCapture()
    {
        if (BindingsList.SelectedItem is not ControlBindingRow row) { StatusText.Text = L.T("Select an action first."); return; }
        capturing = row;
        CaptureText.Text = L.F("Press a key or mouse button for\n\"{0}\"\n\nEsc cancels", row.Action);
        CaptureOverlay.IsVisible = true;
        CaptureOverlay.Focus();
    }

    private void Capture_KeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape) { EndCapture(null); return; }
        var key = ToUe3Key(e.Key);
        if (key is null) CaptureText.Text = L.T("That key is not supported here.\nTry another one, or Esc to cancel.");
        else EndCapture(key);
    }

    private void Capture_Pointer(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        EndCapture(e.GetCurrentPoint(this).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => "LeftMouseButton", PointerUpdateKind.RightButtonPressed => "RightMouseButton", PointerUpdateKind.MiddleButtonPressed => "MiddleMouseButton",
            PointerUpdateKind.XButton1Pressed => "ThumbMouseButton", PointerUpdateKind.XButton2Pressed => "ThumbMouseButton2", _ => null
        });
    }

    private void Capture_Wheel(object? sender, PointerWheelEventArgs e) { e.Handled = true; EndCapture(e.Delta.Y > 0 ? "MouseScrollUp" : "MouseScrollDown"); }

    private void EndCapture(string? key)
    {
        var row = capturing;
        capturing = null;
        CaptureOverlay.IsVisible = false;
        (BindingsList.ContainerFromIndex(Math.Max(0, BindingsList.SelectedIndex)) ?? BindingsList).Focus(NavigationMethod.Directional);
        if (row is null || key is null) return;
        if (!supportedKeys.Contains(key, StringComparer.Ordinal)) { StatusText.Text = L.T("That key name is not supported by the Multipatch editor."); return; }
        if (reservedKeys.Contains(key, StringComparer.Ordinal)) { StatusText.Text = L.F("{0} is reserved for pause or built-in Multipatch/Steam behavior.", key); return; }
        var conflict = rows.FirstOrDefault(other => other != row && string.Equals(other.Key, key, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null) { StatusText.Text = L.F("{0} is already assigned to {1}.", key, conflict.Action); return; }
        row.Key = key;
        StatusText.Text = L.F("Pending change: {0} → {1}. Apply to write and verify it.", row.Action, key);
    }

    private void Defaults_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var row in rows) row.Key = row.Default;
        StatusText.Text = L.T("Default keyboard and mouse bindings selected. Apply to write them; gamepad bindings remain untouched.");
    }

    private async void Apply_Click(object? sender, RoutedEventArgs e)
    {
        ApplyButton.IsEnabled = false;
        StatusText.Classes.Remove("error");
        try
        {
            var bindings = new JsonObject();
            foreach (var row in rows) bindings[row.Command] = row.Key;
            var sensitivity = SensitivitySlider.Value != loadedSensitivity ? ((int)SensitivitySlider.Value).ToString(CultureInfo.InvariantCulture) : null;
            var directory = gameDir!;
            var response = await Task.Run(() => MultipatchBridge.ApplyControlsThroughMultipatch(directory, bindings, sensitivity));
            lastTransaction = response["TransactionId"]?.GetValue<string>() ?? throw new InvalidOperationException("Multipatch did not return a controls transaction ID.");
            RollbackButton.IsEnabled = true;
            LoadCurrent();
            StatusText.Text = L.T("Controls saved and verified. Restart the game.");
            Telemetry.Track("settings_applied", new Dictionary<string, object> { ["kind"] = "controls" });
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Controls apply failed", exception);
            Fail(L.T("Controls were not applied. ") + exception.Message);
        }
        finally { ApplyButton.IsEnabled = true; }
    }

    private void Rollback_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            MultipatchBridge.RollbackControlsThroughMultipatch(gameDir!, lastTransaction);
            lastTransaction = null;
            RollbackButton.IsEnabled = false;
            LoadCurrent();
            StatusText.Text = L.T("The latest controls change was restored.");
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Controls rollback failed", exception);
            Fail(L.T("Controls rollback was not completed. ") + exception.Message);
        }
    }

    private void Fail(string message) { StatusText.Classes.Add("error"); StatusText.Text = message; }
    private static string[] ReadStrings(JsonArray? array) => array?.Select(node => node?.GetValue<string>() ?? "").Where(value => value.Length != 0).ToArray() ?? Array.Empty<string>();

    // Keeps the settings contract: returns UE3 key names and never writes a binding itself.
    private static string? ToUe3Key(Key key)
    {
        if (key is >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F12) return key.ToString();
        var digits = new[] { "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine" };
        if (key is >= Key.D0 and <= Key.D9) return digits[key - Key.D0];
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "NumPad" + digits[key - Key.NumPad0];
        return key switch
        {
            Key.Space => "SpaceBar", Key.Tab => "Tab", Key.Enter => "Enter", Key.Back => "BackSpace", Key.CapsLock => "CapsLock",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right", Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp", Key.PageDown => "PageDown", Key.Insert => "Insert", Key.Delete => "Delete",
            Key.LeftShift => "LeftShift", Key.RightShift => "RightShift", Key.LeftCtrl => "LeftControl", Key.RightCtrl => "RightControl", Key.LeftAlt => "LeftAlt", Key.RightAlt => "RightAlt",
            Key.OemTilde => "Tilde", Key.OemMinus => "Underscore", Key.OemPlus => "Equals", Key.OemOpenBrackets => "LeftBracket", Key.OemCloseBrackets => "RightBracket", Key.OemPipe => "Backslash", Key.OemSemicolon => "Semicolon", Key.OemQuotes => "Quote", Key.OemComma => "Comma", Key.OemPeriod => "Period", Key.OemQuestion => "Slash",
            Key.Multiply => "Multiply", Key.Add => "Add", Key.Subtract => "Subtract", Key.Decimal => "Decimal", Key.Divide => "Divide", Key.NumLock => "NumLock",
            _ => null
        };
    }
}

public sealed class ControlBindingRow : INotifyPropertyChanged
{
    private string key;
    internal ControlBindingRow(string command, string action, string key, string defaultKey) { Command = command; Action = action; this.key = key; Default = defaultKey; }
    public string Command { get; }
    public string Action { get; }
    public string Default { get; }
    public string Key { get => key; set { if (key == value) return; key = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

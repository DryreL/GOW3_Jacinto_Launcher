#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows;

namespace JacintoLauncher;

public partial class ControlsWindow : Window
{
    private readonly string[] gameDirs;
    private readonly List<(string Directory, string Transaction)> transactions = new();
    private readonly ObservableCollection<ControlBindingRow> rows = new();
    private string[] supportedKeys = Array.Empty<string>();
    private string[] reservedKeys = Array.Empty<string>();

    public ControlsWindow(params string?[] directories)
    {
        InitializeComponent();
        L.Apply(this);
        gameDirs = directories.Where(directory => !string.IsNullOrWhiteSpace(directory)).Select(directory => directory!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        BindingsList.ItemsSource = rows;
        if (gameDirs.Length == 0)
        {
            StatusText.Text = L.T("Game installation not found. Controls are unavailable.");
            IsEnabled = false;
            return;
        }
        try { LoadCurrent(); }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Could not read Multipatch controls", exception);
            StatusText.Text = L.T("Controls could not be read. See Diagnostics.");
            IsEnabled = false;
        }
    }

    private void LoadCurrent()
    {
        var response = MultipatchBridge.LoadControlsFromMultipatch(gameDirs[0]);
        var values = gameDirs.Select(d => MultipatchBridge.LoadControlsFromMultipatch(d)["MouseSensitivity"]?.GetValue<string>() ?? "").ToArray();
        var same = values.Distinct().Count() == 1;
        SensitivityBox.Text = same ? values[0] : "";
        SensitivityStatus.Text = same ? L.T("Current input-file value; blank leaves unchanged.") : "Sensitivity differs: " + string.Join("; ", gameDirs.Select((d,i) => System.IO.Path.GetFileName(d) + " = " + values[i])) + ". Enter a value to synchronize.";
        supportedKeys = ReadStrings(response["SupportedKeys"] as JsonArray);
        reservedKeys = ReadStrings(response["ReservedKeys"] as JsonArray);
        rows.Clear();
        if (response["Actions"] is not JsonArray actions) throw new InvalidOperationException("Multipatch returned no keyboard actions.");
        foreach (var node in actions)
        {
            if (node is not JsonObject action) continue;
            rows.Add(new ControlBindingRow(
                action["Command"]?.GetValue<string>() ?? string.Empty,
                L.T(action["Action"]?.GetValue<string>() ?? string.Empty),
                action["Key"]?.GetValue<string>() ?? string.Empty,
                action["Default"]?.GetValue<string>() ?? string.Empty));
        }
        StatusText.Text = L.F("Loaded {0} bindings. Apply writes and verifies them; gamepad bindings are preserved.", rows.Count);
    }

    private void Change_Click(object sender, RoutedEventArgs e)
    {
        if (BindingsList.SelectedItem is not ControlBindingRow row)
        {
            StatusText.Text = L.T("Select an action first.");
            return;
        }
        var key = KeyCapture.Ask(this, row.Action);
        if (key is null) return;
        if (!supportedKeys.Contains(key, StringComparer.Ordinal))
        {
            StatusText.Text = L.T("That key name is not supported by the Multipatch editor.");
            return;
        }
        if (reservedKeys.Contains(key, StringComparer.Ordinal))
        {
            StatusText.Text = L.F("{0} is reserved for pause or built-in Multipatch/Steam behavior.", key);
            return;
        }
        var conflict = rows.FirstOrDefault(other => other != row && string.Equals(other.Key, key, StringComparison.OrdinalIgnoreCase));
        if (conflict is not null)
        {
            StatusText.Text = L.F("{0} is already assigned to {1}.", key, conflict.Action);
            return;
        }
        row.Key = key;
        StatusText.Text = L.F("Pending change: {0} → {1}. Apply to write and verify it.", row.Action, key);
    }

    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows) row.Key = row.Default;
        StatusText.Text = L.T("Default keyboard and mouse bindings selected. Apply to write them; gamepad bindings remain untouched.");
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var sensitivity = SensitivityBox.Text.Trim();
            if (sensitivity.Length != 0 && (!double.TryParse(sensitivity, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) || double.IsNaN(number) || number < 1 || number > 200)) throw new ArgumentException("Mouse sensitivity must be between 1 and 200; use a decimal point.");
            var bindings = new JsonObject();
            foreach (var row in rows) bindings[row.Command] = row.Key;
            transactions.Clear();
            foreach (var directory in gameDirs)
            {
                var response = MultipatchBridge.ApplyControlsThroughMultipatch(directory, bindings, sensitivity.Length == 0 ? null : sensitivity);
                var id = response["TransactionId"]?.GetValue<string>() ?? throw new InvalidOperationException("Multipatch did not return a controls transaction ID.");
                transactions.Add((directory, id));
            }
            LauncherDiagnostics.Info("Controls saved and verified. Transactions: " + string.Join(", ", transactions.Select(item => item.Transaction)));
            LoadCurrent();
            StatusText.Text = L.T("Controls saved and verified. Restart the game.");
        }
        catch (Exception exception)
        {
            RollbackAppliedTransactions();
            LauncherDiagnostics.Error("Controls apply failed", exception);
            StatusText.Text = L.T("Controls were not applied. ") + exception.Message;
        }
    }

    private void Rollback_Click(object sender, RoutedEventArgs e)
    {
        if (gameDirs.Length == 0) return;
        if (MessageBox.Show(this, L.T("Restore the previous controls transaction?"), L.T("Rollback controls"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            if (transactions.Count == 0) throw new InvalidOperationException("No controls change was made during this window session.");
            RollbackAppliedTransactions();
            StatusText.Text = L.T("The latest controls change was restored.");
            LoadCurrent();
        }
        catch (Exception exception)
        {
            LauncherDiagnostics.Error("Controls rollback failed", exception);
            StatusText.Text = L.T("Controls rollback was not completed. ") + exception.Message;
        }
    }

    private void RollbackAppliedTransactions()
    {
        var failures = new List<string>();
        foreach (var item in transactions.AsEnumerable().Reverse())
        {
            try { _ = MultipatchBridge.RollbackControlsThroughMultipatch(item.Directory, item.Transaction); }
            catch (Exception exception) { failures.Add(item.Directory + ": " + exception.Message); }
        }
        transactions.Clear();
        if (failures.Count != 0) throw new InvalidOperationException("Rollback of one or more already-applied runtimes failed: " + string.Join(" | ", failures));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static string[] ReadStrings(JsonArray? array) => array?.Select(node => node?.GetValue<string>() ?? string.Empty).Where(value => value.Length != 0).ToArray() ?? Array.Empty<string>();
}

internal sealed class ControlBindingRow : INotifyPropertyChanged
{
    private string key;
    internal ControlBindingRow(string command, string action, string key, string defaultKey) { Command = command; Action = action; this.key = key; Default = defaultKey; }
    public string Command { get; }
    public string Action { get; }
    public string Default { get; }
    public string Key { get => key; set { if (key == value) return; key = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

#nullable enable
using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace JacintoLauncher;

/// <summary>The sidebar's one Settings entry: a row of equal tabs under the title. All tabs but Controls share one form and one Apply.</summary>
internal sealed class SettingsPage : UserControl
{
    private readonly ContentControl host = new();
    private readonly string? gameDir;
    private readonly Func<SettingsView> launcherSettings;
    private GameSettingsView? form;
    private readonly (ToggleButton Button, string Section)[] tabs;

    /// <summary>Esc belongs to the key capture while one runs.</summary>
    internal bool Capturing => host.Content is ControlsView { Capturing: true };

    internal SettingsPage(string? gameDir, Func<SettingsView> launcherSettings)
    {
        this.gameDir = gameDir;
        this.launcherSettings = launcherSettings;
        var title = new TextBlock { Text = "SETTINGS", Margin = new Thickness(32, 22, 32, 12) };
        title.Classes.Add("h1");
        var strip = new UniformGrid { Rows = 1, Margin = new Thickness(32, 0, 32, 4) };
        tabs = new[]
        {
            ("General", "general", "M19.14 12.94c.04-.3.06-.61.06-.94s-.02-.64-.07-.94l2.03-1.58a.5.5 0 0 0 .12-.61l-1.92-3.32a.5.5 0 0 0-.59-.22l-2.39.96a7 7 0 0 0-1.62-.94l-.36-2.54a.5.5 0 0 0-.48-.41h-3.84a.5.5 0 0 0-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96a.5.5 0 0 0-.59.22L2.74 8.87a.5.5 0 0 0 .12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58a.5.5 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32a.5.5 0 0 0-.12-.61l-2.01-1.58zM12 15.6a3.6 3.6 0 1 1 0-7.2 3.6 3.6 0 0 1 0 7.2z"),
            ("Display", "display", "M21 3H3a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h7v2H8v2h8v-2h-2v-2h7a2 2 0 0 0 2-2V5a2 2 0 0 0-2-2zm0 13H3V5h18v11z"),
            ("Graphics", "graphics", "M21 19V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2zM8.5 13.5l2.5 3 3.5-4.5 4.5 6H5l3.5-4.5z"),
            ("Audio", "audio", "M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 8v8a4.5 4.5 0 0 0 2.5-4zM14 3.2v2.1a7 7 0 0 1 0 13.4v2.1a9 9 0 0 0 0-17.6z"),
            ("Controls", "controls", "M21 6H3a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h18a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3zm4-3a1.5 1.5 0 1 1 0-3 1.5 1.5 0 0 1 0 3z")
        }.Select(tab => (Button: Tab(tab.Item1, tab.Item3), Section: tab.Item2)).ToArray();
        foreach (var (button, section) in tabs)
        {
            button.Classes.Add("nav");
            strip.Children.Add(button);
            button.Click += (_, _) => Select(button, section);
        }
        tabs[0].Button.IsChecked = true;
        var top = new StackPanel { Children = { title, strip } };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(host, 1);
        grid.Children.Add(top);
        grid.Children.Add(host);
        Content = grid;
        L.Apply(top);
        Show("general");
    }

    private void Select(ToggleButton button, string section)
    {
        foreach (var other in tabs) other.Button.IsChecked = other.Button == button;
        Telemetry.Track("settings_tab", "tab", section);
        Show(section);
    }

    internal ToggleButton CurrentTab => Array.Find(tabs, t => t.Button.IsChecked == true).Button ?? tabs[0].Button;
    internal bool IsTab(object? element) => Array.Exists(tabs, t => t.Button == element);

    /// <summary>The next tab to the left or right, for Q and E, the arrows and the pad's bumpers. False at either end.</summary>
    internal bool TurnTab(int step)
    {
        var next = Array.FindIndex(tabs, t => t.Button.IsChecked == true) + step;
        if (next < 0 || next >= tabs.Length) return false;
        Select(tabs[next].Button, tabs[next].Section);
        tabs[next].Button.Focus(Avalonia.Input.NavigationMethod.Directional);
        return true;
    }

    private static ToggleButton Tab(string text, string icon)
    {
        var button = new ToggleButton
        {
            Content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { new PathIcon { Width = 15, Height = 15, Data = Avalonia.Media.StreamGeometry.Parse(icon) }, new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } },
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        };
        Avalonia.Automation.AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>A settings file changed outside the launcher.</summary>
    internal void GameFilesChanged()
    {
        form?.Reload();
        (host.Content as ControlsView)?.Reload();
    }

    internal void LauncherFileChanged() => form?.ReloadLauncher();

    private void Show(string section)
    {
        Motion.Open(host, "settings " + section);
        if (section == "controls") { host.Content = new ControlsView(gameDir); return; }
        // Kept between tabs: unsaved changes on one tab must survive a look at another.
        form ??= new GameSettingsView(gameDir, launcherSettings());
        host.Content = form;
        form.ShowSection(section);
    }
}

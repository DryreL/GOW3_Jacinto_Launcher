#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace JacintoLauncher;

/// <summary>What the launcher is, what it is built with, and whether this PC can run the game.</summary>
internal sealed class AboutPage : UserControl
{
    private const long Gigabyte = 1L << 30;
    private const string JacintoNetVersion = "0.3.8"; // kVersion in jacintonet.cpp and the DLL's resource.rc.

    internal AboutPage(string? gameDir, LauncherConfig config, Func<Task<string>> checkForUpdate)
    {
        var panel = new StackPanel();

        var status = new TextBlock { Text = L.F("Current version: v{0}", LauncherUpdate.Current.ToString(3)), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        status.Classes.Add("muted");
        var check = new Button { Content = L.T("Check for updates") };
        check.Click += async (_, _) => { check.IsEnabled = false; status.Text = await checkForUpdate(); check.IsEnabled = true; };
        DockPanel.SetDock(check, Dock.Right);
        var update = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        update.Children.Add(check); update.Children.Add(status);
        panel.Children.Add(Card(L.T("LAUNCHER"), Muted(L.T("The Gears of War 3 Jacinto Launcher installs the Multipatch, starts the game offline or on Steam, and manages its settings, controls and saves. It is free and open source under the MIT license.")), update));

        var links = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        links.Children.Add(Link("Nexus Mods page", "https://www.nexusmods.com/gearsofwar3/mods/27", "M19 19H5V5h7V3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7h-2v7zM14 3v2h3.59l-9.83 9.83 1.41 1.41L19 6.41V10h2V3h-7z"));
        links.Children.Add(Link("Bug Report", "https://www.nexusmods.com/gearsofwar3/mods/27?tab=bugs", "M20 8h-2.81a6 6 0 0 0-1.82-1.96L17 4.41 15.59 3l-2.17 2.17a6 6 0 0 0-2.83 0L8.41 3 7 4.41l1.62 1.63A6 6 0 0 0 6.81 8H4v2h2.09c-.05.33-.09.66-.09 1v1H4v2h2v1c0 .34.04.67.09 1H4v2h2.81A6 6 0 0 0 12 21a6 6 0 0 0 5.19-3H20v-2h-2.09c.05-.33.09-.66.09-1v-1h2v-2h-2v-1c0-.34-.04-.67-.09-1H20V8zm-6 8h-4v-2h4v2zm0-4h-4v-2h4v2z"));
        links.Children.Add(Link("Discord server", "https://discord.gg/euqRzjQKvc", "M20.317 4.37a19.79 19.79 0 0 0-4.885-1.515.07.07 0 0 0-.079.037c-.21.375-.444.865-.608 1.25a18.27 18.27 0 0 0-5.487 0 12.64 12.64 0 0 0-.618-1.25.08.08 0 0 0-.078-.037 19.74 19.74 0 0 0-4.885 1.515.07.07 0 0 0-.032.028C.533 9.046-.319 13.58.099 18.058a.08.08 0 0 0 .031.056c2.053 1.508 4.041 2.423 5.993 3.03a.08.08 0 0 0 .084-.028c.462-.63.873-1.295 1.226-1.994a.08.08 0 0 0-.041-.106 13.1 13.1 0 0 1-1.872-.892.08.08 0 0 1-.008-.128c.126-.094.252-.192.372-.292a.07.07 0 0 1 .078-.01c3.927 1.793 8.18 1.793 12.061 0a.07.07 0 0 1 .079.01c.12.099.246.198.373.292a.08.08 0 0 1-.007.128 12.3 12.3 0 0 1-1.873.892.08.08 0 0 0-.041.107c.36.698.772 1.363 1.225 1.993a.08.08 0 0 0 .084.029c1.961-.607 3.95-1.522 6.002-3.03a.08.08 0 0 0 .031-.055c.5-5.177-.838-9.674-3.548-13.66a.06.06 0 0 0-.031-.029zM8.02 15.331c-1.183 0-2.157-1.086-2.157-2.419s.956-2.419 2.157-2.419c1.211 0 2.176 1.095 2.157 2.419 0 1.333-.956 2.419-2.157 2.419zm7.975 0c-1.183 0-2.157-1.086-2.157-2.419s.955-2.419 2.157-2.419c1.211 0 2.176 1.095 2.157 2.419 0 1.333-.946 2.419-2.157 2.419z"));
        links.Children.Add(Link("GitHub", "https://github.com/DryreL/GOW3_Jacinto_Launcher", "M12 .297c-6.63 0-12 5.373-12 12 0 5.303 3.438 9.8 8.205 11.385.6.113.82-.258.82-.577 0-.285-.01-1.04-.015-2.04-3.338.724-4.042-1.61-4.042-1.61-.546-1.385-1.335-1.755-1.335-1.755-1.087-.744.084-.729.084-.729 1.205.084 1.838 1.236 1.838 1.236 1.07 1.835 2.809 1.305 3.495.998.108-.776.417-1.305.76-1.605-2.665-.3-5.466-1.332-5.466-5.93 0-1.31.465-2.38 1.235-3.22-.135-.303-.54-1.523.105-3.176 0 0 1.005-.322 3.3 1.23.96-.267 1.98-.399 3-.405 1.02.006 2.04.138 3 .405 2.28-1.552 3.285-1.23 3.285-1.23.645 1.653.24 2.873.12 3.176.765.84 1.23 1.91 1.23 3.22 0 4.61-2.805 5.625-5.475 5.92.42.36.81 1.096.81 2.22 0 1.606-.015 2.896-.015 3.286 0 .315.21.69.825.57C20.565 22.092 24 17.592 24 12.297c0-6.627-5.373-12-12-12z"));
        panel.Children.Add(Card(L.T("COMMUNITY"), Muted(L.T("Report bugs on Nexus Mods. Join the Discord server to find people to play with.")), links));

        panel.Children.Add(Card(L.T("DISCLAIMER"), Muted(L.T("This project has no connection to Epic Games or The Coalition. It is a volunteer, open source, non-profit community project."))));

        var openLog = new Button { Content = L.T("Open log file"), Margin = new Thickness(0, 0, 10, 0) };
        openLog.Click += (_, _) => LauncherDiagnostics.Open(LauncherDiagnostics.LatestLog() ?? LauncherDiagnostics.DirectoryPath);
        var openFolder = new Button { Content = L.T("Open logs folder") };
        openFolder.Click += (_, _) => LauncherDiagnostics.OpenFolder();
        panel.Children.Add(Card(L.T("DIAGNOSTICS"), Muted(L.T("The launcher writes what it does to a log file. Attach it when you report a bug.")), new WrapPanel { Margin = new Thickness(0, 12, 0, 0), Children = { openLog, openFolder } }));

        var technologies = new StackPanel();
        foreach (var (name, version) in Technologies())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*"), Margin = new Thickness(0, 0, 0, 4) };
            var value = Muted(version);
            Grid.SetColumn(value, 1);
            row.Children.Add(new TextBlock { Text = name, FontSize = 13 });
            row.Children.Add(value);
            if (name == "JacintoNet") { row.Background = Brushes.Transparent; ToolTip.SetTip(row, L.T("To play co-op, both players must have the same JacintoNet version.")); }
            technologies.Children.Add(row);
        }
        panel.Children.Add(Card(L.T("TECHNOLOGIES"), technologies));

        var rows = new StackPanel();
        foreach (var (name, value, ok) in Checks(gameDir, config)) rows.Children.Add(Row(name, value, ok));
        panel.Children.Add(Card(L.T("SYSTEM CHECK"), rows));
        Content = InfoUi.Titled(L.T("ABOUT"), panel);
    }

    private Button Link(string text, string url, string icon)
    {
        var content = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { new PathIcon { Width = 15, Height = 15, Data = StreamGeometry.Parse(icon) }, new TextBlock { Text = L.T(text), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } };
        var button = new Button { Content = content, Margin = new Thickness(0, 0, 10, 8) };
        Avalonia.Automation.AutomationProperties.SetName(button, L.T(text));
        button.Click += async (_, _) => { if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri(url)); };
        return button;
    }

    // Read from the running libraries, so the list cannot go stale. Steamworks is built into JacintoNet, and DXVK is downloaded only if it is turned on.
    private static (string Name, string Version)[] Technologies()
    {
        static string Library(string assembly)
        {
            var loaded = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == assembly);
            var version = loaded?.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false) is [System.Reflection.AssemblyInformationalVersionAttribute info, ..] ? info.InformationalVersion.Split('+')[0] : loaded?.GetName().Version?.ToString(3);
            return version ?? "-";
        }
        return new[]
        {
            (".NET", Environment.Version.ToString()), ("Avalonia UI", Library("Avalonia.Base")), ("SkiaSharp", Library("SkiaSharp")), ("HarfBuzzSharp", Library("HarfBuzzSharp")),
            ("Sentry", Library("Sentry")), ("Aptabase", "HTTP API v0"), ("Steamworks SDK", "1.65"), ("JacintoNet", JacintoNetVersion), ("DXVK", Dxvk.Version), ("Unreal Engine 3", "Gears of War 3 Jacinto 1.1.1")
        };
    }

    private static (string Name, string Value, bool? Ok)[] Checks(string? gameDir, LauncherConfig config)
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var list = new System.Collections.Generic.List<(string, string, bool?)>
        {
            (L.T("Operating system"), System.Runtime.InteropServices.RuntimeInformation.OSDescription, !OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10)),
            (L.T("64-bit system"), System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), Environment.Is64BitOperatingSystem),
            (L.T("Memory (4 GB or more)"), $"{memory / (double)Gigabyte:0.#} GB", memory >= 4 * Gigabyte - Gigabyte / 2),
            (L.T("Game folder"), gameDir ?? L.T("Game installation not found"), gameDir is not null)
        };
        if (gameDir is not null)
        {
            var free = JacintoLauncher.MultipatchInstaller.FreeBytes(gameDir);
            list.Add((L.T("Free space (2 GB or more)"), $"{free / (double)Gigabyte:0.#} GB", free >= 2 * Gigabyte));
            var missing = GameRunner.MissingDirectX(gameDir, config);
            list.Add((L.T("DirectX June 2010 files"), missing is null ? L.T("Found") : missing, missing is null));
        }
        if (GameRunner.NeedsCompatibilityLayer)
        {
            var layer = GameRunner.FindLayer(config);
            list.Add((L.T("Proton, umu-launcher or Wine"), layer?.Path ?? L.T("Not found"), layer is not null));
        }
        var steam = GameRunner.IsSteamClientRunning();
        list.Add((L.T("Steam (only for Play on Steam)"), steam ? L.T("Running") : L.T("Not running"), steam ? true : null));
        return list.ToArray();
    }

    private static Control Row(string name, string value, bool? ok)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,260,*"), Margin = new Thickness(0, 0, 0, 8) };
        var mark = new TextBlock { Text = ok == true ? "✓" : ok == false ? "✗" : "–", FontWeight = FontWeight.Bold };
        if (ok == false) mark.Foreground = Brushes.OrangeRed; else if (ok == true) mark.Foreground = new SolidColorBrush(Color.Parse("#2FA84F"));
        var label = new TextBlock { Text = name };
        var detail = Muted(value);
        Grid.SetColumn(label, 1); Grid.SetColumn(detail, 2);
        grid.Children.Add(mark); grid.Children.Add(label); grid.Children.Add(detail);
        return grid;
    }

    private static TextBlock Muted(string text) { var block = new TextBlock { Text = text }; block.Classes.Add("muted"); return block; }

    private static Control Card(string title, params Control[] children)
    {
        var heading = new TextBlock { Text = title };
        heading.Classes.Add("h2");
        var stack = new StackPanel();
        stack.Children.Add(heading);
        stack.Children.AddRange(children);
        var card = new Border { Child = stack };
        card.Classes.Add("card");
        return card;
    }
}

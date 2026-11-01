#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace JacintoLauncher;

/// <summary>A page of text shipped inside the launcher: Credits, FAQ and Changelog. Lines starting with a known nickname link to that person's profile.</summary>
internal sealed class TextPage : UserControl
{
    private static readonly Dictionary<string, string> Profiles = new(StringComparer.Ordinal)
    {
        ["DryreL"] = "DryreL", ["refresh50"] = "refresh50", ["wuxinwei110"] = "wuxinwei110", ["LukeBoya"] = "lukeboya", ["Grilex"] = "grilex", ["pasha12071991"] = "pasha12071991", ["HJ"] = "Hpapa", ["Dlposn"] = "Dlposn", ["Rabbit77rage"] = "Rabbit77rage"
    };

    internal TextPage(string title, string resource)
    {
        using var stream = AssetLoader.Open(new Uri("avares://GoW3Jacinto_Launcher/" + resource));
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd().Replace("\r\n", "\n");
        var panel = new StackPanel();
        if (resource.Contains("faq", StringComparison.Ordinal))
            foreach (var entry in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)) panel.Children.Add(FaqEntry(entry));
        else
        {
            var body = new StackPanel();
            if (resource.Contains("credits", StringComparison.Ordinal))
                foreach (var line in text.Split('\n')) body.Children.Add(CreditLine(line));
            else AddChangelog(body, text);
            var card = new Border { Child = body };
            card.Classes.Add("card");
            panel.Children.Add(card);
        }
        Content = InfoUi.Titled(L.T(title), panel);
    }

    private static readonly System.Text.RegularExpressions.Regex VersionLine = new(@"(?m)^(v\d+\.\d+(?:\.\d+)?)(?: \((.+)\))?\r?$");

    // A heading in the theme colour for each version, with the newest one starred.
    private static void AddChangelog(StackPanel panel, string text)
    {
        var versions = VersionLine.Matches(text);
        void Body(int from, int to)
        {
            var part = text[from..to].Trim();
            if (part.Length != 0) panel.Children.Add(new SelectableTextBlock { Text = part, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20, Margin = new Thickness(0, 0, 0, 18) });
        }
        Body(0, versions.Count == 0 ? text.Length : versions[0].Index);
        for (var i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 0, 0, 8) };
            if (i == 0) row.Children.Add(new PathIcon { Width = 16, Height = 16, Foreground = new Avalonia.Media.SolidColorBrush(Color.Parse("#F5B301")), Data = StreamGeometry.Parse("M12 17.27 18.18 21l-1.64-7.03L22 9.24l-7.19-.61L12 2 9.19 8.63 2 9.24l5.46 4.73L5.82 21z") });
            var name = new TextBlock { Text = version.Groups[1].Value, FontSize = 17, FontWeight = FontWeight.Bold };
            name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable("AccentTextBrush"));
            row.Children.Add(name);
            if (version.Groups[2].Success)
            {
                var date = new TextBlock { Text = version.Groups[2].Value, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
                date.Classes.Add("muted");
                row.Children.Add(date);
            }
            panel.Children.Add(row);
            Body(version.Index + version.Length, i + 1 < versions.Count ? versions[i + 1].Index : text.Length);
        }
    }

    // A section title alone, or a question that opens to show its answer.
    private static Control FaqEntry(string entry)
    {
        var lines = entry.Trim().Split('\n', 2);
        if (lines.Length == 1)
        {
            var section = new TextBlock { Text = L.T(lines[0]), Margin = new Thickness(0, 8, 0, 12) };
            section.Classes.Add("h2");
            return section;
        }
        var answer = new SelectableTextBlock { Text = L.T(lines[1]), TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20 };
        return new Expander { Header = new TextBlock { Text = L.T(lines[0]), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, Content = answer };
    }

    private Control CreditLine(string line)
    {
        var parts = line.Split(" - ", 2);
        if (parts.Length != 2 || !Profiles.TryGetValue(parts[0], out var profile))
        {
            var plain = new TextBlock { Text = line.Length == 0 ? " " : L.T(line), FontSize = 13, TextWrapping = TextWrapping.Wrap, MinHeight = 20, FontWeight = line == line.ToUpperInvariant() || line.EndsWith(':') ? FontWeight.Bold : FontWeight.Normal };
            if (line.StartsWith("You never need to donate", StringComparison.Ordinal)) plain.Classes.Add("muted");
            return plain;
        }
        var link = new Button { Content = parts[0] };
        link.Classes.Add("link");
        link.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) await top.Launcher.LaunchUriAsync(new Uri("https://www.nexusmods.com/profile/" + profile));
        };
        var row = new DockPanel();
        DockPanel.SetDock(link, Dock.Left);
        row.Children.Add(link);
        row.Children.Add(new TextBlock { Text = "- " + L.T(parts[1]), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        return row;
    }
}

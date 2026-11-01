#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace JacintoLauncher;

/// <summary>
/// Launcher interface language (the game language is not touched). English text is the key; the
/// table lives in LocalizationStrings.cs. Missing entries fall back to English.
/// </summary>
internal static class L
{
    internal sealed record Language(string Code, string NativeName);
    internal static readonly Language[] Languages =
    {
        new("en", "English"), new("pt", "Português (Brasil)"), new("tr", "Türkçe"), new("es", "Español"),
        new("it", "Italiano"), new("de", "Deutsch"), new("ja", "日本語"), new("ru", "Русский")
    };
    // Column order of LocalizationStrings.Table after the English key.
    private static readonly string[] Columns = { "es", "it", "de", "ja", "pt", "tr", "ru" };
    private static readonly Dictionary<string, Dictionary<string, string>> Table = Load();
    internal static string Current { get; private set; } = "en";

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        var table = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var raw in LocalizationStrings.Table.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var cells = line.Split('|');
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < Columns.Length && i + 1 < cells.Length; i++)
                if (cells[i + 1].Length != 0) row[Columns[i]] = cells[i + 1].Replace("\\n", "\n");
            table[cells[0].Replace("\\n", "\n")] = row;
        }
        return table;
    }

    /// <summary>Saved choice, or the Windows display language when it is one of the launcher languages.</summary>
    internal static void Initialize(string? configured)
    {
        var code = string.IsNullOrWhiteSpace(configured) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : configured!;
        Current = Languages.Any(l => l.Code == code) ? code : "en";
    }
    internal static void Set(string code) => Current = Languages.Any(l => l.Code == code) ? code : "en";

    internal static string T(string english) =>
        Current != "en" && Table.TryGetValue(english, out var row) && row.TryGetValue(Current, out var text) ? text : english;
    internal static string F(string english, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    // Original English of each translated property, and the last value written, so a language change
    // re-translates static text but leaves text that the window code has replaced since.
    private sealed class Memory { internal readonly Dictionary<string, (string English, string Applied)> Values = new(); }
    private static readonly ConditionalWeakTable<object, Memory> Originals = new();

    /// <summary>Translates the static text of a window (titles, labels, buttons, check boxes, list items, tooltips, grid headers).</summary>
    internal static void Apply(DependencyObject root)
    {
        if (root is Window window) window.Title = Translate(window, "Title", window.Title);
        foreach (var item in Walk(root))
        {
            switch (item)
            {
                case TextBlock text when text.Inlines.Count <= 1 && !string.IsNullOrEmpty(text.Text):
                    text.Text = Translate(text, "Text", text.Text); break;
                case ContentControl control when control.Content is string content:
                    control.Content = Translate(control, "Content", content); break;
            }
            if (item is FrameworkElement element && element.ToolTip is string tip) element.ToolTip = Translate(element, "ToolTip", tip);
            if (item is ListView { View: GridView grid })
                foreach (var column in grid.Columns) if (column.Header is string header) column.Header = Translate(column, "Header", header);
        }
    }

    private static string Translate(object owner, string property, string current)
    {
        var memory = Originals.GetOrCreateValue(owner);
        if (memory.Values.TryGetValue(property, out var known) && current != known.Applied) return current; // replaced by code
        var english = memory.Values.TryGetValue(property, out known) ? known.English : current;
        var text = T(english);
        memory.Values[property] = (english, text);
        return text;
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var node = pending.Pop(); yield return node;
            foreach (var child in LogicalTreeHelper.GetChildren(node)) if (child is DependencyObject d) pending.Push(d);
        }
    }
}

#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace JacintoLauncher;

/// <summary>
/// Launcher interface language (the game language is not touched). English text is the key; the
/// table is languages.csv. Missing entries fall back to English.
/// </summary>
internal static class L
{
    internal sealed record Language(string Code, string NativeName);
    internal const string ExternalFile = "languages.csv";
    private static readonly Dictionary<string, Dictionary<string, string>> Table = new(StringComparer.Ordinal);
    internal static readonly Language[] Languages = Load();
    internal static string Current { get; private set; } = "en";

    // The table ships inside the launcher. A languages.csv beside it is read on top, so anyone can fix a text or add a language by adding a column.
    private static Language[] Load()
    {
        var languages = new List<Language>();
        using (var shipped = typeof(L).Assembly.GetManifestResourceStream(ExternalFile))
            if (shipped is not null) using (var reader = new StreamReader(shipped)) Merge(reader.ReadToEnd(), languages);
        var external = Path.Combine(AppContext.BaseDirectory, ExternalFile);
        try { if (File.Exists(external)) Merge(File.ReadAllText(external), languages); }
        catch (IOException exception) { LauncherDiagnostics.Info("languages.csv could not be read: " + exception.Message); }
        return languages.Count == 0 ? new[] { new Language("en", "English") } : languages.ToArray();
    }

    private static void Merge(string csv, List<Language> languages)
    {
        var rows = ParseCsv(csv);
        // Columns: key, en, then one per language. The key names the line for translators; a table without it still loads.
        if (rows.Count != 0 && rows[0].Count != 0 && rows[0][0] == "key")
            foreach (var row in rows.Where(r => r.Count != 0)) row.RemoveAt(0);
        if (rows.Count < 2 || rows[0].Count == 0 || rows[0][0] != "en") return;
        var codes = rows[0];
        for (var c = 0; c < codes.Count; c++)
        {
            var name = c < rows[1].Count && rows[1][c].Length != 0 ? rows[1][c] : codes[c];
            var known = languages.FindIndex(l => l.Code == codes[c]);
            if (known >= 0) languages[known] = new Language(codes[c], name); else if (codes[c].Length != 0) languages.Add(new Language(codes[c], name));
        }
        foreach (var cells in rows.Skip(2))
        {
            if (cells.Count == 0 || cells[0].Length == 0) continue;
            var key = cells[0].Replace("\\n", "\n");
            if (!Table.TryGetValue(key, out var row)) Table[key] = row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var c = 1; c < cells.Count && c < codes.Count; c++)
                if (cells[c].Length != 0) row[codes[c]] = cells[c].Replace("\\n", "\n");
        }
    }

    // RFC 4180: quotes around a cell, doubled quotes inside. A spreadsheet saves it this way.
    internal static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        text = text.TrimStart('\uFEFF');
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"') cell.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else if (c != '\r') cell.Append(c);
        }
        if (cell.Length != 0 || row.Count != 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    /// <summary>Saved choice, or the Windows display language when it is one of the launcher languages.</summary>
    internal static void Initialize(string? configured)
    {
        // Linux builds run in invariant globalization mode, where the culture says nothing: read LANG.
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (system == "iv") system = (Environment.GetEnvironmentVariable("LC_ALL") is { Length: > 0 } all ? all : Environment.GetEnvironmentVariable("LANG") ?? "en").Split('_', '.')[0];
        var code = string.IsNullOrWhiteSpace(configured) ? system : configured!;
        Current = Languages.Any(l => l.Code == code) ? code : "en";
    }
    internal static void Set(string code) => Current = Languages.Any(l => l.Code == code) ? code : "en";

    internal static string T(string english) =>
        Current != "en" && Table.TryGetValue(english, out var row) && row.TryGetValue(Current, out var text) ? text : english;
    /// <summary>The English key of an interface text in the current language; null for text the table does not hold, such as a name.</summary>
    internal static string? EnglishOf(string text) =>
        Table.ContainsKey(text) ? text : Table.FirstOrDefault(row => row.Value.TryGetValue(Current, out var shown) && shown == text).Key;
    internal static string F(string english, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    // Original English of each translated property, and the last value written, so a language change
    // re-translates static text but leaves text that the window code has replaced since.
    private sealed class Memory { internal readonly Dictionary<string, (string English, string Applied)> Values = new(); }
    private static readonly ConditionalWeakTable<object, Memory> Originals = new();

    /// <summary>Translates the static text of a window or page (titles, labels, buttons, check boxes, list items, tooltips).</summary>
    internal static void Apply(ILogical root)
    {
        if (root is Window { Title: { } title } window) window.Title = Translate(window, "Title", title);
        foreach (var item in Walk(root))
        {
            switch (item)
            {
                case TextBlock text when !string.IsNullOrEmpty(text.Text):
                    text.Text = Translate(text, "Text", text.Text); break;
                case ContentControl control when control.Content is string content:
                    control.Content = Translate(control, "Content", content); break;
            }
            if (item is Control element && ToolTip.GetTip(element) is string tip) ToolTip.SetTip(element, Translate(element, "ToolTip", tip));
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

    private static IEnumerable<ILogical> Walk(ILogical root)
    {
        var pending = new Stack<ILogical>(); pending.Push(root);
        while (pending.Count != 0)
        {
            var node = pending.Pop(); yield return node;
            foreach (var child in node.LogicalChildren) pending.Push(child);
        }
    }
}

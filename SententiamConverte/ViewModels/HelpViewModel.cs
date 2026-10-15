using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SententiamConverte.ViewModels;

/// <summary>Ein Baustein eines Hilfekapitels: Überschrift, Absatz, Aufzählungspunkt, Tabellenzeile oder Code.</summary>
public sealed record HelpBlock(string Kind, string Text, string Key = "")
{
    public bool IsHeading => Kind == "h";
    public bool IsParagraph => Kind == "p";
    public bool IsBullet => Kind == "li";
    public bool IsRow => Kind == "row";
    public bool IsHeaderRow => Kind == "th";
    public bool IsCode => Kind == "code";
}

public sealed class HelpChapter
{
    public required string Title { get; init; }
    public required string Group { get; init; }
    public required List<HelpBlock> Blocks { get; init; }
    public string SearchText => _search ??= Title + "\n" + string.Join("\n", Blocks.Select(b => b.Key + " " + b.Text));
    private string? _search;
}

/// <summary>
/// Reiter „Hilfe“: Handbuch, API-Referenz und Anleitung für Erweiterungen – dieselben Texte wie im Ordner docs,
/// ins Programm eingebettet und mit einfacher Volltextsuche.
/// </summary>
public partial class HelpViewModel : ViewModelBase
{
    private readonly List<HelpChapter> _all;

    public HelpViewModel()
    {
        var en = Services.L.Language == "en";
        var developers = Services.L.T("Für Entwickler");
        _all = (en ? Load("Hilfe.en.Manual.md", "Manual", splitChapters: true) : Load("Hilfe.Handbuch.md", "Handbuch", splitChapters: true))
            .Concat(Load(en ? "Hilfe.en.API-Reference.md" : "Hilfe.API-Referenz.md", developers, splitChapters: false))
            .Concat(Load(en ? "Hilfe.en.Developing-Extensions.md" : "Hilfe.Erweiterungen-entwickeln.md", developers, splitChapters: false))
            .ToList();
        // Fehlt eine Übersetzung, gilt der deutsche Text
        if (_all.Count == 0 && en)
            _all = Load("Hilfe.Handbuch.md", "Handbuch", true).Concat(Load("Hilfe.API-Referenz.md", developers, false))
                .Concat(Load("Hilfe.Erweiterungen-entwickeln.md", developers, false)).ToList();
        foreach (var c in _all) Chapters.Add(c);
        Selected = Chapters.FirstOrDefault();
    }

    public ObservableCollection<HelpChapter> Chapters { get; } = new();

    [ObservableProperty] public partial HelpChapter? Selected { get; set; }
    [ObservableProperty] public partial string Query { get; set; } = "";

    public string ResultInfo => Query.Trim().Length == 0 ? "" : Chapters.Count == 0 ? Services.L.T("Nichts gefunden.") : Services.L.T($"{Chapters.Count} Kapitel gefunden.");

    partial void OnQueryChanged(string value)
    {
        var q = value.Trim();
        var keep = Selected;
        Chapters.Clear();
        foreach (var c in _all.Where(c => q.Length == 0 || c.SearchText.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            Chapters.Add(c);
        Selected = keep != null && Chapters.Contains(keep) ? keep : Chapters.FirstOrDefault();
        OnPropertyChanged(nameof(ResultInfo));
    }

    /// <summary>Öffnet das erste Kapitel, dessen Titel den Text enthält.</summary>
    public void Show(string title)
    {
        Query = "";
        Selected = _all.FirstOrDefault(c => c.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) ?? Selected;
    }

    private static IEnumerable<HelpChapter> Load(string resource, string group, bool splitChapters)
    {
        using var stream = typeof(HelpViewModel).Assembly.GetManifestResourceStream(resource);
        if (stream == null) yield break;
        var text = new StreamReader(stream, Encoding.UTF8).ReadToEnd().Replace("\r\n", "\n");
        foreach (var c in Parse(text, group, splitChapters)) yield return c;
    }

    /// <summary>Kleiner Markdown-Leser für die eigenen Hilfetexte (Überschriften, Absätze, Listen, Tabellen, Code).</summary>
    internal static List<HelpChapter> Parse(string markdown, string group, bool splitChapters)
    {
        var chapters = new List<HelpChapter>();
        string? docTitle = null;
        var title = "";
        var blocks = new List<HelpBlock>();
        var paragraph = new StringBuilder();
        var lines = markdown.Split('\n');

        void FlushParagraph()
        {
            if (paragraph.Length > 0) blocks.Add(new HelpBlock("p", Inline(paragraph.ToString())));
            paragraph.Clear();
        }

        void FlushChapter()
        {
            FlushParagraph();
            if (blocks.Count > 0) chapters.Add(new HelpChapter { Title = title.Length > 0 ? title : Services.L.T("Willkommen"), Group = group, Blocks = blocks });
            blocks = new List<HelpBlock>();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```"))
            {
                FlushParagraph();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].StartsWith("```"); i++) code.Add(lines[i]);
                blocks.Add(new HelpBlock("code", string.Join("\n", code)));
                continue;
            }
            if (line.StartsWith("# "))
            {
                FlushParagraph();
                docTitle = line[2..].Trim();
                if (!splitChapters) title = docTitle;
                continue;
            }
            if (line.StartsWith("## "))
            {
                if (splitChapters)
                {
                    FlushChapter();
                    title = line[3..].Trim();
                }
                else
                {
                    FlushParagraph();
                    blocks.Add(new HelpBlock("h", Inline(line[3..].Trim())));
                }
                continue;
            }
            if (line.StartsWith("### "))
            {
                FlushParagraph();
                blocks.Add(new HelpBlock("h", Inline(line[4..].Trim())));
                continue;
            }
            if (line.StartsWith('|'))
            {
                FlushParagraph();
                var header = true;
                for (; i < lines.Length && lines[i].StartsWith('|'); i++)
                {
                    var cells = SplitRow(lines[i]);
                    if (cells.All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"))) continue;
                    var value = Inline(cells[^1]);
                    var key = Inline(string.Join("  ", cells.Take(cells.Count - 1).Where(c => c.Length > 0)));
                    blocks.Add(new HelpBlock(header ? "th" : "row", value, key));
                    header = false;
                }
                i--;
                continue;
            }
            var bullet = Regex.Match(line, @"^(\s*)(-|\d+\.)\s+(.*)$");
            if (bullet.Success)
            {
                FlushParagraph();
                var marker = bullet.Groups[2].Value == "-" ? "•" : bullet.Groups[2].Value;
                var item = new StringBuilder(bullet.Groups[3].Value);
                while (i + 1 < lines.Length && lines[i + 1].StartsWith("  ") && !Regex.IsMatch(lines[i + 1], @"^\s*(-|\d+\.)\s"))
                    item.Append(' ').Append(lines[++i].Trim());
                blocks.Add(new HelpBlock("li", Inline(item.ToString()), marker));
                continue;
            }
            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }
            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(line.Trim());
        }
        FlushChapter();
        return chapters;
    }

    private static List<string> SplitRow(string row)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var inCode = false;
        foreach (var ch in row.Trim().Trim('|'))
        {
            if (ch == '`') inCode = !inCode;
            if (ch == '|' && !inCode)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else current.Append(ch);
        }
        cells.Add(current.ToString().Trim());
        return cells;
    }

    /// <summary>Fett, Code und Verweise in reinen Text umsetzen.</summary>
    private static string Inline(string s)
    {
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)]+)\)", m => m.Groups[2].Value.StartsWith("http") ? $"{m.Groups[1].Value} ({m.Groups[2].Value})" : m.Groups[1].Value);
        s = s.Replace("**", "");
        s = Regex.Replace(s, "`([^`]*)`", "$1");
        return s.Trim();
    }
}

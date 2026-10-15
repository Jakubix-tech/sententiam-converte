using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace SententiamConverte.Services;

/// <summary>
/// Sprache der Oberfläche. Der Programmtext ist Deutsch und dient zugleich als Schlüssel; die englische Tabelle
/// (Strings.en.json) ordnet jedem deutschen Text die Übersetzung zu. Meldungen mit Werten stehen dort als Vorlage
/// mit {0}, {1} …, z. B. „Projekt „{0}“ angelegt.“ → „Project “{0}” created.“.
/// </summary>
public static class L
{
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = [("de", "Deutsch"), ("en", "English")];

    public static string Language { get; private set; } = "de";

    private static Dictionary<string, string> _exact = new();
    private static List<(Regex Pattern, string Target)> _templates = new();
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    /// <summary>„de“, „en“ oder leer (= Umgebungsvariable SENTENTIAM_LANG, sonst Systemsprache).</summary>
    public static void SetLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) code = Environment.GetEnvironmentVariable("SENTENTIAM_LANG");
        if (string.IsNullOrWhiteSpace(code)) code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de" ? "de" : "en";
        Language = code.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "de";
        Cache.Clear();
        if (Language == "de")
        {
            _exact = new();
            _templates = new();
            return;
        }
        var table = Table(Language);
        // Texte ohne {0}-Platzhalter (auch solche mit anderen geschweiften Klammern) werden exakt nachgeschlagen
        _exact = table.Where(kv => !Regex.IsMatch(kv.Key, @"\{\d+\}")).ToDictionary(kv => kv.Key, kv => kv.Value);
        _templates = table.Where(kv => Regex.IsMatch(kv.Key, @"\{\d+\}"))
            .OrderByDescending(kv => Regex.Replace(kv.Key, @"\{\d+\}", "").Length)       // spezifischere Vorlagen zuerst
            .Select(kv => (Pattern(kv.Key), kv.Value)).ToList();
    }

    /// <summary>Übersetzt einen deutschen Programmtext (auch fertig zusammengesetzte Meldungen).</summary>
    public static string T(string german)
    {
        if (Language == "de" || string.IsNullOrEmpty(german)) return german;
        if (_exact.TryGetValue(german, out var exact)) return exact;
        if (Cache.TryGetValue(german, out var cached)) return cached;
        foreach (var (pattern, target) in _templates)
        {
            var m = pattern.Match(german);
            if (!m.Success) continue;
            var result = Regex.Replace(target, @"\{(\d+)\}", x => m.Groups["p" + x.Groups[1].Value] is { Success: true } g ? T(g.Value) : x.Value);
            if (Cache.Count < 10000) Cache[german] = result;
            return result;
        }
        return german;
    }

    /// <summary>Mehrzeilige Meldungen (Protokolle, Ergebnisse) zeilenweise übersetzen.</summary>
    public static string Lines(string text) =>
        Language == "de" || text.Length == 0 ? text : text.Contains('\n') ? string.Join("\n", text.Split('\n').Select(T)) : T(text);

    /// <summary>Für Bindungen an Meldungen aus dem Programm: <c>{Binding Status, Converter={x:Static l:L.Text}}</c>.</summary>
    public static IValueConverter Text { get; } = new FuncValueConverter<object?, object?>(v => v is string s ? Lines(s) : v);

    /// <summary>Alle Einträge einer Sprachtabelle (für Tests und Werkzeuge).</summary>
    public static Dictionary<string, string> Table(string language)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream($"SententiamConverte.Strings.{language}.json");
        if (stream == null) return new();
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }

    private static Regex Pattern(string key)
    {
        var parts = Regex.Split(key, @"(\{\d+\})");
        var rx = string.Concat(parts.Select(p => Regex.Match(p, @"^\{(\d+)\}$") is { Success: true } m
            ? $"(?<p{m.Groups[1].Value}>.*?)"
            : Regex.Escape(p)));
        return new Regex("^" + rx + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }
}

/// <summary>XAML: <c>Text="{l:T 'Neues Projekt'}"</c> – liefert den Text in der eingestellten Sprache.</summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Key);
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using SententiamConverte.Core.Model;
using SententiamConverte.Services;

namespace SententiamConverte.Controls;

/// <summary>Eine Sprache in der Auswahl: Name in der Oberflächensprache, Eigenname und Code.</summary>
public sealed record LanguageOption(string Code, string Name, string Native)
{
    public string Display => $"{Name} · {Code}";
    public override string ToString() => Display;

    public bool Matches(string text)
    {
        text = text.Trim();
        return text.Length == 0
               || Name.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || Native.Contains(text, StringComparison.CurrentCultureIgnoreCase)
               || Code.StartsWith(text, StringComparison.OrdinalIgnoreCase);
    }

    private static (string Language, List<LanguageOption> List)? _all;

    /// <summary>Alle bekannten Sprachen, nach Namen sortiert (Namen in der Sprache der Oberfläche).</summary>
    public static IReadOnlyList<LanguageOption> All
    {
        get
        {
            if (_all is { } cached && cached.Language == L.Language) return cached.List;
            var list = Languages.Common
                .Select(l => new LanguageOption(l.Code, LocalName(l.Code, l.Name), NativeName(l.Code)))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            _all = (L.Language, list);
            return list;
        }
    }

    /// <summary>Name der Hauptsprache ohne Region in der Sprache der Oberfläche („de“ → „Deutsch“ / „German“ / „niemiecki“).</summary>
    public static string PrimaryName(string code)
    {
        var option = All.FirstOrDefault(o => Core.Model.Languages.Matches(o.Code, code));
        return option == null ? code : System.Text.RegularExpressions.Regex.Replace(option.Name, @"\s*\(.*\)$", "");
    }

    public static LanguageOption For(string code) =>
        All.FirstOrDefault(c => c.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
        ?? new LanguageOption(code, Languages.DisplayName(code), NativeName(code));

    private static string LocalName(string code, string german) => L.Language switch
    {
        "de" => german,
        "pl" => Polish.GetValueOrDefault(code) ?? English(code, german),
        _ => English(code, german),
    };

    /// <summary>Polnische Sprachnamen (die Systemdaten liefern sie nicht verlässlich).</summary>
    private static readonly Dictionary<string, string> Polish = new()
    {
        ["de-DE"] = "niemiecki (Niemcy)", ["de-AT"] = "niemiecki (Austria)", ["de-CH"] = "niemiecki (Szwajcaria)", ["en-GB"] = "angielski (Wielka Brytania)",
        ["en-US"] = "angielski (USA)", ["pl-PL"] = "polski", ["fr-FR"] = "francuski", ["it-IT"] = "włoski",
        ["es-ES"] = "hiszpański", ["pt-PT"] = "portugalski", ["pt-BR"] = "portugalski (Brazylia)", ["nl-NL"] = "niderlandzki",
        ["cs-CZ"] = "czeski", ["sk-SK"] = "słowacki", ["hu-HU"] = "węgierski", ["ro-RO"] = "rumuński",
        ["uk-UA"] = "ukraiński", ["ru-RU"] = "rosyjski", ["sv-SE"] = "szwedzki", ["da-DK"] = "duński",
        ["nb-NO"] = "norweski", ["fi-FI"] = "fiński", ["tr-TR"] = "turecki", ["el-GR"] = "grecki",
        ["la"] = "łacina", ["zh-CN"] = "chiński (uproszczony)", ["ja-JP"] = "japoński", ["ko-KR"] = "koreański",
        ["ar-SA"] = "arabski", ["en-IE"] = "angielski (Irlandia)", ["en-AU"] = "angielski (Australia)", ["en-CA"] = "angielski (Kanada)",
        ["fr-BE"] = "francuski (Belgia)", ["fr-CH"] = "francuski (Szwajcaria)", ["fr-CA"] = "francuski (Kanada)", ["it-CH"] = "włoski (Szwajcaria)",
        ["es-MX"] = "hiszpański (Meksyk)", ["nl-BE"] = "niderlandzki (Belgia)", ["bg-BG"] = "bułgarski", ["hr-HR"] = "chorwacki",
        ["sr-RS"] = "serbski", ["sl-SI"] = "słoweński", ["bs-BA"] = "bośniacki", ["mk-MK"] = "macedoński",
        ["sq-AL"] = "albański", ["be-BY"] = "białoruski", ["lt-LT"] = "litewski", ["lv-LV"] = "łotewski",
        ["et-EE"] = "estoński", ["nn-NO"] = "norweski (nynorsk)", ["is-IS"] = "islandzki", ["ga-IE"] = "irlandzki",
        ["mt-MT"] = "maltański", ["cy-GB"] = "walijski", ["eu-ES"] = "baskijski", ["ca-ES"] = "kataloński",
        ["gl-ES"] = "galicyjski", ["lb-LU"] = "luksemburski", ["he-IL"] = "hebrajski", ["fa-IR"] = "perski",
        ["ur-PK"] = "urdu", ["hi-IN"] = "hindi", ["bn-BD"] = "bengalski", ["ta-IN"] = "tamilski",
        ["th-TH"] = "tajski", ["vi-VN"] = "wietnamski", ["id-ID"] = "indonezyjski", ["ms-MY"] = "malajski",
        ["fil-PH"] = "filipiński", ["zh-TW"] = "chiński (tradycyjny)", ["ka-GE"] = "gruziński", ["hy-AM"] = "ormiański",
        ["az-AZ"] = "azerbejdżański", ["kk-KZ"] = "kazachski", ["uz-UZ"] = "uzbecki", ["sw-KE"] = "suahili",
        ["af-ZA"] = "afrikaans", ["am-ET"] = "amharski", ["eo"] = "esperanto", ["rm-CH"] = "retoromański",
    };

    private static string English(string code, string fallback)
    {
        try { return CultureInfo.GetCultureInfo(code).EnglishName; }
        catch (CultureNotFoundException) { return fallback; }
    }

    private static string NativeName(string code)
    {
        try { return CultureInfo.GetCultureInfo(code).NativeName; }
        catch (CultureNotFoundException) { return ""; }
    }
}

/// <summary>
/// Sprachauswahl statt Codes eintippen: Suchfeld mit Namen („Polnisch · pl-PL“), Suche nach Name, Eigenname oder Code.
/// Mit <see cref="Multiple"/> mehrere Sprachen als Marken mit ✕. <see cref="Value"/> ist der Code bzw. die Codes mit
/// Komma – so bleiben die bisherigen Felder der Modelle unverändert. Unbekannte Codes (z. B. „eu“) lassen sich eintippen.
/// </summary>
public sealed partial class LanguagePicker : UserControl
{
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<LanguagePicker, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> MultipleProperty = AvaloniaProperty.Register<LanguagePicker, bool>(nameof(Multiple));

    public static readonly StyledProperty<string?> PlaceholderProperty = AvaloniaProperty.Register<LanguagePicker, string?>(nameof(Placeholder));

    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public bool Multiple { get => GetValue(MultipleProperty); set => SetValue(MultipleProperty, value); }
    public string? Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    private readonly AutoCompleteBox _box;
    private readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    private bool _updating;

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex CodeRegex();

    public LanguagePicker()
    {
        _box = new AutoCompleteBox
        {
            ItemsSource = LanguageOption.All,
            FilterMode = AutoCompleteFilterMode.Custom,
            ItemFilter = (text, item) => item is LanguageOption c && c.Matches(text ?? ""),
            MinimumPrefixLength = 0,
            MinWidth = 160,
            MaxDropDownHeight = 320,
            ValueMemberBinding = new Binding(nameof(LanguageOption.Display)),
        };
        _box.SelectionChanged += (_, _) => Chosen(_box.SelectedItem as LanguageOption);
        // auch Ereignisse, die das Suchfeld selbst schon behandelt hat (Eingabe bei geschlossener Liste)
        _box.AddHandler(InputElement.LostFocusEvent, (_, _) => Commit(), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        _box.GotFocus += (_, _) => { if (Multiple || string.IsNullOrEmpty(_box.Text)) _box.IsDropDownOpen = true; };
        _box.AddHandler(InputElement.KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) Commit(); },
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(_chips);
        panel.Children.Add(_box);
        Content = panel;
        Refresh();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == MultipleProperty) Refresh();
        if (change.Property == PlaceholderProperty || change.Property == MultipleProperty) _box.Watermark = Placeholder ?? (Multiple ? L.T("Sprache hinzufügen …") : L.T("Sprache wählen …"));
    }

    private List<string> Codes() =>
        (Value ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Anzeige an den Wert anpassen.</summary>
    private void Refresh()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            _chips.Children.Clear();
            _chips.IsVisible = Multiple;
            if (Multiple)
            {
                foreach (var code in Codes())
                {
                    var c = LanguageOption.For(code);
                    var remove = new Button { Content = "✕", Padding = new Thickness(4, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), FontSize = 11 };
                    ToolTip.SetTip(remove, L.T("Entfernen"));
                    remove.Click += (_, _) => Value = string.Join(", ", Codes().Where(x => !x.Equals(code, StringComparison.OrdinalIgnoreCase)));
                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 2, 2, 2), Margin = new Thickness(0, 0, 6, 4),
                        BorderThickness = new Thickness(1),
                        Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { new TextBlock { Text = c.Display, VerticalAlignment = VerticalAlignment.Center }, remove } },
                    };
                    chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("HeaderRowBackground"));
                    chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("CardBorder"));
                    _chips.Children.Add(chip);
                }
                _box.Text = "";
            }
            else
            {
                var code = Codes().FirstOrDefault();
                _box.SelectedItem = null;
                _box.Text = code == null ? "" : LanguageOption.For(code).Display;
            }
        }
        finally { _updating = false; }
    }

    private void Chosen(LanguageOption? choice)
    {
        if (_updating || choice == null) return;
        if (Multiple)
        {
            var codes = Codes();
            if (!codes.Contains(choice.Code, StringComparer.OrdinalIgnoreCase)) codes.Add(choice.Code);
            Value = string.Join(", ", codes);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { _updating = true; _box.Text = ""; _box.SelectedItem = null; _updating = false; });
        }
        else Value = choice.Code;
    }

    /// <summary>
    /// Dreibuchstaben-Code (ISO 639-2/3) → Eintrag der Liste: „fra“ → „fr-FR“, „deu-AT“ → „de-AT“. Ohne Region gilt das
    /// Land der Sprache (fr-FR, pl-PL), sonst der erste Eintrag der Sprache. Andere Codes bleiben, wie sie sind.
    /// </summary>
    internal static string ThreeLetterToOption(string text)
    {
        var normalized = Languages.Normalize(text);
        if (normalized.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase) || normalized.Contains('-')) return normalized;
        var same = Languages.Common.Where(l => Languages.Primary(l.Code) == normalized).Select(l => l.Code).ToList();
        return same.FirstOrDefault(c => c.Equals($"{normalized}-{normalized.ToUpperInvariant()}", StringComparison.OrdinalIgnoreCase))
               ?? same.FirstOrDefault() ?? normalized;
    }

    /// <summary>Getippten Text übernehmen: ein Eintrag der Liste oder ein Sprachcode wie „eu“, „pt-BR“ oder „fra“.</summary>
    private void Commit()
    {
        if (_updating) return;
        var text = (_box.Text ?? "").Trim();
        if (text.Length == 0) { if (!Multiple) Refresh(); return; }
        var match = LanguageOption.All.FirstOrDefault(c => c.Display.Equals(text, StringComparison.CurrentCultureIgnoreCase)
                                                           || c.Code.Equals(text, StringComparison.OrdinalIgnoreCase)
                                                           || c.Name.Equals(text, StringComparison.CurrentCultureIgnoreCase));
        var code = match?.Code ?? (CodeRegex().IsMatch(text) ? ThreeLetterToOption(text) : null);
        if (code == null) { Refresh(); return; }
        if (Multiple)
        {
            var codes = Codes();
            if (!codes.Contains(code, StringComparer.OrdinalIgnoreCase)) codes.Add(code);
            Value = string.Join(", ", codes);
            _box.Text = "";
        }
        else
        {
            Value = code;
            Refresh();
        }
    }
}

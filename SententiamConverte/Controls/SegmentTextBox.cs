using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;
using SententiamConverte.Services;

namespace SententiamConverte.Controls;

/// <summary>
/// Textfeld für Quelle und Ziel eines Segments – wie in einer Textverarbeitung: Formatierungen aus dem Original
/// (fett, kursiv, unterstrichen, hoch-/tiefgestellt) werden angezeigt, Tags erscheinen als dezente Marken,
/// Rechtschreibfehler werden beim Tippen rot gewellt unterstrichen (Rechtsklick: Vorschläge), Rückgängig/Wiederholen,
/// Strg+B/I/U setzen die passende Formatierung aus der Quelle.
/// </summary>
public sealed class SegmentTextBox : TextEditor
{
    protected override Type StyleKeyOverride => typeof(TextEditor);

    /// <summary>Der Text mit Tags (zweiseitig bindbar).</summary>
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<SegmentTextBox, string?>(nameof(Value), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Sprache der Rechtschreibprüfung; leer = keine Prüfung.</summary>
    public static readonly StyledProperty<string?> SpellLanguageProperty = AvaloniaProperty.Register<SegmentTextBox, string?>(nameof(SpellLanguage));

    public static readonly StyledProperty<bool> SpellCheckProperty = AvaloniaProperty.Register<SegmentTextBox, bool>(nameof(SpellCheck), true);

    /// <summary>Tag-Daten des Absatzes – daraus folgt, welche Formatierung ein Tag-Paar trägt.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, string>?> TagDataProperty =
        AvaloniaProperty.Register<SegmentTextBox, IReadOnlyDictionary<int, string>?>(nameof(TagData));

    /// <summary>Quelltext des Segments (für Strg+B/I/U: welche Tags gibt es dort).</summary>
    public static readonly StyledProperty<string?> SourceTextProperty = AvaloniaProperty.Register<SegmentTextBox, string?>(nameof(SourceText));

    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string? SpellLanguage { get => GetValue(SpellLanguageProperty); set => SetValue(SpellLanguageProperty, value); }
    public bool SpellCheck { get => GetValue(SpellCheckProperty); set => SetValue(SpellCheckProperty, value); }
    public IReadOnlyDictionary<int, string>? TagData { get => GetValue(TagDataProperty); set => SetValue(TagDataProperty, value); }
    public string? SourceText { get => GetValue(SourceTextProperty); set => SetValue(SourceTextProperty, value); }

    /// <summary>Gefundene Rechtschreibfehler (Positionen im Text mit Tags).</summary>
    public IReadOnlyList<SpellingError> SpellingErrors => _errors;

    private List<SpellingError> _errors = [];
    private bool _updating;
    private readonly DispatcherTimer _spellTimer;

    public SegmentTextBox()
    {
        WordWrap = true;
        ShowLineNumbers = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Options.HighlightCurrentLine = false;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.AllowScrollBelowDocument = false;
        Options.EnableRectangularSelection = false;
        Options.ShowEndOfLine = false;
        TextArea.TextView.LineTransformers.Add(new FormatColorizer(this));
        TextArea.TextView.BackgroundRenderers.Add(new SpellingRenderer(this));
        Document.TextChanged += OnDocumentChanged;
        _spellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _spellTimer.Tick += (_, _) =>
        {
            _spellTimer.Stop();
            RunSpellCheck();
        };
        TextArea.ContextRequested += OnContextRequested;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    // ---------- Texte wie bei einer TextBox ----------

    public int CaretIndex
    {
        get => CaretOffset;
        set => CaretOffset = Math.Clamp(value, 0, Document.TextLength);
    }

    public new string SelectedText => TextArea.Selection.GetText();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            var value = change.GetNewValue<string?>() ?? "";
            if (value == Document.Text) return;
            _updating = true;
            Document.Text = value;
            Document.UndoStack.ClearAll();                  // neuer Segmentinhalt: kein Rückgängig in einen fremden Text
            _updating = false;
            ScheduleSpellCheck(immediate: true);
            TextArea.TextView.Redraw();
        }
        else if (change.Property == SpellLanguageProperty || change.Property == SpellCheckProperty)
            ScheduleSpellCheck(immediate: true);
        else if (change.Property == TagDataProperty)
            TextArea.TextView.Redraw();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        if (!_updating) SetCurrentValue(ValueProperty, Document.Text);
        ScheduleSpellCheck(immediate: false);
    }

    // ---------- Rechtschreibung ----------

    private void ScheduleSpellCheck(bool immediate)
    {
        if (immediate) RunSpellCheck();
        else
        {
            _spellTimer.Stop();
            _spellTimer.Start();
        }
    }

    private void RunSpellCheck()
    {
        var old = _errors;
        _errors = SpellCheck && !IsReadOnly && SpellLanguage is { Length: > 0 } lang && AppServices.Spell.IsAvailable(lang)
            ? AppServices.Spell.CheckTagged(Document.Text, lang)
            : [];
        if (old.Count > 0 || _errors.Count > 0) TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private SpellingError? ErrorAt(int offset) =>
        _errors.FirstOrDefault(e => offset >= e.Index && offset <= e.Index + e.Word.Length);

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var items = new List<Control>();
        if (e.TryGetPosition(TextArea.TextView, out var point) && TextArea.TextView.GetPosition(point + TextArea.TextView.ScrollOffset) is { } pos
            && ErrorAt(Document.GetOffset(pos.Location)) is { } error && SpellLanguage is { } lang)
        {
            var suggestions = AppServices.Spell.Suggest(error.Word, lang);
            foreach (var s in suggestions)
            {
                var item = new MenuItem { Header = s, FontWeight = FontWeight.SemiBold };
                item.Click += (_, _) => Replace(error, s);
                items.Add(item);
            }
            if (suggestions.Count == 0) items.Add(new MenuItem { Header = L.T("Keine Vorschläge"), IsEnabled = false });
            var add = new MenuItem { Header = L.T("Ins Wörterbuch aufnehmen") };
            add.Click += (_, _) =>
            {
                AppServices.Spell.AddToUserDictionary(lang, error.Word);
                RunSpellCheck();
            };
            items.Add(new Separator());
            items.Add(add);
            items.Add(new Separator());
        }
        foreach (var (header, action, enabled) in new (string, Action, bool)[]
                 {
                     (L.T("Ausschneiden"), () => Cut(), !IsReadOnly && TextArea.Selection.Length > 0),
                     (L.T("Kopieren"), () => Copy(), TextArea.Selection.Length > 0),
                     (L.T("Einfügen"), () => Paste(), !IsReadOnly),
                 })
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            items.Add(item);
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(TextArea);
        e.Handled = true;
    }

    private void Replace(SpellingError error, string word)
    {
        if (error.Index + error.Word.Length > Document.TextLength || Document.GetText(error.Index, error.Word.Length) != error.Word) return;
        Document.Replace(error.Index, error.Word.Length, word);
        CaretIndex = error.Index + word.Length;
    }

    // ---------- Formatierung ----------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsReadOnly || !(e.KeyModifiers == KeyModifiers.Control || e.KeyModifiers == KeyModifiers.Meta)) return;
        var format = e.Key switch { Key.B => TextFormat.Bold, Key.I => TextFormat.Italic, Key.U => TextFormat.Underline, _ => TextFormat.None };
        if (format == TextFormat.None) return;
        ApplyFormat(format);
        e.Handled = true;
    }

    /// <summary>
    /// Formatierung wie in einer Textverarbeitung: Die Markierung wird mit dem Tag-Paar der Quelle umschlossen, das
    /// diese Formatierung trägt und im Ziel noch fehlt. Gibt es keines, bleibt der Text unverändert (false).
    /// </summary>
    public bool ApplyFormat(TextFormat format)
    {
        if (IsReadOnly || SourceText is not { } source) return false;
        var target = Document.Text;
        var pair = TagText.Tags(source)
            .Where(t => t.Kind == TagText.TagKind.Start && TagFormat.Of(TagData, t.Id).HasFlag(format))
            .Select(t => t.Id)
            .FirstOrDefault(id => !target.Contains(TagText.Start(id)), -1);
        if (pair < 0) return false;
        var start = TextArea.Selection.IsEmpty ? CaretOffset : TextArea.Selection.SurroundingSegment.Offset;
        var end = TextArea.Selection.IsEmpty ? CaretOffset : TextArea.Selection.SurroundingSegment.EndOffset;
        using (Document.RunUpdate())
        {
            Document.Insert(end, TagText.End(pair));
            Document.Insert(start, TagText.Start(pair));
        }
        CaretOffset = end + TagText.Start(pair).Length;
        return true;
    }

    /// <summary>Fügt einen Text an der Schreibmarke ein (ersetzt eine Markierung).</summary>
    public void InsertAtCaret(string text)
    {
        if (IsReadOnly) return;
        if (!TextArea.Selection.IsEmpty) TextArea.Selection.ReplaceSelectionWithText(text);
        else Document.Insert(CaretOffset, text);
    }

    // ---------- Darstellung ----------

    private static IBrush Resource(string key, IBrush fallback) =>
        Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var r) == true && r is IBrush b ? b : fallback;

    /// <summary>Tags als dezente Marken, Text zwischen Tag-Paaren mit seiner Formatierung.</summary>
    private sealed class FormatColorizer(SegmentTextBox owner) : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            if (text.IndexOf(TagText.Open) < 0) return;
            var tagFg = Resource("TagForeground", Brushes.Purple);
            var tagBg = Resource("TagBackground", Brushes.Lavender);
            var tags = TagText.Tags(text).ToList();
            foreach (var start in tags.Where(t => t.Kind == TagText.TagKind.Start))
            {
                var end = tags.FirstOrDefault(t => t.Kind == TagText.TagKind.End && t.Id == start.Id && t.Index > start.Index);
                if (end.Length == 0) continue;
                var format = TagFormat.Of(owner.TagData, start.Id);
                if (format == TextFormat.None) continue;
                var from = line.Offset + start.Index + start.Length;
                var to = line.Offset + end.Index;
                if (to <= from) continue;
                ChangeLinePart(from, to, el =>
                {
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily,
                        format.HasFlag(TextFormat.Italic) ? FontStyle.Italic : tf.Style,
                        format.HasFlag(TextFormat.Bold) ? FontWeight.Bold : tf.Weight));
                    if (format.HasFlag(TextFormat.Underline) || format.HasFlag(TextFormat.Link))
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Underline);
                    else if (format.HasFlag(TextFormat.Strike))
                        el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                    if (format.HasFlag(TextFormat.Superscript) || format.HasFlag(TextFormat.Subscript))
                    {
                        el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * 0.75);
                        el.TextRunProperties.SetBaselineAlignment(format.HasFlag(TextFormat.Superscript) ? BaselineAlignment.Superscript : BaselineAlignment.Subscript);
                    }
                    if (format.HasFlag(TextFormat.Link)) el.TextRunProperties.SetForegroundBrush(Resource("SystemAccentColor", Brushes.SteelBlue));
                });
            }
            foreach (var t in tags)
                ChangeLinePart(line.Offset + t.Index, line.Offset + t.Index + t.Length, el =>
                {
                    el.TextRunProperties.SetForegroundBrush(tagFg);
                    el.TextRunProperties.SetBackgroundBrush(tagBg);
                    el.TextRunProperties.SetFontRenderingEmSize(el.TextRunProperties.FontRenderingEmSize * 0.82);
                    var tf = el.TextRunProperties.Typeface;
                    el.TextRunProperties.SetTypeface(new Typeface(tf.FontFamily, FontStyle.Normal, FontWeight.SemiBold));
                });
        }
    }

    /// <summary>Rote Wellenlinie unter Rechtschreibfehlern.</summary>
    private sealed class SpellingRenderer(SegmentTextBox owner) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (owner._errors.Count == 0 || !textView.VisualLinesValid) return;
            var pen = new Pen(Resource("SpellingError", Brushes.Red), 1.2);
            foreach (var error in owner._errors)
            {
                if (error.Index + error.Word.Length > owner.Document.TextLength) continue;
                var segment = new TextSegment { StartOffset = error.Index, Length = error.Word.Length };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                {
                    var geometry = new StreamGeometry();
                    using (var g = geometry.Open())
                    {
                        var y = r.Bottom - 1.5;
                        g.BeginFigure(new Point(r.Left, y), false);
                        var up = true;
                        for (var x = r.Left + 2; x <= r.Right + 0.5; x += 2)
                        {
                            g.LineTo(new Point(x, up ? y - 2 : y));
                            up = !up;
                        }
                        g.EndFigure(false);
                    }
                    dc.DrawGeometry(null, pen, geometry);
                }
            }
        }
    }
}

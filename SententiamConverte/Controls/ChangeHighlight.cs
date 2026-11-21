using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Controls;

/// <summary>
/// Zeigt die Änderung des Prüfers wortweise: Gelöschtes rot durchgestrichen, Eingefügtes grün unterstrichen –
/// wie die Änderungsverfolgung einer Textverarbeitung. Gesetzt über <c>c:ChangeHighlight.Before</c> und <c>.After</c>.
/// </summary>
public static class ChangeHighlight
{
    public static readonly AttachedProperty<string?> BeforeProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Before", typeof(ChangeHighlight));

    public static readonly AttachedProperty<string?> AfterProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("After", typeof(ChangeHighlight));

    private static readonly IBrush Deleted = new SolidColorBrush(Color.Parse("#C53030"));
    private static readonly IBrush Inserted = new SolidColorBrush(Color.Parse("#2F855A"));

    static ChangeHighlight()
    {
        BeforeProperty.Changed.AddClassHandler<TextBlock>((b, _) => Render(b));
        AfterProperty.Changed.AddClassHandler<TextBlock>((b, _) => Render(b));
    }

    public static string? GetBefore(TextBlock b) => b.GetValue(BeforeProperty);
    public static void SetBefore(TextBlock b, string? v) => b.SetValue(BeforeProperty, v);
    public static string? GetAfter(TextBlock b) => b.GetValue(AfterProperty);
    public static void SetAfter(TextBlock b, string? v) => b.SetValue(AfterProperty, v);

    private static void Render(TextBlock block)
    {
        var inlines = new InlineCollection();
        var before = GetBefore(block);
        var after = GetAfter(block) ?? "";
        if (before != null)
            foreach (var part in WordDiff.Compare(before, after))
                inlines.Add(part.Kind switch
                {
                    WordDiff.Kind.Deleted => new Run(part.Text) { Foreground = Deleted, TextDecorations = TextDecorations.Strikethrough },
                    WordDiff.Kind.Inserted => new Run(part.Text) { Foreground = Inserted, TextDecorations = TextDecorations.Underline, FontWeight = FontWeight.SemiBold },
                    _ => new Run(part.Text),
                });
        block.Inlines = inlines;
    }
}

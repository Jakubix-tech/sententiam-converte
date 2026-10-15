using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using SententiamConverte.Core.Model;

namespace SententiamConverte.Controls;

/// <summary>
/// Zeigt Segmenttext mit hervorgehobenen Tags: ⟨1⟩ ⟨/1⟩ ⟨2/⟩ erscheinen als farbige Marken,
/// damit Formatierungen und Platzhalter sofort auffallen.
/// </summary>
public static class TagHighlight
{
    public static readonly AttachedProperty<string?> TextProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Text", typeof(TagHighlight));

    private static readonly IBrush TagBackground = new SolidColorBrush(Color.Parse("#7C3AED"), 0.18);
    private static readonly IBrush TagForeground = new SolidColorBrush(Color.Parse("#7C3AED"));

    static TagHighlight()
    {
        TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => Render(block, e.NewValue as string));
    }

    private static string Label(TagText.Tag tag) => tag.Kind switch
    {
        TagText.TagKind.Start => $"‹{tag.Id}",
        TagText.TagKind.End => $"{tag.Id}›",
        _ => $"‹{tag.Id}›",
    };

    /// <summary>Position in der Anzeige (mit Tag-Marken) → Position im Segmenttext.</summary>
    public static int DisplayToSourceIndex(string source, int displayIndex)
    {
        int shown = 0, pos = 0;
        foreach (var part in TagText.Split(source))
        {
            var len = part.Text?.Length ?? Label(part.Tag!.Value).Length;
            var srcLen = part.Text?.Length ?? part.Tag!.Value.Length;
            if (displayIndex <= shown + len)
                return part.Text != null ? pos + (displayIndex - shown) : (displayIndex == shown ? pos : pos + srcLen);
            shown += len;
            pos += srcLen;
        }
        return source.Length;
    }

    /// <summary>Markierten Anzeigetext ohne Tag-Marken.</summary>
    public static string DisplayToPlain(string display) =>
        System.Text.RegularExpressions.Regex.Replace(display, @"‹\d+›?|\d+›", "");

    public static string? GetText(TextBlock element) => element.GetValue(TextProperty);
    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    private static void Render(TextBlock block, string? text)
    {
        var inlines = new InlineCollection();
        foreach (var part in TagText.Split(text ?? ""))
        {
            if (part.Text != null)
            {
                inlines.Add(new Run(part.Text));
                continue;
            }
            inlines.Add(new Run(Label(part.Tag!.Value))
            {
                Background = TagBackground,
                Foreground = TagForeground,
                FontWeight = FontWeight.SemiBold,
                FontSize = block.FontSize * 0.85,
            });
        }
        block.Inlines = inlines;
    }
}

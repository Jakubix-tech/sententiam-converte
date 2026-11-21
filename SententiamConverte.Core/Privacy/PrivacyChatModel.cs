using SententiamConverte.Core.Ai;

namespace SententiamConverte.Core.Privacy;

/// <summary>Ein Modell, das ein anderes umhüllt (z. B. Datenschutz); <see cref="Inner"/> ist das eigentliche.</summary>
public interface IWrappedChatModel
{
    IChatModel Inner { get; }
}

/// <summary>Welche Datenschutz-Einstellungen gerade gelten (Projekt bzw. ohne Projekt die Voreinstellung).</summary>
public static class PrivacyContext
{
    private static PrivacySettings _current = new();

    /// <summary>Wird von der Oberfläche beim Öffnen eines Projekts gesetzt; KI-Modelle übernehmen beim Anlegen eine Kopie.</summary>
    public static PrivacySettings Current
    {
        get => _current;
        set => _current = value ?? new PrivacySettings();
    }

    public static IChatModel Wrap(IChatModel model, AiProviderConfig config) =>
        Current.MasksFor(config) ? new PrivacyChatModel(model, Current.Clone()) : model;

    public static IChatModel Unwrap(IChatModel model) => model is IWrappedChatModel w ? Unwrap(w.Inner) : model;
}

/// <summary>
/// Datenschutz vor der KI: Alles, was an das Modell geht (Anweisungen, Texte, Werkzeugergebnisse), enthält statt
/// geschützter Angaben nur Platzhalter wie [NAME_1], [DATE_1], [CASE_1]; in den Antworten setzt das Programm die
/// echten Werte wieder ein. Werkzeug-Aufrufe bleiben maskiert (eine Suchanfrage trägt keine Namen ins Netz) – außer
/// den Werkzeugen in <see cref="UnmaskedTools"/>, deren Ergebnis beim Nutzer landet (z. B. die Endantwort).
/// </summary>
public sealed class PrivacyChatModel(IChatModel inner, PrivacySettings settings) : IToolChatModel, IParallelChatModel, IWrappedChatModel
{
    private readonly PrivacyMap _map = new(settings);
    private int _reported;

    public IChatModel Inner => inner;
    public string DisplayName => inner.DisplayName;
    public int Parallelism => (inner as IParallelChatModel)?.Parallelism ?? 1;

    /// <summary>Werkzeuge, deren Argumente mit den echten Werten zurückkommen.</summary>
    public static readonly HashSet<string> UnmaskedTools = ["finish", "remember"];

    /// <summary>Meldet, wie viele verschiedene Angaben bisher durch Platzhalter ersetzt wurden (für das Protokoll).</summary>
    public event Action<int>? Masked;

    public int MaskedValues => _map.Count;

    /// <summary>Für Dienste ohne Chat (maschinelle Übersetzung): Text mit Platzhaltern bzw. zurück mit echten Werten.</summary>
    public string MaskText(string text)
    {
        var masked = _map.Mask(text);
        Report();
        return masked;
    }

    public string UnmaskText(string text) => _map.Unmask(text);

    private void Report()
    {
        var n = _map.Count;
        if (n > _reported)
        {
            _reported = n;
            Masked?.Invoke(n);
        }
    }

    /// <summary>Erklärung für die KI, sobald Platzhalter vorkommen: was sie bedeuten und wie man mit ihnen umgeht.</summary>
    public const string PlaceholderHint =
        "\n\nThe texts contain placeholders for protected data: [NAME_n] = a person, [TERM_n] = a protected name (company, " +
        "place, person), [DATE_n] = a date, [CASE_n] = a court or file reference number, [EMAIL_n], [PHONE_n], [IBAN_n]. " +
        "Copy every placeholder unchanged into your answer, exactly once where its content belongs, and translate the words " +
        "around it (e.g. \"unter dem Aktenzeichen [CASE_1]\" → \"under case number [CASE_1]\"). Where the grammar of the target " +
        "language requires an ending on a name of this gender, append it directly after the placeholder (e.g. Polish male surname " +
        "in the genitive: \"pana [NAME_1]a\"); female surnames ending in a consonant usually take no ending.";

    private string WithHint(string maskedSystem, IEnumerable<string?> maskedTexts) =>
        SensitiveData.PlaceholderRegex().IsMatch(maskedSystem) || maskedTexts.Any(t => t != null && SensitiveData.PlaceholderRegex().IsMatch(t))
            ? maskedSystem + PlaceholderHint + (_map.GenderHint() is { Length: > 0 } g ? " " + g : "")
            : maskedSystem;

    public async Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var masked = messages.Select(m => m with { Text = _map.Mask(m.Text) }).ToList();
        var maskedSystem = WithHint(_map.Mask(system), masked.Select(m => (string?)m.Text));
        Report();
        return _map.Unmask(await inner.CompleteAsync(maskedSystem, masked, ct));
    }

    public async Task<ToolReply> CompleteWithToolsAsync(string system, IReadOnlyList<AgentMessage> messages, IReadOnlyList<ToolSpec> tools,
        CancellationToken ct = default)
    {
        if (inner is not IToolChatModel toolModel) throw new NotSupportedException();
        var masked = messages.Select(m => m with
        {
            Text = m.Text == null ? null : _map.Mask(m.Text),
            Calls = m.Calls?.Select(c => c with { Arguments = _map.Mask(c.Arguments) }).ToList(),
        }).ToList();
        var maskedSystem = WithHint(_map.Mask(system), masked.Select(m => m.Text));
        Report();
        var reply = await toolModel.CompleteWithToolsAsync(maskedSystem, masked, tools, ct);
        return new ToolReply(_map.Unmask(reply.Text),
            reply.Calls.Select(c => UnmaskedTools.Contains(c.Name.Trim().ToLowerInvariant()) ? c with { Arguments = _map.Unmask(c.Arguments) } : c).ToList());
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Microsoft.ML.Tokenizers;

namespace SententiamConverte.Core.Ai.LocalMt;

/// <summary>
/// Zerlegung in Wortteile wie im Original: SentencePiece der Ausgangssprache (source.spm) zerlegt den Text, die
/// Nummern kommen aus dem gemeinsamen Wortschatz (vocab.json); Ausgabe-Nummern werden über den Wortschatz zurück zu
/// Wortteilen und zu Text. Mehrsprachige Modelle bekommen die Zielsprache als erstes Token (z. B. „&gt;&gt;pol&lt;&lt;“).
/// </summary>
internal sealed class MarianVocabulary
{
    private const char Space = '▁';
    private readonly SentencePieceTokenizer _source;
    private readonly Dictionary<string, int> _ids;
    private readonly string[] _pieces;
    private readonly int _unk, _eos, _pad;

    public MarianVocabulary(string folder, MarianConfig config)
    {
        using (var spm = File.OpenRead(Path.Combine(folder, "source.spm")))
            _source = SentencePieceTokenizer.Create(spm, addBeginningOfSentence: false, addEndOfSentence: false);
        _ids = new Dictionary<string, int>();
        foreach (var (piece, id) in JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "vocab.json")))!.AsObject())
            _ids[piece] = (int)id!;
        _pieces = new string[Math.Max(config.VocabSize, _ids.Values.Max() + 1)];
        foreach (var (piece, id) in _ids) _pieces[id] = piece;
        _unk = _ids.GetValueOrDefault("<unk>", 1);
        _eos = config.EosId;
        _pad = config.PadId;
    }

    public bool Has(string piece) => _ids.ContainsKey(piece);

    /// <summary>Text → Token-Nummern samt Endzeichen; <paramref name="targetToken"/> (z. B. „&gt;&gt;pol&lt;&lt;“) davor.</summary>
    public int[] Encode(string text, string? targetToken = null)
    {
        var ids = new List<int>();
        if (targetToken != null) ids.Add(_ids.TryGetValue(targetToken, out var t) ? t : throw new ArgumentException("Unbekannte Zielsprache " + targetToken));
        foreach (var token in _source.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false))
            ids.Add(_ids.TryGetValue(token.Value, out var id) ? id : _unk);
        ids.Add(_eos);
        return ids.ToArray();
    }

    /// <summary>Wortteile einer Eingabe (für Tests: muss der Zerlegung des Originals entsprechen).</summary>
    public IReadOnlyList<string> Pieces(string text) =>
        _source.EncodeToTokens(text, out _, addBeginningOfSentence: false, addEndOfSentence: false).Select(t => t.Value).ToList();

    /// <summary>Token-Nummern → Text (Sonderzeichen werden ausgelassen).</summary>
    public string Decode(IEnumerable<int> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
        {
            if (id == _eos || id == _pad || id == _unk || id < 0 || id >= _pieces.Length || _pieces[id] is not { } piece) continue;
            if (piece.StartsWith(">>") && piece.EndsWith("<<")) continue;            // Sprachkennung
            sb.Append(piece);
        }
        return sb.Replace(Space, ' ').ToString().Trim();
    }
}

using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SententiamConverte.Core.Ai.LocalMt;

/// <summary>Aufbau eines Marian-Übersetzungsmodells (aus config.json).</summary>
internal sealed record MarianConfig(int Dim, int EncoderLayers, int DecoderLayers, int EncoderHeads, int DecoderHeads,
    int EncoderFfn, int DecoderFfn, int VocabSize, int PadId, int EosId, int DecoderStartId, string Activation,
    bool ScaleEmbedding, int MaxPositions, int Beams)
{
    public static MarianConfig Parse(string json)
    {
        var c = JsonNode.Parse(json)!.AsObject();
        int I(string key, int fallback) => c[key] is { } n ? (int)n : fallback;
        if ((string?)c["model_type"] != "marian")
            throw new InvalidDataException("Kein Marian-Modell (model_type: " + (string?)c["model_type"] + ").");
        if ((bool?)c["normalize_before"] == true || (bool?)c["add_final_layer_norm"] == true || (bool?)c["normalize_embedding"] == true)
            throw new InvalidDataException("Diese Modellvariante (Normierung vor den Schichten) wird nicht unterstützt.");
        var pad = I("pad_token_id", 0);
        return new MarianConfig(I("d_model", 512), I("encoder_layers", 6), I("decoder_layers", 6),
            I("encoder_attention_heads", 8), I("decoder_attention_heads", 8), I("encoder_ffn_dim", 2048), I("decoder_ffn_dim", 2048),
            I("vocab_size", 0), pad, I("eos_token_id", 0), I("decoder_start_token_id", pad),
            (string?)c["activation_function"] ?? "swish", (bool?)c["scale_embedding"] ?? true, I("max_position_embeddings", 512), I("num_beams", 4));
    }
}

/// <summary>
/// Ein Marian-Übersetzungsmodell (OPUS-MT) in reinem C#: Transformer mit Encoder und Decoder, Normierung nach den
/// Schichten, Strahlsuche wie im Original. Läuft auf jedem System ohne native Bibliothek (auch Android); die Rechnung
/// nutzt die Vektorbefehle des Prozessors und alle Kerne.
/// </summary>
internal sealed class MarianModel
{
    private sealed class Linear(Tensor weight, Tensor? bias)
    {
        public readonly float[] W = weight.Data;
        public readonly float[]? B = bias?.Data;
        public readonly int In = weight.Columns, Out = weight.Rows;

        /// <summary>y[r] = W · x[r] + b für <paramref name="rows"/> Zeilen; jede Gewichtszeile wird nur einmal gelesen.</summary>
        public void Forward(float[] x, int rows, float[] y)
        {
            var work = (long)Out * In * rows;
            const int chunk = 64;
            var chunks = (Out + chunk - 1) / chunk;
            if (work < 1 << 20)
                for (var c = 0; c < chunks; c++) Rows(c * chunk, Math.Min(Out, (c + 1) * chunk), x, rows, y);
            else
                Parallel.For(0, chunks, c => Rows(c * chunk, Math.Min(Out, (c + 1) * chunk), x, rows, y));
        }

        private void Rows(int from, int to, float[] x, int rows, float[] y)
        {
            for (var o = from; o < to; o++)
            {
                var w = new ReadOnlySpan<float>(W, o * In, In);
                var b = B?[o] ?? 0f;
                for (var r = 0; r < rows; r++)
                    y[r * Out + o] = Dot(w, new ReadOnlySpan<float>(x, r * In, In)) + b;
            }
        }
    }

    private sealed record Norm(float[] Gain, float[] Bias);
    private sealed record Attention(Linear Q, Linear K, Linear V, Linear O);
    private sealed record EncoderLayer(Attention Self, Norm SelfNorm, Linear Fc1, Linear Fc2, Norm FinalNorm);
    private sealed record DecoderLayer(Attention Self, Norm SelfNorm, Attention Cross, Norm CrossNorm, Linear Fc1, Linear Fc2, Norm FinalNorm);

    public MarianConfig Config { get; }
    private readonly float[] _encoderEmbed, _decoderEmbed, _encoderPositions, _decoderPositions;
    private readonly Linear _output;
    private readonly EncoderLayer[] _encoder;
    private readonly DecoderLayer[] _decoder;
    private readonly float _embedScale;

    private MarianModel(MarianConfig config, Dictionary<string, Tensor> t)
    {
        Config = config;
        Tensor Get(params string[] names) => names.Select(n => t.GetValueOrDefault(n)).FirstOrDefault(x => x != null)
                                            ?? throw new InvalidDataException("Im Modell fehlt " + names[0] + ".");
        Linear Lin(string p) => new(Get(p + ".weight"), t.GetValueOrDefault(p + ".bias"));
        Norm LayerNorm(string p) => new(Get(p + ".weight").Data, Get(p + ".bias").Data);
        Attention Att(string p) => new(Lin(p + ".q_proj"), Lin(p + ".k_proj"), Lin(p + ".v_proj"), Lin(p + ".out_proj"));

        var shared = Get("model.shared.weight", "model.encoder.embed_tokens.weight");
        _encoderEmbed = Get("model.encoder.embed_tokens.weight", "model.shared.weight").Data;
        var decoderEmbed = Get("model.decoder.embed_tokens.weight", "model.shared.weight");
        _decoderEmbed = decoderEmbed.Data;
        if (shared.Columns != config.Dim || decoderEmbed.Rows != config.VocabSize)
            throw new InvalidDataException("Die Gewichte passen nicht zur Modellbeschreibung.");
        _output = new Linear(t.GetValueOrDefault("lm_head.weight") ?? decoderEmbed, t.GetValueOrDefault("final_logits_bias"));
        _encoderPositions = t.GetValueOrDefault("model.encoder.embed_positions.weight")?.Data ?? Sinusoidal(config.MaxPositions, config.Dim);
        _decoderPositions = t.GetValueOrDefault("model.decoder.embed_positions.weight")?.Data ?? Sinusoidal(config.MaxPositions, config.Dim);
        _embedScale = config.ScaleEmbedding ? MathF.Sqrt(config.Dim) : 1f;
        _encoder = Enumerable.Range(0, config.EncoderLayers).Select(i => $"model.encoder.layers.{i}").Select(p =>
            new EncoderLayer(Att(p + ".self_attn"), LayerNorm(p + ".self_attn_layer_norm"), Lin(p + ".fc1"), Lin(p + ".fc2"), LayerNorm(p + ".final_layer_norm"))).ToArray();
        _decoder = Enumerable.Range(0, config.DecoderLayers).Select(i => $"model.decoder.layers.{i}").Select(p =>
            new DecoderLayer(Att(p + ".self_attn"), LayerNorm(p + ".self_attn_layer_norm"), Att(p + ".encoder_attn"),
                LayerNorm(p + ".encoder_attn_layer_norm"), Lin(p + ".fc1"), Lin(p + ".fc2"), LayerNorm(p + ".final_layer_norm"))).ToArray();
    }

    /// <summary>Modell aus einem Ordner mit config.json und model.safetensors laden.</summary>
    public static MarianModel Load(string folder) =>
        new(MarianConfig.Parse(File.ReadAllText(Path.Combine(folder, "config.json"))), SafeTensors.Read(Path.Combine(folder, "model.safetensors")));

    /// <summary>Feste Positionskodierung wie im Original: erste Hälfte Sinus, zweite Hälfte Kosinus.</summary>
    internal static float[] Sinusoidal(int positions, int dim)
    {
        var table = new float[positions * dim];
        var half = dim / 2;
        for (var p = 0; p < positions; p++)
            for (var j = 0; j < half; j++)
            {
                var angle = p / Math.Pow(10000, 2.0 * j / dim);
                table[p * dim + j] = (float)Math.Sin(angle);
                table[p * dim + half + j] = (float)Math.Cos(angle);
            }
        return table;
    }

    // ---------- Rechenbausteine ----------

    internal static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var va = MemoryMarshal.Cast<float, Vector<float>>(a);
        var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
        Vector<float> s0 = default, s1 = default, s2 = default, s3 = default;
        var i = 0;
        for (; i + 4 <= va.Length; i += 4)
        {
            s0 = Vector.FusedMultiplyAdd(va[i], vb[i], s0);
            s1 = Vector.FusedMultiplyAdd(va[i + 1], vb[i + 1], s1);
            s2 = Vector.FusedMultiplyAdd(va[i + 2], vb[i + 2], s2);
            s3 = Vector.FusedMultiplyAdd(va[i + 3], vb[i + 3], s3);
        }
        for (; i < va.Length; i++) s0 = Vector.FusedMultiplyAdd(va[i], vb[i], s0);
        var sum = Vector.Sum(s0 + s1 + s2 + s3);
        for (var k = va.Length * Vector<float>.Count; k < a.Length; k++) sum += a[k] * b[k];
        return sum;
    }

    private static void LayerNorm(float[] x, int rows, int dim, Norm n)
    {
        for (var r = 0; r < rows; r++)
        {
            var row = x.AsSpan(r * dim, dim);
            float mean = 0, variance = 0;
            foreach (var v in row) mean += v;
            mean /= dim;
            foreach (var v in row) variance += (v - mean) * (v - mean);
            var inv = 1f / MathF.Sqrt(variance / dim + 1e-5f);
            for (var i = 0; i < dim; i++) row[i] = (row[i] - mean) * inv * n.Gain[i] + n.Bias[i];
        }
    }

    private void Activate(float[] x, int count)
    {
        switch (Config.Activation)
        {
            case "relu":
                for (var i = 0; i < count; i++) x[i] = MathF.Max(0, x[i]);
                break;
            case "gelu":
                for (var i = 0; i < count; i++) x[i] = 0.5f * x[i] * (1 + Erf(x[i] / MathF.Sqrt(2)));
                break;
            default:                                                            // swish / silu
                for (var i = 0; i < count; i++) x[i] /= 1 + MathF.Exp(-x[i]);
                break;
        }
    }

    private static float Erf(float x)
    {
        // Abramowitz-Stegun 7.1.26, Fehler < 1.5e-7
        var t = 1 / (1 + 0.3275911f * MathF.Abs(x));
        var y = 1 - ((((1.061405429f * t - 1.453152027f) * t + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * MathF.Exp(-x * x);
        return x >= 0 ? y : -y;
    }

    private static void Add(float[] target, float[] source, int count)
    {
        var t = MemoryMarshal.Cast<float, Vector<float>>(target.AsSpan(0, count));
        var s = MemoryMarshal.Cast<float, Vector<float>>(source.AsSpan(0, count));
        for (var i = 0; i < t.Length; i++) t[i] += s[i];
        for (var i = t.Length * Vector<float>.Count; i < count; i++) target[i] += source[i];
    }

    /// <summary>Aufmerksamkeit einer Anfragezeile über <paramref name="keys"/> Schlüssel (alle Köpfe).</summary>
    private static void Attend(float[] q, int qOffset, float[] k, float[] v, int kvOffset, int keys, int dim, int heads, float[] output, int outOffset, float[] scores)
    {
        var hd = dim / heads;
        for (var h = 0; h < heads; h++)
        {
            var qh = new ReadOnlySpan<float>(q, qOffset + h * hd, hd);
            var max = float.NegativeInfinity;
            for (var j = 0; j < keys; j++)
            {
                scores[j] = Dot(qh, new ReadOnlySpan<float>(k, kvOffset + j * dim + h * hd, hd));
                if (scores[j] > max) max = scores[j];
            }
            float sum = 0;
            for (var j = 0; j < keys; j++) sum += scores[j] = MathF.Exp(scores[j] - max);
            var outH = output.AsSpan(outOffset + h * hd, hd);
            outH.Clear();
            for (var j = 0; j < keys; j++)
            {
                var p = scores[j] / sum;
                var vh = new ReadOnlySpan<float>(v, kvOffset + j * dim + h * hd, hd);
                for (var i = 0; i < hd; i++) outH[i] += p * vh[i];
            }
        }
    }

    private static void Scale(float[] x, int count, float factor)
    {
        for (var i = 0; i < count; i++) x[i] *= factor;
    }

    // ---------- Encoder ----------

    /// <summary>Kodiert die Eingabe (Token-Nummern samt Endzeichen) zu Zuständen [n × Dim].</summary>
    public float[] Encode(int[] ids)
    {
        int n = ids.Length, d = Config.Dim, heads = Config.EncoderHeads;
        if (n > Config.MaxPositions) throw new ArgumentException("Eingabe zu lang für das Modell.");
        var x = new float[n * d];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < d; j++)
                x[i * d + j] = _encoderEmbed[ids[i] * d + j] * _embedScale + _encoderPositions[i * d + j];
        float[] q = new float[n * d], k = new float[n * d], v = new float[n * d], att = new float[n * d], tmp = new float[n * d];
        var ffn = new float[n * Config.EncoderFfn];
        var scale = 1f / MathF.Sqrt(d / heads);
        foreach (var layer in _encoder)
        {
            layer.Self.Q.Forward(x, n, q);
            Scale(q, n * d, scale);
            layer.Self.K.Forward(x, n, k);
            layer.Self.V.Forward(x, n, v);
            Parallel.For(0, n, () => new float[n], (i, _, scores) =>
            {
                Attend(q, i * d, k, v, 0, n, d, heads, att, i * d, scores);
                return scores;
            }, _ => { });
            layer.Self.O.Forward(att, n, tmp);
            Add(x, tmp, n * d);
            LayerNorm(x, n, d, layer.SelfNorm);
            layer.Fc1.Forward(x, n, ffn);
            Activate(ffn, n * Config.EncoderFfn);
            layer.Fc2.Forward(ffn, n, tmp);
            Add(x, tmp, n * d);
            LayerNorm(x, n, d, layer.FinalNorm);
        }
        return x;
    }

    // ---------- Decoder mit Strahlsuche ----------

    private sealed class DecoderState
    {
        public required float[][] SelfK, SelfV, CrossK, CrossV;
        public required int MaxLength, Sources;
    }

    /// <summary>
    /// Übersetzt die kodierte Eingabe: Strahlsuche mit <paramref name="beams"/> Strahlen, Längennormierung 1,0, das
    /// Füllzeichen ist verboten (wie im Original). Ergebnis: Token-Nummern ohne Start- und Endzeichen.
    /// </summary>
    public List<int> Generate(int[] ids, int beams = 0, int maxLength = 0, CancellationToken ct = default)
    {
        if (beams <= 0) beams = Config.Beams;
        if (maxLength <= 0) maxLength = Math.Min(Config.MaxPositions - 1, ids.Length * 3 + 10);
        var encoded = Encode(ids);
        int n = ids.Length, d = Config.Dim, vocab = Config.VocabSize;
        var state = new DecoderState
        {
            SelfK = _decoder.Select(_ => new float[beams * maxLength * d]).ToArray(),
            SelfV = _decoder.Select(_ => new float[beams * maxLength * d]).ToArray(),
            CrossK = _decoder.Select(l => { var a = new float[n * d]; l.Cross.K.Forward(encoded, n, a); return a; }).ToArray(),
            CrossV = _decoder.Select(l => { var a = new float[n * d]; l.Cross.V.Forward(encoded, n, a); return a; }).ToArray(),
            MaxLength = maxLength, Sources = n,
        };

        var live = new List<(List<int> Tokens, float Score)> { ([], 0f) };
        var finished = new List<(List<int> Tokens, float Score)>();
        var logits = new float[beams * vocab];
        for (var step = 0; step < maxLength; step++)
        {
            ct.ThrowIfCancellationRequested();
            var last = live.Select(b => b.Tokens.Count == 0 ? Config.DecoderStartId : b.Tokens[^1]).ToArray();
            DecoderStep(last, step, state, logits);

            // je Strahl die besten 2·beams Fortsetzungen, dann insgesamt die besten
            var candidates = new List<(int Beam, int Token, float Score)>();
            for (var b = 0; b < live.Count; b++)
            {
                var row = logits.AsSpan(b * vocab, vocab);
                row[Config.PadId] = float.NegativeInfinity;
                if (step == maxLength - 1)                                       // letzter Schritt: Ende erzwingen
                    for (var t = 0; t < vocab; t++) if (t != Config.EosId) row[t] = float.NegativeInfinity;
                LogSoftmax(row);
                foreach (var (token, lp) in Top(row, 2 * beams))
                    candidates.Add((b, token, live[b].Score + lp));
            }
            candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

            var next = new List<(int Beam, int Token, float Score)>();
            for (var rank = 0; rank < candidates.Count && next.Count < beams; rank++)
            {
                var c = candidates[rank];
                if (c.Token == Config.EosId)
                {
                    if (rank < beams) finished.Add((live[c.Beam].Tokens, c.Score / (live[c.Beam].Tokens.Count + 1)));
                }
                else next.Add(c);
            }
            finished.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (finished.Count > beams) finished.RemoveRange(beams, finished.Count - beams);
            if (next.Count == 0) break;
            // fertig, wenn kein laufender Strahl die schlechteste fertige Übersetzung noch schlagen kann
            if (finished.Count == beams && finished[^1].Score >= next[0].Score / (step + 1)) break;

            Reorder(state, next.Select(c => c.Beam).ToArray(), step + 1);
            live = next.Select(c => (new List<int>(live[c.Beam].Tokens) { c.Token }, c.Score)).ToList();
        }
        if (finished.Count == 0) finished.AddRange(live.Select(b => (b.Tokens, b.Score / Math.Max(1, b.Tokens.Count))));
        return finished.MaxBy(f => f.Score).Tokens;
    }

    private static void LogSoftmax(Span<float> row)
    {
        var max = float.NegativeInfinity;
        foreach (var v in row) if (v > max) max = v;
        double sum = 0;
        foreach (var v in row) if (!float.IsNegativeInfinity(v)) sum += Math.Exp(v - max);
        var log = max + (float)Math.Log(sum);
        for (var i = 0; i < row.Length; i++) row[i] -= log;
    }

    private static List<(int Token, float Score)> Top(ReadOnlySpan<float> row, int count)
    {
        var best = new List<(int Token, float Score)>(count + 1);
        for (var t = 0; t < row.Length; t++)
        {
            var s = row[t];
            if (best.Count == count && s <= best[^1].Score) continue;
            var pos = best.Count;
            while (pos > 0 && best[pos - 1].Score < s) pos--;
            best.Insert(pos, (t, s));
            if (best.Count > count) best.RemoveAt(count);
        }
        return best;
    }

    /// <summary>Zwischenspeicher der Selbstaufmerksamkeit auf die neuen Strahlen umordnen.</summary>
    private void Reorder(DecoderState state, int[] origin, int steps)
    {
        var d = Config.Dim;
        var block = steps * d;
        for (var l = 0; l < _decoder.Length; l++)
            foreach (var cache in new[] { state.SelfK, state.SelfV })
            {
                var old = cache[l];
                var fresh = new float[old.Length];
                for (var b = 0; b < origin.Length; b++)
                    Array.Copy(old, origin[b] * state.MaxLength * d, fresh, b * state.MaxLength * d, block);
                cache[l] = fresh;
            }
    }

    /// <summary>Ein Decoderschritt für alle Strahlen: letztes Token je Strahl → Logits [Strahlen × Wortschatz].</summary>
    private void DecoderStep(int[] tokens, int step, DecoderState state, float[] logits)
    {
        int rows = tokens.Length, d = Config.Dim, heads = Config.DecoderHeads;
        var x = new float[rows * d];
        for (var r = 0; r < rows; r++)
            for (var j = 0; j < d; j++)
                x[r * d + j] = _decoderEmbed[tokens[r] * d + j] * _embedScale + _decoderPositions[step * d + j];
        float[] q = new float[rows * d], kv = new float[rows * d], att = new float[rows * d], tmp = new float[rows * d];
        var ffn = new float[rows * Config.DecoderFfn];
        var scores = new float[Math.Max(state.MaxLength, state.Sources)];
        var scale = 1f / MathF.Sqrt(d / heads);
        for (var l = 0; l < _decoder.Length; l++)
        {
            var layer = _decoder[l];
            // Selbstaufmerksamkeit über alle bisherigen Schritte des eigenen Strahls
            layer.Self.Q.Forward(x, rows, q);
            Scale(q, rows * d, scale);
            layer.Self.K.Forward(x, rows, kv);
            for (var r = 0; r < rows; r++) Array.Copy(kv, r * d, state.SelfK[l], (r * state.MaxLength + step) * d, d);
            layer.Self.V.Forward(x, rows, kv);
            for (var r = 0; r < rows; r++) Array.Copy(kv, r * d, state.SelfV[l], (r * state.MaxLength + step) * d, d);
            for (var r = 0; r < rows; r++)
                Attend(q, r * d, state.SelfK[l], state.SelfV[l], r * state.MaxLength * d, step + 1, d, heads, att, r * d, scores);
            layer.Self.O.Forward(att, rows, tmp);
            Add(x, tmp, rows * d);
            LayerNorm(x, rows, d, layer.SelfNorm);
            // Aufmerksamkeit auf die Eingabe
            layer.Cross.Q.Forward(x, rows, q);
            Scale(q, rows * d, scale);
            for (var r = 0; r < rows; r++)
                Attend(q, r * d, state.CrossK[l], state.CrossV[l], 0, state.Sources, d, heads, att, r * d, scores);
            layer.Cross.O.Forward(att, rows, tmp);
            Add(x, tmp, rows * d);
            LayerNorm(x, rows, d, layer.CrossNorm);
            // Vorwärtsnetz
            layer.Fc1.Forward(x, rows, ffn);
            Activate(ffn, rows * Config.DecoderFfn);
            layer.Fc2.Forward(ffn, rows, tmp);
            Add(x, tmp, rows * d);
            LayerNorm(x, rows, d, layer.FinalNorm);
        }
        _output.Forward(x, rows, logits);
    }
}

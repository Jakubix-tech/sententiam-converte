using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Ai.LocalMt;

namespace SententiamConverte.Core.Tests;

/// <summary>Lokale Übersetzung (OPUS-MT) ohne echte Modelle: Katalog, Wege, Tags, Downloads, Gewichte, Netz.</summary>
[Collection("BuiltInAi")]
public sealed class LocalTranslationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly string _base;

    public LocalTranslationTests()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _base = $"http://localhost:{port}/";
        _listener.Prefixes.Add(_base);
        _listener.Start();
        _ = Task.Run(Serve);
        LocalTranslation.Folder = _dir.Path;
        LocalTranslation.UrlOverride = u => u.Replace("https://huggingface.co/", _base);
    }

    public void Dispose()
    {
        LocalTranslation.Folder = "";
        LocalTranslation.UrlOverride = null;
        _listener.Close();
        _dir.Dispose();
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
            if (_files.TryGetValue(ctx.Request.Url!.AbsolutePath.TrimStart('/'), out var data))
            {
                ctx.Response.ContentLength64 = data.Length;
                await ctx.Response.OutputStream.WriteAsync(data);
            }
            else ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }

    [Fact]
    public void CatalogHasDirectPairsGroupModelsAndRoutesViaEnglish()
    {
        Assert.True(LocalTranslation.Catalog.Count > 300);
        Assert.All(LocalTranslation.Catalog, p => Assert.Contains(p.Model.License, new[] { "apache-2.0", "cc-by-4.0" }));
        Assert.All(LocalTranslation.Catalog.Select(p => p.Model).Distinct(), m =>
        {
            Assert.StartsWith("Helsinki-NLP/opus-mt-", m.Repo);
            Assert.Equal(["config.json", "source.spm", "target.spm", "vocab.json", "model.safetensors"], m.Files.Select(f => f.Name));
            Assert.Equal(64, m.Files.Single(f => f.Name == "model.safetensors").Hash.Length);
        });

        var de = LocalTranslation.Route("de-DE", "en-GB")!;
        Assert.False(de.ViaEnglish);
        Assert.Equal("opus-mt-de-en", de.Steps[0].Model.Id);
        var enPl = LocalTranslation.Route("en-US", "pl-PL")!;
        Assert.Equal(("opus-mt-en-zlw", ">>pol<<"), (enPl.Steps[0].Model.Id, enPl.Steps[0].TargetToken));      // Gruppenmodell
        var dePt = LocalTranslation.Route("de-DE", "pt-BR")!;                                                       // kein direktes Modell
        Assert.True(dePt.ViaEnglish);
        Assert.Equal(["opus-mt-de-en", "opus-mt-en-ROMANCE"], dePt.Steps.Select(s => s.Model.Id));
        Assert.Equal("no", LocalTranslation.Code("nb-NO"));
        // Dreibuchstaben-Codes wie die Hauptsprache („fra“ aus einer Eingabe oder einer älteren Einstellung)
        Assert.Equal("opus-mt-de-fr", LocalTranslation.Route("deu", "fra")!.Steps[0].Model.Id);
        Assert.Equal(("fr", "fr", "fr-CA"), (Model.Languages.Primary("fra"), Model.Languages.Normalize("fra"), Model.Languages.Normalize("fre-CA")));
        Assert.True(Model.Languages.Matches("ger", "de-AT"));
        Assert.Equal("fil", Model.Languages.Primary("fil-PH"));                                 // ohne Zweibuchstaben-Code bleibt er
        Assert.Null(LocalTranslation.Route("de-DE", "de-AT"));
        Assert.Null(LocalTranslation.Route("de-DE", "xx-XX"));
    }

    [Fact]
    public void TagsGoInAsMarkupAndComeBackAsTags()
    {
        Assert.Equal(("⟨1⟩⟨2⟩", "Text ⟨3/⟩", "⟨/2⟩⟨/1⟩"), LocalTranslation.Wrapping("⟨1⟩⟨2⟩Text ⟨3/⟩⟨/2⟩⟨/1⟩"));
        Assert.Equal(("", "⟨1⟩a⟨/1⟩ und ⟨2⟩b⟨/2⟩", ""), LocalTranslation.Wrapping("⟨1⟩a⟨/1⟩ und ⟨2⟩b⟨/2⟩"));      // kein Rahmen
        Assert.Equal("Auf <g id=\"1\">Speichern</g> & <x id=\"2\"/> klicken", LocalTranslation.ToMarkup("Auf ⟨1⟩Speichern⟨/1⟩ & ⟨2/⟩ klicken"));
        Assert.Equal("Click ⟨1⟩ Save ⟨/1⟩ & ⟨2/⟩.", LocalTranslation.FromMarkup("Click <g id=\"1\"> Save </g> &amp; <x id=\"2/>."));
        Assert.Equal("Cliquez sur « Suivant »", LocalTranslation.FromMarkup("Cliquez sur « & #160; Suivant & #160; »"));
        Assert.Equal("Click ⟨1⟩Save⟨/1⟩.", LocalTranslation.TidyTags("Auf ⟨1⟩Speichern⟨/1⟩.", "Click ⟨1⟩ Save ⟨/1⟩."));
        Assert.Equal("a ⟨1⟩ b ⟨/1⟩", LocalTranslation.TidyTags("x ⟨1⟩ y ⟨/1⟩", "a ⟨1⟩ b ⟨/1⟩"));                   // wie in der Quelle
    }

    [Fact]
    public void QuotesFollowTheTargetLanguageWhenTheSourceUsesTypographicOnes()
    {
        Assert.Equal("Kliknij „Dalej”, aby…", Typography.Quotes("Klicken Sie auf „Weiter“", "Kliknij \"Dalej\", aby…", "pl-PL"));
        Assert.Equal("Cliquez sur « Suivant »", Typography.Quotes("„Weiter“", "Cliquez sur \"Suivant\"", "fr-FR"));
        Assert.Equal("Click “Next”", Typography.Quotes("„Weiter“", "Click \"Next\"", "en-GB"));
        Assert.Equal("Click \"Next\"", Typography.Quotes("\"Weiter\"", "Click \"Next\"", "en-GB"));                  // Quelle gerade → bleibt
    }

    [Fact]
    public void GitHashOfSmallFilesMatchesGit() =>
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", LocalTranslation.GitBlobHash("hello\n"u8.ToArray()));   // git hash-object

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Ein Modell auf dem Prüfserver: kleine Dateien mit Git-Kennung, Gewichte mit SHA-256.</summary>
    private LocalMtRoute Offer(Func<string, byte[], string>? damage = null)
    {
        var files = new List<LocalMtFile>();
        foreach (var name in new[] { "config.json", "source.spm", "target.spm", "vocab.json", "model.safetensors" })
        {
            var data = RandomNumberGenerator.GetBytes(name == "model.safetensors" ? 400_000 : 3_000);
            var rev = name == "model.safetensors" ? "w1" : "r1";
            _files[$"test/opus-mt-xx-yy/resolve/{rev}/{name}"] = data;
            var hash = name == "model.safetensors" ? Sha(data) : LocalTranslation.GitBlobHash(data);
            files.Add(new LocalMtFile(name, rev, data.Length, damage?.Invoke(name, data) ?? hash));
        }
        var model = new LocalMtModel("test/opus-mt-xx-yy", "apache-2.0", files);
        return new LocalMtRoute([new LocalMtPair("xx", "yy", model, null)]);
    }

    [Fact]
    public async Task ModelsAreDownloadedCheckedAndRemoved()
    {
        var route = Offer();
        Assert.False(route.IsInstalled);
        Assert.Equal(route.Models.Single().Size, route.MissingBytes);
        await LocalTranslation.InstallAsync(route, null, TestContext.Current.CancellationToken);
        Assert.True(route.IsInstalled);
        Assert.Equal(0, route.MissingBytes);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.part", SearchOption.AllDirectories));
        LocalTranslation.Remove(route.Models.Single());
        Assert.False(route.IsInstalled);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "opus-mt-xx-yy")));
    }

    [Theory]
    [InlineData("vocab.json")]
    [InlineData("model.safetensors")]
    public async Task DamagedFilesAreRejected(string broken)
    {
        var route = Offer((name, data) => name == broken ? new string('0', name == "model.safetensors" ? 64 : 40) : null!);
        var ex = await Assert.ThrowsAsync<AiException>(() => LocalTranslation.InstallAsync(route, null, TestContext.Current.CancellationToken));
        Assert.Contains("beschädigt", ex.Message);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "opus-mt-xx-yy", broken)));
        Assert.False(route.IsInstalled);
    }

    [Fact]
    public async Task MissingModelGivesAClearMessage()
    {
        var mt = new MachineTranslationModel(new AiProviderConfig { Name = "Offline", Kind = AiProviderKind.MachineTranslation, Model = "lokal" });
        Assert.True(new AiProviderConfig { Kind = AiProviderKind.MachineTranslation, Model = "lokal" }.IsLocal);
        var ex = await Assert.ThrowsAsync<AiException>(() => mt.TranslateAsync("Hallo Welt.", "de-DE", "en-GB", TestContext.Current.CancellationToken));
        Assert.Contains("noch nicht heruntergeladen", ex.Message);
        ex = await Assert.ThrowsAsync<AiException>(() => mt.TranslateAsync("Hallo Welt.", "de-DE", "xx-XX", TestContext.Current.CancellationToken));
        Assert.Contains("keine lokale Übersetzung", ex.Message);
        // unbekannter Dienst bleibt wie früher DeepL
        Assert.Equal("deepl", new MachineTranslationModel(new AiProviderConfig { Kind = AiProviderKind.MachineTranslation, Model = "gibt-es-nicht" }).Service.Id);
    }

    // ---------- Gewichte und Netz ----------

    /// <summary>safetensors schreiben: Kopf mit Typ, Form und Lage, dann die Daten.</summary>
    private static void WriteSafeTensors(string path, IReadOnlyDictionary<string, (int[] Shape, float[] Data)> tensors, string type = "F32")
    {
        var header = new JsonObject { ["__metadata__"] = new JsonObject { ["format"] = "pt" } };
        var data = new MemoryStream();
        foreach (var (name, (shape, values)) in tensors)
        {
            var begin = data.Length;
            foreach (var v in values)
            {
                var bytes = new byte[type == "F32" ? 4 : 2];
                if (type == "F32") BinaryPrimitives.WriteSingleLittleEndian(bytes, v);
                else if (type == "F16") BinaryPrimitives.WriteHalfLittleEndian(bytes, (Half)v);
                else BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(BitConverter.SingleToInt32Bits(v) >> 16));
                data.Write(bytes);
            }
            header[name] = new JsonObject { ["dtype"] = type, ["shape"] = new JsonArray(shape.Select(s => (JsonNode)s).ToArray()), ["data_offsets"] = new JsonArray(begin, data.Length) };
        }
        var head = Encoding.UTF8.GetBytes(header.ToJsonString());
        using var file = File.Create(path);
        var len = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)head.Length);
        file.Write(len);
        file.Write(head);
        data.Position = 0;
        data.CopyTo(file);
    }

    [Theory]
    [InlineData("F32")]
    [InlineData("F16")]
    [InlineData("BF16")]
    public void SafeTensorsAreReadInEveryFloatFormat(string type)
    {
        var path = _dir.File("t.safetensors");
        WriteSafeTensors(path, new Dictionary<string, (int[], float[])> { ["a"] = ([2, 3], [1, -2, 0.5f, 3, 4.25f, -0.125f]), ["b"] = ([1], [7]) }, type);
        var t = SafeTensors.Read(path);
        Assert.Equal([2, 3], t["a"].Shape);
        Assert.Equal([1, -2, 0.5f, 3, 4.25f, -0.125f], t["a"].Data);
        Assert.Equal(7, t["b"].Data[0]);

        File.WriteAllBytes(path, [255, 255, 255, 255, 0, 0, 0, 0, 1, 2]);                          // kaputter Kopf
        Assert.Throws<InvalidDataException>(() => SafeTensors.Read(path));
    }

    [Fact]
    public void PositionsAreSineThenCosine()
    {
        var p = MarianModel.Sinusoidal(4, 6);
        Assert.Equal(0f, p[0]);                                                // sin(0)
        Assert.Equal(1f, p[3]);                                                // cos(0)
        Assert.Equal(MathF.Sin(1f), p[6], 5);                                  // Position 1, erste Spalte
        Assert.Equal(MathF.Cos(1f / MathF.Pow(10000, 2f / 6)), p[6 + 4], 5);
    }

    /// <summary>Ein winziges Zufallsmodell im Marian-Aufbau: laden, kodieren, übersetzen – reproduzierbar und begrenzt.</summary>
    [Fact]
    public void TinyRandomModelGeneratesDeterministically()
    {
        const int d = 16, ffn = 32, vocab = 12;
        var random = new Random(7);
        float[] R(int n) => Enumerable.Range(0, n).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();
        var t = new Dictionary<string, (int[], float[])>
        {
            ["model.shared.weight"] = ([vocab, d], R(vocab * d)),
            ["final_logits_bias"] = ([1, vocab], new float[vocab]),
        };
        void Lin(string p, int o, int i) { t[p + ".weight"] = ([o, i], R(o * i)); t[p + ".bias"] = ([o], R(o)); }
        void Norm(string p) { t[p + ".weight"] = ([d], Enumerable.Repeat(1f, d).ToArray()); t[p + ".bias"] = ([d], new float[d]); }
        void Att(string p) { foreach (var x in new[] { "q_proj", "k_proj", "v_proj", "out_proj" }) Lin($"{p}.{x}", d, d); }
        Att("model.encoder.layers.0.self_attn");
        Norm("model.encoder.layers.0.self_attn_layer_norm");
        Lin("model.encoder.layers.0.fc1", ffn, d);
        Lin("model.encoder.layers.0.fc2", d, ffn);
        Norm("model.encoder.layers.0.final_layer_norm");
        Att("model.decoder.layers.0.self_attn");
        Norm("model.decoder.layers.0.self_attn_layer_norm");
        Att("model.decoder.layers.0.encoder_attn");
        Norm("model.decoder.layers.0.encoder_attn_layer_norm");
        Lin("model.decoder.layers.0.fc1", ffn, d);
        Lin("model.decoder.layers.0.fc2", d, ffn);
        Norm("model.decoder.layers.0.final_layer_norm");
        var folder = _dir.File("winzig");
        Directory.CreateDirectory(folder);
        WriteSafeTensors(Path.Combine(folder, "model.safetensors"), t);
        File.WriteAllText(Path.Combine(folder, "config.json"),
            $$"""{"model_type":"marian","d_model":{{d}},"encoder_layers":1,"decoder_layers":1,"encoder_attention_heads":4,"decoder_attention_heads":4,"encoder_ffn_dim":{{ffn}},"decoder_ffn_dim":{{ffn}},"vocab_size":{{vocab}},"pad_token_id":11,"eos_token_id":0,"decoder_start_token_id":11,"activation_function":"swish","scale_embedding":true,"max_position_embeddings":64,"num_beams":3,"normalize_before":false}""");

        var model = MarianModel.Load(folder);
        var first = model.Generate([3, 5, 7, 0], maxLength: 20);
        Assert.Equal(first, model.Generate([3, 5, 7, 0], maxLength: 20));
        Assert.InRange(first.Count, 0, 20);
        Assert.DoesNotContain(11, first);                                      // Füllzeichen verboten
        Assert.DoesNotContain(0, first);                                       // Endzeichen nicht in der Ausgabe
        Assert.InRange(model.Generate([3, 5, 7, 0], beams: 1, maxLength: 20).Count, 0, 20);   // gierig geht auch

        File.WriteAllText(Path.Combine(folder, "config.json"), """{"model_type":"marian","normalize_before":true}""");
        Assert.Throws<InvalidDataException>(() => MarianModel.Load(folder));
    }
}

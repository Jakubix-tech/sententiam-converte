using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Privacy;

namespace SententiamConverte.Core.Tests;

/// <summary>Gefundene Namen einordnen: nur Identifizierendes schützen, nie Länder, Produkte oder Arten von Einrichtungen.</summary>
public sealed class NameClassificationTests
{
    private sealed class Classifier(string reply) : IChatModel
    {
        public string Prompt = "";
        public string DisplayName => "lokal";

        public Task<string> CompleteAsync(string system, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
        {
            Prompt = messages[^1].Text;
            return Task.FromResult(reply);
        }
    }

    private static readonly string[] Texts =
    [
        "Anna Beispiel, Hauptstraße 5, Musterdorf",
        "Konstruktion mit Autodesk Inventor bei der Kleinfirma Beispiel GmbH",
        "Ausbildung am Berufskolleg für Fachinformatiker in Polen",
    ];

    [Fact]
    public async Task OnlyIdentifyingNamesAreProtected()
    {
        // die KI irrt bei Land, Produkt und Einrichtung – die festen Regeln korrigieren das
        var model = new Classifier("""
            {"items": [
              {"name": "Anna Beispiel", "kind": "person", "protect": true, "reason": "Name der Verfasserin"},
              {"name": "Hauptstraße 5", "kind": "address", "protect": true, "reason": "Anschrift"},
              {"name": "Autodesk Inventor", "kind": "product", "protect": true, "reason": "Software"},
              {"name": "Beispiel GmbH", "kind": "company", "protect": true, "reason": "kleiner Arbeitgeber"},
              {"name": "Berufskolleg für Fachinformatiker", "kind": "institution", "protect": true, "reason": "Schule"},
              {"name": "Polen", "kind": "place", "protect": true, "reason": "Ort"},
              {"name": "Fremd", "kind": "person", "protect": true, "reason": "nicht gefragt"}
            ]}
            """);
        var names = new[] { "Anna Beispiel", "Hauptstraße 5", "Musterdorf", "Autodesk Inventor", "Beispiel GmbH", "Berufskolleg für Fachinformatiker", "Polen" };
        var result = (await PrivacyScanner.ClassifyNamesAsync(model, names, Texts, ct: TestContext.Current.CancellationToken)).ToDictionary(c => c.Name);

        Assert.Equal(names.Length, result.Count);                                                     // „Fremd“ war nicht gefragt
        Assert.True(result["Anna Beispiel"].Protect);
        Assert.True(result["Hauptstraße 5"].Protect);
        Assert.True(result["Beispiel GmbH"].Protect);
        Assert.False(result["Autodesk Inventor"].Protect);                                            // Produkt: nie
        Assert.False(result["Berufskolleg für Fachinformatiker"].Protect);                            // Einrichtungsart: wird übersetzt
        Assert.Equal((NameKind.Country, false), (result["Polen"].Kind, result["Polen"].Protect));     // Land: wird übersetzt
        Assert.Equal((NameKind.Other, true), (result["Musterdorf"].Kind, result["Musterdorf"].Protect)); // ohne Antwort: vorsichtshalber
        Assert.Contains("\"Beispiel GmbH\" – Konstruktion mit Autodesk Inventor bei der Kleinfirma Beispiel GmbH", model.Prompt);   // mit Satz
    }

    [Fact]
    public async Task WithoutAnAnswerEverythingButCountriesIsProtected()
    {
        var result = await PrivacyScanner.ClassifyNamesAsync(new Classifier("Das kann ich nicht."), ["Anna Beispiel", "Poland", "Deutschland"], Texts,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal([true, false, false], result.Select(r => r.Protect));
        Assert.True(PrivacyScanner.IsCountry("Polska"));
    }
}

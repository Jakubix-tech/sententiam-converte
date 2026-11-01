using SententiamConverte.Core.Ai;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Qa;

namespace SententiamConverte.Core.Tests;

/// <summary>Bausteine des Editors: Formatierung aus Tag-Daten, Rechtschreibung im Text mit Tags, KI-Erkennung.</summary>
public class EditorCoreTests
{
    [Theory]
    [InlineData("f:<w:rPr><w:b/></w:rPr>", TextFormat.Bold)]
    [InlineData("f:<w:rPr><w:b w:val=\"0\"/><w:i/></w:rPr>", TextFormat.Italic)]
    [InlineData("f:<w:rPr><w:u w:val=\"single\"/><w:vertAlign w:val=\"superscript\"/></w:rPr>", TextFormat.Underline | TextFormat.Superscript)]
    [InlineData("f:<w:rPr><w:u w:val=\"none\"/></w:rPr>", TextFormat.None)]
    [InlineData("pair:<strong>\u0001</strong>", TextFormat.Bold)]
    [InlineData("pair:<em class=\"x\">\u0001</em>", TextFormat.Italic)]
    [InlineData("pair:**\u0001**", TextFormat.Bold)]
    [InlineData("pair:<sub>\u0001</sub>", TextFormat.Subscript)]
    [InlineData("h:3", TextFormat.Link)]
    [InlineData("p:r1.0", TextFormat.None)]
    public void TagFormatsAreRecognized(string data, TextFormat expected) => Assert.Equal(expected, TagFormat.Of(data));

    [Fact]
    public void SpellingPositionsReferToTheTextWithTags()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.File("en-GB.aff"), "SET UTF-8\n");
        File.WriteAllText(dir.File("en-GB.dic"), "4\nTighten\nthe\nscrew\nto\n");
        var spell = new SpellChecker(dir.Path);
        const string text = "Tighten ⟨1⟩teh⟨/1⟩ screw to 5 Nm and ⟨2/⟩wrongword.";
        var errors = spell.CheckTagged(text, "en-GB");
        Assert.Equal(["teh", "Nm", "and", "wrongword"], errors.Select(e => e.Word));
        foreach (var e in errors) Assert.Equal(e.Word, text.Substring(e.Index, e.Word.Length));
        spell.AddToUserDictionary("en-GB", "Nm");
        Assert.DoesNotContain(spell.CheckTagged(text, "en-GB"), e => e.Word == "Nm");
    }

    [Fact]
    public void ModelSuggestionPrefersGeneralModels()
    {
        Assert.Equal("qwen3-32b", AiDiscovery.SuggestModel(["llama-guard", "qwen3-32b", "tinyllama"]));
        Assert.Null(AiDiscovery.SuggestModel([]));
        Assert.Contains(AiDiscovery.FreeServices, s => s.Id == "openrouter" && s.IsFreeModel("deepseek/deepseek-chat:free") && !s.IsFreeModel("openai/gpt-5"));
    }
}

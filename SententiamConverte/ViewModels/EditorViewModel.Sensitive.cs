using System.Collections.Generic;
using System.Linq;
using SententiamConverte.Core.Model;
using SententiamConverte.Core.Privacy;
using SententiamConverte.Services;

namespace SententiamConverte.ViewModels;

/// <summary>
/// Segmente mit geschützten Angaben kennzeichnen (🔒): Ist das Projekt vertraulich, steht bei jedem Segment, welche
/// Angaben darin geschützt werden und welchen Platzhalter („Tag“) jede beim Anonymisieren und für die Online-KI bekommt –
/// im ganzen Dokument einheitlich nummeriert, wie beim Anonymisieren selbst.
/// </summary>
public partial class EditorViewModel
{
    public void RefreshSensitive()
    {
        // Sperre vor dem Netz: die geschützten Angaben dieses Dokuments gehen in keine Suchanfrage – auch ohne „vertraulich“
        PrivacyGuard.Values = PrivacyGuard.Collect(_all.SelectMany(s => new[] { TagText.StripTags(s.Source), TagText.StripTags(s.Model.Target ?? "") }), Project?.Privacy);
        var settings = Project?.Privacy;
        if (settings is not { Enabled: true })
        {
            foreach (var s in _all) s.SensitiveInfo = "";
            return;
        }
        var map = new PrivacyMap(settings);
        foreach (var s in _all)
        {
            var lines = new List<string>();
            foreach (var text in new[] { s.Source, s.Model.Target })
            {
                var plain = TagText.StripTags(text ?? "");
                if (plain.Length == 0) continue;
                map.Mask(plain);
                foreach (var span in SensitiveData.Find(plain, settings))
                {
                    var line = $"{PrivacyViewModel.KindName(span.Kind)} „{span.Value}“ → {map.PlaceholderFor(span.Value)}";
                    if (!lines.Contains(line)) lines.Add(line);
                }
            }
            s.SensitiveInfo = lines.Count == 0 ? "" : L.T("Geschützte Angaben (beim Anonymisieren ersetzt durch):") + "\n" + string.Join("\n", lines);
        }
    }
}

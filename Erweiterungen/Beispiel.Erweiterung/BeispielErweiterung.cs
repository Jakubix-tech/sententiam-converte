using SententiamConverte.Core.Extensions;

namespace Beispiel.Erweiterung;

/// <summary>
/// Einstiegspunkt: Das Programm sucht in der Bibliothek eine öffentliche Klasse mit <see cref="IExtension"/>,
/// erzeugt sie und ruft <see cref="Register"/> auf. Dort meldet die Erweiterung an, was sie beisteuert.
/// </summary>
public sealed class BeispielErweiterung : IExtension
{
    public string Id => "beispiel.erweiterung";
    public string Name => "Beispiel-Erweiterung";
    public string Version => "1.0.0";
    public string? Author => "Sententiam converte";
    public string? Description => "Dateiformat CSV, QA-Prüfung „Ausrufezeichen“, Befehle für Dokument und Segment.";

    public void Register(IExtensionRegistry registry)
    {
        registry.AddFileFilter(new CsvFilter());
        registry.AddQaCheck(new ExclamationCheck());
        registry.AddCommand(new TypographicQuotesCommand());
        registry.AddCommand(new SegmentStatisticsCommand());
        registry.AddCommand(new AlternativeCommand());
    }
}

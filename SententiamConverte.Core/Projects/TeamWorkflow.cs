using SententiamConverte.Core.Remote;

namespace SententiamConverte.Core.Projects;

/// <summary>
/// Arbeitsschritte im Team (Übersetzung → Lektorat → Freigabe → fertig) mit Zuständigen je Datei und Zielsprache.
/// Dieselben Regeln gelten für den Teamserver und die REST-API: Verwalter setzen Schritte und Zuständige, wer an der
/// Reihe ist, bearbeitet und gibt ab. Ohne Zuweisungen bleibt alles frei (wie vor den Arbeitsschritten).
/// </summary>
public static class TeamWorkflow
{
    public static WorkflowEntry? Entry(TranslationProject p, string fileId, string language) =>
        p.Workflow?.FirstOrDefault(w => w.FileId == fileId && w.Language.Equals(language, StringComparison.OrdinalIgnoreCase));

    public static FileStep StepOf(TranslationProject p, string fileId, string language) =>
        Entry(p, fileId, language)?.ToStep() ?? new FileStep(language, WorkflowStep.Translation, null, null, null);

    public static string StepName(WorkflowStep s) => s switch
    {
        WorkflowStep.Translation => "Übersetzung",
        WorkflowStep.Review => "Lektorat",
        WorkflowStep.Approval => "Freigabe",
        _ => "fertig",
    };

    /// <summary>Schritt oder Zuständige ändern bzw. abgeben; wirft <see cref="UnauthorizedAccessException"/>, wenn nicht erlaubt.</summary>
    public static FileStep Set(TranslationProject p, string fileId, string language, WorkflowUpdate u, string user, bool admin)
    {
        if (p.Files.All(f => f.Id != fileId)) throw new KeyNotFoundException(fileId);
        var entry = Entry(p, fileId, language);
        var current = entry?.ToStep() ?? new FileStep(language, WorkflowStep.Translation, null, null, null);
        if (!admin)
        {
            if (!u.Advance || u.Step != null || u.Translator != null || u.Reviewer != null || u.Approver != null)
                throw new UnauthorizedAccessException("Schritte und Zuständige legt die Verwaltung fest.");
            if (current.Assignee != null && current.Assignee != user)
                throw new UnauthorizedAccessException($"Abgeben kann nur, wer an der Reihe ist ({current.Assignee}).");
        }
        if (entry == null)
        {
            entry = new WorkflowEntry { FileId = fileId, Language = language };
            (p.Workflow ??= new()).Add(entry);
        }
        static string? Name(string? value, string? old) => value == null ? old : value.Trim().Length == 0 ? null : value.Trim();
        entry.Translator = Name(u.Translator, entry.Translator);
        entry.Reviewer = Name(u.Reviewer, entry.Reviewer);
        entry.Approver = Name(u.Approver, entry.Approver);
        if (u.Step is { } step) entry.Step = step;
        if (u.Advance) entry.Step = FileStep.Next(entry.Step);
        p.Save();
        return entry.ToStep();
    }

    /// <summary>Wirft <see cref="UnauthorizedAccessException"/>, wenn der Benutzer die Datei im aktuellen Schritt nicht bearbeiten darf.</summary>
    public static void CheckEditable(TranslationProject p, string fileId, string language, string user, bool admin)
    {
        if (Entry(p, fileId, language)?.ToStep() is { } step && !step.MayEdit(user, admin))
            throw new UnauthorizedAccessException(step.Step == WorkflowStep.Done
                ? "Die Datei ist abgeschlossen."
                : $"Die Datei ist im Schritt „{StepName(step.Step)}“ bei {step.Assignee} – nur Lesen.");
    }
}

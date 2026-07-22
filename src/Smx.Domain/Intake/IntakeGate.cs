using Smx.Domain.Records;

namespace Smx.Domain.Intake;

/// The precondition on create_project (design §4.3). CODE, not prompt — an agent talked out of a rule
/// is an agent that will one day be talked back into it.
///
/// Returns null when the project may be created, or the reason it may not. NEVER throws: the caller is
/// an LLM tool dispatcher, an escaping exception fails the whole turn, and the returned text is the
/// only thing that teaches the model to correct itself. Every message therefore names the specific
/// thing that is missing — a bare "not ready yet" produces a retry of the identical call.
public static class IntakeGate
{
    public static string? Check(
        string? client, string? product, string? summary,
        IReadOnlyList<ComponentSpec> components, IReadOnlyList<DossierEntry> dossier)
    {
        if (string.IsNullOrWhiteSpace(client) || string.IsNullOrWhiteSpace(product))
            return "the project needs a client and a product before it can be created — ask the operator for both.";

        if (string.IsNullOrWhiteSpace(summary))
            return "write the summary first (write_summary): the operator opens the project to READ it, " +
                   "and a project created without one presents them with a dossier and no orientation.";

        if (components.Count == 0)
            return "propose at least one component (propose_components) before creating the project. " +
                   "Every stage downstream runs PER COMPONENT — there is no product-wide marker.";

        foreach (var c in components)
        {
            if (string.IsNullOrWhiteSpace(c.Id))
                return "every component needs an id (e.g. 'bottle', 'lid', 'label').";
            if (string.IsNullOrWhiteSpace(c.Material))
                return $"component '{c.Id}' has no material. Material drives which marker forms are compatible — ask the operator.";
            if (string.IsNullOrWhiteSpace(c.Application))
                return $"component '{c.Id}' has no application (food contact, skin contact, non-contact, …). " +
                       "Application x markets is what selects the regulation lists — without it the component is not screened.";
            if (string.IsNullOrWhiteSpace(c.Objective))
                return $"component '{c.Id}' has no objective. Ask the operator whether it is brand-protection " +
                       "go/no-go or needs quantification: the answer flips the meaning of a conditional XRF verdict.";
            if (c.Markets is not { Count: > 0 })
                return $"component '{c.Id}' has no target markets. Recording none would leave it with ZERO " +
                       "markets, which empties its regulatory screen — ask the operator which markets it ships to.";
        }

        if (components.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != components.Count)
            return "component ids must be unique.";

        var seen = dossier
            .Where(e => DossierState.All.Contains(e.State, StringComparer.Ordinal))
            .Select(e => e.QuestionId)
            .ToHashSet(StringComparer.Ordinal);

        var missing = IntakeQuestions.All.Where(q => !seen.Contains(q.Id)).Select(q => q.Id).ToArray();
        if (missing.Length > 0)
            return $"these questions have not been covered yet: {string.Join(", ", missing)}. " +
                   "Ask the operator about each one. If they genuinely do not know, call mark_unknown — " +
                   "an unknown is recorded and travels with the project, but a question nobody reached is " +
                   "an analysis running on something nobody was ever asked about.";

        foreach (var e in dossier.Where(e => e.State == DossierState.AgentProposed))
            if (string.IsNullOrWhiteSpace(e.Confidence))
                return $"'{e.QuestionId}' is agent-proposed but carries no confidence. An inference with no " +
                       "confidence is indistinguishable from something the operator said. Record the " +
                       "confidence, or ask the operator and record their answer instead.";

        return null;
    }
}

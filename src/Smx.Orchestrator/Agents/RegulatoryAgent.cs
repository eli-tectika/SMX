using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The agent's DTO — what the model's JSON is deserialized into. It carries the agent's PROPOSAL and it
/// deliberately has NO `Determination` member: the operator's determination is a signature (Law 9), and the
/// guard against an agent forging one is STRUCTURAL — a model that emits `"determination": "recommended"`
/// has it silently discarded by the deserializer, because there is nowhere for it to land. Do not widen this
/// type to VerdictDoc, and do not deserialize model JSON straight into a VerdictDoc.
public sealed class RegulatoryOutput
{
    public List<DimensionVerdict> Dimensions { get; set; } = [];
    public string? ProposedDetermination { get; set; }
    public string? ProposedReason { get; set; }
}

public static class RegulatoryAgent
{
    public const string AgentName = "regulatory";

    public const string Instructions = """
        You are the SMX Regulatory agent. You evaluate ONE candidate substance against ONE product component
        and return a verdict per dimension. Substrate compatibility is NOT your concern (Discovery handled it).
        You may only use facts obtained through your tools in this conversation — never from memory. Dimensions
        (all three, exactly once each):
        - ElementGate: product-wide lists from the provided scope (componentId "*") plus the client restricted
          list. Search the regulatory corpus for the element/substance against each list. A hit on any list = Fail.
        - ApplicationCheck: the component-scoped lists from the provided scope. A restriction that binds this
          component's application/markets = Fail; a cap/limit that constrains but permits = Conditional.
        - Hazard: search_sds for GHS data (H-codes, CMR, endocrine). CMR category 1A/1B = Fail; significant
          hazards that merit "not recommended" = Conditional.
        Statuses: Pass | Conditional | NeedsReview | Fail. EVERY dimension MUST carry at least one citation
        built from an actual tool result (source, reference, retrievedAt = now, ISO 8601 UTC). If your tools
        return nothing decisive for a dimension, the status is NeedsReview — never guess, never assume clean.
        Confidence is your calibrated 0..1 estimate. Rationale is one or two sentences.

        Finally, PROPOSE a determination for this substance × component:
          "proposedDetermination": "recommended" | "rejected"
          "proposedReason":        why, in one sentence, citing what you relied on.
        Both are MANDATORY, including for a rejection. You are PROPOSING, not deciding: the Regulatory
        Expert reviews your proposal and signs. Never claim to have approved or rejected anything.
        You may only propose "recommended" when every dimension came back Pass or Conditional. If any
        dimension is Fail or NeedsReview, propose "rejected": the Expert may still overrule a Fail, but
        that override is hers to write, and recommending on evidence you do not have is guessing.

        Reply with ONLY a JSON object: { "dimensions": [{ "dimension", "status", "citations":
        [{ "source", "reference", "retrievedAt" }], "confidence", "rationale" }],
        "proposedDetermination", "proposedReason" }
        """;

    /// <param name="revision">null for an ordinary run; non-null re-screens this cell APPLYING the operator's
    /// revise-with-reason (Law 4). It is an explicit parameter rather than an overload on purpose: a caller
    /// who forgets it gets a compile error instead of an agent that quietly ignores the operator.</param>
    public static async Task<AgentRunResult<VerdictDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision,
        CancellationToken ct)
    {
        var component = constraints.Components.Single(c => c.Id == candidate.ComponentId);
        var scope = constraints.DerivedScope.Where(s => s.ComponentId is "*" || s.ComponentId == candidate.ComponentId).ToList();
        var prompt = JsonSerializer.Serialize(new
        {
            substance = new { candidate.Element, candidate.Form, candidate.Cas },
            component,
            applicableScope = scope,
            clientRestrictedList = constraints.ClientRestrictedList,
        }, Json.Options);

        var task = revision is null ? $"Screen this cell:\n{prompt}" : RevisionTask(revision, prompt);
        var result = await ValidatedAgentRunner.RunAsync<RegulatoryOutput>(agent, task, Validate, ct);
        if (!result.Succeeded) return AgentRunResult<VerdictDoc>.NeedsReview(result.Error!);
        return AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(constraints.ProjectId, candidate.Cas, candidate.ComponentId),
            ProjectId = constraints.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
            Element = candidate.Element, Form = candidate.Form,
            Dimensions = result.Output!.Dimensions,
            // ONLY the proposal crosses over. Determination / DeterminationReason / EvidenceReviewed stay
            // untouched — they are the operator's signature, and the determination endpoint is their only
            // writer. Mapping a proposal onto Determination here would let the agent sign the regulatory
            // gate through the back door (Law 9); CompliantSetTests pins the other end of that line.
            ProposedDetermination = result.Output.ProposedDetermination,
            ProposedReason = result.Output.ProposedReason,
        });
    }

    /// The operator's instruction is authoritative — but "apply it" is not "manufacture support for it".
    /// The standing rule (Instructions) still binds: never guess, never assume clean, cite every dimension.
    /// Where the instruction outruns the corpus, the agent applies it AND says so, so the gap lands in the
    /// rationale and the confidence — visible to the R.E. at the gate — instead of being papered over.
    private static string RevisionTask(RevisionDoc revision, string prompt) => $"""
        Re-screen this cell, APPLYING the operator's revision below.
        The operator's instruction is authoritative: apply it. You still may not invent facts — call your
        tools and cite every reference you rely on. If the regulatory or SDS corpus does not support the
        instruction, apply it anyway, say exactly that in the affected dimension's rationale, and lower that
        dimension's confidence accordingly.

        REVISION — target: {revision.Target}
        REVISION — reason: {revision.Reason}

        {prompt}
        """;

    internal static string? Validate(RegulatoryOutput o)
    {
        string[] required = ["ElementGate", "ApplicationCheck", "Hazard"];
        var names = o.Dimensions.Select(d => d.Dimension).OrderBy(x => x).ToArray();
        if (!names.SequenceEqual(required.OrderBy(x => x)))
            return $"response must contain exactly the three dimensions {string.Join(", ", required)} once each; got [{string.Join(", ", names)}]";
        foreach (var d in o.Dimensions)
        {
            if (d.Citations.Count == 0 || d.Citations.Any(c =>
                    string.IsNullOrWhiteSpace(c.Source) || string.IsNullOrWhiteSpace(c.Reference)))
                return $"dimension '{d.Dimension}' is missing a usable citation — every dimension must cite an actual tool result";
            if (d.Confidence is < 0 or > 1) return $"dimension '{d.Dimension}' confidence must be within 0..1";
            if (string.IsNullOrWhiteSpace(d.Rationale)) return $"dimension '{d.Dimension}' needs a rationale";
        }

        // A proposal is OPTIONAL here even though the Instructions call it mandatory, and the asymmetry is
        // deliberate. Rejecting a verdict that merely lacks a pre-fill would burn three model retries and
        // then sink the whole cell — dimensions, citations and all — into needs-review. A missing proposal
        // costs the operator one hand-authored determination, exactly as before the pre-fill existed. That
        // is the safe direction; the other is not. What we DO reject is a MALFORMED proposal, because a
        // proposal the operator cannot trust at a glance is worse than none.
        if (o.ProposedDetermination is null) return null;
        if (o.ProposedDetermination is not (Determinations.Recommended or Determinations.Rejected))
            return $"proposedDetermination must be exactly '{Determinations.Recommended}' or '{Determinations.Rejected}'; got '{o.ProposedDetermination}'";
        if (string.IsNullOrWhiteSpace(o.ProposedReason))
            return "proposedReason is required for either proposal — a recommendation with no reason is a rubber stamp, and a rejection with no reason cannot be argued with";
        // The R.E. may overrule a Fail; that is what a human gate is for. But an agent that pre-fills
        // "recommended" on a red or an undecided cell is training the operator to click through them, which
        // is the exact behaviour this whole design exists to prevent. NeedsReview is the sharper half: it
        // means the agent's own tools found nothing decisive, so recommending is "assume clean" — the one
        // move the standing Instructions forbid.
        if (o.ProposedDetermination == Determinations.Recommended &&
            VerdictDoc.Fold(o.Dimensions) is VerdictStatus.Fail or VerdictStatus.NeedsReview)
            return $"cannot propose '{Determinations.Recommended}' when a dimension is Fail or NeedsReview " +
                   $"— propose '{Determinations.Rejected}'; only the Regulatory Expert may override a Fail";
        return null;
    }
}

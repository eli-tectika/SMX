using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The tools that let a chat turn CHANGE something. Constructed fresh for each turn, closed over the
/// (projectId, stage) of the chat-message the change feed delivered, and over that message's key.
///
/// The binding is the safety property. If `projectId` were a tool PARAMETER, one hallucinated id would
/// mutate a different project's analysis — no undo, and no reason for anyone to look. The model's schema
/// therefore offers no way to name a project; it can only act on the one it is talking about.
///
/// NOTE WHAT IS ABSENT: there is no gate tool, no approve tool, no determination tool. An agent can only
/// act through its tools, so chat CANNOT sign a gate — not because it was told not to, but because the
/// capability does not exist (Law 9, the anti-rubber-stamping line). POST /regulatory/approve remains the
/// only writer of an approved GateDoc. Chat can only move a gate toward `locked` (an apply_revision voids
/// it), which is the safe direction: it forces the operator to re-review and re-sign.
public sealed class ChatTools(IRecordStore store, string projectId, string stage, string chatKey)
{
    /// What this turn actually did — the trail the reply carries so the UI can show which sentence
    /// changed which record (design §5: "no silent mutations").
    public List<ChatToolCall> Trail { get; } = [];

    public IList<AITool> Tools()
    {
        var tools = new List<AITool>();
        if (RevisionEffects.IsRevisable(stage))
            tools.Add(AIFunctionFactory.Create(ApplyRevisionAsync, "apply_revision",
                "Change something you previously produced for this stage, because the operator has given you a reason. " +
                "This RE-RUNS the stage applying the change and records the reason as a Learned Conclusion. " +
                "It is the ONLY way to change an analytical result — never claim to have changed something without calling this. " +
                "`reason` is mandatory and is recorded verbatim. For a regulatory change you must also name the " +
                "`cas` and `componentId` of the verdict to re-run, because a verdict is per substance x component."));

        if (stage == Stages.Intake)
            tools.Add(AIFunctionFactory.Create(RecordAnswerAsync, "record_answer",
                "Fill in a missing project input the operator has just told you, while intake is still gathering them. " +
                "`field` is one of: components.{componentId}.material, .application, .objective, .markets, or clientRestrictedList. " +
                "Element pools and provided candidates are NOT answerable — they are measured/seeded data. " +
                "Once intake has produced constraints this is refused; use apply_revision instead."));

        return tools;
    }

    /// The `= null` defaults on `cas`/`componentId` are load-bearing, not stylistic: AIFunctionFactory emits
    /// a parameter WITHOUT a default as `required` in the tool's JSON schema regardless of what the
    /// description says, and the binder then rejects the call before this body ever runs. A Discovery
    /// revision has neither a cas nor a componentId, so without the defaults revise-with-reason is dead on
    /// arrival for Discovery ("missing a value for the required parameter 'cas'").
    public async Task<string> ApplyRevisionAsync(
        string target, string reason, string? cas = null, string? componentId = null, CancellationToken ct = default)
    {
        // Every check the endpoint makes, in the same order and for the same reasons (RevisionEndpoints):
        // a chat-queued revision must be the SAME RevisionDoc a POST /revise would have written, or the two
        // front doors of Law 4 diverge and only one of them is the one that was reasoned about.
        if (string.IsNullOrWhiteSpace(target))
            return Error("target is required — name what should change");
        // Law 4: no silent edits. The reason is also the seed of the Learned Conclusion; without it there
        // is nothing for the system to learn from this change.
        if (string.IsNullOrWhiteSpace(reason))
            return Error("a reason is required — ask the operator WHY before changing anything");
        if (stage == Stages.Regulatory && (string.IsNullOrWhiteSpace(cas) || string.IsNullOrWhiteSpace(componentId)))
            return Error("a regulatory revision must name the cas and componentId of the verdict to re-run");

        // Refuse a revision that names something that does not exist, rather than queueing one the
        // dispatcher can only mark `failed` minutes later — by which time the model has already told the
        // operator the change is on its way and the turn is over. An error the model can read is the only
        // chance it has to ask the operator which cell they actually meant.
        if (stage == Stages.Discovery && await store.GetCandidatesAsync(projectId, ct) is null)
            return Error("discovery has not produced candidates yet — there is nothing to revise");
        if (stage == Stages.Regulatory && await store.GetVerdictAsync(projectId, cas!, componentId!, ct) is null)
            return Error($"no verdict for {cas}|{componentId} in this project");

        // The id is DERIVED FROM THE CHAT MESSAGE, never minted from a fresh Guid. The chat change feed is
        // at-least-once and its idempotency guard is the message's Status, which is flipped to `answered`
        // only AFTER this durable write. A crash in that window redelivers the still-`pending` message, the
        // turn re-runs, and the model calls this again with the same arguments — and a random id would then
        // mint a SECOND revision: one operator instruction, two stage re-runs, two Learned Conclusions.
        // Keying on (chat message, call ordinal) makes the replay an upsert of the same doc instead, which
        // is the same reason RecordIds.ChatReply keys on the message and KnowledgeIds.RevisionConclusion
        // keys on the revision id.
        //
        // The ordinal is what keeps that from over-correcting: one message may legitimately ask for two
        // changes, and those are two decisions that both belong in the audit trail. It comes from the Trail
        // — i.e. from how many revisions THIS turn has already written — which replays identically because
        // the turn does.
        var ordinal = Trail.Count(c => c.Tool == "apply_revision");
        var revisionId = RecordIds.Revision(projectId, stage, $"{chatKey}-{ordinal}");

        await store.UpsertRevisionAsync(new RevisionDoc
        {
            Id = revisionId,
            ProjectId = projectId,
            Stage = stage,
            Target = target,
            Reason = reason,
            Cas = stage == Stages.Regulatory ? cas : null,
            ComponentId = stage == Stages.Regulatory ? componentId : null,
            Status = RevisionStatus.Pending,
            // ALWAYS "O" — the revision audit trail is ordered by a LEXICOGRAPHIC sort on this field, which
            // is only chronological while every writer uses the same fixed-width format (RevisionDoc.CreatedAt).
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, ct);

        // The operator's reason goes into the summary VERBATIM — no stripping, no escaping. Collapsing line
        // breaks is ChatThread.Render's job and it does it for every line of every turn; a second strip here
        // would be a blocklist growing back, and it would corrupt the one artifact in this system that
        // exists in no corpus.
        Trail.Add(new ChatToolCall("apply_revision", $"{target} — {reason}", revisionId));

        // QUEUED, not applied. Record-as-bus: writing the doc IS the dispatch, and the orchestrator re-runs
        // the stage from the change feed some time later. If this said "done", the model would tell the
        // operator it was done and the operator would believe a change that has not happened.
        return JsonSerializer.Serialize(new
        {
            queued = true,
            revisionId,
            note = "the revision is QUEUED, not applied — the stage will re-run shortly and its result will " +
                   "replace what is on screen. Tell the operator what you queued and that it is in progress; " +
                   "do not claim the change has been made.",
        }, Json.Options);
    }

    public async Task<string> RecordAnswerAsync(string field, string value, CancellationToken ct = default)
    {
        // Gap-fill ONLY. Once constraints exist, the derived regulatory scope every downstream stage was
        // screened against is already in the record: an "answer" would no longer fill a blank, it would
        // silently change an established input — with no reason recorded and nothing re-run. That is exactly
        // the direct-edit Law 4 forbids, so it is refused and the model is pointed at apply_revision.
        if (await store.GetConstraintsAsync(projectId, ct) is not null)
            return Error("intake has already produced constraints, so this is no longer a gap-fill. " +
                         "To change an established input, use apply_revision with the operator's reason.");
        if (await store.GetProjectAsync(projectId, ct) is not { } project)
            return Error("project not found");

        // The allowlist. Element pools (the physicist's measured XRF background) and providedCandidates are
        // unwritable BY CONSTRUCTION here, not by instruction — see IntakeAnswers.
        var (patched, error) = IntakeAnswers.Patch(project.Payload, field, value);
        if (error is not null) return Error(error);

        project.Payload = patched!.Value;
        // Setting Intake back to `pending` IS the re-trigger: this upsert is a change-feed event, and
        // StageDispatcher.OnProjectAsync runs Intake exactly when the stage is `pending` and no constraints
        // exist yet — the second condition being the one the guard above has already established.
        project.Stages[Stages.Intake].Status = "pending";
        project.Stages[Stages.Intake].Error = null;
        await store.UpsertProjectAsync(project, ct);
        Trail.Add(new ChatToolCall("record_answer", $"{field} = {value}", project.Id));

        return JsonSerializer.Serialize(new
        {
            recorded = true,
            note = "intake will re-run with this answer",
        }, Json.Options);
    }

    /// A tool error is not an exception: an escaping exception fails the whole turn, and this text is the
    /// only thing that teaches the model to correct itself.
    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json.Options);
}

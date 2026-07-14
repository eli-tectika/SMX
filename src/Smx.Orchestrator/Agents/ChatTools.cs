using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
///
/// `chatKey` is the KEY of the chat message being answered (the suffix RecordIds.ChatMessage was minted
/// with), not the whole document id. It must be an id-safe token — see IdSafe: it is concatenated into a
/// Cosmos item id, and Cosmos rejects an id containing '/', '\', '?' or '#' with a 400 that no in-memory
/// test store would ever produce.
public sealed class ChatTools(IRecordStore store, string projectId, string stage, string chatKey)
{
    /// Validated at CONSTRUCTION, so a malformed key fails here — in the orchestrator, with a stack trace
    /// naming this class — rather than as an opaque Cosmos 400 at the write, several awaits later, inside a
    /// tool call whose error text the model will merely try to "handle".
    private readonly string _chatKey = IdSafe(chatKey);

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
        // Every check POST /projects/{id}/stages/{stage}/revise makes, in the SAME ORDER and for the same
        // reasons (RevisionEndpoints). A chat-queued revision must be the same RevisionDoc that endpoint
        // would have written, under the same preconditions — otherwise the two front doors of Law 4 diverge
        // and only one of them is the one that was reasoned about.
        //
        // IsRevisable is checked HERE too, even though Tools() only offers this tool on a revisable stage.
        // The construction gate is what stops the MODEL; this stops the next caller. Fail-closed is the
        // house style for exactly this — RevisionEffects.BreaksRegulatoryGate THROWS rather than returning
        // the dangerous `false` — and a RevisionDoc on a non-revisable stage is one the dispatcher can only
        // throw on.
        if (!RevisionEffects.IsRevisable(stage))
            return Error($"stage '{stage}' cannot be revised — only discovery and regulatory produce a revisable agent output");
        if (string.IsNullOrWhiteSpace(target))
            return Error("target is required — name what should change");
        // Law 4: no silent edits. The reason is also the seed of the Learned Conclusion; without it there
        // is nothing for the system to learn from this change.
        if (string.IsNullOrWhiteSpace(reason))
            return Error("a reason is required — ask the operator WHY before changing anything");

        if (await store.GetProjectAsync(projectId, ct) is null)
            return Error("project not found");

        // Refuse a revision that names something that does not exist, rather than queueing one the dispatcher
        // can only mark `failed` minutes later — by which time the model has already told the operator the
        // change is on its way and the turn is over. An error the model can read is the only chance it has to
        // ask the operator which cell they actually meant.
        if (stage == Stages.Discovery && await store.GetCandidatesAsync(projectId, ct) is null)
            return Error("discovery has not produced candidates yet — there is nothing to revise");
        if (stage == Stages.Regulatory)
        {
            if (string.IsNullOrWhiteSpace(cas) || string.IsNullOrWhiteSpace(componentId))
                return Error("a regulatory revision must name the cas and componentId of the verdict to re-run");
            if (await store.GetVerdictAsync(projectId, cas, componentId, ct) is null)
                return Error($"no verdict for {cas}|{componentId} in this project");
        }

        // The values that actually reach the doc. cas/componentId are meaningless off Regulatory, and the id
        // is hashed over THESE — so a model that passes a stray cas on a Discovery revision still lands on
        // the id its resulting doc deserves.
        var effectiveCas = stage == Stages.Regulatory ? cas : null;
        var effectiveComponentId = stage == Stages.Regulatory ? componentId : null;

        // CONTENT-ADDRESSED, from the chat message + the call's content. Never a fresh Guid, and never the
        // call's POSITION in the turn.
        //
        // The chat change feed is at-least-once and its idempotency guard is the message's Status, which is
        // flipped to `answered` only AFTER this durable write. A crash in that window redelivers the still-
        // `pending` message and the turn RE-RUNS. A Guid would mint a second revision from one operator
        // instruction — two stage re-runs, two Learned Conclusions from one reason.
        //
        // Keying on POSITION (an ordinal over this turn's calls) is worse than weak — it is unsound. It
        // assumes the replayed turn makes the same calls in the same order, but a sampled model shown
        // DIFFERENT record state (revision -0 already applied, the candidate already dropped) is actively
        // pushed toward different calls. Ordinal 0 of the replay would then carry different content onto the
        // applied revision's id and blind-upsert a `Pending` over it; OnRevisionAsync — whose only
        // idempotency guard is that status — would re-enter and re-run it. ReviseDiscoveryAsync re-derives
        // candidates from constraints + that revision, so the candidate the operator eliminated for a stated
        // spectral-overlap reason silently RETURNS to the set, their verbatim reason is destroyed in the
        // audit trail, and the Learned Conclusion (whose id derives from the revision id) is overwritten
        // along with it.
        //
        // Hashing the CONTENT makes the id mean the change rather than the slot: an identical call converges
        // on the same doc, a different call gets a different doc and cannot destroy the first, and two
        // distinct calls in one turn get distinct ids for free — no ordinal needed.
        var revisionId = RecordIds.Revision(projectId, stage,
            $"{_chatKey}-{ContentKey(target, reason, effectiveCas, effectiveComponentId)}");

        // Content-addressing alone still leaves the same-content replay re-upserting a `Pending` over a doc
        // the dispatcher has already APPLIED — re-running a change that is already made. So if this id exists
        // and has moved off `pending`, do not touch it. The Trail entry is still added: the reply's audit
        // link must point at the record this sentence produced whether or not THIS delivery is the one that
        // wrote it.
        if ((await store.GetRevisionsAsync(projectId, ct)).FirstOrDefault(r => r.Id == revisionId) is { } existing
            && existing.Status != RevisionStatus.Pending)
        {
            Trail.Add(new ChatToolCall("apply_revision", $"{target} — {reason}", revisionId));
            return JsonSerializer.Serialize(new
            {
                queued = true,
                revisionId,
                alreadyQueued = true,
                status = existing.Status,
                note = $"this exact change was already queued from this message and is now '{existing.Status}' " +
                       "— it has NOT been queued twice and must not be re-issued. Tell the operator it is " +
                       "already done or in progress.",
            }, Json.Options);
        }

        await store.UpsertRevisionAsync(new RevisionDoc
        {
            Id = revisionId,
            ProjectId = projectId,
            Stage = stage,
            Target = target,
            Reason = reason,
            Cas = effectiveCas,
            ComponentId = effectiveComponentId,
            Status = RevisionStatus.Pending,
            // ALWAYS "O" — the revision audit trail is ordered by a LEXICOGRAPHIC sort on this field, which
            // is only chronological while every writer uses the same fixed-width format (RevisionDoc.CreatedAt).
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, ct);

        // The operator's reason goes into the summary VERBATIM — no stripping, no escaping. Collapsing line
        // breaks is ChatThread.Render's job and it does it for every line of every turn; a second strip here
        // would be a blocklist growing back, and it would corrupt the one artifact in this system that exists
        // in no corpus.
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
        // the direct edit Law 4 forbids, so it is refused and the model is pointed at apply_revision.
        //
        // This guard is also load-bearing for the re-trigger below: StageDispatcher.OnProjectAsync runs
        // Intake only when the stage is `pending` AND no constraints exist. Loosen it and record_answer would
        // reopen a stage the dispatcher then refuses to run — intake stuck at `pending` forever.
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
        // OnProjectAsync runs Intake exactly when the stage is `pending` and no constraints exist yet — the
        // second condition being the one the guard above has already established.
        //
        // Replay-safe by construction, unlike apply_revision: the patch SETS one field to one value, so
        // re-applying it is a no-op, and re-`pending`ing an already-pending stage changes nothing.
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

    /// A stable fingerprint of ONE apply_revision call — the thing that makes a replayed turn converge
    /// instead of collide.
    ///
    /// SHA-256, NOT string.GetHashCode(): .NET randomises string hash codes PER PROCESS, so a
    /// GetHashCode-derived id would differ across a restart — i.e. it would fail in precisely the
    /// crash-and-redeliver scenario this whole mechanism exists for, while passing every single-process test.
    /// The repo already sets the precedent in LearnedConclusionProjection.SearchKey. ChatToolsTests pins a
    /// GOLDEN id so a swap back to a per-process hash cannot pass unnoticed.
    ///
    /// Each field is LENGTH-PREFIXED rather than delimiter-joined: any delimiter that can occur inside a
    /// field lets two different calls encode to the same bytes, and a collision here is one revision
    /// silently overwriting another — the very failure this method exists to prevent.
    private static string ContentKey(string target, string reason, string? cas, string? componentId)
    {
        var tuple = new StringBuilder();
        foreach (var field in new[] { target, reason, cas ?? "", componentId ?? "" })
        {
            var f = field.Trim();
            tuple.Append(f.Length).Append(':').Append(f);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tuple.ToString())))
            .ToLowerInvariant()[..12];
    }

    /// `chatKey` is concatenated into a Cosmos item id. Cosmos rejects an id containing '/', '\', '?' or '#'
    /// (400 BadRequest), and an in-memory test store — a plain dictionary — accepts every one of them: green
    /// in tests, broken in Azure, which is the exact shape of bugs this repo has already shipped. So the key
    /// is constrained to an id-safe token, and a violation throws at CONSTRUCTION, where it is legible.
    private static string IdSafe(string key) =>
        Regex.IsMatch(key ?? "", "^[A-Za-z0-9_-]+$")
            ? key!
            : throw new ArgumentException(
                "chatKey must be an id-safe token ([A-Za-z0-9_-]+) — it becomes part of a Cosmos item id, " +
                $"which cannot contain '/', '\\', '?' or '#'. Got: '{key}'", nameof(key));

    /// A tool error is not an exception: an escaping exception fails the whole turn, and this text is the
    /// only thing that teaches the model to correct itself.
    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json.Options);
}

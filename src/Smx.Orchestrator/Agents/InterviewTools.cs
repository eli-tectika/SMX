using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The interview agent's tools. Constructed FRESH for each turn, closed over the sessionId of the
/// interview being conducted.
///
/// The binding is the safety property, exactly as in ChatTools. If `sessionId` were a tool PARAMETER,
/// one hallucinated id would let the model write findings into a different operator's interview. The
/// model's schema therefore offers no way to name a session; it can only act on the one it is in.
///
/// NOTE WHAT IS ABSENT — and note that the absence, not the instructions, is what enforces it:
///   * no `search_web`, no `search_regulatory`. The interview elicits what the OPERATOR knows. A
///     regulatory claim must trace to the synced corpus (which is why the Regulatory agent has no web
///     tool and never will), and open-ended search belongs to Discovery, where deterministic rails cap
///     a web-only candidate at Tier B. A web tool on the product's FRONT DOOR would put uncited
///     chemistry into the record at the earliest and least-reviewed point in a project.
///   * nothing that starts the analysis, signs a gate, or records a determination. An agent acts only
///     through its tools. create_project deliberately does NOT start anything (design §2.3).
public sealed class InterviewTools(
    IIntakeSessionStore sessions, IRecordStore records, IAttachmentBlobStore blobs, string sessionId)
{
    public List<string> Trail { get; } = [];

    public IList<AITool> Tools() =>
    [
        AIFunctionFactory.Create(WriteSummaryAsync, "write_summary",
            "Write or rewrite the plain-prose summary of this project. The operator reads this first when " +
            "they open the project, so write it for someone who was not in this conversation. " +
            "Required before create_project will succeed."),

        // The question list is DERIVED from IntakeQuestions, never hand-written here. A question the
        // catalogue accepts but this sentence omits is a question the model never offers to record —
        // it reads the list as exhaustive — and the operator's answer is silently lost.
        AIFunctionFactory.Create(RecordFindingAsync, "record_finding",
            "Record what the operator told you (or what you read in an attachment) about one intake question. " +
            $"`questionId` is one of: {IntakeQuestions.Description}. " +
            "`provenance` is 'operator', or 'file:{fileId}' when you read it out of an attachment, or 'agent' " +
            "when you INFERRED it — and an inference also requires `confidence`. " +
            "Never infer one answer from another and record it as the operator's."),

        AIFunctionFactory.Create(MarkUnknownAsync, "mark_unknown",
            "Record that you ASKED an intake question and the answer is genuinely not known yet. " +
            "This is a real answer, not a failure: an unknown travels with the project as a stated gap. " +
            "Use it rather than pressing the operator for something they do not have."),

        AIFunctionFactory.Create(MarkNotApplicableAsync, "mark_not_applicable",
            "Record that an intake question does not apply to this project, and why."),

        AIFunctionFactory.Create(ProposeComponentsAsync, "propose_components",
            "Propose how this product decomposes into components (bottle, lid, label, liquid…). " +
            "Everything downstream runs PER COMPONENT — there is no product-wide marker. " +
            "Each needs id, material, application, objective (brand or quantification), at least one market, " +
            "and physicalState — the substrate's physical state, e.g. \"liquid\", \"solid\", \"oil-soluble\", " +
            "\"coating\". physicalState drives which marker FORM-CLASS the pool agent proposes right after " +
            "creation (oil-soluble → organocomplex; solid polymer → oxide or salt; coating → a dispersible " +
            "compound), so ASK the operator for it rather than guessing; omit it only if they genuinely do " +
            "not know. " +
            "`components` is a JSON array: " +
            "[{\"id\":\"bottle\",\"material\":\"PET\",\"application\":\"food contact\"," +
            "\"objective\":\"brand\",\"markets\":[\"EU\",\"US\"],\"physicalState\":\"solid\"}]."),

        AIFunctionFactory.Create(ReadAttachmentAsync, "read_attachment",
            "Read the text of a file the operator attached to this interview. `fileId` is the id shown " +
            "beside the filename in the ATTACHMENTS section of your context — never invent one. " +
            "Long documents are paged: `page` defaults to 1 and the reply tells you how many there are. " +
            "Read a file BEFORE asking the operator about what might be in it."),

        AIFunctionFactory.Create(CreateProjectAsync, "create_project",
            "Create the project from everything gathered so far, and write the summary, the dossier, the " +
            "proposed components, the attachments and this conversation into it. " +
            "Call this when the picture is clear enough, or when the operator asks you to. " +
            "It does NOT start the analysis — the operator does that themselves afterwards. " +
            "Tell the operator what is still open BEFORE you call it."),
    ];

    public async Task<string> WriteSummaryAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return "the summary cannot be blank.";
        return await MutateAsync(s => s.Summary = text.Trim(), "write_summary", "summary written.", ct);
    }

    /// `confidence` defaults to null because AIFunctionFactory emits a parameter WITHOUT a default as
    /// `required` in the JSON schema regardless of the description — the binder would then reject every
    /// ordinary operator-sourced call before this body ran. This is the same trap that made
    /// apply_revision dead on arrival for Discovery.
    public async Task<string> RecordFindingAsync(
        string questionId, string answer, string provenance, string? confidence = null,
        CancellationToken ct = default)
    {
        if (IntakeQuestions.ById(questionId) is null)
            return $"'{questionId}' is not an intake question. Use one of: " +
                   $"{string.Join(", ", IntakeQuestions.All.Select(q => q.Id))}.";
        if (string.IsNullOrWhiteSpace(answer))
            return $"'{questionId}' needs a real answer. If the operator does not know, call mark_unknown — " +
                   "recording a blank would mark the question covered while carrying no information.";

        var state = string.Equals(provenance, "agent", StringComparison.OrdinalIgnoreCase)
            ? DossierState.AgentProposed : DossierState.Answered;
        if (state == DossierState.AgentProposed && string.IsNullOrWhiteSpace(confidence))
            return "an agent-proposed answer must carry a `confidence`. Without one it is indistinguishable " +
                   "from something the operator said.";

        return await UpsertEntryAsync(new DossierEntry
        {
            QuestionId = questionId, State = state, Answer = answer.Trim(),
            Provenance = string.IsNullOrWhiteSpace(provenance) ? "operator" : provenance.Trim(),
            Confidence = confidence, RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, "record_finding", ct);
    }

    public Task<string> MarkUnknownAsync(string questionId, string reason, CancellationToken ct = default) =>
        MarkAsync(questionId, reason, DossierState.Unknown, "mark_unknown", ct);

    public Task<string> MarkNotApplicableAsync(string questionId, string reason, CancellationToken ct = default) =>
        MarkAsync(questionId, reason, DossierState.NotApplicable, "mark_not_applicable", ct);

    private async Task<string> MarkAsync(
        string questionId, string reason, string state, string toolName, CancellationToken ct)
    {
        if (IntakeQuestions.ById(questionId) is null)
            return $"'{questionId}' is not an intake question. Use one of: " +
                   $"{string.Join(", ", IntakeQuestions.All.Select(q => q.Id))}.";
        return await UpsertEntryAsync(new DossierEntry
        {
            QuestionId = questionId, State = state,
            Answer = string.IsNullOrWhiteSpace(reason) ? "" : reason.Trim(),
            Provenance = "operator", RecordedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, toolName, ct);
    }

    /// One entry per question, replaced on re-record. The operator corrects themselves mid-interview;
    /// appending would leave the gate reading two contradictory answers for one question.
    private Task<string> UpsertEntryAsync(DossierEntry entry, string toolName, CancellationToken ct) =>
        MutateAsync(s =>
        {
            s.Dossier.RemoveAll(e => string.Equals(e.QuestionId, entry.QuestionId, StringComparison.Ordinal));
            s.Dossier.Add(entry);
        }, $"{toolName}({entry.QuestionId})", $"recorded '{entry.QuestionId}' as {entry.State}.", ct);

    public async Task<string> ProposeComponentsAsync(string components, CancellationToken ct = default)
    {
        List<ComponentSpec>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<ComponentSpec>>(components, Json.Options);
        }
        catch (JsonException e)
        {
            // NEVER throw: the caller is an LLM tool dispatcher and an escaping exception fails the
            // whole turn. The parse error IS the feedback that teaches the model to retry correctly.
            return $"that is not valid JSON ({e.Message}). Send an array like " +
                   "[{\"id\":\"bottle\",\"material\":\"PET\",\"application\":\"food contact\"," +
                   "\"objective\":\"brand\",\"markets\":[\"EU\"]}].";
        }
        if (parsed is not { Count: > 0 }) return "send at least one component.";

        return await MutateAsync(s => s.ProposedComponents = parsed, "propose_components",
            $"recorded {parsed.Count} component(s).", ct);
    }

    /// `page` defaults to 1 because AIFunctionFactory emits a parameter WITHOUT a default as `required`
    /// in the JSON schema regardless of the description — the binder would then reject every ordinary
    /// one-argument call before this body ran. Same trap as `confidence` on record_finding.
    public async Task<string> ReadAttachmentAsync(string fileId, int page = 1, CancellationToken ct = default)
    {
        if (await sessions.GetAsync(sessionId, ct) is not { } session)
            return "this interview session no longer exists. Tell the operator; do not retry.";

        // Resolved THROUGH the session's own attachment list, never by building a path out of fileId.
        // fileId arrives from a language model; interpolating it into a blob path would let a
        // hallucinated or crafted value reach another interview's upload — or anything else in the
        // `bronze` container, which also holds the SDS corpus. The path used below is the one this
        // session recorded at upload.
        if (session.Attachments.FirstOrDefault(a =>
                string.Equals(a.FileId, fileId, StringComparison.Ordinal)) is not { } attachment)
            return session.Attachments.Count == 0
                ? "there are no attachments on this interview."
                : $"'{fileId}' is not an attachment on this interview. The ones there are: " +
                  $"{string.Join(", ", session.Attachments.Select(a => $"{a.FileId} ({a.Filename})"))}.";

        if (attachment.Status != AttachmentStatus.Extracted || attachment.TextBlobPath is not { } textPath)
            return $"'{attachment.Filename}' could not be read ({attachment.Error ?? attachment.Status}). " +
                   "Ask the operator what it contains — their answer is a real answer, and you should " +
                   "record it with record_finding noting which file it describes.";

        if (await blobs.GetTextAsync(textPath, ct) is not { } text)
            return $"'{attachment.Filename}' was extracted but its text is missing from storage. " +
                   "Tell the operator, and ask them what it contains.";

        var pages = Math.Max(1, (int)Math.Ceiling((double)text.Length / AttachmentLimits.PageChars));
        var index = Math.Clamp(page, 1, pages);
        var start = (index - 1) * AttachmentLimits.PageChars;
        var slice = text.Substring(start, Math.Min(AttachmentLimits.PageChars, text.Length - start));

        Trail.Add($"read_attachment({attachment.FileId}, page {index})");
        return $"{attachment.Filename} — page {index} of {pages}\n\n{slice}" +
               (index < pages ? $"\n\n[continues — call read_attachment(\"{fileId}\", {index + 1}) for more]" : "");
    }

    public async Task<string> CreateProjectAsync(CancellationToken ct = default)
    {
        if (await sessions.GetAsync(sessionId, ct) is not { } session)
            return "this interview session no longer exists. Tell the operator; do not retry.";

        // Idempotent: both the model and the transport retry, and a second project would be a silent
        // duplicate of a client's whole engagement.
        if (session.CreatedProjectId is { } already)
            return $"this interview has already created project {already}.";

        if (IntakeGate.Check(session.Client, session.Product, session.Summary,
                session.ProposedComponents, session.Dossier) is { } refusal)
            return refusal;

        var projectId = $"proj-{Guid.NewGuid():N}"[..17];
        var now = DateTimeOffset.UtcNow.ToString("O");

        // The payload is the SAME SHAPE POST /projects writes, so IntakeAgent (which deserializes it
        // into IntakePayload) reads an interview-created project exactly as it reads a form-created
        // one. elementPools is empty and stays empty until Background — see design §6.
        var payload = JsonSerializer.SerializeToElement(new
        {
            components = session.ProposedComponents,
            elementPools = Array.Empty<object>(),
            providedCandidates = Array.Empty<object>(),
            clientRestrictedList = ClientRestrictions(session),
            measuredBackground = Array.Empty<object>(),
        }, Json.Options);

        // AwaitingConfirmation, NOT pending: writing this doc must not dispatch intake. The operator
        // presses Start. This one argument is the whole "the agent may create, only the operator may
        // start" line (design §2.3).
        var project = ProjectDoc.Create(projectId, session.Client, session.Product, payload,
            intakeStatus: StageStatus.AwaitingConfirmation);
        project.CreatedAt = now;
        await records.UpsertProjectAsync(project, ct);

        await records.UpsertIntakeBriefAsync(new IntakeBriefDoc
        {
            Id = RecordIds.IntakeBrief(projectId), ProjectId = projectId, SessionId = sessionId,
            Summary = session.Summary, Dossier = session.Dossier,
            Components = session.ProposedComponents, Attachments = session.Attachments,
            Transcript = session.Turns, CreatedAt = now,
        }, ct);

        // Written LAST, so a crash between the two writes leaves the session retryable rather than
        // marked done with no project. The project upsert is idempotent on its id, and the brief on
        // its singular id, so the retry converges.
        await MutateAsync(s =>
        {
            s.CreatedProjectId = projectId;
            s.Status = IntakeSessionStatus.Created;
        }, "create_project", "", ct);

        var open = session.Dossier.Count(e => e.State == DossierState.Unknown);
        return $"created project {projectId}. {open} question(s) carried as unknown. " +
               "The operator now opens it and presses Start Processing — you cannot start it.";
    }

    private static List<string> ClientRestrictions(IntakeSessionDoc s) =>
        s.Dossier.FirstOrDefault(e => e.QuestionId == "client-restrictions" &&
                                      e.State == DossierState.Answered) is { } e
            ? [.. e.Answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
            : [];

    private async Task<string> MutateAsync(
        Action<IntakeSessionDoc> mutate, string trailEntry, string ok, CancellationToken ct)
    {
        if (await sessions.GetAsync(sessionId, ct) is not { } session)
            return "this interview session no longer exists. Tell the operator; do not retry.";
        mutate(session);
        session.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        await sessions.UpsertAsync(session, ct);
        Trail.Add(trailEntry);
        return ok;
    }
}

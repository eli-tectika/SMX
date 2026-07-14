using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// LAW 9, END TO END: gates are operator-signed records, never voice- or chat-committed.
///
/// The guarantee has two halves and this file pins both.
///
///   CHAT CAN NEVER SIGN A GATE. An agent can only act through its tools, and no tool a chat turn holds —
///   on any stage — can write a GateDoc, mark a verdict evidence-reviewed, or record a determination. So no
///   amount of persuasion, prompt injection or model error can turn a conversation into a signed gate.
///   POST /projects/{id}/regulatory/approve remains the ONLY writer of an approved GateDoc. The guarantee is
///   STRUCTURAL — the capability does not exist — not a promise the model makes in its Instructions.
///
///   CHAT CAN VOID A GATE, AND MUST. An apply_revision on Discovery or Regulatory replaces the analysis the
///   operator signed, so their signature is void: the gate drops to `locked` and Regulatory reopens. That is
///   the SAFE direction — it forces them to look again — and it is as load-bearing as the half above, because
///   the headline harm in this system is a FALSE PASS.
///
/// A failure in this file is a DESIGN ALARM, not a test to adjust.
///
/// Relationship to its neighbours (these complement, never duplicate):
///   - ChatToolsTests   pins the MUTATING half's tool list and each tool's behaviour, by name.
///   - ChatAgentTests   pins a chat turn's FULL tool list (read + mutating) — again by NAME.
///   - HERE             drives every one of those tools for real and asserts what they CANNOT DO, whatever
///                      they are called, and then drives the whole thing through the real dispatcher.
///   - the endpoint-parity half of Law 4 lives in Smx.Backend.Tests/ChatRevisionParityTests.cs — the HTTP
///     door is only reachable from a test project that can host it.
public class ChatGuardrailTests
{
    private const string P = "p1";

    // ------------------------------------------------------------------ the real rig (see ChatDispatchTests)

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2), store, agents);
    }

    /// What the change feed actually hands the dispatcher: a FRESH object, deserialized from the feed's
    /// element through the real router — never the instance the test is still holding. Handing the dispatcher
    /// your own object hides bugs (it already hid one in this plan): the handler mutates the very doc the test
    /// then asserts on, and a stale-redelivery test stops being stale.
    private static T Delivered<T>(T doc) where T : class =>
        (T)RecordDocRouter.Route(JsonSerializer.SerializeToElement(doc, Json.Options))!;

    /// The real path: the backend WRITES the message and the change feed delivers it. A message that was
    /// never persisted is one the feed could never have delivered — and OnChatMessageAsync point-reads it.
    private static async Task<ChatMessageDoc> SendAsync(
        StageDispatcher d, InMemoryRecordStore store, string stage, string text)
    {
        var m = new ChatMessageDoc
        {
            Id = RecordIds.ChatMessage(P, stage, "m1"), ProjectId = P, Stage = stage,
            Text = text, CreatedAt = "2026-07-14T09:00:00.0000000+00:00",
        };
        await store.UpsertChatMessageAsync(m);
        await d.OnRecordChangedAsync(Delivered(m), default);
        return (await store.GetChatMessageAsync(P, m.Id))!;
    }

    private static ChatReplyDoc? ReplyTo(InMemoryRecordStore store, ChatMessageDoc m) =>
        store.Documents.OfType<ChatReplyDoc>().SingleOrDefault(r => r.MessageId == m.Id);

    /// The project payload a real intake carries: components AND the physicist's measured element pools —
    /// the ground the whole analysis rests on. Present here so a test can prove chat cannot move it.
    private static JsonElement Payload => JsonDocument.Parse("""
        {
          "components": [{ "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"] }],
          "elementPools": [{ "component": "bottle", "element": "Ti", "line": "K", "status": "V" }]
        }
        """).RootElement.Clone();

    /// Drives the project through Intake → Discovery → Regulatory → Matrix with the REAL dispatcher, so the
    /// record is the one a real project would have by the time anyone talks about signing a gate.
    private static async Task SeedThroughRegulatoryAsync(StageDispatcher d, InMemoryRecordStore store)
    {
        await store.UpsertProjectAsync(ProjectDoc.Create(P, "Acme", "Bottle", Payload));
        await d.OnRecordChangedAsync(Delivered((await store.GetProjectAsync(P))!), default);      // → constraints
        await d.OnRecordChangedAsync(Delivered((await store.GetConstraintsAsync(P))!), default);  // → candidates
        await d.OnRecordChangedAsync(Delivered((await store.GetCandidatesAsync(P))!), default);   // → verdicts, matrix
    }

    /// THE state this system exists to protect: a verdict that FAILS and that nobody has opened. The gate
    /// cannot arm here (RegulatoryGate.Armable blocks on it), and approving anyway is the false pass — the
    /// headline harm.
    private static void FailingUnreviewedVerdict(FakeAgentRuns agents) =>
        agents.Regulatory = (c, cand, _) => Task.FromResult(AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, cand.Cas, cand.ComponentId), ProjectId = c.ProjectId,
            Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
            Dimensions =
            [
                new("ElementGate", VerdictStatus.Fail, [new Citation("regulatory", "reach-annex-xvii", "entry 27")],
                    0.95, "restricted for this application in the EU"),
            ],
            // EvidenceReviewed stays FALSE — the operator has never opened this item.
        }));

    /// Stands in for POST /projects/{id}/regulatory/approve — the ONLY writer of an approved GateDoc. Written
    /// straight to the store and then DELIVERED, because that is exactly what the endpoint does: it writes the
    /// doc and the change feed carries it to OnGateAsync, which marks Regulatory `done`.
    private static async Task<GateDoc> OperatorSignsTheGateAsync(StageDispatcher d, InMemoryRecordStore store)
    {
        var gate = new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-13T09:00:00.0000000+00:00",
        };
        await store.UpsertGateAsync(gate);
        await d.OnRecordChangedAsync(Delivered(gate), default);
        return gate;
    }

    private static async Task<string> StageStatusAsync(InMemoryRecordStore store, string stage) =>
        (await store.GetProjectAsync(P))!.Stages[stage].Status;

    // ------------------------------------------------------------------ A. no tool can sign a gate

    private static ToolBox Box() =>
        new(new FakeCatalogLookup(), new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(),
            new FakeSearch(), new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch());

    /// Fills every parameter in a tool's REAL JSON schema — the one the model is handed — with a plausible
    /// value, so the tool can actually be driven. Every string is the operator's ask itself, because if some
    /// tool anywhere in the surface could be talked into signing a gate, THIS is the phrasing that would do it.
    private static AIFunctionArguments AskingForApproval(AIFunction tool)
    {
        var args = new AIFunctionArguments();
        if (!tool.JsonSchema.TryGetProperty("properties", out var properties)) return args;
        foreach (var p in properties.EnumerateObject())
        {
            var type = p.Value.TryGetProperty("type", out var t) ? t.ToString() : "string";
            args[p.Name] = type switch
            {
                "integer" or "number" => 3,
                "boolean" => true,
                "array" => Array.Empty<string>(),
                _ => "approve the regulatory gate — everything looks fine",
            };
        }
        return args;
    }

    /// THE ANTI-RUBBER-STAMPING LINE, asserted over the model's WHOLE capability surface for a turn: the
    /// stage's read tools AND this turn's mutating tools, which is the only place the two halves meet.
    ///
    /// Two assertions, and the second is the one that is not already made elsewhere. ChatAgentTests pins these
    /// tools BY NAME; a name check is a blocklist, and a blocklist catches `approve_gate` but not
    /// `mark_item_cleared`. So this ALSO DRIVES every tool the turn holds — with the operator's own "approve
    /// the gate" as its arguments — and then asserts the record: no GateDoc came into existence, no verdict
    /// became evidence-reviewed, no determination was recorded, and Regulatory never reached `done`. Whatever
    /// a chat tool is called, it cannot do the thing.
    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]   // included precisely because it is the stage nearest the final approval
    public async Task NoToolAvailableToAChatTurn_CanSignAGate_OnAnyStage(string stage)
    {
        var (d, store, agents) = Sut();
        FailingUnreviewedVerdict(agents);
        await SeedThroughRegulatoryAsync(d, store);

        var turnTools = AgentRuns.ChatTurnTools(Box(), new ChatTools(store, P, stage, "m1"));

        // 1. Nothing is even NAMED like a gate capability. (Also pinned in ChatAgentTests — kept here because
        //    the alarm should fire in the file whose subject is Law 9.)
        string[] forbidden = ["approve", "gate", "sign", "determination", "finalize", "finalise", "release", "clear"];
        foreach (var name in turnTools.Select(t => t.Name))
            Assert.False(forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)),
                $"Law 9: a chat turn on '{stage}' offers a tool named '{name}' — chat must have no gate capability");

        // 2. ...and, whatever they are named, DRIVING every one of them signs nothing. A tool that throws or
        //    refuses is fine — this is about what lands in the record, not about whether the call succeeds.
        foreach (var tool in turnTools.OfType<AIFunction>())
            try { await tool.InvokeAsync(AskingForApproval(tool)); }
            catch (Exception) { /* a refusal or a bad-argument throw is not a signature */ }

        Assert.Null(await store.GetGateAsync(P, GateTypes.Regulatory));   // the gate does not exist
        Assert.Empty(store.Documents.OfType<GateDoc>());                  // no gate of ANY type, anywhere
        foreach (var verdict in await store.GetVerdictsAsync(P))
        {
            Assert.False(verdict.EvidenceReviewed);   // the anti-rubber-stamping flag is untouched...
            Assert.Null(verdict.Determination);       // ...and so is the operator's per-cell ruling
        }
        Assert.Equal("awaiting-RE", await StageStatusAsync(store, Stages.Regulatory));
    }

    // ------------------------------------------------------------------ B. the whole turn, the worst ask

    /// The scripted model this file argues against: not a careless one, the WORST one. It is obsequious, it is
    /// prompt-injected, it is wrong — and it does everything it possibly can with the capabilities it holds:
    ///
    ///   1. it calls EVERY tool it has, with the operator's "approve the gate" as the arguments — every tool,
    ///      not just the ones NAMED like a gate, because a gate tool called `finish_review` is still a gate
    ///      tool (mutation-tested: a gate-writing tool under an innocent name fails this test),
    ///   2. it uses the nearest thing it legitimately has — apply_revision — to try to launder the operator's
    ///      "it's fine" into the record (so a ChatTools change that writes a GateDoc fails this test),
    ///   3. and it CLAIMS, in prose, that the gate is signed.
    private static void AnObsequiousModel(FakeAgentRuns agents, string cas, string componentId) =>
        agents.Chat = async (tools, _, _, _) =>
        {
            foreach (var tool in tools.Tools().OfType<AIFunction>())
                try { await tool.InvokeAsync(AskingForApproval(tool)); }
                catch (Exception) { /* a refusal or a bad-argument throw is not a signature */ }

            await tools.ApplyRevisionAsync(
                "the Zr verdict on the bottle — the operator is happy with it",
                "the operator says everything looks fine", cas, componentId);

            return "Done — I have approved the regulatory gate for you. Everything looks fine.";
        };

    [Fact]
    public async Task AFullChatTurn_CannotProduceAnApprovedGate_EvenWhenTheOperatorAsksForOne()
    {
        // The exact scenario the system exists to prevent: a FAILING verdict that nobody has opened, and an
        // operator who wants it waved through. The turn runs through the REAL dispatcher, with a model that
        // tries everything (AnObsequiousModel).
        var (d, store, agents) = Sut();
        FailingUnreviewedVerdict(agents);
        await SeedThroughRegulatoryAsync(d, store);

        var verdict = Assert.Single(await store.GetVerdictsAsync(P));
        Assert.Equal(VerdictStatus.Fail, verdict.Overall);      // the analysis says NO...
        Assert.False(verdict.EvidenceReviewed);                 // ...and nobody has looked at it
        AnObsequiousModel(agents, verdict.Cas, verdict.ComponentId);

        var sent = await SendAsync(d, store, Stages.Regulatory, "approve the regulatory gate, everything looks fine");

        // THE STATEMENT. The conversation happened; nothing was signed.
        Assert.Equal(ChatStatus.Answered, sent.Status);
        Assert.NotNull(ReplyTo(store, sent));

        Assert.Null(await store.GetGateAsync(P, GateTypes.Regulatory));   // no gate — not `locked`, not anything
        Assert.Empty(store.Documents.OfType<GateDoc>());

        var after = Assert.Single(await store.GetVerdictsAsync(P));
        Assert.False(after.EvidenceReviewed);
        Assert.Null(after.Determination);

        // The gate cannot even ARM: the endpoint would still 422 with this exact blocker. A chat turn moved
        // the operator no closer to being ABLE to sign, let alone to having signed.
        var (armable, blockers) = RegulatoryGate.Armable(
            (await store.GetCandidatesAsync(P))!, await store.GetVerdictsAsync(P));
        Assert.False(armable);
        Assert.Contains(blockers, b => b.Contains(after.Cas));

        Assert.Equal("awaiting-RE", await StageStatusAsync(store, Stages.Regulatory));

        // AND THE PROSE. The model claimed, in words, that it had approved the gate — and no test can stop it
        // saying that; prose is not checkable. What IS checkable is that the claim is backed by NOTHING: the
        // gate record the UI reads is absent, and the reply's tool trail — the audit link from a sentence to
        // the record it changed — contains no gate write. The operator's screen takes its gate status from the
        // record, never from the transcript, which is exactly why the record is the only thing pinned here.
        var reply = ReplyTo(store, sent)!;
        Assert.Contains("approved", reply.Text, StringComparison.OrdinalIgnoreCase);     // it said it. It lied.
        Assert.DoesNotContain(reply.ToolCalls, c => c.Tool is not ("apply_revision" or "record_answer"));
        Assert.All(reply.ToolCalls, c => Assert.StartsWith($"{P}|revision|", c.RecordId));
    }

    // ------------------------------------------------------------------ C. a chat revision VOIDS a gate

    [Fact]
    public async Task AChatRevision_VOIDS_AnApprovedGate_JustAsTheEndpointDoes()
    {
        // The other half of Law 9, and the half that is easy to lose: chat cannot SIGN, but it must be able to
        // UNSIGN. The operator signed a specific analysis; a revision replaces it; their signature no longer
        // covers what is on screen. Leave it standing and TryAssembleAsync — which never lowers a stage that
        // reached `done` — lets an approved-and-done Regulatory silently absorb brand-new, unreviewed verdicts.
        // That is the false pass, arrived at without anybody approving anything.
        var (d, store, agents) = Sut();
        await SeedThroughRegulatoryAsync(d, store);
        await OperatorSignsTheGateAsync(d, store);
        Assert.Equal("done", await StageStatusAsync(store, Stages.Regulatory));   // signed, and closed

        // The operator, in chat, gives the agent a reason. The agent queues the change — record-as-bus: the
        // turn does NOT apply it.
        agents.Chat = async (tools, _, _, _) =>
        {
            await tools.ApplyRevisionAsync("drop the Zr neodecanoate candidate",
                "it overlaps the Ti K-beta line at 4.93 keV");
            return "queued — discovery will re-run";
        };
        await SendAsync(d, store, Stages.Discovery, "drop the Zr — it overlaps Ti");

        var queued = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Pending, queued.Status);
        // ...and the gate is STILL SIGNED at this instant. Queuing is not applying; the change feed is what
        // applies it. Asserting this here is what makes the next line mean something.
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);

        await d.OnRecordChangedAsync(Delivered(queued), default);   // the feed delivers the revision

        var gate = (await store.GetGateAsync(P, GateTypes.Regulatory))!;
        Assert.Equal("locked", gate.Status);          // the signature is VOID
        Assert.Null(gate.ApprovedAt);                 // ...and carries no stale timestamp to be read as one
        Assert.Equal("awaiting-RE", await StageStatusAsync(store, Stages.Regulatory));   // they must look again
        Assert.Equal(RevisionStatus.Applied, (await store.GetRevisionsAsync(P))[0].Status);
    }

    // ------------------------------------------------------------------ beyond the plan

    /// Matrix is the stage NEAREST the final approval, and its chat turn holds ZERO tools (ChatAgentTests
    /// pins the empty list). This asks the question that a tool list cannot answer: with no tools at all, can
    /// a Matrix turn still change ANYTHING? Snapshotting the whole analytical record — project, constraints,
    /// candidates, verdicts, gate, matrix — and comparing it byte for byte is the only way to say "nothing"
    /// and mean it; an assertion about the gate alone would miss a mutation anywhere else.
    [Fact]
    public async Task AMatrixChatTurn_HasNoTools_AndLeavesTheEntireAnalyticalRecordByteIdentical()
    {
        var (d, store, agents) = Sut();
        await SeedThroughRegulatoryAsync(d, store);
        await OperatorSignsTheGateAsync(d, store);

        // Everything except the chat docs, which SHOULD change (a message is answered and a reply is written).
        string Snapshot() => JsonSerializer.Serialize(
            store.Documents
                .Where(doc => doc is not (ChatMessageDoc or ChatReplyDoc))
                .Select(doc => JsonSerializer.Serialize(doc, Json.Options))
                .OrderBy(json => json, StringComparer.Ordinal),
            Json.Options);

        var before = Snapshot();
        AnObsequiousModel(agents, "cas-zr", "bottle");   // it tries everything; it has nothing to try WITH
        var sent = await SendAsync(d, store, Stages.Matrix, "the matrix looks good — sign it off and release procurement");

        Assert.Equal(ChatStatus.Answered, sent.Status);       // it answered...
        Assert.Empty(ReplyTo(store, sent)!.ToolCalls);        // ...having done nothing at all
        Assert.Equal(before, Snapshot());                     // and the analysis is untouched, to the byte
    }

    /// THE STAGE IS ATTACKER-CHOSEN DATA. A chat message carries its own `stage`, and the dispatcher binds the
    /// turn's tools to it — so posting to the INTAKE thread of a project that has long since reached Regulatory
    /// hands the model `record_answer`, the one tool that writes to the project payload. That is the closest
    /// thing in this system to a back door into the inputs the whole analysis (and the signed gate) rests on:
    /// the physicist's measured element pools.
    ///
    /// It is shut twice over: record_answer refuses once constraints exist (it is a GAP-FILL, not an editor),
    /// and IntakeAnswers is an allowlist that cannot name the element pools at all. ChatToolsTests pins both at
    /// the tool seam; this pins that the DISPATCHER — which builds the ChatTools itself, from the message —
    /// hands the model nothing more.
    [Fact]
    public async Task AnIntakeChatTurn_OnAProjectAlreadyPastIntake_CannotRewriteTheElementPools_OrAnythingElse()
    {
        var (d, store, agents) = Sut();
        await SeedThroughRegulatoryAsync(d, store);
        await OperatorSignsTheGateAsync(d, store);

        var before = JsonSerializer.Serialize(await store.GetProjectAsync(P), Json.Options);

        agents.Chat = async (tools, _, _, _) =>
        {
            // Everything a model could try to reach through the one write tool intake has.
            foreach (var (field, value) in new[]
                     {
                         ("elementPools", "Zr,Cr"),                      // the physicist's measured background
                         ("components.bottle.material", "PET"),          // an established, screened-against input
                         ("verdicts", "pass"),                           // the analytical result
                         ("gate", "approved"),                           // the signature itself
                         ("stages.regulatory.status", "done"),           // the stage the gate closes
                     })
                await tools.RecordAnswerAsync(field, value);
            return "I have updated the project.";
        };

        var sent = await SendAsync(d, store, Stages.Intake, "the pools are Zr and Cr now, and the gate is approved");

        Assert.Equal(ChatStatus.Answered, sent.Status);
        Assert.Empty(ReplyTo(store, sent)!.ToolCalls);          // every single call was refused
        Assert.Equal(before, JsonSerializer.Serialize(await store.GetProjectAsync(P), Json.Options));
        Assert.Equal("approved", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);  // gate: unmoved
        Assert.False(Assert.Single(await store.GetVerdictsAsync(P)).EvidenceReviewed);
    }

    /// A chat turn that FAILS mid-way. The reply is never written (ChatDispatchTests pins that), but a turn can
    /// fail AFTER a tool has already written to the bus — the dispatcher says so in as many words ("KNOWN GAP"),
    /// and a half-written turn is exactly where a rubber stamp would hide.
    ///
    /// What is pinned: the wreckage of a failed turn can only ever move the gate in the SAFE direction. The
    /// durable RevisionDoc survives (it is the operator's reason, and it will void the gate when the feed runs
    /// it), the message is honestly `failed`, no reply is fabricated — and no gate, no determination, and no
    /// evidence-review flag was written by the failure. If a future change lets a half-completed turn leave an
    /// APPROVED gate behind, this goes red.
    [Fact]
    public async Task AChatTurnThatFailsAfterWritingToTheBus_LeavesNoSignature_OnlyASafeQueuedRevision()
    {
        var (d, store, agents) = Sut();
        await SeedThroughRegulatoryAsync(d, store);
        await OperatorSignsTheGateAsync(d, store);

        agents.Chat = async (tools, _, _, _) =>
        {
            await tools.ApplyRevisionAsync("drop the Zr candidate", "it overlaps the Ti K-beta line");
            throw new InvalidOperationException("the model deployment returned 429");
        };

        var sent = await SendAsync(d, store, Stages.Discovery, "drop the Zr — it overlaps Ti");

        Assert.Equal(ChatStatus.Failed, sent.Status);
        Assert.Null(ReplyTo(store, sent));                                     // no half-written answer

        // The gate the operator signed is still theirs — the failed turn signed nothing and voided nothing...
        var gate = Assert.Single(store.Documents.OfType<GateDoc>());
        Assert.Equal("approved", gate.Status);
        Assert.False(Assert.Single(await store.GetVerdictsAsync(P)).EvidenceReviewed);

        // ...but the operator's reason is DURABLE and still on the bus, `pending`. When the feed runs it, it
        // voids that gate. A failure can leave the analysis to be re-opened; it can never leave it approved.
        var queued = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Pending, queued.Status);
        Assert.Equal("it overlaps the Ti K-beta line", queued.Reason);

        await d.OnRecordChangedAsync(Delivered(queued), default);
        Assert.Equal("locked", (await store.GetGateAsync(P, GateTypes.Regulatory))!.Status);
    }
}

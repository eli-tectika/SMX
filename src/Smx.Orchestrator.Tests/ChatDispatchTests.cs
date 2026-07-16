using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// The conversational surface, end to end through the bus (design §5). The backend cannot run an agent —
/// writing a ChatMessageDoc IS the dispatch — so everything about a chat turn that can go wrong goes wrong
/// HERE: the thread that is the agent's only memory, the stage scoping that keeps two agents' conversations
/// apart, and the at-least-once redelivery that could re-run a turn which has already queued a revision.
public class ChatDispatchTests
{
    private const string P = "p1";

    private static (StageDispatcher Dispatcher, InMemoryRecordStore Store, FakeAgentRuns Agents) Sut()
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new StageDispatcher(store, agents, conclusions, 2), store, agents);
    }

    /// A project driven to Discovery: constraints and candidates are in the record, so there IS a stage
    /// output for the operator to ask about — and to revise.
    private static async Task SeedAsync(StageDispatcher d, InMemoryRecordStore store)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(project);
        await d.OnRecordChangedAsync(project, default);                                // intake → constraints
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync(P))!, default);  // discovery → candidates
    }

    private static ChatMessageDoc Message(string stage, string key, string text, string createdAt) => new()
    {
        Id = RecordIds.ChatMessage(P, stage, key), ProjectId = P, Stage = stage,
        Text = text, CreatedAt = createdAt,
    };

    /// A turn that already ran. Seeding a PENDING prior message would leave a second live trigger lying in
    /// the record, which is not the state a rehydration test is about.
    private static ChatMessageDoc Answered(ChatMessageDoc m) { m.Status = ChatStatus.Answered; return m; }

    /// What the change feed actually hands the dispatcher: a FRESH object, deserialized from the feed's
    /// element (through the real router), never the instance the caller is still holding.
    ///
    /// This is not ceremony — it is the difference between a real idempotency test and a vacuous one. Pass
    /// the dispatcher the test's OWN doc and a handler that trusts the feed's snapshot will mutate that very
    /// object to `answered`; the "stale pending redelivery" the test then performs is no longer stale, and the
    /// test goes green against a dispatcher that in production re-runs the turn. (Mutation-tested: dropping
    /// the dispatcher's point-read survives without this.)
    private static ChatMessageDoc Delivered(ChatMessageDoc m) =>
        (ChatMessageDoc)RecordDocRouter.Route(JsonSerializer.SerializeToElement(m, Smx.Domain.Json.Options))!;

    /// The real path, and the reason the tests do not just hand a doc to the dispatcher: the backend WRITES
    /// the message, and the change feed then delivers it. A message that was never persisted is one the feed
    /// could never have delivered.
    private static async Task<ChatMessageDoc> SendAsync(StageDispatcher d, InMemoryRecordStore store, ChatMessageDoc m)
    {
        await store.UpsertChatMessageAsync(m);
        await d.OnRecordChangedAsync(Delivered(m), default);
        return (await store.GetChatMessageAsync(P, m.Id))!;
    }

    private static ChatReplyDoc? ReplyTo(InMemoryRecordStore store, ChatMessageDoc m) =>
        store.Documents.OfType<ChatReplyDoc>().SingleOrDefault(r => r.MessageId == m.Id);

    [Fact]
    public async Task ChatMessage_WritesAReply_AndMarksTheMessageAnswered()
    {
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);

        var sent = await SendAsync(d, store, Message(Stages.Discovery, "m1", "why is Zr tier A here?", "2026-07-14T09:00:00.0000000+00:00"));

        Assert.Equal(1, agents.ChatCalls);

        // The reply is a RECORD, not a returned value: the backend that wrote the message is a different
        // process from the orchestrator that answered it, so the answer only reaches the operator by landing
        // on the bus. Its id is derived from the message's key — see the redelivery test.
        var reply = ReplyTo(store, sent);
        Assert.NotNull(reply);
        Assert.Equal(RecordIds.ChatReply(P, Stages.Discovery, "m1"), reply!.Id);
        Assert.Equal(Stages.Discovery, reply.Stage);
        Assert.Equal("Echo: why is Zr tier A here?", reply.Text);   // the OPERATOR's message reached the agent
        Assert.Equal(P, reply.ProjectId);
        Assert.NotEmpty(reply.CreatedAt);

        Assert.Equal(ChatStatus.Answered, sent.Status);
        Assert.Null(sent.Error);
    }

    [Fact]
    public async Task ChatMessage_RehydratesThePriorThreadIntoThePrompt()
    {
        // THE test for Law 6. The MAF session is fresh every turn and cannot be rehydrated, so the agent
        // remembers NOTHING: this rendered thread is its entire memory of the conversation. Drop it and the
        // operator's third message is answered by an agent that has never heard the first two — on Thursday,
        // about a conversation it had on Monday.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);

        // A turn that already happened, both halves of it.
        await store.UpsertChatMessageAsync(Answered(Message(Stages.Discovery, "m1",
            "why did you drop the sulfate?", "2026-07-14T09:00:00.0000000+00:00")));
        await store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Discovery, "m1"), ProjectId = P, Stage = Stages.Discovery,
            MessageId = RecordIds.ChatMessage(P, Stages.Discovery, "m1"),
            Text = "it overlaps the Ti K-beta line at 4.93 keV",
            CreatedAt = "2026-07-14T09:00:01.0000000+00:00",
        });

        string? thread = null, inputs = null, message = null;
        agents.Chat = (_, t, i, msg) => { thread = t; inputs = i; message = msg; return Task.FromResult("ok"); };

        await SendAsync(d, store, Message(Stages.Discovery, "m2", "and the neodecanoate?", "2026-07-14T09:05:00.0000000+00:00"));

        // BOTH halves of the prior turn reach the agent. Only the operator's half would leave the agent
        // unable to see what it already told them — free to contradict a citation it gave one message ago.
        Assert.Contains("Operator: why did you drop the sulfate?", thread);
        Assert.Contains("You: it overlaps the Ti K-beta line at 4.93 keV", thread);

        // The thread is PRIOR conversation only. The message being answered is already in the record — the
        // backend's write is what dispatched this turn — so an unfiltered read would hand the agent its own
        // new question as "conversation so far" and then repeat it as the new message.
        Assert.Equal("and the neodecanoate?", message);
        Assert.DoesNotContain("and the neodecanoate?", thread);

        // ...and the agent is answering ABOUT something: the stage's CURRENT record, not just the chatter.
        // Without it the agent would be reasoning about a candidate set it cannot see.
        Assert.Contains("neodecanoate", inputs);   // the seeded candidate's form
        Assert.Contains("cas-zr", inputs);
    }

    [Fact]
    public async Task ChatMessage_ExcludesTheMessageBeingAnswered_ByIdAndNotByTimestamp()
    {
        // The obvious way to render "prior conversation only" is a time predicate — everything before the new
        // message. It works right up until two writes land on the same tick, and then it silently eats a turn.
        // That is not hypothetical here: a reply can carry the SAME CreatedAt as the message it answers (it is
        // why ChatTurns.InOrder needs a tiebreak at all), and a fixed-width "O" timestamp makes ties cheap.
        //
        // So the prior REPLY below shares the new message's timestamp exactly. A time filter drops it and the
        // agent loses the answer it gave one turn ago — free to contradict its own citation. An id filter
        // keeps it, because an id is what actually identifies the one turn that must not be its own history.
        const string tick = "2026-07-14T09:05:00.0000000+00:00";
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);

        await store.UpsertChatMessageAsync(Answered(Message(Stages.Discovery, "m1",
            "why did you drop the sulfate?", "2026-07-14T09:00:00.0000000+00:00")));
        await store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Discovery, "m1"), ProjectId = P, Stage = Stages.Discovery,
            MessageId = RecordIds.ChatMessage(P, Stages.Discovery, "m1"),
            Text = "it overlaps the Ti K-beta line at 4.93 keV",
            CreatedAt = tick,                       // the same clock tick as the message below
        });

        string? thread = null;
        agents.Chat = (_, t, _, _) => { thread = t; return Task.FromResult("ok"); };

        await SendAsync(d, store, Message(Stages.Discovery, "m2", "and the neodecanoate?", tick));

        Assert.Contains("You: it overlaps the Ti K-beta line at 4.93 keV", thread);   // kept, despite the tie
        Assert.DoesNotContain("and the neodecanoate?", thread);                       // and only THIS one dropped
    }

    [Fact]
    public async Task ChatMessage_OnTheFirstTurn_TellsTheAgentThereIsNoPriorConversation()
    {
        // ChatThread.Render has a branch for an empty thread — "(This is the operator's first message in this
        // stage — there is no prior conversation.)" — and it exists to stop the agent inventing a history it
        // never had. The dispatcher is its ONLY production caller, so if the thread it renders always contains
        // the message being answered, that branch is unreachable and the intent behind it is quietly dead: on
        // the very first turn the agent would instead be shown a one-message "CONVERSATION SO FAR", which
        // reads as context it should already have answered.
        //
        // This test is what pins the wiring to the intent: the branch is reachable, and it is reached.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);

        string? thread = null;
        agents.Chat = (_, t, _, _) => { thread = t; return Task.FromResult("ok"); };

        await SendAsync(d, store, Message(Stages.Discovery, "m1", "why is Zr tier A here?", "2026-07-14T09:00:00.0000000+00:00"));

        Assert.Equal(ChatThread.Render([]), thread);              // the empty-thread branch, exactly
        Assert.Contains("no prior conversation", thread);
        Assert.DoesNotContain("why is Zr tier A here?", thread);  // the new message is NOT its own history
    }

    [Fact]
    public async Task ChatMessage_ThreadsAreScopedToTheirStage()
    {
        // Agents do not share a conversation (Law 9), so neither do their threads. A Discovery exchange
        // reaching the Regulatory agent would let it "recall" reasoning it never did — and then answer a
        // regulatory question from a discovery premise, in a system whose whole discipline is that every
        // claim traces to a cited source for THAT stage.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync(P))!, default);   // regulatory → verdicts

        // Two stages, each with a conversation of its own. Both are in the same Cosmos partition, so only the
        // stage predicate keeps them apart.
        await store.UpsertChatMessageAsync(Answered(Message(Stages.Discovery, "m1",
            "drop the sulfate, it overlaps Ti", "2026-07-14T09:00:00.0000000+00:00")));
        await store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Discovery, "m1"), ProjectId = P, Stage = Stages.Discovery,
            MessageId = RecordIds.ChatMessage(P, Stages.Discovery, "m1"),
            Text = "the sulfate is gone from the candidate set",
            CreatedAt = "2026-07-14T09:00:01.0000000+00:00",
        });
        await store.UpsertChatMessageAsync(Answered(Message(Stages.Regulatory, "r0",
            "which markets are in scope?", "2026-07-14T09:30:00.0000000+00:00")));
        await store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Regulatory, "r0"), ProjectId = P, Stage = Stages.Regulatory,
            MessageId = RecordIds.ChatMessage(P, Stages.Regulatory, "r0"),
            Text = "EU only, per the constraints",
            CreatedAt = "2026-07-14T09:30:01.0000000+00:00",
        });

        string? thread = null, inputs = null;
        agents.Chat = (_, t, i, _) => { thread = t; inputs = i; return Task.FromResult("ok"); };

        await SendAsync(d, store, Message(Stages.Regulatory, "r1", "is Zr cleared for EU food contact?", "2026-07-14T10:00:00.0000000+00:00"));

        // The Regulatory agent gets its OWN prior turn...
        Assert.Contains("Operator: which markets are in scope?", thread);
        Assert.Contains("You: EU only, per the constraints", thread);
        // ...and not one word of the Discovery conversation.
        Assert.DoesNotContain("sulfate", thread);
        Assert.DoesNotContain("the sulfate is gone from the candidate set", thread);

        // The stage inputs are scoped the same way: Regulatory is answering about its VERDICTS, not about
        // Discovery's candidate set. Hand it the wrong stage's record and it answers a regulatory question
        // from a discovery premise — with a citation, which makes it look checked.
        Assert.Contains("ElementGate", inputs);        // a verdict dimension; candidates have none
        Assert.DoesNotContain("\"tier\"", inputs);     // a candidate field; verdicts have none
    }

    [Fact]
    public async Task ChatMessage_OnDosing_SeesTheDosingDoc_NotEmptyInputs()
    {
        // Adding `dosing` to Stages.All opened POST /stages/dosing/chat immediately. Without a
        // StageInputsJsonAsync arm the dispatcher would hand the agent "{}" — a confident conversation about
        // a dosing analysis it cannot see. This pins the arm: the turn answers ABOUT the DosingDoc.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);
        await store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P,
            Windows = [new PpmWindow("bottle", "1314-36-9", "Y",
                Floor: new Bound(12.0, "3-sigma over measured background", BoundKinds.Measured, 1.0),
                Upper: new Bound(1200.0, "solubility estimate", BoundKinds.Estimate, 0.4),
                RecommendedPpm: 600.0, QuantificationPpm: 40.0)],
            GeneratedAt = "2026-07-15T00:00:00Z",
        });

        string? inputs = null;
        agents.Chat = (_, _, i, _) => { inputs = i; return Task.FromResult("ok"); };

        await SendAsync(d, store, Message(Stages.Dosing, "d1", "why 600 ppm for Y?", "2026-07-15T09:00:00.0000000+00:00"));

        // The DosingDoc's content reached the agent — its CAS and its recommended ppm. A "{}" arm carries
        // neither, so reverting StageInputsJsonAsync's Dosing arm turns this red.
        Assert.Contains("1314-36-9", inputs);
        Assert.Contains("600", inputs);
    }

    [Fact]
    public async Task ChatMessage_OnCost_SeesTheCostDoc_NotEmptyInputs()
    {
        // Cost holds no tools (it is deterministic), but the chat surface is still a read-only Q&A over the
        // finished audit — so the CostDoc must reach the agent as inputs, not "{}". This is the other half of
        // the "one commit makes the stage live" wiring: the arm exists and carries the audit.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);
        await store.UpsertCostAsync(new CostDoc
        {
            Id = RecordIds.Cost(P), ProjectId = P, GeneratedAt = "2026-07-15T00:00:00Z",
            Substances = [new SupplierAudit("10035-04-8", "Zr", ["OnlySource"],
                BestQuote: null, PriceNote: "single listing", Risks: ["single-source"])],
        });

        string? inputs = null;
        agents.Chat = (_, _, i, _) => { inputs = i; return Task.FromResult("ok"); };

        await SendAsync(d, store, Message(Stages.Cost, "c1", "is Zr single-source?", "2026-07-15T09:00:00.0000000+00:00"));

        Assert.Contains("10035-04-8", inputs);
        Assert.Contains("single-source", inputs);
    }

    /// A project driven to a priced answer: a DosingDoc naming a marker, a CostDoc carrying a cited price,
    /// and a DecisionDoc carrying the agent's proposed final code — enough for the chat surface on ANY of
    /// the new stages to have its own record to read.
    private static async Task SeedCostedProjectAsync(InMemoryRecordStore store)
    {
        var project = ProjectDoc.Create(P, "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        project.Stages[Stages.Dosing].Status = "done";
        project.Stages[Stages.Cost].Status = "done";
        await store.UpsertProjectAsync(project);
        await store.UpsertDosingAsync(new DosingDoc
        {
            Id = RecordIds.Dosing(P), ProjectId = P, GeneratedAt = "t",
            // A code is 2-3 markers (RatioSignature needs a ratio BETWEEN them) — the first is the one the
            // theory asserts reached the agent.
            Codes = [new MarkerCode("bottle",
                [new CodeMarker("1314-36-9", "Y", 20, 0.787, 1, 2), new CodeMarker("10035-04-8", "Zr", 10, 0.5, 1, 2)],
                "r")],
        });
        await store.UpsertCostAsync(new CostDoc
        {
            Id = RecordIds.Cost(P), ProjectId = P, GeneratedAt = "t",
            Substances = [new SupplierAudit("1314-36-9", "Y", ["Acme"],
                new PriceQuote(0.42, "USD", "Acme", "100 g", new Citation("ref-catalog", "ref-catalog/acme", "t")),
                "note", [])],
        });
        await store.UpsertDecisionAsync(new DecisionDoc
        {
            Id = RecordIds.Decision(P), ProjectId = P, GeneratedAt = "t",
            Components = [new ComponentDecision("bottle",
                Rows:
                [
                    new DecisionRow("1314-36-9", "Y", "recommended", 20,
                        Cleared: new ClearedCriteria(Regulatory: true, Dosing: true, Cost: true),
                        Traceability: new TraceRefs(
                            Verdict: RecordIds.Verdict(P, "1314-36-9", "bottle"),
                            Window: RecordIds.Dosing(P), Audit: RecordIds.Cost(P))),
                ],
                ProposedCode: new ProposedCode("Y:Zr = 1.00:0.50", ["1314-36-9", "10035-04-8"],
                    "final code covering both criteria at lowest cost"))],
        });
    }

    /// PLAN-4 TRIPWIRE. Adding `dosing` and `cost` to Stages.All opened POST /stages/{stage}/chat for both,
    /// and the chat surface (Plan 3c) works on every stage in Stages.All. The two StageInputsJsonAsync arms
    /// that give those stages their own record already exist; break either to "{}" and the operator gets an
    /// agent holding a confident conversation about an analysis it cannot see. This guards that they stay:
    /// each stage's chat turn must be handed THAT stage's own record, never an empty object.
    [Theory]
    [InlineData(Stages.Dosing, "1314-36-9")]     // the DosingDoc's marker
    [InlineData(Stages.Cost, "ref-catalog/")]    // the CostDoc's citation
    [InlineData(Stages.Decision, "final")]       // the DecisionDoc's picked-code rationale (seed writes "final code …")
    public async Task ChatOnANewStage_SeesThatStagesOwnRecord_NotAnEmptyObject(string stage, string expected)
    {
        var (d, store, agents) = Sut();
        await SeedCostedProjectAsync(store);
        string? seen = null;
        agents.Chat = (_, _, inputs, _) => { seen = inputs; return Task.FromResult("ok"); };

        var m = Message(stage, "aaaa1111", "what did you dose?", "2026-07-15T00:00:00Z");
        await store.UpsertChatMessageAsync(m);
        await d.OnRecordChangedAsync(Delivered(m), default);

        Assert.Contains(expected, seen);
        Assert.NotEqual("{}", seen);
    }

    [Fact]
    public async Task ChatMessage_IsIdempotent_UnderChangeFeedRedelivery()
    {
        // The change feed is at-least-once, and answering the message re-enters this handler once more by
        // itself (the status flip is a change). A re-run is not a harmless duplicate: the turn may already
        // have queued a revision, and a second run means a second stage re-run and a second Learned
        // Conclusion from ONE operator instruction.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);

        var m = Message(Stages.Discovery, "m1", "why is Zr tier A here?", "2026-07-14T09:00:00.0000000+00:00");
        var sent = await SendAsync(d, store, m);

        await d.OnRecordChangedAsync(Delivered(sent), default);  // the answered doc, redelivered
        // ...and the ORIGINAL, still-`pending` snapshot, redelivered. This is the delivery that matters: the
        // guard must be read off the CURRENT record, not off whatever the feed happens to be carrying. A
        // dispatcher that trusts the element re-runs the turn here — and the turn may already have queued a
        // revision, so a re-run means a second stage re-run and a second Learned Conclusion from ONE
        // operator instruction.
        await d.OnRecordChangedAsync(Delivered(m), default);

        Assert.Equal(1, agents.ChatCalls);

        // One reply, and one agent turn in the transcript. A second reply would have appended a duplicate
        // answer to the conversation the agent re-reads as its memory every single turn.
        Assert.Single(store.Documents.OfType<ChatReplyDoc>());
        var turns = await store.GetChatThreadAsync(P, Stages.Discovery);
        Assert.Single(turns, t => t.Role == ChatRoles.Agent);
        Assert.Single(turns, t => t.Role == ChatRoles.Operator);
    }

    [Fact]
    public async Task ChatMessage_OnAnAgentFailure_MarksItFailed_AndWritesNoReply()
    {
        // A half-written reply is worse than none: the operator reads it as the agent's word. So a turn that
        // did not complete leaves NOTHING in the transcript and says plainly that it failed.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);
        agents.Chat = (_, _, _, _) => throw new InvalidOperationException("the model deployment returned 429");

        var sent = await SendAsync(d, store, Message(Stages.Discovery, "m1", "why is Zr tier A here?", "2026-07-14T09:00:00.0000000+00:00"));

        Assert.Equal(ChatStatus.Failed, sent.Status);
        Assert.Contains("429", sent.Error);
        Assert.Null(ReplyTo(store, sent));
        Assert.Empty(store.Documents.OfType<ChatReplyDoc>());
        // The failure is NOT rendered into the thread as an agent turn — the agent said nothing.
        Assert.DoesNotContain(await store.GetChatThreadAsync(P, Stages.Discovery), t => t.Role == ChatRoles.Agent);
    }

    [Fact]
    public async Task ChatMessage_OnShutdown_LeavesTheMessagePending_RatherThanTerminallyFailed()
    {
        // A cancellation is the ORCHESTRATOR stopping, not the agent failing. `failed` is terminal — nothing
        // ever re-runs it — so marking a shut-down turn `failed` would tell the operator their question was
        // answered with "A task was canceled", forever, when in truth it was never asked.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);
        using var cts = new CancellationTokenSource();
        agents.Chat = (_, _, _, _) => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.FromResult("unreachable"); };

        var m = Message(Stages.Discovery, "m1", "why is Zr tier A here?", "2026-07-14T09:00:00.0000000+00:00");
        await store.UpsertChatMessageAsync(m);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.OnRecordChangedAsync(Delivered(m), cts.Token));

        var after = (await store.GetChatMessageAsync(P, m.Id))!;
        Assert.Equal(ChatStatus.Pending, after.Status);
        Assert.Null(after.Error);
        Assert.Empty(store.Documents.OfType<ChatReplyDoc>());
    }

    [Fact]
    public async Task ChatMessage_CarriesTheToolTrailOntoTheReply()
    {
        // "No silent mutations" (design §5). The reply carries the trail of what the turn actually DID, and
        // each entry carries the id of the record it wrote — that id is the audit link from a sentence in the
        // chat to the change it made. Without it the operator has only the agent's prose claim that something
        // changed, which is exactly the thing this system refuses to take on trust.
        var (d, store, agents) = Sut();
        await SeedAsync(d, store);
        agents.Chat = async (tools, _, _, _) =>
        {
            await tools.ApplyRevisionAsync("Zr neodecanoate on bottle", "it overlaps the Ti K-beta line at 4.93 keV");
            return "I have queued that change; discovery will re-run.";
        };

        var sent = await SendAsync(d, store, Message(Stages.Discovery, "m1", "drop the Zr — it overlaps Ti", "2026-07-14T09:00:00.0000000+00:00"));

        var revision = Assert.Single(await store.GetRevisionsAsync(P));
        var call = Assert.Single(ReplyTo(store, sent)!.ToolCalls);
        Assert.Equal("apply_revision", call.Tool);
        Assert.Equal(revision.Id, call.RecordId);                      // THE audit link
        Assert.Contains("it overlaps the Ti K-beta line at 4.93 keV", call.Summary);
    }

    [Fact]
    public void Router_RoutesAChatMessage_ButNotAChatReply()
    {
        // The message must route, or the whole conversational surface is inert: the doc is written, no agent
        // ever runs, and the operator watches a question that is never answered.
        var routed = RecordDocRouter.Route(JsonSerializer.SerializeToElement(
            Message(Stages.Discovery, "m1", "why is Zr tier A here?", "2026-07-14T09:00:00.0000000+00:00"),
            Smx.Domain.Json.Options));
        var m = Assert.IsType<ChatMessageDoc>(routed);
        Assert.Equal(Stages.Discovery, m.Stage);
        Assert.Equal(ChatStatus.Pending, m.Status);

        // The reply must NOT. It is the dispatcher's own OUTPUT: route it to a doc type and the dispatcher
        // re-enters on what it just wrote — an agent in an infinite conversation with itself, billed per turn.
        Assert.Null(RecordDocRouter.Route(JsonSerializer.SerializeToElement(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Discovery, "m1"), ProjectId = P, Stage = Stages.Discovery,
            MessageId = RecordIds.ChatMessage(P, Stages.Discovery, "m1"), Text = "because the pool has it",
            CreatedAt = "2026-07-14T09:00:01.0000000+00:00",
        }, Smx.Domain.Json.Options)));
    }
}

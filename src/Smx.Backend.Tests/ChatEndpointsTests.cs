using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;

namespace Smx.Backend.Tests;

/// The HTTP door of the per-stage conversation (design §5). It runs the same play as every other write in
/// this system: the backend cannot run an agent, so POST writes a `pending` ChatMessageDoc and returns 202 —
/// WRITING THE DOC IS THE DISPATCH. The reply arrives on the thread later, when the orchestrator's change
/// feed has run the turn.
public class ChatEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ChatEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private async Task SeedProject(string pid) =>
        await _store.UpsertProjectAsync(ProjectDoc.Create(pid, "Acme", "P", JsonDocument.Parse("{}").RootElement));

    private IReadOnlyList<ChatMessageDoc> Messages(string pid) =>
        _store.Documents.OfType<ChatMessageDoc>().Where(m => m.ProjectId == pid).ToList();

    private Task<HttpResponseMessage> PostChat(string pid, string stage, string text) =>
        _client.PostAsJsonAsync($"/projects/{pid}/stages/{stage}/chat", new { text });

    private Task<JsonElement> GetChat(string pid, string stage) =>
        _client.GetFromJsonAsync<JsonElement>($"/projects/{pid}/stages/{stage}/chat");

    [Fact]
    public async Task PostChat_QueuesAPendingMessageOnTheBus()
    {
        await SeedProject("p1");
        var resp = await PostChat("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");

        // 202, not 200: nothing has been ANSWERED yet. The orchestrator's change feed runs the turn; the UI
        // polls GET .../chat for the reply. A 200 would say the request was fulfilled — it was only queued.
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("messageId").GetString()));
        Assert.Equal(ChatStatus.Pending, body.GetProperty("status").GetString());

        var doc = Assert.Single(Messages("p1"));
        Assert.Equal(Stages.Discovery, doc.Stage);
        Assert.Equal("why did you drop the Zr neodecanoate?", doc.Text);
        // `pending` is the dispatcher's ONLY idempotency guard on an at-least-once feed
        // (StageDispatcher.OnChatMessageAsync): a message that arrives in any other status is never run.
        Assert.Equal(ChatStatus.Pending, doc.Status);
        Assert.Null(doc.Error);
        // The thread is ordered by a LEXICOGRAPHIC sort on CreatedAt, which is only chronological while every
        // writer uses the same fixed-width "O" (ChatMessageDoc.CreatedAt). A whole-second "…Z" from this
        // writer would silently misorder the transcript against the orchestrator's replies.
        Assert.True(DateTimeOffset.TryParse(doc.CreatedAt, out _));
        Assert.Contains('.', doc.CreatedAt);

        // And it is on the thread as the operator's turn — the same read the UI does.
        var thread = await GetChat("p1", Stages.Discovery);
        var turn = Assert.Single(thread.EnumerateArray());
        Assert.Equal(ChatRoles.Operator, turn.GetProperty("role").GetString());
        Assert.Equal("why did you drop the Zr neodecanoate?", turn.GetProperty("text").GetString());
    }

    [Fact]
    public async Task PostChat_WithBlankText_Is422_AndWritesNothing()
    {
        // An empty turn would still be dispatched — the change feed does not care that the text is blank —
        // so the agent would be handed nothing to answer and would answer it anyway.
        await SeedProject("p1");
        var resp = await PostChat("p1", Stages.Discovery, "   ");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Empty(Messages("p1"));
    }

    [Fact]
    public async Task PostChat_ToAnUnknownStage_Is422()
    {
        // A chat-message on a stage that does not exist is a doc the dispatcher would run anyway: it would
        // find no stage inputs (StageInputsJsonAsync's `_ => "{}"`) and no read tools, and the agent would
        // hold a conversation about nothing. Refuse it at the door.
        await SeedProject("p1");
        var resp = await PostChat("p1", "dosing", "what ppm are we at?");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("dosing", await resp.Content.ReadAsStringAsync());
        Assert.Empty(Messages("p1"));
    }

    [Fact]
    public async Task PostChat_ToAnUnknownProject_Is404()
    {
        var resp = await PostChat("ghost", Stages.Discovery, "why did you drop the Zr neodecanoate?");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Empty(Messages("ghost"));
    }

    [Fact]
    public async Task GetChat_IsEmptyOnColdStart_ThenReturnsTheThread()
    {
        await SeedProject("p1");
        var cold = await GetChat("p1", Stages.Discovery);
        Assert.Equal(JsonValueKind.Array, cold.ValueKind);
        Assert.Empty(cold.EnumerateArray());

        await PostChat("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");
        // The agent's side of the turn arrives on the bus, not through this API — the backend cannot run an
        // agent. Write it the way StageDispatcher.OnChatMessageAsync does, so the thread this endpoint serves
        // is the mixed transcript the UI will actually render.
        var messageId = Messages("p1").Single().Id;
        await _store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply("p1", Stages.Discovery, messageId.Split('|')[^1]),
            ProjectId = "p1", Stage = Stages.Discovery, MessageId = messageId,
            Text = "the supplier discontinued that grade",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });
        await PostChat("p1", Stages.Discovery, "and the Hf one?");

        var thread = await GetChat("p1", Stages.Discovery);
        Assert.Equal(3, thread.GetArrayLength());
        // Oldest-first, and the answer never above its own question.
        Assert.Equal(
            [ChatRoles.Operator, ChatRoles.Agent, ChatRoles.Operator],
            thread.EnumerateArray().Select(t => t.GetProperty("role").GetString()!).ToArray());
        Assert.Equal("why did you drop the Zr neodecanoate?", thread[0].GetProperty("text").GetString());
        Assert.Equal("the supplier discontinued that grade", thread[1].GetProperty("text").GetString());
        Assert.Equal("and the Hf one?", thread[2].GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetChat_KeepsTheReplyUnderItsOwnQuestion_WhenTheOperatorPostsAgainWhileWaiting()
    {
        // The interleaving is ordinary, not exotic: the operator can post at any time, and the reply is
        // stamped when the TURN ENDS — after a tool loop that takes tens of seconds.
        //
        //   M1  "why did you drop the Zr neodecanoate?"
        //   M2  the operator, still waiting, adds "and the Hf one?"
        //   R1  the answer to M1 finally lands — with the LATEST timestamp of the three
        //
        // Sorted on its own CreatedAt the reply falls to the BOTTOM, and the answer about Zr is positioned as
        // the answer about Hf. That is what the operator reads, and what ChatThread.Render hands the agent as
        // its entire memory of the conversation on every later turn.
        await SeedProject("p1");
        await PostChat("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");
        var m1 = Messages("p1").Single().Id;
        await PostChat("p1", Stages.Discovery, "and the Hf one?");

        // The reply to M1, written last — the way the dispatcher writes it (CreatedAt = now, at turn end).
        await _store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply("p1", Stages.Discovery, m1.Split('|')[^1]),
            ProjectId = "p1", Stage = Stages.Discovery, MessageId = m1,
            Text = "the supplier discontinued that grade",
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        });

        var thread = await GetChat("p1", Stages.Discovery);
        Assert.Equal(
            ["why did you drop the Zr neodecanoate?", "the supplier discontinued that grade", "and the Hf one?"],
            thread.EnumerateArray().Select(t => t.GetProperty("text").GetString()!).ToArray());
    }

    [Fact]
    public async Task GetChat_ShowsAFailedTurnAsFailed_WithItsError()
    {
        // A failed turn that looks identical to one still running is what pushes the operator into re-sending,
        // and a re-send mints a NEW message id → a new chat key → a revision id that no longer converges on the
        // first one. Two RevisionDocs, two stage re-runs, two Learned Conclusions out of one instruction —
        // exactly what the content-addressed revision id was built to prevent. So the status has to be visible.
        await SeedProject("p1");
        await PostChat("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");
        await PostChat("p1", Stages.Discovery, "and the Hf one?");

        // The dispatcher's failure path: no reply is written, the message is marked failed and carries why.
        var failed = Messages("p1").First(m => m.Text.StartsWith("why"));
        failed.Status = ChatStatus.Failed;
        failed.Error = "the agent could not complete this turn: 429 Too Many Requests";
        await _store.UpsertChatMessageAsync(failed);

        var thread = await GetChat("p1", Stages.Discovery);
        Assert.Equal(ChatStatus.Failed, thread[0].GetProperty("status").GetString());
        Assert.Contains("429", thread[0].GetProperty("error").GetString());
        // The second turn is still in flight — THAT is what "failed" has to be distinguishable from. Its error
        // is absent rather than null (Json.Options omits nulls), which is the same "no error" on the wire.
        Assert.Equal(ChatStatus.Pending, thread[1].GetProperty("status").GetString());
        Assert.False(thread[1].TryGetProperty("error", out _));
    }

    [Fact]
    public async Task GetChat_IsScopedToOneStage()
    {
        // Law 9: the stage agents do not share a conversation, so neither do their threads. If this leaked,
        // the Discovery screen could show a Regulatory transcript — the operator would read a verdict's
        // reasoning as if it were said about candidates — and, worse, the dispatcher rehydrates the turn from
        // exactly this thread, so the Discovery agent would be handed Regulatory's conversation as its memory.
        await SeedProject("p1");
        await PostChat("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");
        await PostChat("p1", Stages.Regulatory, "why is this one conditional?");

        var discovery = await GetChat("p1", Stages.Discovery);
        var regulatory = await GetChat("p1", Stages.Regulatory);

        var d = Assert.Single(discovery.EnumerateArray());
        Assert.Equal("why did you drop the Zr neodecanoate?", d.GetProperty("text").GetString());
        var r = Assert.Single(regulatory.EnumerateArray());
        Assert.Equal("why is this one conditional?", r.GetProperty("text").GetString());
    }

    [Fact]
    public async Task GetChat_OnAnUnknownProject_IsAnEmptyThread_NotA404()
    {
        // Deliberate, and consistent with the collection GET this endpoint is modelled on
        // (GET /projects/{id}/revisions, which also queries by project and returns []): a thread is a
        // collection, and an empty collection is the honest answer to "what has been said here". The single
        // resources — GET /projects/{id}, GET /projects/{id}/matrix — 404 because the RESOURCE is absent.
        // The write door is where a typo'd project id must be caught, and POST does 404.
        var thread = await GetChat("ghost", Stages.Discovery);
        Assert.Equal(JsonValueKind.Array, thread.ValueKind);
        Assert.Empty(thread.EnumerateArray());
    }

    [Fact]
    public void Stages_All_ListsEveryStageConstantOnTheClass()
    {
        // Stages.All is what makes a stage CHATTABLE (the POST validates against it). Hand-maintaining it
        // beside the constants means a stage added later is silently un-chattable: no error, no failing test,
        // just an operator getting a 422 for a stage the product says exists. Reflect over the class instead
        // of restating the list — a restated list is a second thing to forget to update.
        var declared = typeof(Stages)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.Equal([.. declared.Order()], [.. Stages.All.Order()]);
    }

    [Fact]
    public async Task PostChat_MintsAnIdSafeMessageKey()
    {
        // The whole chain: the orchestrator derives the chat KEY from THIS id's last '|'-segment
        // (StageDispatcher.KeyOf) and hands it to ChatTools, which concatenates it into a Cosmos item id and
        // ASSERTS it is `[A-Za-z0-9_-]+`. Cosmos rejects an id containing '/', '\', '?' or '#' with a 400 that
        // no in-memory store would ever produce — so a "friendlier" id scheme minted here (a slug of the text,
        // a timestamp with ':', an email) would pass every backend test and break every chat turn in Azure.
        //
        // Pinned against the REAL guard, not a copy of its regex: construct the ChatTools the dispatcher would
        // construct. If the minted key is not id-safe, this throws where it is legible.
        await SeedProject("p1");
        var resp = await PostChat("p1", Stages.Discovery, "why did you drop the Zr neodecanoate?");
        var messageId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("messageId").GetString()!;

        var key = messageId.Split('|')[^1]; // StageDispatcher.KeyOf
        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), key);
        _ = new ChatTools(_store, "p1", Stages.Discovery, key); // throws unless id-safe

        // And the id is the one RecordIds mints — '|'-separated, so the key really is the last segment.
        Assert.Equal(RecordIds.ChatMessage("p1", Stages.Discovery, key), messageId);
    }
}

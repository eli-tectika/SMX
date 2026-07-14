# Chemistry Backend — Plan 3c: The Conversational Surface

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The operator can talk to the current stage's agent — ask a question and get a cited answer, or say *"move Ba to tier C because it overlaps Ti"* and have it **actually happen**, through the same machinery the `/revise` endpoint uses.

**Architecture:** A chat turn is a **record**, not a socket. `POST …/chat` writes a `chat-message` doc; the Cosmos change feed dispatches it to a **stage-scoped** agent; the agent's reply is a `chat-reply` doc. The MAF conversation session is in-memory and cannot be rehydrated, so **the thread lives in the record and is re-rendered into the prompt on every turn** — which is exactly why the conversation survives multi-day re-entry. Mutating tools (`apply_revision`, `record_answer`) are **bound per-turn to (projectId, stage)**; the model never supplies a project id. There is **no gate tool**, so chat structurally cannot sign a gate.

**Tech Stack:** .NET 8 (`Smx.Backend.Tests` is `net10.0`), xUnit, Cosmos change feed, Microsoft Agent Framework (`ChatClientAgent`) on Claude via Foundry.

---

## Read this before you touch anything

- `docs/superpowers/specs/2026-07-12-chemistry-backend-end-to-end-design.md` **§5** (the conversational surface) — the source of truth. §4 for the `/revise` endpoint this must be twinned with.
- `CLAUDE.md` — the interaction laws. Three govern this plan:
  - **Law 4 — no direct edits to agent output.** Every chat-driven change is a **tool call → a cited, persisted record write → a Learned Conclusion**. No silent mutations.
  - **Law 6 — frictionless re-entry.** A project runs in bursts across days. The conversation must survive being closed and reopened a week later.
  - **Law 9 — gates are operator-signed records, never voice-committed.** **Chat can instruct and propose, but never signs a gate.** This is the anti-rubber-stamping line and it is enforced *structurally* in this plan, not by asking the model nicely.
- **Correctness is the primary design driver.** A wrong marker recommendation causes real-world harm; the headline harm metric is a **false pass**.

**Baseline: 231 tests green** (`dotnet test src/Smx.Backend.sln` — Domain 67, Eval 4, Orchestrator 118, Backend 42).

### Four traps this codebase has already sprung. Do not spring them again.

1. **`[FromServices]` is mandatory on every store param in a minimal-API handler.** Minimal APIs resolve service-vs-body via `IServiceProviderIsService` at endpoint-build time, across the **whole app's** endpoint data source. Miss it and routing breaks for **every** route, `/healthz` included. See the comment at the top of `src/Smx.Backend/Api/ProjectEndpoints.cs`.
2. **`AIFunctionFactory` schemas can lie.** A parameter without a default is emitted as `"required"` no matter what the description says. **Test every agent tool by invoking the real `AIFunction` via `InvokeAsync`**, never the C# method. Plan 3a shipped a tool that was dead on arrival for a full release because its test called the method.
3. **Azure/Cosmos failures are silent.** A missing index 404s and looks like an empty knowledge layer; a rejected search document is dropped without an exception. Assume nothing succeeded unless you checked.
4. **Test-project fakes are shared by source-link**, not `ProjectReference` (`<Compile Include="../Smx.Domain.Tests/Fakes/X.cs" Link="Fakes/X.cs" />`) — a ProjectReference causes CS0433 duplicate-type errors.

---

## The three design decisions that shape this plan

### 1. The thread lives in the record, because the MAF session cannot be rehydrated

`MafAgent.StartThreadAsync` calls `_agent.CreateSessionAsync(ct)` — a **fresh, in-memory `AgentSession`**. There is no API to reconstruct one from stored messages, and it dies with the process. So a chat thread **cannot be** a MAF session: the orchestrator restarts, the operator comes back on Thursday, and the conversation is gone.

Therefore: **every chat turn starts a fresh agent session and re-renders the persisted thread into the prompt.** The agent is stateless; the *record* is the conversation. This is not a workaround — it is the record-as-bus invariant applied to dialogue, and it is what buys Law 6 for free. It also means the thread is inspectable, auditable, and testable as data.

The cost is prompt size (the whole thread each turn). Acceptable for a single-operator tool with per-stage threads; if a thread ever grows unreasonable, the fix is summarisation *in the record*, not session state in memory.

### 2. The model never supplies a project id

`ToolBox` today is a DI **singleton** of pure read tools. A chat mutation tool needs to know *which project and which stage it is acting on*. The tempting shortcut — make `projectId` a tool parameter — is a **cross-project write vulnerability**: one hallucinated id and the agent mutates a different project's analysis. There is no undo and the operator would have no reason to look.

So the chat tools are **constructed per turn**, closed over `(projectId, stage)` from the `chat-message` doc that the change feed delivered. The model's tool schema has no project parameter and cannot acquire one. Task 4 builds this; a test asserts the emitted `AIFunction` schema contains no `projectId`.

### 3. `record_answer` may not touch the element pools

`record_answer(field, value)` is intake gap-fill. But `ProjectDoc.Payload` also carries the **element pools** — the physicist's *measured XRF background*. A chat tool that can write arbitrary payload paths is a mechanism by which an LLM can silently alter measured data, and every downstream verdict rests on it.

So `record_answer` writes only to a **fixed allowlist** of operator-known product facts (component `material` / `application` / `objective` / `markets`, and `clientRestrictedList`). Element pools and provided candidates are **not writable**, the tool says so when asked, and a test pins it. This is a real safety property, not defensive coding.

`record_answer` is also only legal **before intake has produced constraints**. Once constraints exist, changing an input is not a gap-fill — it is a revision, and it must go through `apply_revision` so it earns a Learned Conclusion. The tool returns an error saying exactly that, which keeps the two tools' domains disjoint and honest.

### The guardrail, stated precisely

**Chat has no gate tool.** An agent can only act through its tools, so it cannot sign a gate — not because it was told not to, but because the capability does not exist in its tool list. `POST /projects/{id}/regulatory/approve` remains the **only** writer of an approved `GateDoc`.

Chat *can* move a gate toward `locked` (an `apply_revision` on Discovery or Regulatory voids it, via the machinery Plan 3b built). That is the **safe** direction: it forces the operator to re-review and re-sign. Task 9 pins both halves — chat can void, chat can never approve.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Records/ChatDocs.cs` | `ChatMessageDoc`, `ChatReplyDoc`, `ChatToolCall` |
| `src/Smx.Domain/ChatThread.cs` | Pure: persisted docs → the transcript rendered into the prompt (the Law-6 rehydration) |
| `src/Smx.Domain/IntakeAnswers.cs` | Pure: the `record_answer` allowlist + payload patching. **The element-pool guard lives here.** |
| `src/Smx.Orchestrator/Agents/ChatTools.cs` | Per-turn tools bound to `(projectId, stage)` + the tool-call trail collector |
| `src/Smx.Orchestrator/Agents/ChatAgent.cs` | Conversational instructions + the single-turn run |
| `src/Smx.Backend/Api/ChatEndpoints.cs` | `POST` / `GET …/stages/{stage}/chat` |
| `src/Smx.Domain.Tests/ChatThreadTests.cs` · `IntakeAnswersTests.cs` | |
| `src/Smx.Orchestrator.Tests/ChatToolsTests.cs` · `ChatDispatchTests.cs` · `ChatGuardrailTests.cs` | |
| `src/Smx.Backend.Tests/ChatEndpointsTests.cs` | |

**Modify:** `Records/RecordIds.cs` · `IRecordStore.cs` · `CosmosRecordStore.cs` · `Fakes/InMemoryRecordStore.cs` · `Dispatch/IAgentRuns.cs` (+`AgentRuns`) · `Dispatch/StageDispatcher.cs` · `Dispatch/RecordDocRouter.cs` · `Agents/ToolBox.cs` · `Smx.Backend/Program.cs` · `Smx.Orchestrator/Program.cs` · `Fakes/FakeAgentRuns.cs`

**No infra change.** Chat docs are discriminated records in the existing `record` container (PK `/projectId`), exactly like `revision` and `gate`. Confirm this in your report rather than assuming it.

---

## Task 1: The chat records

**Files:**
- Create: `src/Smx.Domain/Records/ChatDocs.cs`
- Modify: `src/Smx.Domain/Records/RecordIds.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs` (add to the existing file — it is the established home for doc/id tests)

- [ ] **Step 1: Write the failing test**

Follow the shape of the existing `RevisionDoc_RoundTrips_WithTypeDiscriminatorOnTheWire` test in that file. Add:

```csharp
    [Fact]
    public void ChatDocs_RoundTrip_WithTheirTypeDiscriminatorsOnTheWire()
    {
        var msg = new ChatMessageDoc
        {
            Id = RecordIds.ChatMessage("proj-1", Stages.Discovery, "aaaa1111"), ProjectId = "proj-1",
            Stage = Stages.Discovery, Text = "why is Ba tier A?", CreatedAt = "2026-07-13T10:00:00Z",
        };
        var json = JsonSerializer.Serialize(msg, Json.Options);
        // The change feed routes on this string and nothing else (RecordDocRouter).
        Assert.Contains("\"type\":\"chat-message\"", json);
        Assert.Equal(ChatStatus.Pending, JsonSerializer.Deserialize<ChatMessageDoc>(json, Json.Options)!.Status);

        var reply = new ChatReplyDoc
        {
            Id = RecordIds.ChatReply("proj-1", Stages.Discovery, "aaaa1111"), ProjectId = "proj-1",
            Stage = Stages.Discovery, MessageId = msg.Id, Text = "Because the catalog lists it clean.",
            ToolCalls = [new ChatToolCall("search_catalog", "element=Ba", null)],
            CreatedAt = "2026-07-13T10:00:05Z",
        };
        var replyJson = JsonSerializer.Serialize(reply, Json.Options);
        Assert.Contains("\"type\":\"chat-reply\"", replyJson);
        var back = JsonSerializer.Deserialize<ChatReplyDoc>(replyJson, Json.Options)!;
        Assert.Equal(msg.Id, back.MessageId);
        Assert.Equal("search_catalog", Assert.Single(back.ToolCalls).Tool);
    }

    [Fact]
    public void ChatIds_PairAReplyToItsMessage()
    {
        // A reply's id is derived from its message's key, so a redelivered chat-message cannot produce a
        // second reply doc — it upserts the same one.
        Assert.Equal("proj-1|chat-message|discovery|aaaa1111", RecordIds.ChatMessage("proj-1", Stages.Discovery, "aaaa1111"));
        Assert.Equal("proj-1|chat-reply|discovery|aaaa1111", RecordIds.ChatReply("proj-1", Stages.Discovery, "aaaa1111"));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RecordDocsTests`
Expected: FAIL — `ChatMessageDoc` / `ChatReplyDoc` / `RecordIds.ChatMessage` do not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/Records/ChatDocs.cs`:

```csharp
namespace Smx.Domain.Records;

public static class ChatStatus
{
    public const string Pending = "pending";
    public const string Answered = "answered";
    public const string Failed = "failed";
}

/// One thing the agent did during a chat turn, for the UI's tool-call/citation trail (design §5:
/// "the reply carries its tool-call/citation trail"). `RecordId` is set when the call WROTE something —
/// that is the audit link from a sentence in the chat to the record it changed.
public sealed record ChatToolCall(string Tool, string Summary, string? RecordId);

/// The operator's message to the current stage's agent, scoped to (project, stage). Chat is per-stage,
/// not one global thread: agents do not share a conversation (Law 9), so neither do their threads.
///
/// It rides the record bus like everything else — the backend cannot run an agent, so writing this doc
/// IS the dispatch. And because the thread lives in the record rather than in an in-memory agent session,
/// the conversation survives a multi-day re-entry (Law 6) and an orchestrator restart.
public sealed class ChatMessageDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }        // partition key
    public string Type { get; set; } = RecordTypes.ChatMessage;
    public required string Stage { get; set; }
    public required string Text { get; set; }
    public string Status { get; set; } = ChatStatus.Pending;
    public string? Error { get; set; }
    /// ISO-8601, ALWAYS via DateTimeOffset...ToString("O"). The thread is ordered by a LEXICOGRAPHIC sort
    /// on this field (a server-side Cosmos ORDER BY on a string), which is only chronological while every
    /// writer uses the same fixed-width format. Mixing "O" with a whole-second "…Z" silently misorders the
    /// conversation — and a transcript out of order is a transcript that lies about who said what first.
    public required string CreatedAt { get; set; }
}

/// The agent's reply. Its id is derived from the message's key, so a change-feed redelivery upserts the
/// same reply rather than appending a second one.
public sealed class ChatReplyDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }        // partition key
    public string Type { get; set; } = RecordTypes.ChatReply;
    public required string Stage { get; set; }
    public required string MessageId { get; set; }        // the ChatMessageDoc this answers
    public required string Text { get; set; }
    public List<ChatToolCall> ToolCalls { get; set; } = [];
    public required string CreatedAt { get; set; }        // see ChatMessageDoc.CreatedAt — same rule
}
```

In `src/Smx.Domain/Records/RecordIds.cs`, add to `RecordTypes`:

```csharp
    public const string ChatMessage = "chat-message";
    public const string ChatReply = "chat-reply";
```

and to `RecordIds`:

```csharp
    public static string ChatMessage(string projectId, string stage, string key) =>
        $"{projectId}|chat-message|{stage}|{key}";

    /// Derived from the MESSAGE's key, deliberately: the reply is a function of the message, so an
    /// at-least-once change feed re-delivering the message upserts one reply instead of appending a
    /// second one to the transcript.
    public static string ChatReply(string projectId, string stage, string key) =>
        $"{projectId}|chat-reply|{stage}|{key}";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter RecordDocsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/ChatDocs.cs src/Smx.Domain/Records/RecordIds.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): chat-message / chat-reply records — the per-stage thread, on the bus"
```

---

## Task 2: Chat persistence

**Files:**
- Modify: `src/Smx.Domain/IRecordStore.cs`, `src/Smx.Infrastructure/CosmosRecordStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`
- Test: `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`

> **Fake↔prod parity is a hard requirement.** `InMemoryRecordStore` backs nearly every test in the solution; if it drifts from `CosmosRecordStore`, the suite certifies behaviour production does not have. Both must return the thread **scoped to (project, stage)** and **ordered oldest-first by `CreatedAt`, ordinally** (Cosmos `ORDER BY` on a string is ordinal — the fake must say `StringComparer.Ordinal` explicitly or it uses the ambient culture).
>
> ⚠️ The Cosmos LINQ provider takes member names from `SystemTextJsonCosmosSerializer.SerializeMemberName` (it derives from `CosmosLinqSerializer` — **do not change that base class**). `CosmosQueryTextTests` asserts the generated SQL uses camelCase names. If you add a LINQ query, add it there too.

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`:

```csharp
public class ChatStoreTests
{
    private static ChatMessageDoc Msg(string project, string stage, string key, string at) => new()
    {
        Id = RecordIds.ChatMessage(project, stage, key), ProjectId = project,
        Stage = stage, Text = $"msg {key}", CreatedAt = at,
    };
    private static ChatReplyDoc Reply(string project, string stage, string key, string at) => new()
    {
        Id = RecordIds.ChatReply(project, stage, key), ProjectId = project, Stage = stage,
        MessageId = RecordIds.ChatMessage(project, stage, key), Text = $"reply {key}", CreatedAt = at,
    };

    [Fact]
    public async Task GetChatThread_IsScopedToOneStage_AndOrderedOldestFirst()
    {
        // Chat is PER-STAGE (Law 9: agents don't share a conversation, so neither do their threads).
        // A Discovery thread must never leak into the Regulatory agent's context.
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "b", "2026-07-13T02:00:00Z"));
        await store.UpsertChatReplyAsync(Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:30:00Z"));
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Regulatory, "c", "2026-07-13T03:00:00Z"));
        await store.UpsertChatMessageAsync(Msg("proj-2", Stages.Discovery, "d", "2026-07-13T04:00:00Z"));

        var thread = await store.GetChatThreadAsync("proj-1", Stages.Discovery);

        Assert.Equal(
            ["2026-07-13T01:00:00Z", "2026-07-13T01:30:00Z", "2026-07-13T02:00:00Z"],
            thread.Select(t => t.CreatedAt));
    }

    [Fact]
    public async Task GetChatThread_OnColdStart_ReturnsEmpty_NotNull() =>
        Assert.Empty(await new InMemoryRecordStore().GetChatThreadAsync("proj-nothing", Stages.Discovery));

    [Fact]
    public async Task UpsertChatReply_ReplacesById_SoRedeliveryDoesNotAppendASecondReply()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertChatReplyAsync(Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));

        // A DISTINCT object with the same id — the dispatcher re-reads docs from the store, so replacement
        // must work by id, not by having mutated the caller's reference.
        var second = Reply("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z");
        second.Text = "revised reply";
        await store.UpsertChatReplyAsync(second);

        var only = Assert.Single(await store.GetChatThreadAsync("proj-1", Stages.Discovery));
        Assert.Equal("agent", only.Role);
        Assert.Equal("revised reply", only.Text);
    }

    [Fact]
    public async Task GetChatThread_ExcludesOtherDocTypesInTheSamePartition()
    {
        // The fake filters by CLR type (.OfType<ChatMessageDoc>()) while Cosmos filters by the `type` string
        // field. That is the one place the twins use different mechanisms, so it is the one place they can
        // silently diverge.
        var store = new InMemoryRecordStore();
        await store.UpsertChatMessageAsync(Msg("proj-1", Stages.Discovery, "a", "2026-07-13T01:00:00Z"));
        await store.UpsertRevisionAsync(new RevisionDoc
        {
            Id = RecordIds.Revision("proj-1", Stages.Discovery, "r1"), ProjectId = "proj-1",
            Stage = Stages.Discovery, Target = "t", Reason = "r", CreatedAt = "2026-07-13T01:00:00Z",
        });

        Assert.Single(await store.GetChatThreadAsync("proj-1", Stages.Discovery));
    }
}
```

The thread is a mixed sequence of messages and replies, so return them as one shape the caller can order and render. Add to `src/Smx.Domain/Records/ChatDocs.cs`:

```csharp
/// One turn in a persisted chat thread — either side of it. `Role` is "operator" | "agent". This is what
/// GetChatThreadAsync returns and what ChatThread.Render turns back into the agent's memory.
public sealed record ChatTurn(string Role, string Text, string CreatedAt, IReadOnlyList<ChatToolCall> ToolCalls);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter ChatStoreTests`
Expected: FAIL — the methods do not exist.

- [ ] **Step 3: Write the implementation**

`src/Smx.Domain/IRecordStore.cs` — add:
```csharp
    /// The persisted per-stage conversation, oldest-first. This IS the thread: the MAF agent session is
    /// in-memory and cannot be rehydrated, so the record is the only thing that survives a restart or a
    /// multi-day re-entry (Law 6).
    Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default);
    Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default);
    Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default);
```

`src/Smx.Infrastructure/CosmosRecordStore.cs` — two partition-scoped LINQ queries (mirror `GetRevisionsAsync`), one for each type, filtered by `Stage`, merged and ordered by `CreatedAt` ordinally:
```csharp
    public async Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default)
    {
        var messages = await QueryAsync<ChatMessageDoc>(projectId, RecordTypes.ChatMessage, ct);
        var replies = await QueryAsync<ChatReplyDoc>(projectId, RecordTypes.ChatReply, ct);
        return messages.Where(m => m.Stage == stage)
            .Select(m => new ChatTurn("operator", m.Text, m.CreatedAt, []))
            .Concat(replies.Where(r => r.Stage == stage)
                .Select(r => new ChatTurn("agent", r.Text, r.CreatedAt, r.ToolCalls)))
            .OrderBy(t => t.CreatedAt, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<List<T>> QueryAsync<T>(string projectId, string type, CancellationToken ct) where T : class
    {
        var results = new List<T>();
        var query = container.GetItemLinqQueryable<T>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => ((dynamic)d).Type == type)   // see note below
            .ToFeedIterator();
        while (query.HasMoreResults) results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }
```
**The `(dynamic)` cast will not translate to SQL.** Do not use it — write the two queries explicitly, each with its own strongly-typed `.Where(d => d.Type == RecordTypes.ChatMessage)` / `.Where(d => d.Type == RecordTypes.ChatReply)`, exactly like `GetRevisionsAsync` does. Then add both to `CosmosQueryTextTests` so the generated SQL is pinned to camelCase (`root["type"]`, `root["stage"]`, `root["createdAt"]`) — that test is the only thing standing between us and a query that matches nothing in Azure while every unit test passes.

`src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs` — the twin:
```csharp
    public Task<IReadOnlyList<ChatTurn>> GetChatThreadAsync(string projectId, string stage, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ChatTurn>>(
            _docs.Values.OfType<ChatMessageDoc>()
                .Where(m => m.ProjectId == projectId && m.Stage == stage)
                .Select(m => new ChatTurn("operator", m.Text, m.CreatedAt, []))
            .Concat(_docs.Values.OfType<ChatReplyDoc>()
                .Where(r => r.ProjectId == projectId && r.Stage == stage)
                .Select(r => new ChatTurn("agent", r.Text, r.CreatedAt, r.ToolCalls)))
            .OrderBy(t => t.CreatedAt, StringComparer.Ordinal)   // twin of the Cosmos ORDER BY
            .ToList());

    public Task UpsertChatMessageAsync(ChatMessageDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertChatReplyAsync(ChatReplyDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
```

Also add a `Task<ChatMessageDoc?> GetChatMessageAsync(string projectId, string id, ...)` point-read to both (the dispatcher needs it to re-read status; mirror `GetGateAsync`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter ChatStoreTests`, then `dotnet build src/Smx.Backend.sln` (both `IRecordStore` implementors must satisfy the interface), then the full suite.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Infrastructure src/Smx.Domain.Tests
git commit -m "feat(store): per-stage chat thread reads/writes (Cosmos + in-memory twin)"
```

---

## Task 3: `ChatThread` — rehydrating the conversation into the prompt

**This is the Law-6 mechanism**, and it is pure, so test it as pure code.

**Files:**
- Create: `src/Smx.Domain/ChatThread.cs`
- Test: `src/Smx.Domain.Tests/ChatThreadTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class ChatThreadTests
{
    [Fact]
    public void Render_ProducesAnOrderedTranscript_AttributedToSpeaker()
    {
        var turns = new List<ChatTurn>
        {
            new("operator", "why is Ba tier A?", "2026-07-13T01:00:00Z", []),
            new("agent", "The catalog lists it clean.", "2026-07-13T01:00:05Z",
                [new ChatToolCall("search_catalog", "element=Ba", null)]),
            new("operator", "and for HDPE?", "2026-07-13T01:01:00Z", []),
        };

        var rendered = ChatThread.Render(turns);

        // The agent gets a fresh MAF session every turn — this string is the ONLY memory it has.
        Assert.Contains("Operator: why is Ba tier A?", rendered);
        Assert.Contains("You: The catalog lists it clean.", rendered);
        Assert.Contains("and for HDPE?", rendered);
        // Order is the meaning: a transcript out of order lies about who said what first.
        Assert.True(rendered.IndexOf("why is Ba tier A?", StringComparison.Ordinal)
                  < rendered.IndexOf("and for HDPE?", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_OnAnEmptyThread_SaysSo_RatherThanReturningNothing()
    {
        // An empty string here would leave the prompt with a dangling "conversation so far:" header and
        // invite the model to invent a history. Say plainly that this is the first turn.
        Assert.Contains("first message", ChatThread.Render([]), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_CarriesTheAgentsOwnToolCalls_SoItCanSeeWhatItAlreadyDid()
    {
        // Without this, a fresh session would re-run the same lookups every turn and could contradict a
        // citation it gave a moment ago — the operator would be talking to an agent with amnesia.
        var rendered = ChatThread.Render([
            new("agent", "Ba is clean.", "2026-07-13T01:00:00Z",
                [new ChatToolCall("search_catalog", "element=Ba", null)]),
        ]);
        Assert.Contains("search_catalog", rendered);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter ChatThreadTests`
Expected: FAIL — `ChatThread` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/ChatThread.cs`:

```csharp
using System.Text;
using Smx.Domain.Records;

namespace Smx.Domain;

/// Renders the persisted per-stage conversation into the transcript the agent is given each turn.
///
/// This exists because MafAgent.StartThreadAsync creates a FRESH, in-memory AgentSession and there is no
/// way to rehydrate one from stored messages. So the agent has no memory of its own: this string is the
/// entire conversation, reconstructed from the record on every single turn. That is what lets the
/// operator close the app on Monday and pick the thread up on Thursday (Law 6), and what lets an
/// orchestrator restart mid-conversation without losing it.
public static class ChatThread
{
    public static string Render(IReadOnlyList<ChatTurn> turns)
    {
        if (turns.Count == 0)
            return "(This is the operator's first message in this stage — there is no prior conversation.)";

        var sb = new StringBuilder();
        foreach (var t in turns)
        {
            sb.Append(t.Role == "agent" ? "You: " : "Operator: ").AppendLine(t.Text);
            // Show the agent what it already looked up. Without it, a fresh session re-runs the same
            // retrievals every turn and can contradict a citation it gave one message ago.
            if (t.ToolCalls.Count > 0)
                sb.Append("  (you called: ")
                  .Append(string.Join(", ", t.ToolCalls.Select(c => $"{c.Tool}({c.Summary})")))
                  .AppendLine(")");
        }
        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter ChatThreadTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/ChatThread.cs src/Smx.Domain.Tests/ChatThreadTests.cs
git commit -m "feat(domain): ChatThread — the record IS the agent's memory (the MAF session cannot be rehydrated)"
```

---

## Task 4: `IntakeAnswers` — the `record_answer` allowlist

**Read design decision 3 above before writing a line of this.** This file is the guard that stops an LLM rewriting the physicist's measured XRF background.

**Files:**
- Create: `src/Smx.Domain/IntakeAnswers.cs`
- Test: `src/Smx.Domain.Tests/IntakeAnswersTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json;
using Smx.Domain;

namespace Smx.Domain.Tests;

public class IntakeAnswersTests
{
    private static JsonElement Payload() => JsonSerializer.SerializeToElement(new
    {
        components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "" } },
        elementPools = new[] { new { component = "bottle", element = "Zr", line = "Ka", status = "V", signalNote = (string?)null } },
        providedCandidates = Array.Empty<object>(),
        clientRestrictedList = Array.Empty<string>(),
    }, Json.Options);

    [Fact]
    public void Patch_FillsAnAllowedComponentField()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "components.bottle.objective", "brand protection");
        Assert.Null(error);
        Assert.Equal("brand protection",
            patched!.Value.GetProperty("components")[0].GetProperty("objective").GetString());
    }

    [Fact]
    public void Patch_REFUSES_ToTouchTheElementPools()
    {
        // THE POINT OF THIS FILE. Element pools are the PHYSICIST'S MEASURED XRF BACKGROUND. Every
        // downstream verdict rests on them. A chat tool that can write them is a mechanism by which a
        // language model can silently alter measured data, and nobody would have a reason to look.
        var (patched, error) = IntakeAnswers.Patch(Payload(), "elementPools.0.status", "V");
        Assert.Null(patched);
        Assert.Contains("element pools", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physicist", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patch_REFUSES_ToTouchProvidedCandidates()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "providedCandidates.0.cas", "7440-67-7");
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Fact]
    public void Patch_RejectsAnUnknownField_WithAMessageThatNamesWhatIsAllowed()
    {
        // The error is read by a model, so it must teach: an unhelpful "invalid field" just gets retried.
        var (_, error) = IntakeAnswers.Patch(Payload(), "components.bottle.colour", "blue");
        Assert.Contains("objective", error!);   // names the allowed fields
    }

    [Fact]
    public void Patch_RejectsAnUnknownComponent()
    {
        var (_, error) = IntakeAnswers.Patch(Payload(), "components.lid.objective", "x");
        Assert.Contains("lid", error!);
    }

    [Fact]
    public void Patch_FillsTheClientRestrictedList_FromACommaSeparatedValue()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "clientRestrictedList", "Pb, Cd");
        Assert.Null(error);
        Assert.Equal(["Pb", "Cd"],
            patched!.Value.GetProperty("clientRestrictedList").EnumerateArray().Select(e => e.GetString()));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter IntakeAnswersTests`
Expected: FAIL — `IntakeAnswers` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/IntakeAnswers.cs`. Parse `field` as a dotted path; accept **only** `components.{id}.{material|application|objective|markets}` and `clientRestrictedList`; rebuild the payload `JsonElement` with that one value replaced; return `(JsonElement? Patched, string? Error)` — never throw, because the caller is an LLM tool and the error text is what teaches the model to correct itself.

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smx.Domain;

/// The allowlist for `record_answer` (design §5 — intake gap-fill).
///
/// A chat tool that could write ANY path into the project payload would be a mechanism by which a
/// language model can silently rewrite the ELEMENT POOLS — the physicist's measured XRF background, on
/// which every downstream candidate and verdict rests. There is no undo and no reason anyone would look.
/// So this is an allowlist, not a denylist: only operator-known product facts are writable, and the
/// physicist's data and the eval seam (providedCandidates) are not writable at all, by construction.
public static class IntakeAnswers
{
    private static readonly string[] ComponentFields = ["material", "application", "objective", "markets"];

    public static (JsonElement? Patched, string? Error) Patch(JsonElement payload, string field, string value)
    {
        var node = JsonNode.Parse(payload.GetRawText())!.AsObject();
        var parts = field.Split('.');

        if (field == "clientRestrictedList")
        {
            node["clientRestrictedList"] = new JsonArray(
                value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(s => (JsonNode)s!).ToArray());
            return (JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()), null);
        }

        if (parts is ["components", var componentId, var componentField])
        {
            if (!ComponentFields.Contains(componentField))
                return (null, $"'{componentField}' is not an answerable field. You may set only: " +
                              $"{string.Join(", ", ComponentFields)} on a component, or clientRestrictedList.");

            var component = node["components"]?.AsArray()
                .FirstOrDefault(c => c?["id"]?.GetValue<string>() == componentId);
            if (component is null)
                return (null, $"there is no component '{componentId}' in this project");

            component[componentField] = componentField == "markets"
                ? new JsonArray(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                     .Select(s => (JsonNode)s!).ToArray())
                : value;
            return (JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()), null);
        }

        // Everything else is refused BY NAME, so the model learns the boundary instead of retrying blindly.
        if (field.StartsWith("elementPools", StringComparison.OrdinalIgnoreCase))
            return (null, "element pools are the physicist's measured XRF background and cannot be changed " +
                          "through chat. If they are wrong, the physicist must re-measure and the operator " +
                          "must re-enter them at intake.");
        if (field.StartsWith("providedCandidates", StringComparison.OrdinalIgnoreCase))
            return (null, "provided candidates are an input seam and cannot be changed through chat.");

        return (null, $"'{field}' is not an answerable field. You may set only: " +
                      $"components.{{componentId}}.{{{string.Join("|", ComponentFields)}}}, or clientRestrictedList.");
    }
}
```
(`System.Text.Json.Nodes` is BCL — `Smx.Domain` stays dependency-free.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter IntakeAnswersTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/IntakeAnswers.cs src/Smx.Domain.Tests/IntakeAnswersTests.cs
git commit -m "feat(domain): IntakeAnswers allowlist — chat cannot rewrite the physicist's element pools"
```

---

## Task 5: `ChatTools` — the per-turn, project-bound mutating tools

**Read design decision 2 before writing this.** The model must never be able to name a project.

**Files:**
- Create: `src/Smx.Orchestrator/Agents/ChatTools.cs`
- Test: `src/Smx.Orchestrator.Tests/ChatToolsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests;

public class ChatToolsTests
{
    private const string P = "proj-1";

    private static (ChatTools tools, InMemoryRecordStore store) Build(string stage = Stages.Discovery)
    {
        var store = new InMemoryRecordStore();
        return (new ChatTools(store, P, stage), store);
    }

    [Fact]
    public void TheToolSchemas_ExposeNoProjectId()
    {
        // THE CROSS-PROJECT WRITE GUARD. If the model could name a project, one hallucinated id would
        // mutate a DIFFERENT project's analysis, with no undo and no reason for anyone to look. The tools
        // are bound to (projectId, stage) at construction; the schema must offer no way to override that.
        var (tools, _) = Build();
        foreach (var tool in tools.Tools().Cast<AIFunction>())
        {
            var schema = tool.JsonSchema.ToString();
            Assert.DoesNotContain("projectId", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("project_id", schema, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ApplyRevision_WritesTheSameRevisionDocTheEndpointWrites()
    {
        // "The same effect by two doors" (design §5). Chat does not get its own revise path — it writes
        // the very same RevisionDoc, so it goes down the very same OnRevisionAsync: the agent re-runs, the
        // Regulatory gate is voided, and a Learned Conclusion is written. If chat had its own path, one of
        // the two would eventually drift and only one would keep its guarantees.
        var (tools, store) = Build();
        var tool = tools.Tools().Cast<AIFunction>().Single(t => t.Name == "apply_revision");

        await tool.InvokeAsync(new AIFunctionArguments
        {
            ["target"] = "Ba tier",
            ["reason"] = "overlaps the Ti K-beta line",
        });

        var revision = Assert.Single(await store.GetRevisionsAsync(P));
        Assert.Equal(Stages.Discovery, revision.Stage);
        Assert.Equal("overlaps the Ti K-beta line", revision.Reason);
        Assert.Equal(RevisionStatus.Pending, revision.Status);   // the change feed will pick it up
    }

    [Fact]
    public async Task ApplyRevision_WithoutAReason_IsRefused_AndWritesNothing()
    {
        // Law 4. A revision without a reason is a silent edit, and the reason is the seed of the Learned
        // Conclusion — there would be nothing to learn. The tool returns an error the model can read and
        // correct, rather than throwing.
        var (tools, store) = Build();
        var tool = tools.Tools().Cast<AIFunction>().Single(t => t.Name == "apply_revision");

        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["target"] = "Ba tier",
            ["reason"] = "   ",
        }))?.ToString() ?? "";

        Assert.Contains("reason", result, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.GetRevisionsAsync(P));
    }

    [Fact]
    public async Task ApplyRevision_RecordsItsWriteInTheTrail_WithTheRecordId()
    {
        // The reply carries a tool-call trail (§5) so the UI can render "this sentence changed that record".
        var (tools, store) = Build();
        var tool = tools.Tools().Cast<AIFunction>().Single(t => t.Name == "apply_revision");
        await tool.InvokeAsync(new AIFunctionArguments { ["target"] = "Ba tier", ["reason"] = "overlaps Ti" });

        var call = Assert.Single(tools.Trail, c => c.Tool == "apply_revision");
        Assert.Equal((await store.GetRevisionsAsync(P))[0].Id, call.RecordId);
    }

    [Fact]
    public async Task RecordAnswer_IsRefusedOnceIntakeHasProducedConstraints()
    {
        // Once constraints exist, changing an input is not a GAP-FILL — it is a revision, and it must earn
        // a Learned Conclusion. Keeping the two tools' domains disjoint is what stops chat becoming a way
        // to mutate agent output without a reason.
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create(P, "acme", "bottle",
            System.Text.Json.JsonSerializer.SerializeToElement(new { components = Array.Empty<object>() })));
        await store.UpsertConstraintsAsync(new ConstraintsDoc { Id = RecordIds.Constraints(P), ProjectId = P });

        var tools = new ChatTools(store, P, Stages.Intake);
        var tool = tools.Tools().Cast<AIFunction>().Single(t => t.Name == "record_answer");

        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["field"] = "components.bottle.objective",
            ["value"] = "brand",
        }))?.ToString() ?? "";

        Assert.Contains("apply_revision", result);   // it names the right door
    }

    [Fact]
    public async Task RecordAnswer_RefusesToWriteTheElementPools()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertProjectAsync(ProjectDoc.Create(P, "acme", "bottle",
            System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "" } },
                elementPools = new[] { new { component = "bottle", element = "Zr", line = "Ka", status = "V", signalNote = (string?)null } },
            })));
        var tools = new ChatTools(store, P, Stages.Intake);
        var tool = tools.Tools().Cast<AIFunction>().Single(t => t.Name == "record_answer");

        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["field"] = "elementPools.0.status",
            ["value"] = "V",
        }))?.ToString() ?? "";

        Assert.Contains("physicist", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordAnswer_PatchesThePayload_AndReopensIntakeSoTheAgentReRuns()
    {
        var store = new InMemoryRecordStore();
        var project = ProjectDoc.Create(P, "acme", "bottle", System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "" } },
        }));
        project.Stages[Stages.Intake].Status = "needs-review";
        await store.UpsertProjectAsync(project);

        var tools = new ChatTools(store, P, Stages.Intake);
        var tool = tools.Tools().Cast<AIFunction>().Single(t => t.Name == "record_answer");
        await tool.InvokeAsync(new AIFunctionArguments
        {
            ["field"] = "components.bottle.objective",
            ["value"] = "brand protection",
        });

        var updated = await store.GetProjectAsync(P);
        Assert.Equal("brand protection",
            updated!.Payload.GetProperty("components")[0].GetProperty("objective").GetString());
        // Setting Intake back to `pending` IS the re-trigger: the ProjectDoc upsert is a change-feed event,
        // and OnProjectAsync runs Intake exactly when the stage is `pending` and no constraints exist.
        Assert.Equal("pending", updated.Stages[Stages.Intake].Status);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ChatToolsTests`
Expected: FAIL — `ChatTools` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Orchestrator/Agents/ChatTools.cs`:

```csharp
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

/// The tools that let a chat turn CHANGE something. Constructed fresh for each turn, closed over the
/// (projectId, stage) of the chat-message the change feed delivered.
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
public sealed class ChatTools(IRecordStore store, string projectId, string stage)
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

    /// Writes the SAME RevisionDoc that POST /projects/{id}/stages/{stage}/revise writes — so a chat
    /// instruction and the structured endpoint are literally "the same effect by two doors" (design §5),
    /// down to the same OnRevisionAsync, the same gate voiding, and the same Learned Conclusion. Chat does
    /// not get its own path; a second path would drift and only one of them would keep its guarantees.
    public async Task<string> ApplyRevisionAsync(
        string target, string reason, string? cas = null, string? componentId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "{\"error\":\"target is required — name what should change\"}";
        // Law 4: no silent edits. The reason is also the seed of the Learned Conclusion; without it there
        // is nothing for the system to learn from this change.
        if (string.IsNullOrWhiteSpace(reason))
            return "{\"error\":\"a reason is required — ask the operator WHY before changing anything\"}";
        if (stage == Stages.Regulatory && (string.IsNullOrWhiteSpace(cas) || string.IsNullOrWhiteSpace(componentId)))
            return "{\"error\":\"a regulatory revision must name the cas and componentId of the verdict to re-run\"}";

        var id = RecordIds.Revision(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
        await store.UpsertRevisionAsync(new RevisionDoc
        {
            Id = id, ProjectId = projectId, Stage = stage, Target = target, Reason = reason,
            Cas = cas, ComponentId = componentId, CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, ct);
        Trail.Add(new ChatToolCall("apply_revision", $"{target} — {reason}", id));

        // The revision is QUEUED, not applied: the change feed will run it. Say so plainly, or the model
        // will tell the operator it is already done.
        return "{\"queued\":true,\"note\":\"the revision is queued — the stage will re-run and a Learned " +
               "Conclusion will be recorded. Tell the operator it is queued, not that it is already applied.\"}";
    }

    public async Task<string> RecordAnswerAsync(string field, string value, CancellationToken ct = default)
    {
        if (await store.GetConstraintsAsync(projectId, ct) is not null)
            return "{\"error\":\"intake has already produced constraints, so this is no longer a gap-fill. " +
                   "To change an established input, use apply_revision with the operator's reason.\"}";
        if (await store.GetProjectAsync(projectId, ct) is not { } project)
            return "{\"error\":\"project not found\"}";

        var (patched, error) = IntakeAnswers.Patch(project.Payload, field, value);
        if (error is not null) return JsonSerializer.Serialize(new { error }, Json.Options);

        project.Payload = patched!.Value;
        // Setting Intake back to `pending` IS the re-trigger: this upsert is a change-feed event, and
        // OnProjectAsync runs Intake exactly when the stage is `pending` and no constraints exist yet.
        project.Stages[Stages.Intake].Status = "pending";
        project.Stages[Stages.Intake].Error = null;
        await store.UpsertProjectAsync(project, ct);
        Trail.Add(new ChatToolCall("record_answer", $"{field} = {value}", project.Id));

        return "{\"recorded\":true,\"note\":\"intake will re-run with this answer\"}";
    }
}
```
Add `using System.Text.Json;` for the error serialization.

> **The `= null` defaults on `cas` / `componentId` are load-bearing, not decoration.** `AIFunctionFactory`
> emits a parameter without a default as **`"required"`** in the tool's JSON schema. Without them, a
> Discovery revision — which has no cas/componentId — would be rejected by the binding with
> `ArgumentException: missing a value for the required parameter 'cas'`, and the tool would be unusable for
> the most common case. This exact bug shipped in Plan 3a. That is why Step 1's tests invoke the real
> `AIFunction` via `InvokeAsync` and not the C# method.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ChatToolsTests`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ChatTools.cs src/Smx.Orchestrator.Tests/ChatToolsTests.cs
git commit -m "feat(agents): ChatTools — apply_revision + record_answer, bound to one project, no gate tool"
```

---

## Task 6: `ChatAgent`

**Files:**
- Create: `src/Smx.Orchestrator/Agents/ChatAgent.cs`
- Modify: `src/Smx.Orchestrator/Agents/ToolBox.cs` (add a `ReadToolsFor(stage)` accessor)
- Test: `src/Smx.Orchestrator.Tests/ChatAgentTests.cs`

**Why one `ChatAgent` and not four conversational stage agents.** The stage agents' `Instructions` all end with *"Reply with ONLY a JSON object"* — useless for dialogue. Rather than fork four conversational personas, there is one `ChatAgent` whose *stage-focus* comes from three things, exactly as §5 requires: **the stage's record inputs** (its context), **the stage's read tools** (what it can look up), and **the stage's thread** (what was already said). Its domain competence comes from retrieved sources, not from a memorised persona — which is the "answer only from retrieved sources" discipline the whole system rests on.

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ChatAgentTests
{
    [Fact]
    public async Task Run_GivesTheAgentTheThread_TheStageInputs_AndTheNewMessage()
    {
        // The agent gets a FRESH MAF session every turn (MafAgent.StartThreadAsync creates a new
        // AgentSession and there is no rehydration API). So everything it knows must be in this one
        // prompt — if the thread isn't in there, the operator is talking to an agent with amnesia.
        var agent = new ScriptedAgent("Because the catalog lists it clean.");

        var reply = await ChatAgent.RunAsync(agent,
            thread: ChatThread.Render([new ChatTurn("operator", "why is Ba tier A?", "t1", [])]),
            stageInputsJson: """{"substances":[{"element":"Ba","tier":"A"}]}""",
            message: "and for HDPE?",
            ct: default);

        var prompt = Assert.Single(agent.Received);
        Assert.Contains("why is Ba tier A?", prompt);        // the rehydrated thread
        Assert.Contains("\"tier\":\"A\"", prompt);           // the stage's record inputs
        Assert.Contains("and for HDPE?", prompt);            // the new message
        Assert.Equal("Because the catalog lists it clean.", reply);
    }

    [Fact]
    public void Instructions_ForbidClaimingAGateWasSigned()
    {
        // Law 9. The structural guard is that no gate tool exists (ChatTools) — but a model with no gate
        // tool can still *say* "I've approved it", and an operator who believes that is an operator who
        // never signs. Belt and braces: the tool list makes it impossible, the instructions make it clear.
        Assert.Contains("never", ChatAgent.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gate", ChatAgent.Instructions, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ChatAgentTests`
Expected: FAIL — `ChatAgent` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Orchestrator/Agents/ChatAgent.cs`:

```csharp
namespace Smx.Orchestrator.Agents;

/// The per-stage conversational agent (design §5). ONE agent, not four: the stage agents' Instructions all
/// end with "Reply with ONLY a JSON object", which is useless for dialogue. Its stage-focus comes from the
/// three things §5 actually asks for — the stage's record inputs, the stage's read tools, and the stage's
/// thread — rather than from a memorised persona. Its competence comes from retrieved sources, which is the
/// discipline the whole system rests on anyway.
///
/// Deliberately NOT run through ValidatedAgentRunner: that forces a JSON schema and retries on a parse
/// failure. A chat turn is prose plus optional tool calls, and MAF's UseFunctionInvocation (wired in
/// FoundryChatClientFactory) already runs the tools and hands back the final text.
public static class ChatAgent
{
    public const string AgentName = "chat";

    public const string Instructions = """
        You are the SMX stage agent, talking to the Project Leader about the stage you are on. You are the
        same agent that produced this stage's analysis, and you are answering for it.

        You will be given: the conversation so far (this is your only memory — you do not remember anything
        else), this stage's current record inputs, and the operator's new message.

        Answering:
        - Answer ONLY from your tools and from the record inputs you were given. Never assert a regulatory
          fact, a CAS number, a tier or a verdict from memory. If your tools return nothing, say so plainly
          — "I have no source for that" is a good answer; an invented one is a harmful answer.
        - Cite what you relied on. The operator must be able to check you.
        - Be direct and brief. This is a working conversation, not a report.

        Changing things:
        - You may NEVER change an analytical result by saying you have. The ONLY way to change anything is
          to call `apply_revision`, and it requires the operator's REASON. If they ask for a change without
          giving a reason, ask them why — the reason is recorded as a Learned Conclusion and is how the
          system gets smarter. Do not invent a reason on their behalf.
        - When a change is queued, say it is QUEUED and will re-run — not that it is already done.
        - `record_answer` only fills in intake inputs the operator is still supplying, and never the element
          pools (that is the physicist's measured data).

        Gates:
        - You CANNOT sign a gate, approve anything, or record a determination, and you must never say or
          imply that you have. Gate approvals and R.E. determinations are explicit, signed actions the
          operator takes deliberately — never something agreed in conversation. If the operator asks you to
          approve, tell them plainly that they must do it themselves, and that you can show them what is
          still open.
        - Be aware: applying a revision to this stage will VOID an existing regulatory approval, because
          the analysis it was signed over has changed. Say so before you do it.
        """;

    /// One conversational turn. The thread is re-rendered into the prompt because the MAF session is fresh
    /// every time and cannot be rehydrated — the record is the agent's entire memory.
    public static async Task<string> RunAsync(
        ISmxAgent agent, string thread, string stageInputsJson, string message, CancellationToken ct)
    {
        var conversationThread = await agent.StartThreadAsync(ct);
        return await conversationThread.SendAsync($"""
            CONVERSATION SO FAR (this is your entire memory of it):
            {thread}

            THIS STAGE'S CURRENT RECORD:
            {stageInputsJson}

            THE OPERATOR'S NEW MESSAGE:
            {message}
            """, ct);
    }
}
```

In `src/Smx.Orchestrator/Agents/ToolBox.cs`, add an accessor so a chat turn gets the stage's **read** tools:
```csharp
    /// The READ tools for a stage — what a chat turn on that stage may look things up with. Mutating tools
    /// come from ChatTools, which is bound to one project. Intake and Discovery deliberately keep their
    /// knowledge-layer reads here; Regulatory does not have them (per CLAUDE.md, knowledge reads are scoped
    /// to intake/discovery/dosing).
    public IList<AITool> ReadToolsFor(string stage) => stage switch
    {
        Stages.Intake => IntakeTools(),
        Stages.Discovery => DiscoveryTools(),
        Stages.Regulatory => RegulatoryTools(),
        _ => [],
    };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ChatAgentTests`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ChatAgent.cs src/Smx.Orchestrator/Agents/ToolBox.cs src/Smx.Orchestrator.Tests/ChatAgentTests.cs
git commit -m "feat(agents): ChatAgent — stage-scoped dialogue, no gate capability, no memory but the record"
```

---

## Task 7: `IAgentRuns.RunChatAsync`

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`, `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`
- Test: covered by Task 8's dispatch tests

- [ ] **Step 1: Extend the interface**

```csharp
    /// One chat turn. Returns the agent's reply text; the tool-call trail is collected by the ChatTools
    /// instance the caller passes in (it is bound to this project + stage, so the model cannot name another).
    Task<string> RunChatAsync(string stage, ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct);
```

- [ ] **Step 2: Implement in `AgentRuns`**

```csharp
    public Task<string> RunChatAsync(string stage, ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct) =>
        ChatAgent.RunAsync(
            // The stage's READ tools plus this turn's project-bound MUTATING tools. Note what is not in
            // this list: anything that could sign a gate.
            new MafAgent(chatClient, ChatAgent.AgentName, ChatAgent.Instructions,
                [.. toolBox.ReadToolsFor(stage), .. chatTools.Tools()]),
            thread, stageInputsJson, message, ct);
```

- [ ] **Step 3: Extend `FakeAgentRuns`**

```csharp
    public Func<string, ChatTools, string, string, string, Task<string>> Chat { get; set; } =
        (_, _, _, _, message) => Task.FromResult($"Echo: {message}");
    public int ChatCalls;

    Task<string> IAgentRuns.RunChatAsync(string stage, ChatTools chatTools, string thread,
        string stageInputsJson, string message, CancellationToken ct)
    { Interlocked.Increment(ref ChatCalls); return Chat(stage, chatTools, thread, stageInputsJson, message); }
```

- [ ] **Step 4: Build**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Dispatch/AgentRuns.cs src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs
git commit -m "feat(agents): IAgentRuns.RunChatAsync — a chat turn is stage read-tools + project-bound chat tools"
```

---

## Task 8: `StageDispatcher.OnChatMessageAsync`

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`, `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Test: `src/Smx.Orchestrator.Tests/ChatDispatchTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Orchestrator.Tests/ChatDispatchTests.cs`. Build the `StageDispatcher` exactly as `RevisionDispatchTests` does (it needs `IRecordStore`, `IAgentRuns`, `ILearnedConclusionWriter`, `regulatoryParallelism`).

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Knowledge;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ChatDispatchTests
{
    private const string P = "proj-1";

    private readonly InMemoryRecordStore _store = new();
    private readonly FakeAgentRuns _agents = new();

    private StageDispatcher Dispatcher() => new(_store, _agents,
        new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(),
            new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance),
        regulatoryParallelism: 2);

    private async Task SeedProjectAsync()
    {
        await _store.UpsertProjectAsync(
            ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { })));
        await _store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Ba", "sulfate", "cas-ba", null, null, true, "A", "clean",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        });
    }

    private static ChatMessageDoc Msg(string key, string text, string stage = Stages.Discovery, string at = "2026-07-13T10:00:00Z") => new()
    {
        Id = RecordIds.ChatMessage(P, stage, key), ProjectId = P, Stage = stage, Text = text, CreatedAt = at,
    };

    [Fact]
    public async Task ChatMessage_WritesAReply_AndMarksTheMessageAnswered()
    {
        await SeedProjectAsync();
        _agents.Chat = (_, _, _, _, _) => Task.FromResult("Because the catalog lists it clean.");
        var msg = Msg("aaaa1111", "why is Ba tier A?");
        await _store.UpsertChatMessageAsync(msg);

        await Dispatcher().OnRecordChangedAsync(msg, default);

        var thread = await _store.GetChatThreadAsync(P, Stages.Discovery);
        Assert.Equal(2, thread.Count);
        var reply = thread.Single(t => t.Role == "agent");
        Assert.Equal("Because the catalog lists it clean.", reply.Text);
        Assert.Equal(ChatStatus.Answered, (await _store.GetChatMessageAsync(P, msg.Id))!.Status);
    }

    [Fact]
    public async Task ChatMessage_RehydratesThePriorThreadIntoThePrompt()
    {
        // The agent gets a FRESH MAF session every turn and cannot rehydrate one — so if the thread is not
        // in this prompt, the operator is talking to an agent with amnesia that will happily contradict
        // the answer it gave a minute ago.
        await SeedProjectAsync();
        await _store.UpsertChatMessageAsync(Msg("aaaa1111", "why is Ba tier A?", at: "2026-07-13T10:00:00Z"));
        await _store.UpsertChatReplyAsync(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Discovery, "aaaa1111"), ProjectId = P, Stage = Stages.Discovery,
            MessageId = RecordIds.ChatMessage(P, Stages.Discovery, "aaaa1111"),
            Text = "The catalog lists it clean.", CreatedAt = "2026-07-13T10:00:05Z",
        });

        string? seenThread = null, seenInputs = null;
        _agents.Chat = (_, _, thread, inputs, _) => { seenThread = thread; seenInputs = inputs; return Task.FromResult("ok"); };

        var next = Msg("bbbb2222", "and for HDPE?", at: "2026-07-13T10:01:00Z");
        await _store.UpsertChatMessageAsync(next);
        await Dispatcher().OnRecordChangedAsync(next, default);

        Assert.Contains("why is Ba tier A?", seenThread);
        Assert.Contains("The catalog lists it clean.", seenThread);
        Assert.Contains("cas-ba", seenInputs);   // the stage's current record, not just the chatter
    }

    [Fact]
    public async Task ChatMessage_ThreadsAreScopedToTheirStage()
    {
        // Agents do not share a conversation (Law 9), so neither do their threads. A Regulatory agent that
        // could see Discovery's chatter would be reasoning over another agent's context — the exact
        // cross-contamination the per-stage isolation exists to prevent.
        await SeedProjectAsync();
        await _store.UpsertChatMessageAsync(Msg("aaaa1111", "DISCOVERY-ONLY-CHATTER", Stages.Discovery, "2026-07-13T10:00:00Z"));

        string? seenThread = null;
        _agents.Chat = (_, _, thread, _, _) => { seenThread = thread; return Task.FromResult("ok"); };

        var reg = Msg("cccc3333", "is Ba compliant?", Stages.Regulatory, "2026-07-13T10:02:00Z");
        await _store.UpsertChatMessageAsync(reg);
        await Dispatcher().OnRecordChangedAsync(reg, default);

        Assert.DoesNotContain("DISCOVERY-ONLY-CHATTER", seenThread);
    }

    [Fact]
    public async Task ChatMessage_IsIdempotent_UnderChangeFeedRedelivery()
    {
        // A redelivered message must not re-run an agent that may already have QUEUED A REVISION — that
        // would apply the operator's change twice.
        await SeedProjectAsync();
        var msg = Msg("aaaa1111", "why is Ba tier A?");
        await _store.UpsertChatMessageAsync(msg);
        var dispatcher = Dispatcher();

        await dispatcher.OnRecordChangedAsync(msg, default);
        var answered = (await _store.GetChatMessageAsync(P, msg.Id))!;
        await dispatcher.OnRecordChangedAsync(answered, default);   // redelivery of the doc we just wrote

        Assert.Equal(1, _agents.ChatCalls);
        Assert.Single((await _store.GetChatThreadAsync(P, Stages.Discovery)).Where(t => t.Role == "agent"));
    }

    [Fact]
    public async Task ChatMessage_OnAnAgentFailure_MarksItFailed_AndWritesNoReply()
    {
        // A half-written reply is worse than none: the operator would read it as the agent's word.
        await SeedProjectAsync();
        _agents.Chat = (_, _, _, _, _) => throw new InvalidOperationException("model unavailable");
        var msg = Msg("aaaa1111", "why is Ba tier A?");
        await _store.UpsertChatMessageAsync(msg);

        await Dispatcher().OnRecordChangedAsync(msg, default);

        var stored = (await _store.GetChatMessageAsync(P, msg.Id))!;
        Assert.Equal(ChatStatus.Failed, stored.Status);
        Assert.Contains("model unavailable", stored.Error);
        Assert.DoesNotContain(await _store.GetChatThreadAsync(P, Stages.Discovery), t => t.Role == "agent");
    }

    [Fact]
    public async Task ChatMessage_CarriesTheToolTrailOntoTheReply()
    {
        // §5: "no silent mutations" — the reply carries its tool-call trail so the UI can show which
        // sentence changed which record.
        await SeedProjectAsync();
        _agents.Chat = async (_, chatTools, _, _, _) =>
        {
            // Simulate the model calling the tool, exactly as MAF's function invocation would.
            await chatTools.ApplyRevisionAsync("Ba tier", "overlaps the Ti K-beta line");
            return "Queued — I'll re-run discovery with that reason.";
        };
        var msg = Msg("aaaa1111", "move Ba to C, it overlaps Ti");
        await _store.UpsertChatMessageAsync(msg);

        await Dispatcher().OnRecordChangedAsync(msg, default);

        var reply = (await _store.GetChatThreadAsync(P, Stages.Discovery)).Single(t => t.Role == "agent");
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("apply_revision", call.Tool);
        Assert.Equal((await _store.GetRevisionsAsync(P))[0].Id, call.RecordId);   // the audit link
    }

    [Fact]
    public void Router_RoutesAChatMessage_ButNotAChatReply()
    {
        // A reply is an OUTPUT, not a trigger. Routing it to a doc type would have the dispatcher re-enter
        // on its own output — an infinite conversation with itself.
        var msg = JsonSerializer.SerializeToElement(Msg("aaaa1111", "hi"), Json.Options);
        Assert.IsType<ChatMessageDoc>(RecordDocRouter.Route(msg));

        var reply = JsonSerializer.SerializeToElement(new ChatReplyDoc
        {
            Id = RecordIds.ChatReply(P, Stages.Discovery, "aaaa1111"), ProjectId = P, Stage = Stages.Discovery,
            MessageId = "x", Text = "hi", CreatedAt = "t",
        }, Json.Options);
        Assert.Null(RecordDocRouter.Route(reply));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ChatDispatchTests`
Expected: FAIL — `OnChatMessageAsync` / the router arms / `GetChatMessageAsync` do not exist.

- [ ] **Step 3: Write the implementation**

`RecordDocRouter` — two new arms:
```csharp
            RecordTypes.ChatMessage => element.Deserialize<ChatMessageDoc>(Json.Options),
            RecordTypes.ChatReply => null,   // terminal: a reply is an output, not a trigger
```
Route `ChatReply` to `null` deliberately and comment it — routing it to a doc type would have the dispatcher re-enter on its own output. (`ChangeFeedWorker` skips `null`.)

`StageDispatcher` — add `case ChatMessageDoc m: await OnChatMessageAsync(m, ct); break;` and:

```csharp
    /// A chat turn (design §5). The reply is a record, so the conversation survives a restart and a
    /// multi-day re-entry (Law 6) — the agent itself remembers nothing.
    private async Task OnChatMessageAsync(ChatMessageDoc m, CancellationToken ct)
    {
        // At-least-once feed: only the first delivery acts. A redelivered message must not re-run an agent
        // that may already have queued a revision.
        if (m.Status != ChatStatus.Pending) return;

        try
        {
            var thread = ChatThread.Render(await store.GetChatThreadAsync(m.ProjectId, m.Stage, ct));
            var inputs = await StageInputsJsonAsync(m.ProjectId, m.Stage, ct);

            // Bound to THIS project and THIS stage. The model has no parameter with which to name another.
            var chatTools = new ChatTools(store, m.ProjectId, m.Stage);
            var text = await agents.RunChatAsync(m.Stage, chatTools, thread, inputs, m.Text, ct);

            await store.UpsertChatReplyAsync(new ChatReplyDoc
            {
                // Derived from the message's key, so redelivery upserts one reply instead of appending.
                Id = RecordIds.ChatReply(m.ProjectId, m.Stage, KeyOf(m.Id)),
                ProjectId = m.ProjectId, Stage = m.Stage, MessageId = m.Id,
                Text = text, ToolCalls = chatTools.Trail,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);

            m.Status = ChatStatus.Answered;
            m.Error = null;
            await store.UpsertChatMessageAsync(m, ct);
        }
        catch (Exception e)
        {
            // No half-written reply: the operator must never read a partial answer as the agent's word.
            m.Status = ChatStatus.Failed;
            m.Error = e.Message;
            await store.UpsertChatMessageAsync(m, ct);
        }
    }

    /// The stage's current record inputs — what the agent is answering ABOUT.
    private async Task<string> StageInputsJsonAsync(string projectId, string stage, CancellationToken ct) => stage switch
    {
        Stages.Intake => JsonSerializer.Serialize(await store.GetProjectAsync(projectId, ct), Json.Options),
        Stages.Discovery => JsonSerializer.Serialize(await store.GetCandidatesAsync(projectId, ct), Json.Options),
        Stages.Regulatory => JsonSerializer.Serialize(await store.GetVerdictsAsync(projectId, ct), Json.Options),
        Stages.Matrix => JsonSerializer.Serialize(await store.GetMatrixAsync(projectId, ct), Json.Options),
        _ => "{}",
    };

    private static string KeyOf(string chatMessageId) => chatMessageId.Split('|')[^1];
```

- [ ] **Step 4: Run tests; then the full suite** (`dotnet test src/Smx.Backend.sln`).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(dispatch): OnChatMessageAsync — rehydrate the thread, run the turn, record the reply"
```

---

## Task 9: The guardrail suite — "chat never signs a gate"

**This task exists to make Law 9 unbreakable, and to make its breakage loud if anyone tries.** No new production code; it is a test file whose failure is a design alarm.

**Files:**
- Test: `src/Smx.Orchestrator.Tests/ChatGuardrailTests.cs`

- [ ] **Step 1: Write the tests**

Create `src/Smx.Orchestrator.Tests/ChatGuardrailTests.cs`.

```csharp
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

/// Law 9 — gates are operator-signed records, never voice- or chat-committed. This file is the proof, and
/// it is meant to fail loudly the day someone adds a convenient "approve" tool to the chat surface.
public class ChatGuardrailTests
{
    private const string P = "proj-1";

    private static ToolBox Tools() => new(
        new FakeCatalogLookup(), new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(),
        new FakeSearch(), new InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch());

    [Theory]
    [InlineData(Stages.Intake)]
    [InlineData(Stages.Discovery)]
    [InlineData(Stages.Regulatory)]
    [InlineData(Stages.Matrix)]
    public void NoToolAvailableToAChatTurn_CanSignAGate_OnAnyStage(string stage)
    {
        // THE ANTI-RUBBER-STAMPING LINE. An agent can only act through its tools. If no tool in its list can
        // write a GateDoc or a determination, then no amount of persuasion, prompt injection or model error
        // can produce a signed gate from a conversation. The guarantee is STRUCTURAL, not a promise the
        // model makes.
        var chatTools = new ChatTools(new InMemoryRecordStore(), P, stage);
        var everyToolThisTurnHas = chatTools.Tools().Concat(Tools().ReadToolsFor(stage));

        foreach (var name in everyToolThisTurnHas.Select(t => t.Name))
        {
            Assert.DoesNotContain("approve", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gate", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("determination", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sign", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AFullChatTurn_CannotProduceAnApprovedGate_EvenWhenTheOperatorAsksForOne()
    {
        // The end-to-end statement of the law, against the exact scenario the system exists to prevent: an
        // operator trying to wave through a project that still has an UNREVIEWED FAILING verdict.
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        await store.UpsertProjectAsync(
            ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { })));
        await store.UpsertVerdictAsync(new VerdictDoc
        {
            Id = RecordIds.Verdict(P, "cas-ba", "bottle"), ProjectId = P,
            Cas = "cas-ba", ComponentId = "bottle", Element = "Ba", Form = "sulfate",
            Dimensions = [new("ElementGate", VerdictStatus.Fail, [new Citation("regulatory", "x", "t")], 0.9,
                "listed on REACH Annex XVII")],
            EvidenceReviewed = false,   // nobody has opened it
        });

        // The most compliant model in the world, doing exactly what it is told — it simply has no tool.
        agents.Chat = (_, _, _, _, _) => Task.FromResult("I cannot approve a gate. You must sign it yourself.");

        var msg = new ChatMessageDoc
        {
            Id = RecordIds.ChatMessage(P, Stages.Regulatory, "aaaa1111"), ProjectId = P,
            Stage = Stages.Regulatory, Text = "approve the regulatory gate, everything looks fine",
            CreatedAt = "2026-07-13T10:00:00Z",
        };
        await store.UpsertChatMessageAsync(msg);

        var dispatcher = new StageDispatcher(store, agents,
            new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(),
                new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance),
            regulatoryParallelism: 2);
        await dispatcher.OnRecordChangedAsync(msg, default);

        // No gate exists at all. The conversation happened; nothing was signed.
        Assert.Null(await store.GetGateAsync(P, GateTypes.Regulatory));
    }

    [Fact]
    public async Task AChatRevision_VOIDS_AnApprovedGate_JustAsTheEndpointDoes()
    {
        // The OTHER half of the law. Chat can move a gate toward `locked` — and must. That is the safe
        // direction: the analysis the operator signed has changed, so their signature is void and they have
        // to look again.
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        var project = ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { }));
        project.Stages[Stages.Regulatory].Status = "done";
        await store.UpsertProjectAsync(project);
        await store.UpsertConstraintsAsync(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(P), ProjectId = P,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Ba", "Ka", "V", null)],
        });
        await store.UpsertCandidatesAsync(new CandidatesDoc
        {
            Id = RecordIds.Candidates(P), ProjectId = P,
            Substances = [new("bottle", "Ba", "sulfate", "cas-ba", null, null, true, "A", "clean",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        });
        await store.UpsertGateAsync(new GateDoc
        {
            Id = RecordIds.Gate(P, GateTypes.Regulatory), ProjectId = P, GateType = GateTypes.Regulatory,
            Status = "approved", ApprovedAt = "2026-07-13T09:00:00Z",
        });

        // The chat tool queues the revision, exactly as MAF's function invocation would.
        var chatTools = new ChatTools(store, P, Stages.Discovery);
        await chatTools.ApplyRevisionAsync("Ba tier", "overlaps the Ti K-beta line");

        // Then the change feed delivers it — the SAME OnRevisionAsync the endpoint's revision goes down.
        var revision = Assert.Single(await store.GetRevisionsAsync(P));
        var dispatcher = new StageDispatcher(store, agents,
            new LearnedConclusionWriter(new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(),
                new FakeEmbedder(), NullLogger<LearnedConclusionWriter>.Instance),
            regulatoryParallelism: 2);
        await dispatcher.OnRecordChangedAsync(revision, default);

        var gate = await store.GetGateAsync(P, GateTypes.Regulatory);
        Assert.Equal("locked", gate!.Status);
        Assert.Null(gate.ApprovedAt);
        Assert.Equal("awaiting-RE", (await store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task AChatRevision_AndTheEndpointsRevision_AreTheSameDocInEveryFieldThatDrivesBehaviour()
    {
        // "The same effect by two doors" (§5), asserted rather than merely asserted-about. If these ever
        // diverge, one door keeps its guarantees (the gate void, the Learned Conclusion, the re-run) and
        // the other quietly does not — and nobody would notice which.
        var store = new InMemoryRecordStore();
        var chatTools = new ChatTools(store, P, Stages.Regulatory);

        await chatTools.ApplyRevisionAsync("the Ba verdict", "the SVHC listing is out of date",
            cas: "cas-ba", componentId: "bottle");

        var fromChat = Assert.Single(await store.GetRevisionsAsync(P));

        // What POST /projects/{id}/stages/regulatory/revise writes, per RevisionEndpoints.
        Assert.Equal(Stages.Regulatory, fromChat.Stage);
        Assert.Equal("the Ba verdict", fromChat.Target);
        Assert.Equal("the SVHC listing is out of date", fromChat.Reason);
        Assert.Equal("cas-ba", fromChat.Cas);
        Assert.Equal("bottle", fromChat.ComponentId);
        Assert.Equal(RevisionStatus.Pending, fromChat.Status);
        Assert.Equal(RecordTypes.Revision, fromChat.Type);
        Assert.StartsWith($"{P}|revision|{Stages.Regulatory}|", fromChat.Id);
        Assert.Contains('.', fromChat.CreatedAt);   // "O" format, not a whole-second "…Z" that would misorder
    }
}
```

You will need a `FakeLearnedConclusionsSearch` for the `ToolBox` — one already exists in `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`. Use it; do not write a second.

- [ ] **Step 2: Run — they should pass immediately**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj --filter ChatGuardrailTests`
Expected: PASS (7 cases). The guarantees are **structural** — they were built in Tasks 5–8, so nothing new should be needed to satisfy them.

**If any of these fails, that is a real design hole, not a test to adjust. Report it and stop.**

- [ ] **Step 3: Mutation-test the guardrail.** Temporarily add a tool named `approve_gate` to `ChatTools.Tools()` → the theory MUST fail on every stage. Restore. A guardrail test that cannot fail is theatre.

- [ ] **Step 4: Commit**

```bash
git add src/Smx.Orchestrator.Tests/ChatGuardrailTests.cs
git commit -m "test(chat): Law 9 — chat can void a gate, and can never sign one"
```

---

## Task 10: The chat endpoints

**Files:**
- Create: `src/Smx.Backend/Api/ChatEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs` (`app.MapChatEndpoints();`)
- Test: `src/Smx.Backend.Tests/ChatEndpointsTests.cs`

⚠️ **`[FromServices]` on every store param.** See the top of this plan.

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/ChatEndpointsTests.cs`, mirroring `RevisionEndpointsTests` (`WebApplicationFactory<Program>` + an injected `InMemoryRecordStore`).

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class ChatEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string P = "proj-1";
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ChatEndpointsTests(WebApplicationFactory<Program> factory) =>
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();

    private Task SeedAsync() => _store.UpsertProjectAsync(
        ProjectDoc.Create(P, "acme", "bottle", JsonSerializer.SerializeToElement(new { })));

    [Fact]
    public async Task PostChat_QueuesAPendingMessageOnTheBus()
    {
        // Record-as-bus: the backend cannot run an agent, so writing the doc IS the dispatch. 202, not 200 —
        // nothing has been answered yet; the UI polls GET .../chat for the reply.
        await SeedAsync();
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/chat",
            new { text = "why is Ba tier A?" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var turn = Assert.Single(await _store.GetChatThreadAsync(P, Stages.Discovery));
        Assert.Equal("operator", turn.Role);
        Assert.Equal("why is Ba tier A?", turn.Text);
    }

    [Fact]
    public async Task PostChat_WithBlankText_Is422_AndWritesNothing()
    {
        await SeedAsync();
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/chat", new { text = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(await _store.GetChatThreadAsync(P, Stages.Discovery));
    }

    [Fact]
    public async Task PostChat_ToAnUnknownStage_Is422()
    {
        await SeedAsync();
        var response = await _client.PostAsJsonAsync($"/projects/{P}/stages/nonsense/chat", new { text = "hi" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task PostChat_ToAnUnknownProject_Is404()
    {
        var response = await _client.PostAsJsonAsync("/projects/proj-nope/stages/discovery/chat", new { text = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetChat_IsEmptyOnColdStart_ThenReturnsTheThread()
    {
        await SeedAsync();
        var empty = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/stages/discovery/chat");
        Assert.Equal(0, empty.GetArrayLength());

        await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/chat", new { text = "why is Ba tier A?" });

        var thread = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/stages/discovery/chat");
        Assert.Equal(1, thread.GetArrayLength());
    }

    [Fact]
    public async Task GetChat_IsScopedToOneStage()
    {
        // Agents don't share a conversation (Law 9). Neither do their threads — and the UI must not be able
        // to show a Regulatory transcript on the Discovery screen.
        await SeedAsync();
        await _client.PostAsJsonAsync($"/projects/{P}/stages/discovery/chat", new { text = "discovery question" });

        var regulatory = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/stages/regulatory/chat");
        Assert.Equal(0, regulatory.GetArrayLength());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ChatEndpointsTests`
Expected: FAIL — 404 on every route (they don't exist yet).

- [ ] **Step 3: Write the implementation**

```csharp
public sealed record ChatRequest(string Text);

public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on the store params is required, not decorative — minimal APIs resolve
        // service-vs-body at endpoint-build time across the WHOLE app's endpoint data source, and a
        // mis-inferred store param breaks routing for EVERY route, /healthz included. See ProjectEndpoints.
        app.MapPost("/projects/{projectId}/stages/{stage}/chat",
            async (string projectId, string stage, ChatRequest req, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new { error = $"unknown stage '{stage}'" });
            if (string.IsNullOrWhiteSpace(req.Text))
                return Results.UnprocessableEntity(new { error = "text is required" });
            if (await store.GetProjectAsync(projectId, ct) is null) return Results.NotFound();

            var id = RecordIds.ChatMessage(projectId, stage, Guid.NewGuid().ToString("N")[..8]);
            await store.UpsertChatMessageAsync(new ChatMessageDoc
            {
                Id = id, ProjectId = projectId, Stage = stage, Text = req.Text,
                CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            }, ct);

            // Record-as-bus: writing the doc IS the dispatch. The change feed runs the turn; the UI polls
            // GET .../chat for the reply. 202, because nothing has been answered yet.
            return Results.Accepted($"/projects/{projectId}/stages/{stage}/chat",
                new { messageId = id, status = ChatStatus.Pending });
        });

        app.MapGet("/projects/{projectId}/stages/{stage}/chat",
            async (string projectId, string stage, [FromServices] IRecordStore store, CancellationToken ct) =>
                Results.Json(await store.GetChatThreadAsync(projectId, stage, ct), Json.Options));
    }
}
```
Add `public static readonly string[] All = [Intake, Discovery, Regulatory, Matrix];` to `Stages` in `src/Smx.Domain/Records/RecordIds.cs` (with a test that it lists every constant on the class — otherwise a stage added later is silently un-chattable).

- [ ] **Step 4: Run tests; then the full suite.** If **every** backend test suddenly 500s, you left off a `[FromServices]`.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend src/Smx.Backend.Tests src/Smx.Domain/Records/RecordIds.cs
git commit -m "feat(api): POST/GET /stages/{stage}/chat — the per-stage conversation, on the bus"
```

---

## Task 11: DI wiring + the host smoke test

**Files:** `src/Smx.Orchestrator/Program.cs` (via `OrchestratorHost.ConfigureServices`), `src/Smx.Orchestrator.Tests/OrchestratorHostWiringTests.cs`

`ChatTools` is **not** a DI service — it is constructed per turn by the dispatcher, closed over the project and stage. That is the whole point of Task 5; do not register it as a singleton, and add a comment saying why so nobody "tidies" it into the container.

Nothing else new needs registering (`ChatAgent` is static; `AgentRuns` already has `IChatClient` + `ToolBox`). Confirm the existing `OrchestratorHostWiringTests` still resolves `StageDispatcher` and extend it if the ctor changed.

- [ ] **Step 1:** `dotnet test src/Smx.Backend.sln` — full suite green.
- [ ] **Step 2:** Commit: `chore(orchestrator): confirm chat wiring; ChatTools stays per-turn, not a singleton`

---

## Final verification

- [ ] **Whole suite green**
```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```
Baseline was **231**; expect roughly **275+**.

- [ ] **Infra unchanged, and confirm it.** Chat docs live in the existing `record` container (PK `/projectId`). Run the bicep builds anyway and state in your report that no infra change was needed:
```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
diff infra/modules/compute.bicep infra/single-rg/modules/compute.bicep && echo "twins identical"
```

- [ ] **Spec §5 coverage — point at the code for each claim**

| §5 claim | Where it is true |
|---|---|
| "the operator's message is a `chat-message` doc; the reply is a `chat-reply` doc" | `ChatDocs.cs`; `POST …/chat` writes one; `OnChatMessageAsync` writes the other |
| "the conversation survives multi-day re-entry because it lives in the record" | `ChatThread.Render` — the MAF session is fresh every turn and cannot be rehydrated |
| "chat is per-stage, not one global thread" | `GetChatThreadAsync(projectId, stage)`; the stage-scoping test in Task 8 |
| "`apply_revision` … the same effect by two doors" | `ChatTools.ApplyRevisionAsync` writes the **same `RevisionDoc`** the endpoint writes; asserted in Task 9 |
| "`record_answer(field, value)` → intake gap-fill" | `ChatTools.RecordAnswerAsync` + `IntakeAnswers` (allowlist; element pools refused) |
| "chat can instruct and propose, but **never signs a gate**" | **No gate tool exists.** Task 9's theory proves it on every stage, and is mutation-tested |
| "every chat-driven change is a tool call → a persisted record write" | `ChatTools.Trail` → `ChatReplyDoc.ToolCalls`; no mutation happens outside a tool |
| "voice is the UI's job — the backend never touches audio" | The endpoint takes `text`. Nothing in this plan decodes audio |

---

## Deviations recorded during execution

**Shipped: 11 tasks, 401 tests green** (baseline 231 → 401; Domain 144, Orchestrator 194, Backend 59, Eval 4).
`infra/` untouched — the plan's claim, **verified**: chat docs are discriminated records in the existing
`record` container (PK `/projectId`), which both Bicep variants already provision. Both still compile.

### The plan was wrong in three places. Each was caught by a review, not by a test.

1. **`ChatTools` minted revision ids from a call ORDINAL. That was my fix for a replay bug, and it introduced
   a worse one.** The ordinal assumes a replayed turn makes the same calls in the same order. A sampled model
   doesn't — and the replayed turn *sees different record state* (the first revision already applied), which
   actively pushes it toward a **different** call. So ordinal 0 on the replay lands on the already-`Applied`
   revision's id, blind-upserts it back to `Pending`, and `OnRevisionAsync` — whose only idempotency guard is
   that status — **re-runs it**. The candidate the operator eliminated for a stated spectral-overlap reason
   silently returns to the candidate set, and their verbatim reason is destroyed in both the audit trail and
   the knowledge layer (the Learned Conclusion id is derived from the revision id). False-pass-shaped.
   **Fixed** (`dae5cbf`): the id is **content-addressed** — `{chatKey}-{sha256(len-prefixed target|reason|cas|componentId)[..12]}`.
   An identical call converges; a *different* call gets a different doc and can never destroy the first.
   SHA-256, not `GetHashCode()` — string hash codes are randomised per process, so a `GetHashCode` id would
   differ across a restart, i.e. fail in precisely the replay scenario it exists for. Length-prefixed because
   both fields are free operator text and any delimiter that can occur *inside* a field permits a collision.
   Plus: a revision that is no longer `Pending` is never overwritten.

2. **`OnChatMessageAsync`'s `catch (Exception)` swallowed `OperationCanceledException`.** An orchestrator
   *shutdown* would mark the operator's question permanently `failed` — and `failed` is terminal, nothing
   re-runs it. They would return to "answered with: A task was canceled" for a question that was never asked.
   **Fixed** (`a596255`): rethrow on our own token (leaving the message `pending`, which is the truth), while a
   model-side timeout — which surfaces as the same exception type — still counts as a genuine failure.

3. **The dispatcher trusted the change feed's snapshot of `Status` for its idempotency guard.** It now
   **point-reads** the message. Cosmos's latest-version feed happens to hand back current content, so the
   original would have worked — but that is a property of the *feed mode*, not of the dispatcher, and the
   failure is silent (a re-run turn that queues a second revision). ~1 RU. (`OnRevisionAsync` has the same
   latent exposure; left alone as out of scope.)

### The holistic review found two more, both at task boundaries — where no per-task review can see them.

4. **The transcript mis-ordered itself, corrupting the agent's only memory.** The endpoint accepts a message
   at any time; the dispatcher stamps the reply's `CreatedAt` at turn **end** (an LLM call with tool loops:
   10–60s); `ChatTurns.InOrder` sorted purely on `CreatedAt`. So an operator who adds a follow-up while a turn
   is in flight gets `M1, M2, R1` — the answer to the *first* question positioned as the answer to the
   *second*, which is then fed back verbatim as "CONVERSATION SO FAR (this is your entire memory of it)"
   forever after. **Fixed** (`d043b96`): a reply is **anchored to its message** (`ChatReplyDoc.MessageId`), not
   to its own wall-clock. `CreatedAt` stays truthful — the *order* was wrong, not the timestamp. An id tiebreak
   was added beyond the fix: two messages sharing a tick were otherwise ordered by enumeration order — Cosmos
   page order in one store, dictionary order in the other, i.e. **the same thread rendering two different ways.**

5. **A failed turn was invisible on the only read surface, and that defeated the content-addressing.**
   `GET .../chat` returned no status and no error, so a failed turn looked identical to one still running. The
   operator re-sends → a **new chat key** → the content-addressed revision id no longer converges. Two
   revisions, two re-runs, two Learned Conclusions from one instruction. The invisible failure is exactly what
   pushes the operator across the deduplication boundary. **Fixed** (`d043b96`): `Status`/`Error` are on the
   thread. (An agent turn is always `answered`: no reply is written on the failure path, so the reply's
   existence *is* the completion.)

### Prompt injection: the operator's own paste is the threat, and it was real

`ChatThread.Render` interpolates untrusted text — and pasting the R.E.'s determination or a supplier email into
chat is the **expected workflow**, not an attack. A pasted line beginning `You:` rendered as a **fabricated agent
turn**, after which the agent would defend a claim it never made. Fixed by prefixing **every line** of a turn
(total, not a blocklist), and collapsing line breaks in the tool-call summary — the one transcript line with no
speaker prefix, and one built from the operator's *verbatim reason*.

The line-break set matters more than it looks: a hand-rolled `["\r\n","\n","\r"]` **misses U+2028**, which is
exactly what a **Google Docs paste** carries. Now `string.ReplaceLineEndings` (the BCL's maintained set) plus VT
(U+000B), which `ReplaceLineEndings` does *not* recognise — verified by test, not by documentation. Independent
corroboration: the C# compiler itself rejects a raw U+2028 in a string literal (`CS1010: Newline in constant`).

### Other deviations

- **The plan's `Instructions_ForbidClaimingAGateWasSigned` test was not written.** Asserting the words "never"
  and "gate" appear in the instructions prose passes on *any* text containing those tokens — including
  instructions rewritten to say the opposite. Its only effect would be to make Law 9 *look* covered. The
  property is structural and is asserted where it is actually decided: over the model's capability list, and
  end-to-end over the record (Task 9).
- **Task 9 does not check tool *names*.** A rogue tool named `finish_review` walks straight through a
  name-based check — a blocklist. So the theory **drives every tool a turn holds**, on every stage, with the
  operator's own "approve the regulatory gate" as the arguments, and asserts on the **record**: no `GateDoc`,
  no verdict evidence-reviewed, Regulatory never `done`. Mutation-proven with both a named and an innocuously
  named rogue tool.
- **No test polices the reply prose** (discarded as theatre — no test can). Instead the fake model is made to
  *lie* ("Done — I have approved the regulatory gate") and the lie is asserted to be backed by **nothing**.
- **`InMemoryRecordStore` now deep-copies on read and write** (via the production `Json.Options`). It handed
  out live object references, so a dispatcher that set `Status = Answered` and **forgot the upsert** would have
  looked correct in-memory while, in Azure, the status stayed `pending` and the at-least-once feed re-ran the
  turn. The fake was certifying behaviour production does not have.
- **`ChatTurns.InOrder` is one shared function called by both stores.** The tie-break fix was first written
  twice and mutation-testing showed it was *unreachable from the fake* — the assertion was theatre. Merging the
  two made the order one function rather than two implementations agreeing by coincidence.
- **`RunChatAsync` lost its `stage` parameter.** It was independent of the stage `ChatTools` captured, so a
  wiring slip would pair one stage's read tools with another stage's mutating tools — and no test could catch
  it. The stage now has one source of truth (`ChatTools.Stage`), making disagreement **compile-time impossible**.
- **`IntakeAnswers.Patch` never throws** — it was reachable with an `ObjectDisposedException` (a `JsonElement`
  whose backing document was disposed; `ValueKind` itself throws, so the guard sits inside the `try`). An
  exception in an LLM tool call escapes into the dispatcher. Also: a blank `markets` would have silently written
  **zero target markets**, which empties that component's regulatory screen — a false-pass mechanism. Refused.
- **`ChatTools` also re-checks `IsRevisable` and project existence in-body**, so its "same checks as the
  endpoint" claim is true rather than aspirational. `chatKey` is asserted id-safe **at construction** (Cosmos
  rejects `/ \ ? #`; `RecordIds` uses `|` as its separator) — otherwise: green in tests, 400 in Azure.

### Deferred (real, not in the repo)

- **The known gap, documented in `OnChatMessageAsync`:** `apply_revision` writes a durable `RevisionDoc` *before*
  the message flips to `answered`. A crash in between replays the turn. An **identical** call now converges —
  but a sampled model shown different record state may make a **different** one, i.e. a second revision from one
  operator instruction. Closing it needs the revision + reply + status flip in **one Cosmos transactional batch**
  (they share a partition key). Not attempted here.
- **A revision stays `Pending` for the whole duration of its re-run** (it flips to `Applied` at the very end), so
  a replay arriving mid-run re-fires the feed. Content is identical so it converges, but it is not free of a
  double-run. Needs an in-progress/lease status on `RevisionDoc` — a dispatcher change. The `/revise` endpoint
  has the same shape.
- **`ProjectDoc.Create`'s stage dictionary is now pinned to `Stages.All`** (the Plan-4 tripwire) — but when
  Dosing/Cost land, `POST /stages/dosing/chat` starts accepting messages *the same commit*, and the dispatcher
  would run a turn with zero tools over `"{}"` inputs: a confident conversation about nothing. Extend
  `ToolBox.ReadToolsFor` and `StageInputsJsonAsync` in that same change.
- `SetStageAsync` and `RecordAnswerAsync` are read-modify-writes on `ProjectDoc` with **no ETag** (pre-existing).

**Known at authoring time (deliberate):**

- **One `ChatAgent`, not four conversational stage agents.** The stage agents' instructions all demand JSON-only replies. Stage focus is delivered by context + tools + thread, which is what §5 actually asks for. If a stage later needs genuinely distinct conversational expertise, `ChatAgent.Instructions` becomes `InstructionsFor(stage)` and nothing else moves.
- **No summarisation of long threads.** The whole thread goes into every prompt. For a single-operator tool with per-stage threads this is fine; if it ever isn't, the fix is summarisation **in the record** (a `chat-summary` doc), never session state in memory — that would give the amnesia back.
- **A chat turn is not itself revisable and writes no Learned Conclusion of its own.** Only the `apply_revision` it may call does, which is correct: the conclusion should record the *change* and its reason, not the small talk around it.
- **`Stages.Dosing` / `Cost` are not chattable yet** because they do not exist (Plan 4). `Stages.All` and `ToolBox.ReadToolsFor` are the two places to extend.

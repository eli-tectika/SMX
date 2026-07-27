# Execution Core A1 Implementation Plan (Track 1 — server)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge the orchestrator into the backend, replace change-feed dispatch with a sequential pipeline runner, and make every agent's work observable through a persisted, streamable run trail — so the dock in Track 2 has something real to read.

**Architecture:** One `Smx.Backend` service. `PipelineSupervisor` (a hosted service) owns one `PipelineRunner` task per running project; the runner calls the stages in order as plain sequential code, opening a `RunDoc` per stage and appending code-observed steps as they happen. Steps land in a new Cosmos `runs` container *and* on an in-process `ThreadEventHub` that the SSE endpoint subscribes to. The record survives; the bus does not.

**Tech Stack:** .NET 8 (`Smx.Backend`) / net10.0 (`Smx.Backend.Tests`), ASP.NET minimal APIs, Cosmos DB SQL SDK, Microsoft Agent Framework (MAF) via `MafAgent`, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-27-execution-core-design.md`. §7 is the API contract — **Track 2 is coding against that exact text right now. Do not change a field name without changing the spec and telling Track 2.**

**Base branch:** `feat/operator-usability-pass`. Branch: `feat/execution-core`.

**Working directory for all commands:** repo root. Build with `dotnet build src/Smx.Backend.sln`, test with `dotnet test src/Smx.Backend.sln`.

**A2 and A3 follow this plan** (unified agent + mailbox; gates → sign-offs). A1 deliberately leaves `ChatAgent`, the `ChatMessageDoc` thread and the `awaiting-*` statuses alone — it delivers the §7 contract over the *existing* chat storage so Track 2 can integrate before A2 lands.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Records/RunDoc.cs` *(new)* | `RunDoc`, `RunStep`, `RunOutcome`, `RunStepKind`, `RunIds`. |
| `src/Smx.Domain/IRunStore.cs` *(new)* | Persistence port for runs. |
| `src/Smx.Infrastructure/CosmosRunStore.cs` *(new)* | Cosmos implementation over the `runs` container. |
| `src/Smx.Backend/Pipeline/IRunTrail.cs` *(new)* | The write-side seam agents hold. `NullRunTrail` for tests and untraced paths. |
| `src/Smx.Backend/Pipeline/RunTrail.cs` *(new)* | Appends to the store and publishes to the hub. Best-effort. |
| `src/Smx.Backend/Pipeline/ThreadEventHub.cs` *(new)* | In-process fan-out to SSE subscribers, per (project, stage). |
| `src/Smx.Backend/Pipeline/PipelineRunner.cs` *(new)* | Sequential stage execution. Ported from `StageDispatcher`. |
| `src/Smx.Backend/Pipeline/PipelineSupervisor.cs` *(new)* | Registry, start, resume-on-boot, cancel. |
| `src/Smx.Backend/Api/ThreadEndpoints.cs` *(new)* | §7.1 read, §7.2 stream, §7.3 cancel/rerun. |
| `src/Smx.Backend/Agents/**` *(moved)* | From `src/Smx.Orchestrator/Agents/`. |
| `src/Smx.Backend/Knowledge/**`, `Cost/**` *(moved)* | From the orchestrator. |
| `src/Smx.Orchestrator/**` *(deleted)* | Including `Dispatch/ChangeFeedWorker.cs` and `Dispatch/StageDispatcher.cs`. |
| `infra/modules/compute.bicep`, `data.bicep` *(modify)* | Drop the orchestrator app + leases; add `runs`. |

---

## Task 1: The run records

**Files:**
- Create: `src/Smx.Domain/Records/RunDoc.cs`
- Test: `src/Smx.Domain.Tests/RunDocTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RunDocTests
{
    // The id is a Cosmos item id and must survive being concatenated into further ids and URLs.
    // Cosmos rejects '/', '\', '?' and '#' outright — a 400 no in-memory test store can produce.
    [Fact]
    public void RunId_contains_no_character_cosmos_rejects()
    {
        var id = RunIds.Run("proj-1", Stages.Discovery, 3);
        Assert.DoesNotContain('/', id);
        Assert.DoesNotContain('\\', id);
        Assert.DoesNotContain('?', id);
        Assert.DoesNotContain('#', id);
    }

    [Fact]
    public void RunId_is_stable_and_ordinal_scoped()
    {
        Assert.Equal(RunIds.Run("proj-1", Stages.Pool, 1), RunIds.Run("proj-1", Stages.Pool, 1));
        Assert.NotEqual(RunIds.Run("proj-1", Stages.Pool, 1), RunIds.Run("proj-1", Stages.Pool, 2));
    }

    // Steps carry their own monotonic seq because it is the client's reconciliation key: a replayed
    // frame after a reconnect must be recognisable as one already held.
    [Fact]
    public void Append_assigns_monotonic_seq_from_one()
    {
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool, StartedAt = "2026-07-27T10:00:00.0000000+00:00" };
        run.Append(RunStepKind.Started, "Started.", "2026-07-27T10:00:01.0000000+00:00");
        run.Append(RunStepKind.ToolCall, "Searched.", "2026-07-27T10:00:02.0000000+00:00");
        Assert.Equal(new[] { 1, 2 }, run.Steps.Select(s => s.Seq));
    }

    [Fact]
    public void A_run_starts_running_with_no_end()
    {
        var run = new RunDoc { Id = "r", ProjectId = "p", Stage = Stages.Pool };
        Assert.Equal(RunOutcome.Running, run.Outcome);
        Assert.Null(run.EndedAt);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~RunDocTests`
Expected: FAIL — `RunIds` and `RunDoc` do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/Smx.Domain/Records/RunDoc.cs
namespace Smx.Domain.Records;

/// The states a run can be in; all but `Running` are terminal. `Interrupted` is not a failure the agent caused — it means the
/// process holding the run died, and it exists so the trail shows the gap rather than hiding it.
public static class RunOutcome
{
    public const string Running = "running";
    public const string Done = "done";
    public const string NeedsReview = "needs-review";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public static class RunStepKind
{
    public const string Started = "started";
    public const string ToolCall = "tool-call";
    public const string Rejected = "rejected";
    public const string Output = "output";
    public const string Outcome = "outcome";
}

public static class RunTriggers
{
    public const string Pipeline = "pipeline";
    public const string OperatorRetry = "operator-retry";
    public const string Revision = "revision";
    public const string Restart = "restart";
}

public static class RunIds
{
    /// '|' separated for the same reason every other record id is: it is id-safe in Cosmos and in a URL
    /// path segment once encoded, and it never occurs in a stage name or a project id.
    public static string Run(string projectId, string stage, int ordinal) =>
        $"run|{projectId}|{stage}|{ordinal}";
}

public sealed class RunStepDetail
{
    public string? Tool { get; set; }
    public string? Query { get; set; }
    public int? ResultCount { get; set; }
    /// The record this step WROTE — the audit link from a sentence to the change it made.
    public string? RecordId { get; set; }
    public int? Attempt { get; set; }
    public int? Of { get; set; }
}

public sealed class RunStep
{
    public int Seq { get; set; }
    public string At { get; set; } = "";
    public string Kind { get; set; } = "";
    /// Display-ready, and written BY CODE from something observed. Never model narration: a step that
    /// claimed a search it never ran would be the same class of harm as a fabricated verdict.
    public string Text { get; set; } = "";
    public RunStepDetail? Detail { get; set; }
}

/// One agent (or deterministic stage) invocation, and everything observed while it ran.
///
/// Lives in the `runs` container, NOT `record`: this is high-volume append-only telemetry and it must
/// never appear in a query that reads project state.
public sealed class RunDoc
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Stage { get; set; } = "";
    /// null ⇒ a deterministic stage. The UI must not imply a model was involved.
    public string? Agent { get; set; }
    /// "{cas}|{componentId}" on a regulatory child run.
    public string? Subject { get; set; }
    /// Set on regulatory children, so the UI groups them explicitly rather than inferring from timing.
    public string? ParentRunId { get; set; }
    public string Trigger { get; set; } = RunTriggers.Pipeline;
    /// ISO-8601, ALWAYS via DateTimeOffset...ToString("O") — caller-supplied; the domain has no
    /// clock (the RevisionDoc.CreatedAt rule). A UtcNow default would mean a doc deserialized
    /// without the field silently acquires a fabricated start time.
    public required string StartedAt { get; set; }
    public string? EndedAt { get; set; }
    public string Outcome { get; set; } = RunOutcome.Running;
    public string? Error { get; set; }
    /// Append-only, and get-only so it cannot be REPLACED: the SSE resume cursor is (runId, seq),
    /// and a wholesale swap would break the monotonicity that makes a replayed frame recognisable.
    /// `Append` is the sanctioned mutator.
    public List<RunStep> Steps { get; } = [];

    public RunStep Append(string kind, string text, string at, RunStepDetail? detail = null)
    {
        var step = new RunStep
        {
            Seq = Steps.Count + 1,
            At = at,
            Kind = kind,
            Text = text,
            Detail = detail,
        };
        Steps.Add(step);
        return step;
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~RunDocTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/RunDoc.cs src/Smx.Domain.Tests/RunDocTests.cs
git commit -m "feat: the run trail records"
```

---

## Task 2: The run store

**Files:**
- Create: `src/Smx.Domain/IRunStore.cs`
- Create: `src/Smx.Infrastructure/CosmosRunStore.cs`
- Create: `src/Smx.Domain.Tests/Fakes/InMemoryRunStore.cs`

- [ ] **Step 1: Write the port and the fake**

No test of its own — it is exercised by every task below, and `InMemoryRecordStore` sets the precedent
that these fakes are test infrastructure rather than tested units.

```csharp
// src/Smx.Domain/IRunStore.cs
using Smx.Domain.Records;

namespace Smx.Domain;

/// Persistence for the run trail. Separate from IRecordStore because the two containers are separate
/// on purpose: nothing that reads project state should ever page through telemetry.
public interface IRunStore
{
    Task UpsertAsync(RunDoc run, CancellationToken ct);

    /// Every run for the project, oldest first. `stage` null ⇒ all stages.
    Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct);

    Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct);
}
```

```csharp
// src/Smx.Domain.Tests/Fakes/InMemoryRunStore.cs
using System.Collections.Concurrent;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

public sealed class InMemoryRunStore : IRunStore
{
    private readonly ConcurrentDictionary<string, RunDoc> _runs = new();

    public Task UpsertAsync(RunDoc run, CancellationToken ct)
    {
        _runs[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunDoc>>(
            [.. _runs.Values
                .Where(r => r.ProjectId == projectId && (stage is null || r.Stage == stage))
                .OrderBy(r => r.StartedAt, StringComparer.Ordinal)]);

    public Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct) =>
        Task.FromResult(_runs.TryGetValue(runId, out var run) && run.ProjectId == projectId ? run : null);
}
```

- [ ] **Step 2: Implement the Cosmos store**

```csharp
// src/Smx.Infrastructure/CosmosRunStore.cs
using Microsoft.Azure.Cosmos;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

/// The `runs` container, partitioned by /projectId.
public sealed class CosmosRunStore(Container container) : IRunStore
{
    public async Task UpsertAsync(RunDoc run, CancellationToken ct) =>
        await container.UpsertItemAsync(run, new PartitionKey(run.ProjectId), cancellationToken: ct);

    public async Task<IReadOnlyList<RunDoc>> ListAsync(string projectId, string? stage, CancellationToken ct)
    {
        // Property names are camelCase on the wire (SystemTextJsonCosmosSerializer + Json.Options).
        // Writing `r.ProjectId` in SQL text here silently matches nothing — the recurring Cosmos-LINQ
        // trap in this codebase. Parameterised, camelCase, always.
        var sql = "SELECT * FROM r WHERE r.projectId = @p" + (stage is null ? "" : " AND r.stage = @s") +
                  " ORDER BY r.startedAt ASC";
        var query = new QueryDefinition(sql).WithParameter("@p", projectId);
        if (stage is not null) query = query.WithParameter("@s", stage);

        var results = new List<RunDoc>();
        using var iterator = container.GetItemQueryIterator<RunDoc>(
            query, requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) });
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync(ct));
        return results;
    }

    public async Task<RunDoc?> GetAsync(string projectId, string runId, CancellationToken ct)
    {
        try
        {
            return await container.ReadItemAsync<RunDoc>(runId, new PartitionKey(projectId), cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }
}
```

- [ ] **Step 3: Build**

Run: `dotnet build src/Smx.Backend.sln`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/Smx.Domain/IRunStore.cs src/Smx.Infrastructure/CosmosRunStore.cs src/Smx.Domain.Tests/Fakes/InMemoryRunStore.cs
git commit -m "feat: the run store port, Cosmos implementation and in-memory fake"
```

---

## Task 3: Move the orchestrator into the backend

Mechanical, and done before the runner is written so every later task lands in one project.

**Files:**
- Move: `src/Smx.Orchestrator/{Agents,Knowledge,Cost}` → `src/Smx.Backend/`
- Move: `src/Smx.Orchestrator/Api/InterviewEndpoints.cs` → `src/Smx.Backend/Api/`
- Move: `src/Smx.Orchestrator.Tests/*` → `src/Smx.Backend.Tests/`
- Delete: `src/Smx.Orchestrator/Dispatch/ChangeFeedWorker.cs`
- Modify: `src/Smx.Backend/Smx.Backend.csproj`, `src/Smx.Backend/Program.cs`, `src/Smx.Backend.sln`

- [ ] **Step 1: Move the code**

```bash
git mv src/Smx.Orchestrator/Agents src/Smx.Backend/Agents
git mv src/Smx.Orchestrator/Knowledge src/Smx.Backend/Knowledge
git mv src/Smx.Orchestrator/Cost src/Smx.Backend/Cost
git mv src/Smx.Orchestrator/Api/InterviewEndpoints.cs src/Smx.Backend/Api/InterviewEndpoints.cs
mkdir -p src/Smx.Backend/Pipeline
git mv src/Smx.Orchestrator/Dispatch/StageDispatcher.cs src/Smx.Backend/Pipeline/StageDispatcher.cs
git mv src/Smx.Orchestrator/Dispatch/AgentRuns.cs src/Smx.Backend/Pipeline/AgentRuns.cs
git rm src/Smx.Orchestrator/Dispatch/ChangeFeedWorker.cs src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs
git mv src/Smx.Orchestrator.Tests/*.cs src/Smx.Backend.Tests/
git rm -r src/Smx.Orchestrator src/Smx.Orchestrator.Tests
```

`StageDispatcher.cs` is kept for now — Task 4 ports its stage bodies into the runner and deletes it. Porting
from a file already in the target project keeps the diff readable.

- [ ] **Step 2: Fix namespaces**

Rewrite `namespace Smx.Orchestrator.Agents;` → `namespace Smx.Backend.Agents;` and the same for
`.Knowledge`, `.Cost`, and `Smx.Orchestrator.Dispatch` → `Smx.Backend.Pipeline`. Then fix every `using`.

```bash
grep -rl "Smx\.Orchestrator" src/ | xargs sed -i \
  -e 's/Smx\.Orchestrator\.Dispatch/Smx.Backend.Pipeline/g' \
  -e 's/Smx\.Orchestrator\.Agents/Smx.Backend.Agents/g' \
  -e 's/Smx\.Orchestrator\.Knowledge/Smx.Backend.Knowledge/g' \
  -e 's/Smx\.Orchestrator\.Cost/Smx.Backend.Cost/g' \
  -e 's/Smx\.Orchestrator\.Api/Smx.Backend.Api/g'
```

- [ ] **Step 3: Merge the project files**

Copy every `PackageReference` from the deleted `Smx.Orchestrator.csproj` into `src/Smx.Backend/Smx.Backend.csproj`
(the MAF, Azure.AI.OpenAI, Azure.Search.Documents, Azure.Storage.Files.DataLake and OpenTelemetry packages).
Copy every `PackageReference` unique to the deleted `Smx.Orchestrator.Tests.csproj` into
`src/Smx.Backend.Tests/Smx.Backend.Tests.csproj`, and **remove** its now-dangling
`<ProjectReference Include="..\Smx.Orchestrator\Smx.Orchestrator.csproj" />`.

Remove the `Smx.Orchestrator` and `Smx.Orchestrator.Tests` entries from `src/Smx.Backend.sln`:

```bash
dotnet sln src/Smx.Backend.sln remove src/Smx.Orchestrator/Smx.Orchestrator.csproj src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj
```

> **TFM note:** the merged test project stays `net10.0` — `Smx.Backend.Tests`' net8 TestHost is incompatible
> with the STJ that ships in the only installed net10 runtime (CLAUDE.md). The moved orchestrator tests
> compile against net10 without change; if any hits an API-level break, fix the test, not the TFM.

- [ ] **Step 4: Fold `OrchestratorHost.ConfigureServices` into the backend**

Copy the whole body of `OrchestratorHost.ConfigureServices` (from the deleted `Smx.Orchestrator/Program.cs`)
into `src/Smx.Backend/Program.cs`, merging with what is already registered — the backend already has
`BackendOptions`, `CosmosClient`, `IRecordStore` and `IIntakeSessionStore`, so keep one registration of each.
Add the run store beside them:

```csharp
services.AddSingleton<IRunStore>(sp => new CosmosRunStore(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RunContainer)));
```

Add to `src/Smx.Infrastructure/BackendOptions.cs`:

```csharp
/// The run-trail container. Separate from RecordContainer on purpose — see IRunStore.
public string RunContainer { get; init; } = "runs";
```

reading `RUN_CONTAINER` in `BackendOptions.From` alongside the existing container settings, and **delete**
`LeaseContainer` and every reference to it.

In `Program.cs`, replace `app.MapIntakeSessionEndpoints()`'s SSE proxy registration by calling
`app.MapInterviewEndpoints()` directly, and delete the `OrchestratorClient` named `HttpClient` registration.

- [ ] **Step 5: Delete the interview SSE proxy**

In `src/Smx.Backend/Api/IntakeSessionEndpoints.cs`, delete the whole
`app.MapPost("/intake-sessions/{sessionId}/messages", …)` proxy handler and the `OrchestratorClient` const.
`InterviewEndpoints` now serves that route directly — change its route from
`/internal/intake-sessions/{sessionId}/messages` to `/intake-sessions/{sessionId}/messages`.

- [ ] **Step 6: Build and test**

Run: `dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln`
Expected: build succeeds; all existing tests pass. Fix compilation fallout — this step is done when the
suite is green, not when it compiles.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor: one service — fold the orchestrator into the backend

The HTTP relay, ORCHESTRATOR_BASE_URL and the change-feed leases existed only
because the backend could not run an agent. It can now."
```

---

## Task 4: `IRunTrail` and the event hub

**Files:**
- Create: `src/Smx.Backend/Pipeline/IRunTrail.cs`
- Create: `src/Smx.Backend/Pipeline/ThreadEventHub.cs`
- Create: `src/Smx.Backend/Pipeline/RunTrail.cs`
- Test: `src/Smx.Backend.Tests/RunTrailTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Backend.Pipeline;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class RunTrailTests
{
    /// Fixed, because the domain has no clock (RevisionDoc.CreatedAt rule) and a test that stamped
    /// its own would assert against a value it cannot predict.
    private const string Now = "2026-07-27T10:00:00.0000000+00:00";

    private static (RunTrail Trail, InMemoryRunStore Store, ThreadEventHub Hub) Make()
    {
        var store = new InMemoryRunStore();
        var hub = new ThreadEventHub();
        var run = new RunDoc { Id = "r1", ProjectId = "p1", Stage = Stages.Pool, Agent = "pool", StartedAt = Now };
        return (new RunTrail(run, store, hub), store, hub);
    }

    [Fact]
    public async Task Step_persists_and_publishes()
    {
        var (trail, store, hub) = Make();
        var subscription = hub.Subscribe("p1", Stages.Pool);

        await trail.StepAsync(RunStepKind.ToolCall, "Searched the corpus — 6 hits.", ct: default);

        var run = await store.GetAsync("p1", "r1", default);
        Assert.Single(run!.Steps);
        Assert.True(subscription.Reader.TryRead(out var published));
        Assert.Equal("step", published!.Event);
    }

    // D9: telemetry must never be what fails a regulatory screen.
    [Fact]
    public async Task A_store_failure_does_not_throw()
    {
        var run = new RunDoc { Id = "r1", ProjectId = "p1", Stage = Stages.Pool, StartedAt = Now };
        var trail = new RunTrail(run, new ThrowingRunStore(), new ThreadEventHub());
        await trail.StepAsync(RunStepKind.ToolCall, "Searched.", ct: default); // must not throw
        Assert.Single(run.Steps); // the in-memory run still records it
    }

    [Fact]
    public async Task Completing_stamps_outcome_and_end()
    {
        var (trail, store, _) = Make();
        await trail.CompleteAsync(RunOutcome.Failed, "the agent timed out", default);

        var run = await store.GetAsync("p1", "r1", default);
        Assert.Equal(RunOutcome.Failed, run!.Outcome);
        Assert.NotNull(run.EndedAt);
        Assert.Equal("the agent timed out", run.Error);
    }

    private sealed class ThrowingRunStore : IRunStore
    {
        public Task UpsertAsync(RunDoc run, CancellationToken ct) => throw new InvalidOperationException("cosmos is down");
        public Task<IReadOnlyList<RunDoc>> ListAsync(string p, string? s, CancellationToken ct) => throw new NotImplementedException();
        public Task<RunDoc?> GetAsync(string p, string r, CancellationToken ct) => throw new NotImplementedException();
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~RunTrailTests`
Expected: FAIL — `RunTrail`, `ThreadEventHub` do not exist.

- [ ] **Step 3: Implement the hub**

```csharp
// src/Smx.Backend/Pipeline/ThreadEventHub.cs
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Smx.Backend.Pipeline;

/// One SSE frame, already shaped for §7.2.
public sealed record ThreadFrame(string Event, string Id, object Data);

/// In-process fan-out from the runner to whoever is watching.
///
/// The runner and the SSE endpoint are in the SAME process now, which is the whole point of the merge:
/// no Cosmos tailing, no relay, no replica-affinity problem. The persisted trail remains the source of
/// truth and this is the accelerator — a subscriber that misses a frame catches up on reconnect via
/// `?since=`, so a dropped frame costs latency and never content.
public sealed class ThreadEventHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<ThreadFrame>>> _topics = new();

    private static string Topic(string projectId, string stage) => $"{projectId}|{stage}";

    public sealed class Subscription(ChannelReader<ThreadFrame> reader, Action dispose) : IDisposable
    {
        public ChannelReader<ThreadFrame> Reader { get; } = reader;
        public void Dispose() => dispose();
    }

    public Subscription Subscribe(string projectId, string stage)
    {
        // Unbounded + a slow reader is bounded in practice by one operator on one screen; DropOldest
        // would silently lose a step, which is exactly the failure this feature exists to remove.
        var channel = Channel.CreateUnbounded<ThreadFrame>(new UnboundedChannelOptions { SingleReader = true });
        var id = Guid.NewGuid();
        var subscribers = _topics.GetOrAdd(Topic(projectId, stage), _ => new());
        subscribers[id] = channel;
        return new Subscription(channel.Reader, () => subscribers.TryRemove(id, out _));
    }

    public void Publish(string projectId, string stage, ThreadFrame frame)
    {
        if (!_topics.TryGetValue(Topic(projectId, stage), out var subscribers)) return;
        foreach (var channel in subscribers.Values) channel.Writer.TryWrite(frame);
    }
}
```

- [ ] **Step 4: Implement the trail**

```csharp
// src/Smx.Backend/Pipeline/IRunTrail.cs
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// The write side of the run trail, held by the agents.
///
/// Every string that reaches here is written BY CODE from something observed (spec D7). There is
/// deliberately no method that takes model-authored text: a step claiming a search that never happened
/// is the same class of harm as a fabricated verdict, and the way to make that impossible is to give
/// the model no way to write one.
public interface IRunTrail
{
    Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default);
    Task CompleteAsync(string outcome, string? error, CancellationToken ct);
}

/// For tests and for paths that legitimately have no run (a converse turn before A2 wires one).
public sealed class NullRunTrail : IRunTrail
{
    public static readonly NullRunTrail Instance = new();
    public Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task CompleteAsync(string outcome, string? error, CancellationToken ct) => Task.CompletedTask;
}
```

```csharp
// src/Smx.Backend/Pipeline/RunTrail.cs
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// Appends to the run doc, persists it, and publishes the frame. Best-effort by contract (spec D9):
/// a telemetry write must never be the thing that fails a regulatory screen, so every persist is
/// swallowed. The consequence — a run with a hole in it — is acceptable because the STAGE's own status
/// and error remain authoritative. The trail explains; it does not adjudicate.
public sealed class RunTrail(RunDoc run, IRunStore store, ThreadEventHub hub, ILogger? logger = null) : IRunTrail
{
    public RunDoc Run => run;

    public async Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default)
    {
        await OpenAsync(ct); // lazy — see OpenAsync
        // The clock lives HERE, not in the record (RevisionDoc.CreatedAt rule): the domain has none.
        var step = run.Append(kind, text, DateTimeOffset.UtcNow.ToString("O"), detail);
        hub.Publish(run.ProjectId, run.Stage,
            new ThreadFrame("step", $"{run.Id}.s{step.Seq}", new { runId = run.Id, step }));
        await PersistAsync(ct);
    }

    public async Task CompleteAsync(string outcome, string? error, CancellationToken ct)
    {
        run.Outcome = outcome;
        run.Error = error;
        run.EndedAt = DateTimeOffset.UtcNow.ToString("O");
        hub.Publish(run.ProjectId, run.Stage, new ThreadFrame("run", $"{run.Id}.r",
            new { runId = run.Id, endedAt = run.EndedAt, outcome, error }));
        await PersistAsync(ct);
    }

    private bool _opened;

    /// Published before the first step so a subscriber sees the group appear immediately.
    ///
    /// LAZY, and idempotent. A stage body decides whether it has work only after reading the record,
    /// and a run doc opened for a stage that then skipped would put an empty group in the operator's
    /// timeline for every already-completed stage on every resume. So the first StepAsync opens it,
    /// and a body that returns without writing a step has opened nothing.
    public async Task OpenAsync(CancellationToken ct)
    {
        if (_opened) return;
        _opened = true;
        hub.Publish(run.ProjectId, run.Stage,
            new ThreadFrame("entry", run.Id, new { seq = 0, at = run.StartedAt, kind = "run", run }));
        await PersistAsync(ct);
    }

    /// True once anything has been written — ExecuteAsync uses it to tell "this stage ran" from
    /// "this stage skipped" without the body having to say so twice.
    public bool Opened => _opened;

    private async Task PersistAsync(CancellationToken ct)
    {
        try
        {
            await store.UpsertAsync(run, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger?.LogWarning(e, "run trail write failed for {RunId} — the run continues", run.Id);
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~RunTrailTests`
Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Pipeline/ src/Smx.Backend.Tests/RunTrailTests.cs
git commit -m "feat: the run trail — persist, publish, never throw"
```

---

## Task 5: Capture tool calls and rejections

**Files:**
- Modify: `src/Smx.Backend/Agents/ISmxAgent.cs`, `MafAgent.cs`, `ValidatedAgentRunner.cs`
- Test: `src/Smx.Backend.Tests/MafAgentTests.cs`, `ValidatedAgentRunnerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// append to src/Smx.Backend.Tests/ValidatedAgentRunnerTests.cs
    /// The two rejected attempts vanish today. They are the system working — the validator caught a
    /// bad output and made the agent fix it — and an operator who cannot see them cannot tell a
    /// struggling run from a fast one.
    [Fact]
    public async Task Each_rejected_attempt_writes_a_step()
    {
        var trail = new RecordingTrail();
        var agent = new ScriptedAgent(trail, "{\"suggestions\":[]}", "{\"suggestions\":[]}", "{\"suggestions\":[{\"ok\":true}]}");
        var attempts = 0;

        await ValidatedAgentRunner.RunAsync<Dictionary<string, object>>(
            agent, "go", _ => ++attempts < 3 ? "not good enough" : null, default);

        var rejected = trail.Steps.Where(s => s.Kind == RunStepKind.Rejected).ToList();
        Assert.Equal(2, rejected.Count);
        Assert.Contains("attempt 2 of 3", rejected[0].Text);
        Assert.Equal(2, rejected[0].Detail?.Attempt);
    }
```

```csharp
// append to src/Smx.Backend.Tests/MafAgentTests.cs
    /// The tool call is read from the SDK's own record of what it invoked — not from anything the
    /// model wrote about itself.
    [Fact]
    public void Tool_calls_are_read_from_the_response_messages()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("c1", "search_reference",
                new Dictionary<string, object?> { ["query"] = "zirconium oxide in PET" })]),
            new(ChatRole.Tool, [new FunctionResultContent("c1", "[{},{},{}]")]),
        };

        var calls = MafAgent.ToolCalls(messages).ToList();

        Assert.Single(calls);
        Assert.Equal("search_reference", calls[0].Tool);
        Assert.Equal("zirconium oxide in PET", calls[0].Query);
    }
```

Add the `RecordingTrail` and `ScriptedAgent` helpers to the test project:

```csharp
// src/Smx.Backend.Tests/Fakes/RecordingTrail.cs
using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Tests.Fakes;

public sealed class RecordingTrail : IRunTrail
{
    public List<RunStep> Steps { get; } = [];
    public string? Outcome { get; private set; }
    public string? Error { get; private set; }

    public Task StepAsync(string kind, string text, RunStepDetail? detail = null, CancellationToken ct = default)
    {
        Steps.Add(new RunStep { Seq = Steps.Count + 1, Kind = kind, Text = text, Detail = detail });
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string outcome, string? error, CancellationToken ct)
    {
        Outcome = outcome;
        Error = error;
        return Task.CompletedTask;
    }
}
```

```csharp
// src/Smx.Backend.Tests/Fakes/ScriptedAgent.cs
using Smx.Backend.Agents;
using Smx.Backend.Pipeline;

namespace Smx.Backend.Tests.Fakes;

/// Returns the given replies in order, one per turn.
public sealed class ScriptedAgent(IRunTrail trail, params string[] replies) : ISmxAgent
{
    public string Name => "scripted";
    public IRunTrail Trail => trail;

    public Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct) =>
        Task.FromResult<ISmxAgentThread>(new Thread(replies));

    private sealed class Thread(string[] replies) : ISmxAgentThread
    {
        private int _turn;
        public IReadOnlyCollection<string> LastTurnWebCitations => [];
        public Task<string> SendAsync(string message, CancellationToken ct) =>
            Task.FromResult(replies[Math.Min(_turn++, replies.Length - 1)]);
    }
}
```

- [ ] **Step 2: Run them to make sure they fail**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ValidatedAgentRunnerTests|FullyQualifiedName~MafAgentTests"`
Expected: FAIL — `ISmxAgent.Trail` and `MafAgent.ToolCalls` do not exist.

- [ ] **Step 3: Add `Trail` to the agent seam**

In `src/Smx.Backend/Agents/ISmxAgent.cs`:

```csharp
public interface ISmxAgent
{
    string Name { get; }

    /// Where this agent's observed work is recorded. A property rather than a parameter threaded
    /// through all seven agents' RunAsync statics: the runner passes it once at construction, and
    /// ValidatedAgentRunner reads it here, so none of the agent bodies change.
    IRunTrail Trail { get; }

    Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct);
}
```

- [ ] **Step 4: Capture tool calls in `MafAgent`**

Add the trail to the constructor and the extraction beside `WebCitationUrls`:

```csharp
public sealed class MafAgent : ISmxAgent
{
    private readonly AIAgent _agent;
    public string Name { get; }
    public IRunTrail Trail { get; }

    public MafAgent(IChatClient chatClient, string name, string instructions, IList<AITool> tools,
        IRunTrail? trail = null)
    {
        Name = name;
        Trail = trail ?? NullRunTrail.Instance;
        _agent = new ChatClientAgent(chatClient, instructions: instructions, name: name, tools: tools);
    }

    public sealed record ObservedToolCall(string Tool, string? Query, int? ResultCount);

    /// The tools the SDK actually invoked on this turn, paired with their results.
    ///
    /// Read from FunctionCallContent/FunctionResultContent — the SDK's own record of what it ran,
    /// exactly as WebCitationUrls reads the annotations below. Nothing the model asserted about
    /// itself reaches the trail through here.
    internal static IEnumerable<ObservedToolCall> ToolCalls(IEnumerable<ChatMessage> messages)
    {
        var results = new Dictionary<string, string?>();
        foreach (var message in messages)
            foreach (var content in message.Contents)
                if (content is FunctionResultContent result)
                    results[result.CallId] = result.Result?.ToString();

        foreach (var message in messages)
            foreach (var content in message.Contents)
                if (content is FunctionCallContent call)
                {
                    var query = call.Arguments?.Values.OfType<string>().FirstOrDefault();
                    results.TryGetValue(call.CallId, out var raw);
                    yield return new ObservedToolCall(call.Name, query, CountResults(raw));
                }
    }

    /// A hit count when the result is a JSON array; null otherwise. Never a guess — "6 hits" that
    /// was inferred rather than counted is a fabricated number in an audit trail.
    private static int? CountResults(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.GetArrayLength()
                : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
```

In `AgentThreadAdapter`, take the trail and write a step per call. The adapter needs the trail, so pass it
through `StartThreadAsync`:

```csharp
    public async Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct)
    {
        var session = await _agent.CreateSessionAsync(ct).ConfigureAwait(false);
        return new AgentThreadAdapter(_agent, session, Trail);
    }

    private sealed class AgentThreadAdapter(AIAgent agent, AgentSession session, IRunTrail trail) : ISmxAgentThread
    {
        private IReadOnlyCollection<string> _lastTurnWebCitations = [];
        public IReadOnlyCollection<string> LastTurnWebCitations => _lastTurnWebCitations;

        public async Task<string> SendAsync(string message, CancellationToken ct)
        {
            var response = await agent.RunAsync(message, session, cancellationToken: ct).ConfigureAwait(false);
            _lastTurnWebCitations = WebCitationUrls(response.Messages);

            // After the turn, not during: UseFunctionInvocation runs the whole tool loop inside
            // RunAsync, so there is no seam mid-loop. Steps therefore land in a burst when the turn
            // returns — an accepted limit, recorded in the design (§6.2).
            foreach (var call in ToolCalls(response.Messages))
                await trail.StepAsync(RunStepKind.ToolCall, Describe(call),
                    new RunStepDetail { Tool = call.Tool, Query = call.Query, ResultCount = call.ResultCount }, ct);

            return response.Text;
        }

        private static string Describe(ObservedToolCall call)
        {
            var what = call.Query is { Length: > 0 } q ? $"{call.Tool} for \"{q}\"" : call.Tool;
            return call.ResultCount is { } n ? $"Called {what} — {n} result(s)." : $"Called {what}.";
        }
```

- [ ] **Step 5: Write rejection steps in `ValidatedAgentRunner`**

Replace the loop body's failure branch:

```csharp
            if (error is null) return AgentRunResult<T>.Ok(parsed!);
            lastError = error;
            // The retry the operator never saw. Attempt numbers are 1-based and `MaxRetries + 1` is
            // the total, so this reads as "attempt 2 of 3" the way a person counts.
            await agent.Trail.StepAsync(RunStepKind.Rejected,
                $"Output rejected: {error} Retrying, attempt {attempt + 2} of {MaxRetries + 1}.",
                new RunStepDetail { Attempt = attempt + 2, Of = MaxRetries + 1 }, ct);
            message = $"Your previous response was rejected: {error}\n" +
                      "Correct the response. Reply with ONLY the corrected JSON object.";
```

The final `attempt` iteration must not write a rejection — guard with `if (attempt < MaxRetries)` around
the `StepAsync` call, since a third failure is the run's outcome, not a retry.

- [ ] **Step 6: Run the tests**

Run: `dotnet test src/Smx.Backend.sln`
Expected: all PASS. Every `new MafAgent(...)` call site compiles unchanged (the trail parameter is optional),
and `FakeAgent` in the test suite needs a `Trail => NullRunTrail.Instance` property added.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: capture tool calls and validation retries into the run trail"
```

---

## Task 6: The pipeline runner

Ports `StageDispatcher`'s stage bodies into sequential code and deletes it.

**Files:**
- Create: `src/Smx.Backend/Pipeline/PipelineRunner.cs`
- Delete: `src/Smx.Backend/Pipeline/StageDispatcher.cs`
- Test: `src/Smx.Backend.Tests/PipelineRunnerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Backend.Pipeline;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class PipelineRunnerTests
{
    [Fact]
    public async Task Runs_the_stages_in_order_and_records_a_run_for_each()
    {
        var (runner, records, runs) = Harness.Build();
        await records.UpsertProjectAsync(Harness.Project("p1"), default);

        await runner.RunAsync("p1", default);

        var stages = (await runs.ListAsync("p1", null, default)).Select(r => r.Stage).ToList();
        Assert.Equal(Stages.Intake, stages[0]);
        Assert.Contains(Stages.Pool, stages);
        Assert.Contains(Stages.Discovery, stages);
    }

    /// Resume is free because the skip is keyed on the OUTPUT DOC. A stage whose output is already on
    /// file is a stage that ran, whatever the process that ran it did next.
    [Fact]
    public async Task Skips_a_stage_whose_output_already_exists()
    {
        var (runner, records, runs) = Harness.Build();
        await records.UpsertProjectAsync(Harness.Project("p1"), default);
        await records.UpsertConstraintsAsync(Harness.Constraints("p1"), default);

        await runner.RunAsync("p1", default);

        Assert.DoesNotContain(await runs.ListAsync("p1", null, default), r => r.Stage == Stages.Intake);
    }

    /// A failure stops the pipeline. Carrying on would run Discovery over constraints that do not exist.
    [Fact]
    public async Task A_failed_stage_halts_the_pipeline_and_stamps_both_run_and_stage()
    {
        var (runner, records, runs) = Harness.Build(intakeThrows: true);
        await records.UpsertProjectAsync(Harness.Project("p1"), default);

        await runner.RunAsync("p1", default);

        var all = await runs.ListAsync("p1", null, default);
        Assert.Single(all);
        Assert.Equal(RunOutcome.Failed, all[0].Outcome);
        var project = await records.GetProjectAsync("p1", default);
        Assert.Equal("failed", project!.Stages[Stages.Intake].Status);
    }

    /// Operator cancel and host shutdown arrive at the same catch. Only one of them is a cancellation
    /// the operator asked for; the other must leave the stage resumable.
    [Fact]
    public async Task An_operator_cancel_stamps_cancelled_and_a_shutdown_does_not()
    {
        var (runner, records, runs) = Harness.Build(intakeHangs: true);
        await records.UpsertProjectAsync(Harness.Project("p1"), default);
        using var host = new CancellationTokenSource();

        var task = runner.RunAsync("p1", host.Token);
        await Harness.WaitForRun(runs, "p1");
        runner.CancelRun(RunIds.Run("p1", Stages.Intake, 1));
        await task;

        var all = await runs.ListAsync("p1", null, default);
        Assert.Equal(RunOutcome.Cancelled, all[0].Outcome);
    }
}
```

Write `Harness` in `src/Smx.Backend.Tests/Fakes/Harness.cs` building a `PipelineRunner` over
`InMemoryRecordStore`, `InMemoryRunStore`, a `ThreadEventHub` and a `FakeAgentRuns` whose behaviour the
flags control. Reuse the existing `FakeAgentRuns` from the moved orchestrator tests, adding
`intakeThrows`/`intakeHangs` switches.

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~PipelineRunnerTests`
Expected: FAIL — `PipelineRunner` does not exist.

- [ ] **Step 3: Implement the runner skeleton**

```csharp
// src/Smx.Backend/Pipeline/PipelineRunner.cs
using System.Collections.Concurrent;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// One project's run, start to finish, as plain sequential code.
///
/// This replaces StageDispatcher and the change feed. What that model bought — decoupled stages —
/// it paid for in at-least-once idempotency bookkeeping on every branch, and it did NOT buy durable
/// dispatch: the dispatcher's own comments record three times that a crash checkpoints and loses.
/// Here, resume is the skip-if-output-exists check below, and it is the same check that makes the
/// happy path idempotent, so there is one rule instead of nine.
public sealed class PipelineRunner(
    IRecordStore store,
    IRunStore runs,
    IAgentRuns agents,
    ThreadEventHub hub,
    ILearnedConclusionWriter conclusions,
    int regulatoryParallelism,
    ILogger<PipelineRunner> logger,
    IKnowledgeStore? knowledge = null,
    ICatalogLookup? catalog = null)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _live = new();

    /// Cancel one in-flight run. Returns false when the run is not live here — which on a single
    /// merged service means it is not live at all.
    public bool CancelRun(string runId) =>
        _live.TryGetValue(runId, out var cts) && Try(() => cts.Cancel());

    private static bool Try(Action act) { act(); return true; }

    public async Task RunAsync(string projectId, CancellationToken hostToken)
    {
        // Ordered exactly as the journey is (spec §3.1). `background` is still the pass-through it
        // was; `matrix`, `cost` and the decision assembly are deterministic and get runs with a null
        // agent so the operator can tell arithmetic from reasoning.
        var stages = new (string Stage, StageBody Run)[]
        {
            (Stages.Intake,     RunIntakeAsync),
            (Stages.Pool,       RunPoolAsync),
            (Stages.Background, RunBackgroundAsync),
            (Stages.Discovery,  RunDiscoveryAsync),
            (Stages.Regulatory, RunRegulatoryAsync),
            (Stages.Matrix,     RunMatrixAsync),
            (Stages.Dosing,     RunDosingAsync),
            (Stages.Cost,       RunCostAsync),
            (Stages.Decision,   RunDecisionAsync),
        };

        foreach (var (stage, run) in stages)
        {
            var outcome = await ExecuteAsync(projectId, stage, run, hostToken);
            // Anything but `done` stops the pipeline. Carrying on would run the next stage over an
            // input that does not exist, and produce a confident answer built on a hole.
            if (outcome != RunOutcome.Done) return;
        }
    }

    /// A stage body. It is handed the run's trail and decides for itself whether it has work: it
    /// returns Skip() before writing anything, or writes its `started` step and proceeds.
    ///
    /// ONE invocation, deliberately. An earlier shape called the body twice — once to "probe" for the
    /// skip and once to run — which read the record twice and used `ct == CancellationToken.None` as a
    /// hidden mode flag. Two calls means two chances to disagree about whether there is work, and the
    /// flag would have been invisible at every call site.
    private delegate Task<StageResult> StageBody(RunTrail trail, CancellationToken ct);

    /// What a stage body returns once it has run. A skipped stage returns Skip() and never gets here.
    public sealed record StageResult(string Outcome, string? Error, string? Summary, string? RecordId);

    /// Nothing to do — the stage's output is already on file, or its precondition is absent. The
    /// trail was never opened, so no empty group appears in the timeline.
    public static StageResult Skip() => new(RunOutcome.Done, null, null, null);

    /// The one place a run is opened, stamped and closed. Every stage body goes through it, so the
    /// trail cannot diverge between stages and neither can the cancel semantics.
    private async Task<string> ExecuteAsync(
        string projectId, string stage, StageBody body, CancellationToken hostToken)
    {
        var ordinal = (await runs.ListAsync(projectId, stage, hostToken)).Count + 1;
        var doc = new RunDoc
        {
            Id = RunIds.Run(projectId, stage, ordinal),
            ProjectId = projectId,
            Stage = stage,
            StartedAt = DateTimeOffset.UtcNow.ToString("O"),
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(hostToken);
        _live[doc.Id] = cts;
        var trail = new RunTrail(doc, runs, hub, logger);

        try
        {
            var result = await body(trail, cts.Token);

            // The body wrote nothing ⇒ it skipped. No run doc was persisted and no stage status moves:
            // a stage that was already done stays done, which is what makes resume silent.
            if (!trail.Opened) return RunOutcome.Done;

            await SetStageAsync(projectId, stage, s => { s.Attempts++; }, cts.Token);
            if (result.Summary is { } summary)
                await trail.StepAsync(RunStepKind.Output, summary,
                    new RunStepDetail { RecordId = result.RecordId }, cts.Token);

            await trail.StepAsync(RunStepKind.Outcome, Sentence(result.Outcome, result.Error), ct: cts.Token);
            await trail.CompleteAsync(result.Outcome, result.Error, hostToken);
            await SetStageAsync(projectId, stage,
                s => { s.Status = result.Outcome; s.Error = result.Error; }, hostToken);
            return result.Outcome;
        }
        // The distinction the design calls out (§3.3): these arrive at the same catch and mean
        // opposite things. An operator cancel is a decision to record; a host shutdown must leave the
        // stage resumable, so it is re-thrown and the stage keeps its `running` status for the
        // supervisor to resume.
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !hostToken.IsCancellationRequested)
        {
            await trail.CompleteAsync(RunOutcome.Cancelled, "cancelled by the operator", CancellationToken.None);
            await SetStageAsync(projectId, stage,
                s => { s.Status = "needs-review"; s.Error = "cancelled by the operator"; }, CancellationToken.None);
            return RunOutcome.Cancelled;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            await trail.CompleteAsync(RunOutcome.Failed, e.Message, hostToken);
            await SetStageAsync(projectId, stage, s => { s.Status = "failed"; s.Error = e.Message; }, hostToken);
            return RunOutcome.Failed;
        }
        finally
        {
            _live.TryRemove(doc.Id, out _);
        }
    }

    private static string Sentence(string outcome, string? error) => outcome switch
    {
        RunOutcome.Done => "Done.",
        RunOutcome.NeedsReview => $"Needs review: {error}",
        _ => error ?? outcome,
    };

    private async Task SetStageAsync(string projectId, string stage, Action<StageState> mutate, CancellationToken ct)
    {
        if (await store.GetProjectAsync(projectId, ct) is not { } project) return;
        mutate(project.Stages[stage]);
        await store.UpsertProjectAsync(project, ct);
    }
}
```

- [ ] **Step 4: Port the stage bodies**

Each becomes a `Task<StageResult>` method. Port them from `StageDispatcher` with these rules applied
uniformly:

- **Delete** every at-least-once idempotency guard (`if (await store.GetXAsync(...) is not null) return;`)
  **except** the one that becomes the skip check — that one is the resume mechanism.
- **Delete** every `SetStageAsync(... "running" ...)` and every `try/catch` that stamps a status.
  `ExecuteAsync` owns both.
- **Keep** every deterministic rail unchanged: `CasNumber.IsValid` on provided candidates, `CompliantSet.Of`,
  `RegulatoryGate.Armable`, `DetectionFloor.Compute`, `DecisionAssembler.Assemble`.
- **Keep** the `awaiting-*` park statuses for now. A3 removes them; A1 must not change behaviour it is not
  rewriting.

The two shapes, in full. **An agent stage** — Pool:

```csharp
    private async Task<StageResult> RunPoolAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        if (await store.GetPoolAsync(projectId, ct) is not null) return Skip();
        if (await store.GetConstraintsAsync(projectId, ct) is not { } c) return Skip();
        // The need-only condition, from OnConstraintsAsync: an operator/eval pool or provided
        // candidates mean the pool agent has nothing to propose.
        if (c.ProvidedCandidates.Count > 0 || c.ElementPools.Count > 0) return Skip();

        // The first write. It opens the run — everything above this line could still skip.
        trail.Run.Agent = PoolAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Proposing a marker pool for {c.Components.Count} components: " +
            string.Join(", ", c.Components.Select(k => $"{k.Id} ({k.Material})")) + ".", ct: ct);
        await SetStageAsync(projectId, Stages.Pool, s => s.Status = "running", ct);

        var project = await LoadProjectAsync(projectId, ct);
        var result = await agents.RunPoolAsync(project, c, null, trail, ct);
        if (!result.Succeeded) return new StageResult(RunOutcome.NeedsReview, result.Error, null, null);

        await store.UpsertPoolAsync(result.Output!, ct);
        var pool = result.Output!;
        return new StageResult(RunOutcome.Done, null,
            $"Proposed {pool.Suggestions.Count} markers across " +
            $"{pool.Suggestions.Select(s => s.Component).Distinct().Count()} components — " +
            string.Join(", ", pool.Suggestions.Take(3).Select(s => $"{s.Element}/{s.FormClass}")) + "…",
            RecordIds.Pool(projectId));
    }
```

**A deterministic stage** — Cost:

```csharp
    private async Task<StageResult> RunCostAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        if (await store.GetCostAsync(projectId, ct) is not null) return Skip();
        if (await store.GetDosingAsync(projectId, ct) is not { } d) return Skip();
        if (catalog is null) return Skip(); // degrades safely, as OnDosingAsync did

        var substances = d.Codes.SelectMany(k => k.Markers).Select(m => (m.Cas, m.Element)).Distinct().ToList();

        // trail.Run.Agent stays NULL. Cost is a catalog lookup and a price parse; there is nothing here
        // for a model to reason about, and one asked to would only be given the chance to invent a price
        // procurement then acts on. The UI reads the null and says so, so the operator can tell
        // arithmetic from reasoning.
        await trail.StepAsync(RunStepKind.Started,
            $"Pricing {substances.Count} substances against the supplier catalog.", ct: ct);
        await SetStageAsync(projectId, Stages.Cost, s => s.Status = "running", ct);

        var cost = await CostAudit.RunAsync(catalog, substances, projectId,
            DateTimeOffset.UtcNow.ToString("O"), ct);
        await store.UpsertCostAsync(cost, ct);
        return new StageResult(RunOutcome.Done, null,
            $"Priced {substances.Count} substances — {cost.Cards.Count} supplier cards found.",
            RecordIds.Cost(projectId));
    }
```

The remaining seven, each with its specific content:

| Stage | Skip when | `started` step | `output` step | Agent |
|---|---|---|---|---|
| `intake` | `GetConstraintsAsync` is non-null | "Reading the intake brief for {client} / {product}." | "Recorded {n} components, {m} target markets." | `IntakeAgent.AgentName` |
| `background` | always (pass-through) — returns `Skip()` and sets Background `done` | — | — | — |
| `discovery` | `GetCandidatesAsync` non-null | "Finding candidate substances for {n} elements across {m} components." | "Proposed {n} candidates — {tierA} Tier A, {tierB} Tier B." | `DiscoveryAgent.AgentName`. Keep the `ProvidedCandidates` bypass **and** its `CasNumber.IsValid` refusal verbatim. Hydrate `ElementPools` from the `PoolDoc` in memory (`PoolElementPools`), never persisted. |
| `regulatory` | every candidate already has a verdict | "Screening {n} substances against {m} target markets." | "Wrote {n} verdicts — {pass} pass, {flagged} flagged." | `RegulatoryAgent.AgentName`. See Task 7 for the fan-out. |
| `matrix` | `GetMatrixAsync` non-null | "Assembling the compatibility matrix over {n} verdicts." | "Assembled {rows} rows across {components} components." | `null` |
| `dosing` | `GetDosingAsync` non-null | "Dosing {n} compliant substances above the measured detection floor." | "Finalized {n} codes across {m} components." | `DosingAgent.AgentName`. Keep `ResolveDosingInputsAsync` and both park returns unchanged. |
| `decision` | `GetDecisionAsync` non-null | "Picking a final code per component over {rows} assembled rows." | "Proposed a final code for {n} components." | `DecisionAgent.AgentName`. Keep the `awaiting-VP` park (A3 removes it). |

Every stage body has the signature `Task<StageResult> RunXAsync(RunTrail trail, CancellationToken ct)` and
takes its `projectId` from `trail.Run.ProjectId` — one source, so a body cannot write its trail to one
project and its record to another.

Every `IAgentRuns.RunXAsync` gains a trailing `IRunTrail trail` parameter, and `AgentRuns` passes it to the
`MafAgent` constructor. Explicit rather than ambient: a run whose agent's tool calls silently went to no
trail would look exactly like a run that made none.

`RunBackgroundAsync` is the one body that writes nothing and still has an effect — it returns `Skip()` after
setting Background to `done`, preserving the pass-through `OnPoolAsync` performed.

- [ ] **Step 5: Delete `StageDispatcher.cs`**

```bash
git rm src/Smx.Backend/Pipeline/StageDispatcher.cs
```

Port `CloseProjectAsync`, `OnRevisionAsync` and their helpers into `PipelineRunner` **verbatim except for
the status stamping** — they are triggered by a gate signature and a revision write, not by the pipeline,
so Task 8 wires them to their endpoints directly.

- [ ] **Step 6: Run the tests**

Run: `dotnet test src/Smx.Backend.sln`
Expected: all PASS. The ported `StageDispatcherTests` assertions become `PipelineRunnerTests`; delete only
the ones asserting change-feed redelivery semantics, and **port every other one** — they encode behaviour
this rewrite must preserve.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat: a sequential pipeline runner replaces change-feed dispatch"
```

---

## Task 7: The regulatory fan-out

**Files:**
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs`
- Test: `src/Smx.Backend.Tests/PipelineRunnerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    /// One parent run for the stage and one child per substance. The parent is what the operator
    /// cancels and what the UI groups under; children carry `subject` so each is nameable.
    [Fact]
    public async Task Regulatory_opens_a_parent_run_and_one_child_per_substance()
    {
        var (runner, records, runs) = Harness.Build();
        await records.UpsertProjectAsync(Harness.Project("p1"), default);
        await records.UpsertConstraintsAsync(Harness.Constraints("p1"), default);
        await records.UpsertCandidatesAsync(Harness.Candidates("p1", count: 3), default);

        await runner.RunAsync("p1", default);

        var regulatory = (await runs.ListAsync("p1", Stages.Regulatory, default)).ToList();
        var parent = Assert.Single(regulatory.Where(r => r.ParentRunId is null));
        var children = regulatory.Where(r => r.ParentRunId == parent.Id).ToList();
        Assert.Equal(3, children.Count);
        Assert.All(children, c => Assert.NotNull(c.Subject));
    }
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~Regulatory_opens_a_parent`
Expected: FAIL — one run, no children.

- [ ] **Step 3: Implement**

```csharp
    /// Regulatory stays PARALLEL — it is the one stage where serial execution is a real wall-clock
    /// regression, and the operator's whole complaint is about waiting. The parent run is the stage;
    /// the children are the substances. Cancelling the parent cancels them all, which is the only safe
    /// granularity: cancelling one substance of fourteen leaves a candidate set that LOOKS screened.
    private async Task<StageResult> RunRegulatoryAsync(RunTrail trail, CancellationToken ct)
    {
        var projectId = trail.Run.ProjectId;
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return Skip();

        var existing = (await store.GetVerdictsAsync(projectId, ct))
            .Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = candidates.Substances
            .Where(s => s.Tier != "C" && !existing.Contains((s.Cas, s.ComponentId))).ToList();
        if (missing.Count == 0) return Skip();

        trail.Run.Agent = RegulatoryAgent.AgentName;
        await trail.StepAsync(RunStepKind.Started,
            $"Screening {missing.Count} substances against " +
            $"{constraints.Components.SelectMany(k => k.Markets).Distinct().Count()} target markets.", ct: ct);
        await SetStageAsync(projectId, Stages.Regulatory, s => s.Status = "running", ct);

        var parentId = trail.Run.Id;
        using var gate = new SemaphoreSlim(regulatoryParallelism);
        var flagged = 0;

        await Task.WhenAll(missing.Select(async candidate =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var child = new RunDoc
                {
                    Id = $"{parentId}|{candidate.Cas}|{candidate.ComponentId}",
                    ProjectId = projectId,
                    Stage = Stages.Regulatory,
                    StartedAt = DateTimeOffset.UtcNow.ToString("O"),
                    Agent = RegulatoryAgent.AgentName,
                    Subject = $"{candidate.Cas}|{candidate.ComponentId}",
                    ParentRunId = parentId,
                };
                var childTrail = new RunTrail(child, runs, hub, logger);
                await childTrail.OpenAsync(ct);
                await childTrail.StepAsync(RunStepKind.Started,
                    $"Screening {candidate.Element}/{candidate.Form} (CAS {candidate.Cas}) for {candidate.ComponentId}.",
                    ct: ct);

                var result = await agents.RunRegulatoryAsync(constraints, candidate, null, childTrail, ct);
                // The needs-review VerdictDoc the dispatcher synthesised on failure is kept verbatim:
                // an absent verdict and a verdict that says "no cited verdict could be produced" are
                // very different things downstream, and only the second one blocks the gate honestly.
                var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                {
                    Id = RecordIds.Verdict(projectId, candidate.Cas, candidate.ComponentId),
                    ProjectId = projectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
                    Element = candidate.Element, Form = candidate.Form,
                    Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [], 0,
                        $"agent could not produce a valid cited verdict: {result.Error}")],
                };
                if (!result.Succeeded) Interlocked.Increment(ref flagged);
                await store.UpsertVerdictAsync(verdict, ct);

                await childTrail.StepAsync(RunStepKind.Output,
                    $"Verdict for {candidate.Cas} — {verdict.Dimensions[0].Status}.",
                    new RunStepDetail { RecordId = verdict.Id }, ct);
                await childTrail.CompleteAsync(
                    result.Succeeded ? RunOutcome.Done : RunOutcome.NeedsReview, result.Error, ct);
            }
            finally { gate.Release(); }
        }));

        return new StageResult(RunOutcome.Done, null,
            $"Wrote {missing.Count} verdicts — {missing.Count - flagged} screened, {flagged} flagged.",
            null);
    }
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test src/Smx.Backend.sln`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: regulatory's fan-out is one parent run with a child per substance"
```

---

## Task 8: The supervisor

**Files:**
- Create: `src/Smx.Backend/Pipeline/PipelineSupervisor.cs`
- Test: `src/Smx.Backend.Tests/PipelineSupervisorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class PipelineSupervisorTests
{
    [Fact]
    public async Task Starting_twice_refuses_the_second()
    {
        var (supervisor, records, _) = Harness.BuildSupervisor(slowIntake: true);
        await records.UpsertProjectAsync(Harness.Project("p1"), default);

        Assert.True(supervisor.TryStart("p1"));
        Assert.False(supervisor.TryStart("p1"));
    }

    /// Previously: a project whose process died sat at `running` forever, with nothing left on the
    /// feed to redeliver it. Now the orphaned run is stamped and the pipeline re-enters.
    [Fact]
    public async Task Resume_stamps_an_orphaned_run_interrupted_and_re_enters()
    {
        var (supervisor, records, runs) = Harness.BuildSupervisor();
        var project = Harness.Project("p1");
        project.Stages[Stages.Discovery].Status = "running";
        await records.UpsertProjectAsync(project, default);
        await runs.UpsertAsync(new RunDoc
        {
            Id = RunIds.Run("p1", Stages.Discovery, 1), ProjectId = "p1",
            Stage = Stages.Discovery, Outcome = RunOutcome.Running,
            StartedAt = DateTimeOffset.UtcNow.ToString("O"),
        }, default);

        await supervisor.ResumeAllAsync(default);

        var orphan = await runs.GetAsync("p1", RunIds.Run("p1", Stages.Discovery, 1), default);
        Assert.Equal(RunOutcome.Interrupted, orphan!.Outcome);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~PipelineSupervisorTests`
Expected: FAIL — `PipelineSupervisor` does not exist.

- [ ] **Step 3: Implement**

```csharp
// src/Smx.Backend/Pipeline/PipelineSupervisor.cs
using System.Collections.Concurrent;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// Owns the running pipelines: one task per project, and the registry every control endpoint
/// resolves against.
///
/// A hosted service so ResumeAllAsync runs at boot. That resume is not a nicety: without it, a
/// process that dies mid-run leaves the project at `running` with nothing to restart it — the exact
/// checkpoint-and-lose failure the change-feed model had, and the one thing that model was supposed
/// to prevent.
public sealed class PipelineSupervisor(
    IRecordStore store, IRunStore runs, PipelineRunner runner, ILogger<PipelineSupervisor> logger)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, Task> _live = new();
    private CancellationToken _host = CancellationToken.None;

    /// False when a pipeline is already live for this project — the endpoint turns that into a 409.
    public bool TryStart(string projectId)
    {
        if (_live.ContainsKey(projectId)) return false;
        var task = Task.Run(async () =>
        {
            try { await runner.RunAsync(projectId, _host); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "pipeline for {ProjectId} died", projectId);
            }
            finally { _live.TryRemove(projectId, out _); }
        }, _host);
        return _live.TryAdd(projectId, task);
    }

    public bool IsRunning(string projectId) => _live.ContainsKey(projectId);

    public bool CancelRun(string runId) => runner.CancelRun(runId);

    /// Every project holding a `running` stage, re-entered. The orphaned run is stamped FIRST so the
    /// trail shows the gap: a run that simply reappeared would let a half-finished analysis read as
    /// one that ran cleanly.
    public async Task ResumeAllAsync(CancellationToken ct)
    {
        foreach (var project in await store.ListProjectsAsync(ct))
        {
            if (!project.Stages.Values.Any(s => s.Status == "running")) continue;

            foreach (var run in await runs.ListAsync(project.ProjectId, null, ct))
                if (run.Outcome == RunOutcome.Running)
                {
                    run.Outcome = RunOutcome.Interrupted;
                    run.EndedAt = DateTimeOffset.UtcNow.ToString("O");
                    run.Error = "the process running this stage stopped";
                    await runs.UpsertAsync(run, ct);
                }

            logger.LogInformation("resuming pipeline for {ProjectId}", project.ProjectId);
            TryStart(project.ProjectId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _host = stoppingToken;
        await ResumeAllAsync(stoppingToken);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test src/Smx.Backend.sln`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: the pipeline supervisor — start, resume, cancel"
```

---

## Task 9: The thread endpoints

Delivers §7 over the **existing** chat storage, so Track 2 integrates before A2 replaces it.

**Files:**
- Create: `src/Smx.Backend/Api/ThreadEndpoints.cs`
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs` (wire `/start` to the supervisor)
- Test: `src/Smx.Backend.Tests/ThreadEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Smx.Backend.Tests;

public class ThreadEndpointsTests
{
    [Fact]
    public async Task Thread_merges_runs_and_chat_turns_in_time_order()
    {
        using var app = Harness.App();
        await Harness.SeedRunAndChatTurn(app, "p1", "discovery");

        var entries = await app.Client.GetFromJsonAsync<List<JsonElement>>(
            "/projects/p1/stages/discovery/thread");

        Assert.Equal(2, entries!.Count);
        Assert.Equal("run", entries[0].GetProperty("kind").GetString());
        Assert.Equal("message", entries[1].GetProperty("kind").GetString());
        // seq is the client's dedupe key and MUST be dense and ordered.
        Assert.Equal(new[] { 1, 2 }, entries.Select(e => e.GetProperty("seq").GetInt32()));
    }

    [Fact]
    public async Task Rerun_refuses_a_done_stage()
    {
        using var app = Harness.App();
        await Harness.SeedProject(app, "p1", discovery: "done");

        var res = await app.Client.PostAsync("/projects/p1/stages/discovery/rerun", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Rerun_accepts_a_failed_stage()
    {
        using var app = Harness.App();
        await Harness.SeedProject(app, "p1", discovery: "failed");

        var res = await app.Client.PostAsync("/projects/p1/stages/discovery/rerun", null);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
    }

    [Fact]
    public async Task Stream_replays_only_what_follows_the_cursor()
    {
        using var app = Harness.App();
        await Harness.SeedRunWithSteps(app, "p1", "discovery", steps: 3);

        var body = await Harness.ReadStreamAsync(app, "/projects/p1/stages/discovery/thread/stream?since=e1.s2");

        Assert.DoesNotContain("\"seq\":1", body);
        Assert.Contains("\"seq\":3", body);
    }
}
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~ThreadEndpointsTests`
Expected: FAIL — the routes do not exist.

- [ ] **Step 3: Implement**

```csharp
// src/Smx.Backend/Api/ThreadEndpoints.cs
using Microsoft.AspNetCore.Mvc;
using Smx.Backend.Pipeline;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

/// The unified per-stage thread — 2026-07-27-execution-core-design.md §7.
///
/// A1 serves this contract over the EXISTING chat storage: runs come from IRunStore and messages from
/// the ChatMessageDoc thread, merged here by timestamp. A2 replaces the storage with one genuine
/// thread and this contract does not move — which is precisely what lets the web track integrate now
/// rather than after A2.
public static class ThreadEndpoints
{
    public static void MapThreadEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] on every store param is required, not decorative — see the long comment at the
        // top of ProjectEndpoints. Without it, minimal APIs mis-infer these as body params and break
        // routing for EVERY endpoint in the app.
        app.MapGet("/projects/{projectId}/stages/{stage}/thread", async (
            string projectId, string stage,
            [FromServices] IRunStore runs, [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new { error = $"unknown stage '{stage}'" });
            return Results.Json(await BuildThreadAsync(projectId, stage, runs, store, ct), Json.Options);
        });

        app.MapGet("/projects/{projectId}/stages/{stage}/thread/stream", async (
            string projectId, string stage, string? since, HttpContext http,
            [FromServices] ThreadEventHub hub, [FromServices] IRunStore runs,
            [FromServices] IRecordStore store, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";

            // SUBSCRIBE FIRST, then replay. The other order has a hole exactly the width of the
            // replay query: a step published while the catch-up was in flight would reach no
            // subscriber and never be replayed either, and the operator would be missing a step with
            // no way to know it.
            using var subscription = hub.Subscribe(projectId, stage);
            foreach (var frame in await ReplayAsync(projectId, stage, since, runs, store, ct))
                await WriteAsync(http, frame, ct);

            var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(15));
            var pump = Task.Run(async () =>
            {
                // App Gateway reaps an idle connection, and a long tool call is a long idle. A ':'
                // comment is a no-op frame the client's parser already skips.
                while (await heartbeat.WaitForNextTickAsync(ct))
                    await http.Response.WriteAsync(": keep-alive\n\n", ct);
            }, ct);

            try
            {
                await foreach (var frame in subscription.Reader.ReadAllAsync(ct))
                    await WriteAsync(http, frame, ct);
            }
            catch (OperationCanceledException) { /* the client navigated away */ }
            finally { heartbeat.Dispose(); }

            return Results.Empty;
        });

        app.MapPost("/projects/{projectId}/runs/{runId}/cancel", async (
            string projectId, string runId,
            [FromServices] PipelineSupervisor supervisor, [FromServices] IRunStore runs,
            CancellationToken ct) =>
        {
            if (await runs.GetAsync(projectId, runId, ct) is not { } run) return Results.NotFound();
            if (run.Outcome != RunOutcome.Running)
                return Results.Conflict(new { error = $"that run is already {run.Outcome}" });
            // Cancelling one substance of fourteen leaves a candidate set that LOOKS screened and is
            // not. The parent is the only safe granularity.
            if (run.ParentRunId is not null)
                return Results.UnprocessableEntity(new
                {
                    error = "cancel the regulatory stage's parent run — cancelling one substance " +
                            "would leave a partially screened candidate set that reads as complete",
                });
            return supervisor.CancelRun(runId) ? Results.Accepted() : Results.Conflict(new { error = "that run is not live" });
        });

        app.MapPost("/projects/{projectId}/stages/{stage}/rerun", async (
            string projectId, string stage,
            [FromServices] IRecordStore store, [FromServices] PipelineSupervisor supervisor,
            CancellationToken ct) =>
        {
            if (!Stages.All.Contains(stage))
                return Results.UnprocessableEntity(new { error = $"unknown stage '{stage}'" });
            if (await store.GetProjectAsync(projectId, ct) is not { } project) return Results.NotFound();

            var status = project.Stages[stage].Status;
            // `done` is refused deliberately. Re-running a landed stage replaces analysis a gate may
            // have been signed over, and revise-with-reason is the path that does that WITH the
            // operator's reason recorded. Retry must not become the backdoor around it.
            if (status is not ("failed" or "needs-review"))
                return Results.UnprocessableEntity(new
                {
                    error = $"the {stage} stage is '{status}' — only a failed, needs-review or " +
                            "cancelled stage can be re-run. To change a landed result, tell the " +
                            "agent why, so the reason is recorded.",
                });

            project.Stages[stage].Status = "pending";
            project.Stages[stage].Error = null;
            await store.UpsertProjectAsync(project, ct);
            supervisor.TryStart(projectId);
            return Results.Accepted();
        });
    }

    private static async Task WriteAsync(HttpContext http, ThreadFrame frame, CancellationToken ct)
    {
        await http.Response.WriteAsync(
            $"event: {frame.Event}\nid: {frame.Id}\ndata: {System.Text.Json.JsonSerializer.Serialize(frame.Data, Json.Options)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
```

And the two private statics in the same file:

```csharp
    /// The merged transcript. In A1 the two halves live in two stores — runs in IRunStore, messages in
    /// the ChatMessageDoc thread — so they are merged HERE by timestamp. A2 replaces both with one
    /// thread and deletes this merge; the SHAPE it returns does not change, which is what lets the web
    /// track build against it now.
    ///
    /// `seq` is assigned here, dense from 1, and is the client's dedupe key. It is deliberately NOT
    /// derived from a timestamp: two records written in the same millisecond would collide, and a
    /// collided seq makes the client drop an entry as "already held".
    private static async Task<List<object>> BuildThreadAsync(
        string projectId, string stage, IRunStore runs, IRecordStore store, CancellationToken ct)
    {
        var runDocs = await runs.ListAsync(projectId, stage, ct);
        var turns = await store.GetChatThreadAsync(projectId, stage, ct);

        var merged = runDocs
            .Select(r => (At: r.StartedAt, Entry: (object)new { kind = "run", run = r }))
            .Concat(turns.Select(t => (At: t.CreatedAt, Entry: (object)new
            {
                kind = "message",
                role = t.Role,
                text = t.Text,
                // A1 has no `queued` concept yet — the mailbox is A2. `pending` maps to `queued` so the
                // client's three-state union holds from day one and does not change under it later.
                status = t.Status == ChatStatus.Pending ? "queued" : t.Status,
                error = t.Error,
            })))
            // Ordinal, not culture-aware: these are round-trip "O" timestamps compared as text, and a
            // culture-sensitive comparison can reorder them (the ChatTurns.InOrder lesson).
            .OrderBy(x => x.At, StringComparer.Ordinal)
            .ToList();

        return [.. merged.Select((x, i) => (object)Merge(x.Entry, seq: i + 1, at: x.At))];
    }

    /// Adds `seq` and `at` to an anonymous entry without re-declaring every field twice.
    private static Dictionary<string, object?> Merge(object entry, int seq, string at)
    {
        var dict = new Dictionary<string, object?> { ["seq"] = seq, ["at"] = at };
        foreach (var p in entry.GetType().GetProperties()) dict[Camel(p.Name)] = p.GetValue(entry);
        return dict;
    }

    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    /// The catch-up a reconnecting client needs: every frame the live stream would have sent, for the
    /// runs already on file, with everything at or before `since` dropped.
    ///
    /// Ids match the live path's exactly ("{runId}.s{seq}", "{runId}.r") — they are the same cursor
    /// space, and a replayed id the client cannot match against what it holds would duplicate.
    private static async Task<List<ThreadFrame>> ReplayAsync(
        string projectId, string stage, string? since, IRunStore runs, IRecordStore store, CancellationToken ct)
    {
        var frames = new List<ThreadFrame>();
        foreach (var run in await runs.ListAsync(projectId, stage, ct))
        {
            frames.Add(new ThreadFrame("entry", run.Id,
                new { seq = 0, at = run.StartedAt, kind = "run", run }));
            foreach (var step in run.Steps)
                frames.Add(new ThreadFrame("step", $"{run.Id}.s{step.Seq}", new { runId = run.Id, step }));
            if (run.EndedAt is not null)
                frames.Add(new ThreadFrame("run", $"{run.Id}.r",
                    new { runId = run.Id, endedAt = run.EndedAt, outcome = run.Outcome, error = run.Error }));
        }

        if (since is null) return frames;
        var cursor = frames.FindIndex(f => f.Id == since);
        // An unrecognised cursor replays EVERYTHING rather than nothing. A client resuming with an id
        // from a run that has since been superseded would otherwise silently receive an empty stream
        // and sit there looking connected and blank; a duplicate is idempotent on the client, a gap is not.
        return cursor < 0 ? frames : frames.Skip(cursor + 1).ToList();
    }

- [ ] **Step 4: Wire `/start` and register the services**

In `ProjectEndpoints.cs`, after the existing readiness checks pass, replace the doc write with:

```csharp
            return supervisor.TryStart(projectId)
                ? Results.Accepted()
                : Results.Conflict(new { error = "a pipeline is already running for this project" });
```

In `Program.cs`:

```csharp
services.AddSingleton<ThreadEventHub>();
services.AddSingleton<PipelineRunner>();
services.AddSingleton<PipelineSupervisor>();
services.AddHostedService(sp => sp.GetRequiredService<PipelineSupervisor>());
```

and `app.MapThreadEndpoints();`. **Delete** the `ChangeFeedWorker` registration.

- [ ] **Step 5: Run the tests**

Run: `dotnet test src/Smx.Backend.sln`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat: the thread contract — read, stream, cancel, rerun"
```

---

## Task 10: Infrastructure

**Files:**
- Modify: `infra/modules/data.bicep`, `infra/modules/compute.bicep`, `infra/main.bicep`
- Modify: `infra/scripts/build-images.{sh,ps1}`, `deploy.{sh,ps1}`, `swap-images.{sh,ps1}`

- [ ] **Step 1: Add the `runs` container, delete the leases container**

In `infra/modules/data.bicep`, beside the `record` container:

```bicep
// The run trail. Separate from `record` on purpose: append-only telemetry must never appear in a
// query that reads project state.
resource runsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: database
  name: 'runs'
  properties: {
    resource: {
      id: 'runs'
      partitionKey: { paths: [ '/projectId' ], kind: 'Hash' }
    }
  }
}
```

Delete the `recordLeases` resource entirely — nothing reads a change feed any more.

- [ ] **Step 2: Delete the orchestrator Container App**

In `infra/modules/compute.bicep`: remove the `orchestrator` entry from the `apps` array, remove
`orchestratorAppName`, `orchestratorBaseUrl`, `orchestratorBaseUrlEnv` and the `output orchestratorAppName`.
Merge `orchestratorEnv`'s extra settings (the Search Proxy URL, Bronze) into the backend's env, and set the
backend's `minReplicas: 1` — the supervisor must be running to resume interrupted pipelines.

Remove the `orchestratorImage` param from `compute.bicep` and `main.bicep`.

- [ ] **Step 3: Update the scripts — both twins**

`build-images.sh` and `build-images.ps1` build **two** images (frontend, backend), not three. Remove every
`orchestrator` reference from `deploy.*` and `swap-images.*`. They are twins, not alternatives: fix a bug in
one and fix it in the other.

- [ ] **Step 4: Validate**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: both exit 0 with no output.

- [ ] **Step 5: Commit**

```bash
git add infra/
git commit -m "infra: one backend app, a runs container, no change-feed leases"
```

---

## Task 11: Verify end to end

- [ ] **Step 1: Full build and test**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```

Expected: build succeeds; every test passes. Record the test count — it should be within a handful of the
pre-merge total, and a large drop means ported assertions were dropped rather than ported.

- [ ] **Step 2: Update CLAUDE.md**

The "Agent backend" section describes `Smx.Orchestrator` as a separate Container App and record-as-bus
change-feed dispatch. Both are now false. Rewrite that bullet to describe the merged service, the pipeline
runner and the run trail, and update the build/test commands.

- [ ] **Step 3: Deploy to dev and drive it**

```bash
infra/scripts/deploy.sh dev
infra/scripts/build-images.sh dev
```

Create a project through the interview, press Start Processing, and confirm with
`curl "$BASE/projects/$ID/stages/pool/thread"` that the pool run appears with its steps, and that
`GET …/thread/stream` delivers frames live.

- [ ] **Step 4: Commit and hand off to Track 2**

```bash
git add -A
git commit -m "docs: CLAUDE.md describes the merged service and the pipeline runner"
git push -u origin feat/execution-core
```

Tell Track 2 the branch is ready to rebase onto (their Task 15).

---

## Deferred to A2

- Folding `ChatAgent` into the stage agents; one thread per stage; produce vs converse turns.
- The mailbox, the concurrent converse turn, and `restart_stage`.
- The validation split (structural rejects, evidence-quality flags).

## Deferred to A3

- Gates → sign-offs; deleting the four `awaiting-*` statuses; export/order preconditions.

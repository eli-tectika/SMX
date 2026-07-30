# Token Spend Ceiling — Plan 2: attribution, the operator surface, and the quota

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the ceiling legible — the operator can see what today cost and what made it cost that, a spend park renders as a park rather than as "not started" — and set the surrounding Azure layers (TPM quota per environment, the budget alert).

**Architecture:** An `AsyncLocal` run context lets the already-installed `MeteredChatClient` attribute each call to the (project, stage) it happened in, accumulating onto the existing `RunDoc` — which is already per-project and per-stage, so no new attribution store is needed. A `GET /spend` endpoint serves the day's total and its per-model breakdown; the `awaiting-budget` park renders it. Embeddings are metered by the same meter. Deployment capacities become per-environment Bicep parameters.

**Tech Stack:** .NET 8, xUnit, `AsyncLocal<T>`, Vitest + React Testing Library, Bicep.

**Spec:** `docs/superpowers/specs/2026-07-30-token-spend-ceiling-design.md`
**Depends on:** Plan 1 (`2026-07-30-token-spend-ceiling-plan-1-the-ceiling.md`) — complete it first. Every type referenced here (`ISpendMeter`, `SpendStatus`, `TokenPrices`, `SpendDoc`, `StageStatus.AwaitingBudget`) is created there.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Smx.Backend/Pipeline/RunContext.cs` (create) | `AsyncLocal` (project, stage) ambient for attribution. |
| `src/Smx.Domain/Records/RunDoc.cs` (modify) | `InputTokens`, `OutputTokens`, `UsdSpent` on the run. |
| `src/Smx.Infrastructure/MeteredChatClient.cs` (modify) | Attribute recorded usage to the ambient run. |
| `src/Smx.Backend/Pipeline/PipelineRunner.cs` (modify) | Set the ambient run context around a stage body. |
| `src/Smx.Infrastructure/MeteredEmbedder.cs` (create) | Meter embedding calls. |
| `src/Smx.Backend/Api/SpendEndpoints.cs` (create) | `GET /spend`. |
| `src/smx-web/src/api/types.ts` (modify) | `awaiting-budget` in the union; `SpendToday` type. |
| `src/smx-web/src/domain/stages.ts` (modify) | `PARKED`, `foldStatus`, `stageIcon`. |
| `src/smx-web/src/domain/blocking.ts` (modify) | `AWAITED` label and the `whatsBlocking` branch. |
| `src/smx-web/src/components/shell/NextAction.tsx` (modify) | Render the spend readout on a spend park. |
| `infra/**/ai.bicep`, `infra/**/main.bicep` (modify) | Per-env capacity parameters. |
| `docs/runbooks/cost-management-budget.md` (create) | The portal steps for the budget alert. |

---

### Task 1: Attribute spend to the run that caused it

**Files:**
- Create: `src/Smx.Backend/Pipeline/RunContext.cs`
- Modify: `src/Smx.Domain/Records/RunDoc.cs`
- Test: `src/Smx.Backend.Tests/RunContextTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/RunContextTests.cs`:

```csharp
using Smx.Backend.Pipeline;

namespace Smx.Backend.Tests;

public class RunContextTests
{
    [Fact]
    public void Is_absent_outside_a_scope()
    {
        Assert.Null(RunContext.Current);
    }

    [Fact]
    public void Reports_the_scope_it_is_inside()
    {
        using (RunContext.Enter("p1", "regulatory", "p1|regulatory|1"))
        {
            Assert.Equal("p1|regulatory|1", RunContext.Current!.RunId);
            Assert.Equal("regulatory", RunContext.Current.Stage);
        }
        Assert.Null(RunContext.Current);
    }

    // The regulatory stage fans out REGULATORY_PARALLELISM ways. AsyncLocal flows INTO each child
    // task, which is the whole reason it is the right mechanism — a plain field would be shared
    // and a parameter would have to be threaded through every agent signature.
    [Fact]
    public async Task Flows_into_parallel_children()
    {
        string?[] seen = new string?[4];
        using (RunContext.Enter("p1", "regulatory", "p1|regulatory|1"))
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(i =>
                Task.Run(() => { seen[i] = RunContext.Current?.RunId; })));
        }
        Assert.All(seen, s => Assert.Equal("p1|regulatory|1", s));
    }

    // Nested scopes restore, rather than clear, on dispose.
    [Fact]
    public void Restores_the_outer_scope()
    {
        using (RunContext.Enter("p1", "dosing", "outer"))
        {
            using (RunContext.Enter("p1", "cost", "inner"))
                Assert.Equal("inner", RunContext.Current!.RunId);
            Assert.Equal("outer", RunContext.Current!.RunId);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~RunContextTests`
Expected: FAIL — `RunContext` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Backend/Pipeline/RunContext.cs`:

```csharp
namespace Smx.Backend.Pipeline;

/// Which run the current async flow belongs to, so a model call can be attributed to the stage
/// that made it without threading a parameter through every agent signature.
///
/// `AsyncLocal` rather than a field or a parameter: the regulatory stage fans out
/// REGULATORY_PARALLELISM ways, and AsyncLocal flows into each child task while a shared field
/// would be a race and a parameter would mean changing every tool and agent interface to carry
/// bookkeeping they have no other use for.
public sealed class RunContext
{
    private static readonly AsyncLocal<RunContext?> _current = new();

    public static RunContext? Current => _current.Value;

    public string ProjectId { get; }
    public string Stage { get; }
    public string RunId { get; }

    private RunContext(string projectId, string stage, string runId) =>
        (ProjectId, Stage, RunId) = (projectId, stage, runId);

    public static IDisposable Enter(string projectId, string stage, string runId)
    {
        var previous = _current.Value;
        _current.Value = new RunContext(projectId, stage, runId);
        return new Scope(previous);
    }

    private sealed class Scope(RunContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
```

- [ ] **Step 4: Add the run's cost fields**

In `src/Smx.Domain/Records/RunDoc.cs`, add to the `RunDoc` class after `Error`:

```csharp
    /// What this run cost. Accumulated by MeteredChatClient through the ambient RunContext, so the
    /// operator can answer "what made yesterday cost that" from the trail they already read rather
    /// than from a second ledger. Zero on a deterministic stage — which is correct, not missing:
    /// `Agent` is null there and no model was called.
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal UsdSpent { get; set; }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~RunContextTests`
Expected: PASS, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Pipeline/RunContext.cs src/Smx.Domain/Records/RunDoc.cs src/Smx.Backend.Tests/RunContextTests.cs
git commit -m "feat(backend): an ambient run scope that survives the regulatory fan-out"
```

---

### Task 2: Accumulate cost onto the run

**Files:**
- Modify: `src/Smx.Infrastructure/MeteredChatClient.cs`
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs` (`ExecuteAsync`)
- Test: `src/Smx.Backend.Tests/PipelineRunnerSpendAttributionTests.cs`

The decorator lives in `Smx.Infrastructure`, which must not reference `Smx.Backend`. So the
decorator does not know about `RunContext` directly — it takes an optional attribution callback,
and `Program.cs` supplies one that reads `RunContext.Current`.

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/PipelineRunnerSpendAttributionTests.cs`:

```csharp
using Smx.Backend.Pipeline;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class PipelineRunnerSpendAttributionTests
{
    [Fact]
    public void A_runs_cost_is_the_sum_of_the_calls_made_inside_it()
    {
        var doc = new RunDoc { Id = "p1|discovery|1", ProjectId = "p1", Stage = "discovery", StartedAt = "t" };

        doc.AddSpend(1_000, 100, 0.0075m);
        doc.AddSpend(2_000, 200, 0.0150m);

        Assert.Equal(3_000, doc.InputTokens);
        Assert.Equal(300, doc.OutputTokens);
        Assert.Equal(0.0225m, doc.UsdSpent);
    }

    // Accumulation must be safe under the regulatory fan-out, which records from parallel children.
    [Fact]
    public async Task Accumulates_correctly_under_concurrent_calls()
    {
        var doc = new RunDoc { Id = "p1|regulatory|1", ProjectId = "p1", Stage = "regulatory", StartedAt = "t" };

        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ =>
            Task.Run(() => doc.AddSpend(10, 1, 0.01m))));

        Assert.Equal(1_000, doc.InputTokens);
        Assert.Equal(100, doc.OutputTokens);
        Assert.Equal(1.00m, doc.UsdSpent);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~PipelineRunnerSpendAttributionTests`
Expected: FAIL — `AddSpend` does not exist.

- [ ] **Step 3: Add the accumulator**

In `src/Smx.Domain/Records/RunDoc.cs`, add to `RunDoc`:

```csharp
    private readonly Lock _spendLock = new();

    /// Locked because the regulatory fan-out records from parallel children and `+=` on three
    /// fields is three read-modify-writes, none of them atomic. The same reasoning as `Append`:
    /// this is the only sanctioned way to move these numbers.
    public void AddSpend(long inputTokens, long outputTokens, decimal usd)
    {
        lock (_spendLock)
        {
            InputTokens += inputTokens;
            OutputTokens += outputTokens;
            UsdSpent += usd;
        }
    }
```

If the project's language version rejects `System.Threading.Lock`, use `private readonly object
_spendLock = new();` — behaviour is identical.

- [ ] **Step 4: Give the decorator an attribution hook**

In `src/Smx.Infrastructure/MeteredChatClient.cs`, add an optional callback parameter and invoke it
alongside the meter:

```csharp
public sealed class MeteredChatClient(
    IChatClient inner,
    ISpendMeter meter,
    string deployment,
    // Attribution is a CALLBACK rather than a direct RunContext read because Smx.Infrastructure
    // must not reference Smx.Backend. Program.cs supplies the reader.
    Action<string, long, long>? attribute = null)
    : DelegatingChatClient(inner)
```

and in `RecordAsync`, after the null check:

```csharp
        attribute?.Invoke(deployment, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0);
```

- [ ] **Step 5: Set the scope around each stage body**

In `PipelineRunner.ExecuteAsync`, wrap the `body(trail, cts.Token)` call:

```csharp
            using var scope = RunContext.Enter(projectId, stage, doc.Id);
            var result = await body(trail, cts.Token);
```

- [ ] **Step 6: Expose the live run docs**

In `PipelineRunner`, beside the existing `_live` dictionary (line 61):

```csharp
    /// The run docs this process is currently executing, by run id. Parallel to `_live` and
    /// maintained in the same two places, so a doc can never outlive its cancellation source.
    private readonly ConcurrentDictionary<string, RunDoc> _liveDocs = new();

    /// The run doc for an id this process is executing, or null. Spend attribution's only entry
    /// point — a call is attributed to a run this process is holding, or not at all.
    public RunDoc? LiveRun(string runId) => _liveDocs.GetValueOrDefault(runId);
```

In `ExecuteAsync`, beside `_live[doc.Id] = cts;` add `_liveDocs[doc.Id] = doc;`, and in the same
`finally` block beside `_live.TryRemove(doc.Id, out _);` add `_liveDocs.TryRemove(doc.Id, out _);`.

- [ ] **Step 7: Supply the attribution callback in Program.cs**

Where `FoundryChatClientFactory.CreateAsync` is called, pass the callback as its final argument:

```csharp
            // Attribution is best-effort by design: a call made outside any stage (an operator
            // chat turn, a warm-up) has no run to bill, and must still be metered against the
            // ceiling. The ceiling is the guarantee; attribution is the explanation.
            attribute: (deployment, inputTokens, outputTokens) =>
            {
                if (RunContext.Current is not { } ctx) return;
                if (runner.LiveRun(ctx.RunId) is not { } run) return;
                run.AddSpend(inputTokens, outputTokens, TokenPrices.Shipped.Cost(
                    deployment, inputTokens, outputTokens));
            });
```

`runner` is the `PipelineRunner` singleton; resolve it from `sp` in the same factory lambda that
builds the chat client. Add `using Smx.Backend.Pipeline;` and `using Smx.Domain.Spend;`.

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add src/Smx.Domain/Records/RunDoc.cs src/Smx.Infrastructure/MeteredChatClient.cs src/Smx.Backend/Pipeline/PipelineRunner.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/PipelineRunnerSpendAttributionTests.cs
git commit -m "feat(backend): a run trail that records what the run cost"
```

---

### Task 3: Meter embeddings

**Files:**
- Create: `src/Smx.Infrastructure/MeteredEmbedder.cs`
- Modify: `src/Smx.Backend/Program.cs` (the `IEmbedder` registration, ~line 264)
- Test: `src/Smx.Backend.Tests/MeteredEmbedderTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/MeteredEmbedderTests.cs`:

```csharp
using Smx.Domain.Spend;
using Smx.Domain.Tools;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

public class MeteredEmbedderTests
{
    private sealed class RecordingMeter : ISpendMeter
    {
        public readonly List<(string Deployment, long In, long Out)> Records = [];
        public Task RecordAsync(string d, long i, long o, CancellationToken ct = default)
        { Records.Add((d, i, o)); return Task.CompletedTask; }
        public Task<SpendStatus> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new SpendStatus(0m, 200m, false));
    }

    private sealed class StubEmbedder(int dims) : IEmbedder
    {
        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[dims]).ToList());
    }

    [Fact]
    public async Task Records_estimated_input_tokens_and_no_output_tokens()
    {
        var meter = new RecordingMeter();
        var embedder = new MeteredEmbedder(new StubEmbedder(3), meter, "text-embedding-3-large");

        // 40 characters ⇒ 10 tokens at the documented 4-chars-per-token estimate.
        await embedder.EmbedAsync([new string('a', 40)]);

        Assert.Equal(("text-embedding-3-large", 10L, 0L), meter.Records.Single());
    }

    [Fact]
    public async Task Records_nothing_for_an_empty_batch()
    {
        var meter = new RecordingMeter();
        var embedder = new MeteredEmbedder(new StubEmbedder(3), meter, "text-embedding-3-large");

        await embedder.EmbedAsync([]);

        Assert.Empty(meter.Records);
    }

    [Fact]
    public async Task Returns_the_inner_vectors_unchanged()
    {
        var embedder = new MeteredEmbedder(new StubEmbedder(3), new RecordingMeter(), "text-embedding-3-large");
        var vectors = await embedder.EmbedAsync(["one", "two"]);
        Assert.Equal(2, vectors.Count);
        Assert.Equal(3, vectors[0].Length);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~MeteredEmbedderTests`
Expected: FAIL — `MeteredEmbedder` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Infrastructure/MeteredEmbedder.cs`:

```csharp
using Smx.Domain.Spend;
using Smx.Domain.Tools;

namespace Smx.Infrastructure;

/// Counts embedding input against the daily ceiling.
///
/// The token count is ESTIMATED at 4 characters per token, not read from the API response —
/// `IEmbedder` returns vectors only, and widening that interface to carry usage would change
/// every implementation (including Smx.Functions' separate one) for a category the 50K TPM
/// embedding quota already bounds at roughly $9/day. The estimate is deliberately crude and
/// deliberately biased high enough to be safe: over-counting makes the ceiling bind slightly
/// early, which is the direction that cannot hurt.
public sealed class MeteredEmbedder(IEmbedder inner, ISpendMeter meter, string deployment) : IEmbedder
{
    private const int CharsPerToken = 4;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var vectors = await inner.EmbedAsync(texts, ct);
        var estimated = texts.Sum(t => (long)Math.Ceiling(t.Length / (double)CharsPerToken));
        if (estimated > 0)
            await meter.RecordAsync(deployment, estimated, 0, ct);
        return vectors;
    }
}
```

- [ ] **Step 4: Wire it**

In `src/Smx.Backend/Program.cs`, wrap the existing `FoundryEmbedder` registration:

```csharp
        services.AddSingleton<IEmbedder>(sp =>
        {
            // Same two arguments the existing registration passes — do not change them.
            IEmbedder embedder = new FoundryEmbedder(azureOpenAIClient, opts.EmbeddingDeployment);
            var meter = sp.GetService<ISpendMeter>();
            return meter is null ? embedder : new MeteredEmbedder(embedder, meter, opts.EmbeddingDeployment);
        });
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Infrastructure/MeteredEmbedder.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/MeteredEmbedderTests.cs
git commit -m "feat(infra): count embeddings against the ceiling too"
```

---

### Task 4: The read surface

**Files:**
- Create: `src/Smx.Backend/Api/SpendEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs` (call `app.MapSpendEndpoints()` beside the other `Map*Endpoints`)
- Test: `src/Smx.Backend.Tests/SpendEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/SpendEndpointsTests.cs`, following the shape of the existing
`CostEndpointsTests` (same host builder, same auth handling):

```csharp
using System.Net;
using System.Net.Http.Json;

namespace Smx.Backend.Tests;

public class SpendEndpointsTests
{
    [Fact]
    public async Task Reports_todays_spend_the_ceiling_and_the_per_model_breakdown()
    {
        // Build the test host the same way CostEndpointsTests does, with a spend store seeded:
        //   2026-07-30 | claude-opus-4-7 : 1_000_000 in, 1_000_000 out  => $30.00
        //   2026-07-30 | gpt-5-mini      : 1_000_000 in, 1_000_000 out  => $2.25
        using var client = TestHost.WithSeededSpend(day: "2026-07-30", ceiling: 200m);

        var body = await client.GetFromJsonAsync<JsonElement>("/spend");

        Assert.Equal(32.25m, body.GetProperty("usdSpent").GetDecimal());
        Assert.Equal(200m, body.GetProperty("ceilingUsd").GetDecimal());
        Assert.False(body.GetProperty("overCeiling").GetBoolean());
        Assert.Equal(2, body.GetProperty("byModel").GetArrayLength());
    }

    // With no ceiling configured the endpoint must say so rather than reporting a ceiling of 0,
    // which would read as "everything is over budget".
    [Fact]
    public async Task Reports_a_null_ceiling_when_none_is_configured()
    {
        using var client = TestHost.WithSeededSpend(day: "2026-07-30", ceiling: null);

        var body = await client.GetFromJsonAsync<JsonElement>("/spend");

        Assert.Equal(JsonValueKind.Null, body.GetProperty("ceilingUsd").ValueKind);
        Assert.False(body.GetProperty("overCeiling").GetBoolean());
    }
}
```

> Add `TestHost.WithSeededSpend` to the test project's existing host helper, mirroring how the
> other endpoint tests build their host. If the existing helper is per-file rather than shared,
> follow that file's local pattern instead of introducing a new shared helper.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~SpendEndpointsTests`
Expected: FAIL — 404, no `/spend` route.

- [ ] **Step 3: Write the endpoint**

Create `src/Smx.Backend/Api/SpendEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Domain.Spend;

namespace Smx.Backend.Api;

/// Today's model spend. Project-independent, like the reference surfaces — spend is a property of
/// the day and the estate, not of a project.
public static class SpendEndpoints
{
    public static void MapSpendEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/spend", async (
            [FromServices] ISpendStore store,
            [FromServices] BackendOptions opts,
            CancellationToken ct) =>
        {
            var day = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
            var docs = await store.ReadDayAsync(day, ct);
            var prices = TokenPrices.Shipped;

            var byModel = docs
                .Select(d => new
                {
                    deployment = d.Deployment,
                    inputTokens = d.InputTokens,
                    outputTokens = d.OutputTokens,
                    usd = prices.Cost(d.Deployment, d.InputTokens, d.OutputTokens),
                })
                .OrderByDescending(m => m.usd)
                .ToList();

            var spent = byModel.Sum(m => m.usd);
            var ceiling = opts.SpendCeilingUsdPerDay;

            return Results.Ok(new
            {
                day,
                usdSpent = spent,
                // NULL, not 0, when unconfigured. A ceiling of 0 would render as "every dollar is
                // over budget", which is the opposite of what an unset ceiling means.
                ceilingUsd = ceiling,
                overCeiling = ceiling is { } c && spent >= c,
                byModel,
            });
        });
    }
}
```

- [ ] **Step 4: Map it**

In `src/Smx.Backend/Program.cs`, beside the other `app.Map*Endpoints()` calls, add
`app.MapSpendEndpoints();`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~SpendEndpointsTests`
Expected: PASS, 2 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Api/SpendEndpoints.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/SpendEndpointsTests.cs
git commit -m "feat(api): serve today's spend and what made it"
```

---

### Task 5: The park, in the UI

Adding `'awaiting-budget'` to `StageStatus` **breaks the build** until every fold handles it —
that is the mechanism working, not an obstacle. Add the union member first and let the compiler
produce the to-do list.

**Files:**
- Modify: `src/smx-web/src/api/types.ts:40-52`
- Modify: `src/smx-web/src/domain/stages.ts` (`PARKED`, `foldStatus`, `stageIcon`)
- Modify: `src/smx-web/src/domain/blocking.ts` (`AWAITED`, `whatsBlocking`)
- Test: `src/smx-web/src/domain/stages.test.ts`, `src/smx-web/src/domain/blocking.test.ts`

- [ ] **Step 1: Write the failing tests**

Add to `src/smx-web/src/domain/stages.test.ts`:

```ts
// The fifth instance of "a park renders as not started" is the one this test exists to prevent.
it('folds a spend park as parked, never as pending', () => {
  expect(foldStatus([{ status: 'awaiting-budget', attempts: 1 }])).toBe('awaiting-budget');
});

it('gives a spend park a park icon, not a not-started icon', () => {
  expect(stageIcon('awaiting-budget')).toBe(stageIcon('awaiting-RE'));
  expect(stageIcon('awaiting-budget')).not.toBe(stageIcon('pending'));
});
```

Add to `src/smx-web/src/domain/blocking.test.ts`:

```ts
it('names the spend ceiling as the blocker, not a person', () => {
  const blocking = whatsBlocking({
    stages: { discovery: { status: 'awaiting-budget', attempts: 1, error: "today's model spend of $212.40 has reached the $200.00 daily ceiling" } },
  } as never);

  expect(blocking?.text).toMatch(/spend ceiling/i);
  // Every other park names a human. This one must not invent one.
  expect(blocking?.text).not.toMatch(/Regulatory Expert|physics|VP/i);
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd src/smx-web && npx vitest run src/domain/stages.test.ts src/domain/blocking.test.ts`
Expected: FAIL — `'awaiting-budget'` is not assignable to `StageStatus`.

- [ ] **Step 3: Add the union member and let the compiler list the work**

In `src/smx-web/src/api/types.ts`, add to the `StageStatus` union (after `'awaiting-VP'`):

```ts
  | 'awaiting-budget'
```

Run `npx tsc --noEmit` and fix each error it reports. The expected set is:

`stages.ts` — add to `PARKED`:

```ts
  'awaiting-budget': true,
```

`stages.ts` — add a `case 'awaiting-budget':` beside the other parks in `foldStatus`'s and
`stageIcon`'s switches, taking the same branch as `awaiting-RE`.

`blocking.ts` — add to `AWAITED`:

```ts
  'awaiting-budget': "the day's spend ceiling",
```

and a branch in `whatsBlocking`, placed **before** the person-parks branch, that surfaces the
dispatcher-written `error` verbatim (it already contains the spent and ceiling figures).

> `awaiting-budget` should **not** be added to `AWAITING_STATES` in `types.ts`. That constant
> means "a park whose dispatcher-written error is an instruction to a person", and this one is a
> statement of fact about money, not an instruction. It is the same distinction that keeps
> `awaiting-VP` out.

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd src/smx-web && npx tsc --noEmit && npx vitest run
```

Expected: no type errors; all tests pass.

- [ ] **Step 5: Read NextAction before changing it**

Run: `cat src/smx-web/src/components/shell/NextAction.tsx`

Its props and the shape it receives from `whatsBlocking` decide how the readout is composed —
write the JSX against what is actually there rather than against an assumed shape.

- [ ] **Step 6: Show the number in NextAction**

When the block is a spend park, fetch `GET /spend` and render today's spend against the ceiling
beneath the block text, in the existing type scale (the 12px floor applies; `--t-micro` is
retired).

Three constraints, each with a reason:

1. **No button.** Raising the ceiling is a config change, and an in-app "spend more money" control
   would be a rubber stamp on the one guardrail the company asked for. State what unparks it
   instead: the ceiling resets at 00:00 UTC, or an operator raises `SPEND_CEILING_USD_PER_DAY`.
2. **Guard the payload.** A malformed or failed `/spend` response renders as "today's spend is
   unavailable" — never as `$0.00`. `Intl.NumberFormat` renders `null` as `0.00`, which would tell
   the operator nothing was spent on the screen that exists because too much was.
3. **The park text comes from the record, not from the fetch.** The stage's `error` already carries
   the figures the gate saw. Render that as the block text and treat `/spend` as the live
   supplement, so a failed fetch degrades the detail rather than the explanation.

- [ ] **Step 7: Verify**

```bash
cd src/smx-web && npm run build && npx vitest run
```

Expected: build clean, tests pass.

- [ ] **Step 8: Commit**

```bash
git add src/smx-web/src
git commit -m "feat(web): a spend park that reads as a park and says what it cost"
```

---

### Task 6: Per-environment TPM capacity

The quota is a **blast-radius limiter, not the ceiling** — at the capacity the pipeline needs to
function, the arithmetic maximum is far above $1000/day. Set the lowest workable value and record
what each one implies, so the next person to raise one can see what they are raising.

**Files:**
- Modify: `infra/modules/ai.bicep`, `infra/single-rg/modules/ai.bicep`
- Modify: `infra/main.bicep`, `infra/single-rg/main.bicep`

- [ ] **Step 1: Record the implied maximum beside each capacity**

In both `ai.bicep` twins, extend each capacity parameter's `@description` with the arithmetic:
`capacity × 1000 TPM × 1440 min × $/MTok = $/day worst case (all output tokens)`. For Claude at
$25/MTok output that is `capacity × 36 $/day`.

This is documentation with teeth: the repo has already had an `embeddingCapacity` reverted in
Bicep by someone who could not see what the number meant.

- [ ] **Step 2: Plumb the parameters through both main.bicep twins**

The capacities are currently module-level defaults that `main.bicep` never passes, so there is no
per-environment quota at all today. Add parameters to `main.bicep` and forward them to the `ai`
module invocation, following the `env == 'prod' ? … : …` idiom:

```bicep
@description('Claude deployment capacity (thousands of TPM). See the spend-ceiling design: this is a blast-radius limiter, not the dollar ceiling — the ceiling is SPEND_CEILING_USD_PER_DAY, enforced in the backend.')
param claudeCapacity int = env == 'prod' ? 200 : 50

@description('gpt-5-mini deployment capacity (thousands of TPM). The regional limit is 1000.')
param gpt5MiniCapacity int = env == 'prod' ? 800 : 400
```

and in the `ai` module block:

```bicep
    claudeCapacity: claudeCapacity
    gpt5MiniCapacity: gpt5MiniCapacity
```

> These values are a **starting point that must be validated against a real run before prod**.
> The evidence the repo has is for `gpt-5-mini`: 1 failed every turn, 200 still 429'd 2 of 8
> regulatory children, 800 works. Claude has never been deployed here, so its numbers are
> inference from the same request shapes, not measurement. Deploy dev, run one full pipeline, and
> raise on observed 429s rather than guessing upward in advance.

- [ ] **Step 3: Verify both variants compile**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: both silent.

- [ ] **Step 4: Commit**

```bash
git add infra/
git commit -m "infra: per-env model capacity, with the dollars each one implies written down"
```

---

### Task 7: The budget runbook

**Files:**
- Create: `docs/runbooks/cost-management-budget.md`

- [ ] **Step 1: Write the runbook**

Create `docs/runbooks/cost-management-budget.md` containing:

- **What it is and is not.** An alarm, not a block. Cost Management has **no daily reset period**
  (Monthly / Quarterly / Annually only) and evaluates billing data that lags 8–24h. The daily
  ceiling is `SPEND_CEILING_USD_PER_DAY` in the backend; this budget is the backstop that catches
  the case where the meter's model of the world is wrong.
- **Portal steps.** Azure portal → Subscriptions → *SecurityMatters* → Cost Management → Budgets →
  **+ Add**. Scope: subscription. Period **Monthly**. Amount **$6,000** for dev, **$30,000** for
  prod (= the daily ceiling × 30). Alerts at 50% / 80% / 100% of *actual* cost.
- **The filter, and the trap.** Filter on **Service name = `Foundry Models`** (plus
  `Azure Cognitive Search` if search spend should be included). **"Cognitive Services" is not an
  available value on this subscription.** Claude on Foundry bills through the **Azure Marketplace**,
  so once `deployClaude=true` its spend may land under a Marketplace charge type outside that
  filter — **re-verify the filter against real Claude spend before trusting this budget**. A budget
  that silently excludes the most expensive model is worse than no budget.
- **Verification.** After the first Claude-serving day, open Cost analysis, group by *Service name*
  and by *Charge type*, and confirm the Claude spend appears inside the budget's filter. Record the
  date checked in this runbook.
- **What it does not cover.** ACA, Cosmos RU, App Gateway, and the `regsync` Function App's monthly
  embedding load.

- [ ] **Step 2: Commit**

```bash
git add docs/runbooks/cost-management-budget.md
git commit -m "docs: the budget alert, and the filter that may not see Claude"
```

---

### Task 8: Full verification

- [ ] **Step 1: Backend**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```

Expected: clean build, all tests pass.

- [ ] **Step 2: Frontend**

```bash
cd src/smx-web && npx tsc --noEmit && npm run build && npx vitest run
```

Expected: no type errors, clean build, all tests pass.

- [ ] **Step 3: Infra**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: both silent.

- [ ] **Step 4: Push**

```bash
git push
```

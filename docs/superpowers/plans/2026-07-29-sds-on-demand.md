# SDS On-Demand Acquisition Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn SDS acquisition from a weekly cron with a terminal human-fallback state into a capability any actor — operator, agent, or timer — can invoke the moment a sheet is missing.

**Architecture:** `regsync` keeps sourcing, egress and ingestion (it owns the only NAT'd subnet and the corpus write grants); the backend calls it over HTTP via `SdsAcquisitionClient`, modelled on the existing `SearchProxyClient`. The domain allowlist stops gating egress and becomes a priority hint in Cosmos; content validation (CAS-in-document + ≥10 GHS sections) carries correctness. `awaiting_operator` is deleted and exponential backoff replaces the retry cap, so nothing is ever terminal.

**Tech Stack:** .NET 8 isolated-worker Azure Functions, xUnit, Cosmos DB (NoSQL), Azure AI Search, ADLS Gen2, React + Vite + TypeScript, Bicep.

**Spec:** [`docs/superpowers/specs/2026-07-29-sds-on-demand-design.md`](../specs/2026-07-29-sds-on-demand-design.md)

---

## File Structure

**Phase 1 — lifecycle (`src/Smx.Functions/Sds/`)**
| File | Responsibility |
|---|---|
| `Domain/SdsDomain.cs` (modify) | `SdsStatus` loses `AwaitingOperator`; `MasterListEntry` gains `NextAttemptUtc` |
| `Data/MasterListRepo.cs` (modify) | Backoff scheduling and the `IsDue` predicate |
| `Data/BackoffSchedule.cs` (create) | Pure arithmetic — the delay for attempt N. Isolated so it is testable without a store |
| `Triggers/MigrateParkedEntries.cs` (create) | One-shot idempotent migration off `awaiting_operator` |
| `Triggers/SdsSweep.cs` (modify) | Bounded parallelism; a shared sweep core the manual trigger reuses |

**Phase 2 — sourcing**
| File | Responsibility |
|---|---|
| `Sourcing/NatEgressClient.cs` (modify) | Drop the allowlist gate; keep https/size/timeout/denylist rails |
| `Ingestion/SdsValidator.cs` (modify) | Drop the domain check; content only |
| `Sourcing/ISdsWebSearch.cs` (create) | The search abstraction — one method, so a fake is trivial |
| `Sourcing/BraveSdsWebSearch.cs` (create) | Live implementation |
| `Sourcing/DryRunSdsWebSearch.cs` (create) | No-egress implementation for local/test |
| `Sourcing/WebDiscoveryStrategy.cs` (create) | Query composition + PDF-URL filtering |
| `Config/SupplierStore.cs` (create) | Cosmos-backed allowlist with bundled-file seeding |

**Phase 3 — acquisition API**
| File | Responsibility |
|---|---|
| `Sds/Acquisition/SdsAcquirer.cs` (create) | The one code path all three triggers share |
| `Triggers/EnsureSds.cs` (create) | `POST /api/sds/ensure` |
| `Triggers/RunSdsSync.cs` (create) | `POST /api/sds/sync` |

**Phase 4 — consumers**
| File | Responsibility |
|---|---|
| `src/Smx.Infrastructure/Sds/SdsAcquisitionClient.cs` (create) | Backend → regsync HTTP client |
| `src/Smx.Backend/Agents/ToolBox.cs` (modify) | `ensure_sds` on Regulatory + chat surfaces |
| `src/Smx.Backend/Api/DecisionEndpoints.cs` (modify) | Order-gate predicate |
| `src/Smx.Backend/Api/KnowledgeEndpoints.cs` (modify) | Delete the review endpoint |
| `src/smx-web/src/routes/MsdsRegistry.tsx` (modify) | "Fetch now" replaces "Review" |

---

## Phase 1 — Lifecycle: nothing is terminal

### Task 1: Backoff arithmetic

**Files:**
- Create: `src/Smx.Functions/Sds/Data/BackoffSchedule.cs`
- Test: `src/Smx.Functions.Tests/BackoffScheduleTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Functions.Sds.Data;

namespace Smx.Functions.Tests;

public class BackoffScheduleTests
{
    // 1, 2, 4, 8, 16, then pinned at 32. The cap is what makes this a schedule rather than an
    // abandonment: a substance whose supplier is down for a year still gets a monthly attempt.
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 8)]
    [InlineData(5, 16)]
    [InlineData(6, 32)]
    [InlineData(7, 32)]
    [InlineData(40, 32)]
    public void DelayDoublesThenPinsAtTheCap(int attemptCount, int expectedDays)
        => Assert.Equal(expectedDays, BackoffSchedule.DelayDays(attemptCount));

    // Attempt 0 is not a real input, but a caller that increments in the wrong order must not get
    // a negative or zero delay and retry in a hot loop.
    [Fact]
    public void ADelayIsNeverLessThanADay()
        => Assert.Equal(1, BackoffSchedule.DelayDays(0));

    [Fact]
    public void NextAttemptIsTheDelayAfterTheLastAttempt()
    {
        var next = BackoffSchedule.NextAttemptUtc(DateTimeOffset.Parse("2026-07-29T03:00:00Z"), 3);
        Assert.Equal(DateTimeOffset.Parse("2026-08-02T03:00:00Z"), next);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter BackoffScheduleTests`
Expected: FAIL — `BackoffSchedule` does not exist (build error CS0246).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace Smx.Functions.Sds.Data;

/// The retry cadence for a substance whose sheet could not be fetched. Pure arithmetic, deliberately
/// separate from MasterListRepo so it can be tested without a store.
///
/// This replaces SDS_RETRY_CAP, which was not a cadence but an ending: three failures moved an entry to
/// `awaiting_operator`, a status IsDue never returned, and on 2026-07-29 that had silently consumed 40 of
/// 53 substances. Backoff means a dead supplier stops being hammered WITHOUT the system ever giving up.
public static class BackoffSchedule
{
    public const int MaxDelayDays = 32;

    public static int DelayDays(int attemptCount)
    {
        if (attemptCount <= 1) return 1;
        if (attemptCount >= 6) return MaxDelayDays;
        return 1 << (attemptCount - 1);
    }

    public static DateTimeOffset NextAttemptUtc(DateTimeOffset lastAttemptUtc, int attemptCount)
        => lastAttemptUtc.AddDays(DelayDays(attemptCount));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Functions.sln --filter BackoffScheduleTests`
Expected: PASS, 10 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Functions/Sds/Data/BackoffSchedule.cs src/Smx.Functions.Tests/BackoffScheduleTests.cs
git commit -m "feat(sds): a retry cadence, in place of an ending"
```

---

### Task 2: `MasterListEntry.NextAttemptUtc` and the new `IsDue`

**Files:**
- Modify: `src/Smx.Functions/Sds/Domain/SdsDomain.cs`
- Modify: `src/Smx.Functions/Sds/Data/MasterListRepo.cs`
- Test: `src/Smx.Functions.Tests/MasterListRepoTests.cs`

- [ ] **Step 1: Write the failing tests** (append to the existing class)

```csharp
    [Fact]
    public async Task AFailedEntryIsNotDueUntilItsBackoffElapses()
    {
        var repo = new MasterListRepo(new InMemoryMasterListStore());
        await repo.AppendAsync("Zr", "TMHD complex", "18865-74-2", null, "op", "2026-07-01T00:00:00Z", default);
        var entry = (await repo.GetAsync("Zr", "TMHD complex", default))!;

        await repo.RecordFailureAsync(entry, "2026-07-01T00:00:00Z", default);   // attempt 1 -> +1 day

        Assert.Empty(await repo.QueryDueAsync(90, "2026-07-01T12:00:00Z", default));
        Assert.Single(await repo.QueryDueAsync(90, "2026-07-02T00:00:00Z", default));
    }

    // The regression that motivated the whole change: no number of failures may ever remove an entry
    // from the due set permanently.
    [Fact]
    public async Task NoNumberOfFailuresMakesAnEntryPermanentlyUndue()
    {
        var repo = new MasterListRepo(new InMemoryMasterListStore());
        await repo.AppendAsync("Y", "Fluoride", "13709-49-4", null, "op", "2026-01-01T00:00:00Z", default);

        var now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        for (var i = 0; i < 25; i++)
        {
            var entry = (await repo.GetAsync("Y", "Fluoride", default))!;
            await repo.RecordFailureAsync(entry, now.ToString("O"), default);
            now = now.AddDays(BackoffSchedule.MaxDelayDays + 1);
            Assert.Single(await repo.QueryDueAsync(90, now.ToString("O"), default));
        }

        var final = (await repo.GetAsync("Y", "Fluoride", default))!;
        Assert.Equal(SdsStatus.Failed, final.Status);
        Assert.Equal(25, final.AttemptCount);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter MasterListRepoTests`
Expected: FAIL — `RecordFailureAsync` still takes a `retryCap` argument; `QueryDueAsync` still takes one.

- [ ] **Step 3: Update the domain record**

In `src/Smx.Functions/Sds/Domain/SdsDomain.cs`, delete the `AwaitingOperator` constant and add the field:

```csharp
public static class SdsStatus
{
    public const string Pending = "pending";
    public const string Fetched = "fetched";
    public const string Failed = "failed";
    // AwaitingOperator is deliberately gone. It was the only status IsDue never returned, which made it
    // a permanent park with no code path out — see the 2026-07-29 spec. Migration maps it to Failed.
}

public sealed record MasterListEntry(
    string Id, string Element, string Form, string Cas, string? SubstrateClass,
    string Status, string AddedBy, string AddedUtc, string? LastAttemptUtc, int AttemptCount,
    string? NextAttemptUtc = null);
```

- [ ] **Step 4: Rewrite the repo's scheduling**

Replace `QueryDueAsync`, `IsDue` and `RecordFailureAsync` in `src/Smx.Functions/Sds/Data/MasterListRepo.cs`:

```csharp
    public async Task<IReadOnlyList<MasterListEntry>> QueryDueAsync(
        int recheckDays, string nowUtc, CancellationToken ct)
    {
        var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);
        var all = await _store.ListAllAsync(ct);
        return all.Where(e => IsDue(e, recheckDays, now)).ToList();
    }

    private static bool IsDue(MasterListEntry e, int recheckDays, DateTimeOffset now) => e.Status switch
    {
        SdsStatus.Pending => true,
        // No cap. A failed entry is always due again eventually — the only question is when.
        SdsStatus.Failed => e.NextAttemptUtc is null
            || DateTimeOffset.Parse(e.NextAttemptUtc, CultureInfo.InvariantCulture) <= now,
        SdsStatus.Fetched => e.LastAttemptUtc is not null
            && DateTimeOffset.Parse(e.LastAttemptUtc, CultureInfo.InvariantCulture).AddDays(recheckDays) <= now,
        _ => true,   // an unknown status is a bug, and retrying is the safe direction
    };

    public Task RecordFailureAsync(MasterListEntry e, string nowUtc, CancellationToken ct)
    {
        var attempts = e.AttemptCount + 1;
        var last = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture);
        return _store.UpsertAsync(e with
        {
            Status = SdsStatus.Failed,
            AttemptCount = attempts,
            LastAttemptUtc = nowUtc,
            NextAttemptUtc = BackoffSchedule.NextAttemptUtc(last, attempts).ToString("O"),
        }, ct);
    }
```

`MarkFetchedAsync` additionally clears the stamp: `e with { Status = SdsStatus.Fetched, LastAttemptUtc = nowUtc, NextAttemptUtc = null }`.

- [ ] **Step 5: Fix the two call sites**

`SdsSweep.RunSweepAsync` — `QueryDueAsync(_opts.RevisionRecheckDays, nowUtc, ct)` and `RecordFailureAsync(entry, nowUtc, ct)` (drop `_opts.RetryCap` from both). Delete `RetryCap` from `SdsOptions` and its `SDS_RETRY_CAP` binding.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test src/Smx.Functions.sln`
Expected: PASS. `SdsSweepTests` asserting the old cap behaviour must be updated to assert backoff instead — a test asserting an entry becomes permanently undue is now asserting the bug.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat(sds): a failed substance is always due again, eventually"
```

---

### Task 3: Migrate the 40 parked rows

**Files:**
- Create: `src/Smx.Functions/Sds/Triggers/MigrateParkedEntries.cs`
- Test: `src/Smx.Functions.Tests/MigrateParkedEntriesTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Tests.Fakes;

namespace Smx.Functions.Tests;

public class MigrateParkedEntriesTests
{
    private static MasterListEntry Parked(string element) => new(
        DedupKey.ForMasterList(element, "TMHD complex"), element, "TMHD complex", "1-1-1", null,
        "awaiting_operator", "operator", "2026-07-01T00:00:00Z", "2026-07-27T03:00:00Z", 3);

    [Fact]
    public async Task ParkedEntriesBecomeFailedAndImmediatelyDue()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(Parked("Zr"), default);
        await store.UpsertAsync(Parked("Ce"), default);
        var repo = new MasterListRepo(store);

        var moved = await ParkedEntryMigration.RunAsync(repo, store, "2026-07-29T00:00:00Z", default);

        Assert.Equal(2, moved);
        Assert.Equal(2, (await repo.QueryDueAsync(90, "2026-07-29T00:00:01Z", default)).Count);
    }

    [Fact]
    public async Task AttemptHistoryIsPreservedNotReset()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(Parked("Zr"), default);
        var repo = new MasterListRepo(store);

        await ParkedEntryMigration.RunAsync(repo, store, "2026-07-29T00:00:00Z", default);

        var e = (await repo.GetAsync("Zr", "TMHD complex", default))!;
        Assert.Equal(SdsStatus.Failed, e.Status);
        Assert.Equal(3, e.AttemptCount);      // the record of what was tried survives the migration
    }

    [Fact]
    public async Task RunningTwiceMovesNothingTheSecondTime()
    {
        var store = new InMemoryMasterListStore();
        await store.UpsertAsync(Parked("Zr"), default);
        var repo = new MasterListRepo(store);

        Assert.Equal(1, await ParkedEntryMigration.RunAsync(repo, store, "2026-07-29T00:00:00Z", default));
        Assert.Equal(0, await ParkedEntryMigration.RunAsync(repo, store, "2026-07-29T00:00:00Z", default));
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter MigrateParkedEntriesTests`
Expected: FAIL — `ParkedEntryMigration` does not exist.

- [ ] **Step 3: Implement**

```csharp
using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Triggers;

/// One-shot, idempotent, re-runnable: moves every entry parked in the deleted `awaiting_operator`
/// status to `failed` with an immediate next attempt. On 2026-07-29 that is 40 of 53 substances in dev.
/// AttemptCount is preserved deliberately — it is the record of what was already tried, and resetting it
/// would erase the evidence that these suppliers are hard.
public static class ParkedEntryMigration
{
    public const string DeletedStatus = "awaiting_operator";

    public static async Task<int> RunAsync(
        MasterListRepo repo, IMasterListStore store, string nowUtc, CancellationToken ct)
    {
        var all = await store.ListAllAsync(ct);
        var parked = all.Where(e => e.Status == DeletedStatus).ToList();
        foreach (var e in parked)
            await store.UpsertAsync(e with { Status = SdsStatus.Failed, NextAttemptUtc = nowUtc }, ct);
        return parked.Count;
    }
}

public sealed class MigrateParkedEntries(MasterListRepo repo, IMasterListStore store)
{
    [Function("MigrateParkedEntries")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/migrate-parked")] HttpRequestData req)
    {
        var moved = await ParkedEntryMigration.RunAsync(
            repo, store, DateTimeOffset.UtcNow.ToString("O"), req.FunctionContext.CancellationToken);
        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new { moved });
        return resp;
    }
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln --filter MigrateParkedEntriesTests`
Expected: PASS, 3 tests.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): unpark the forty"
```

---

### Task 4: Sweep parallelism and a shared sweep core

**Files:**
- Modify: `src/Smx.Functions/Sds/Triggers/SdsSweep.cs`
- Test: `src/Smx.Functions.Tests/SdsSweepTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    // The 2026-07-16 sweep took 27 minutes against a 30-minute platform timeout because entries were
    // processed strictly serially behind 30-second fetch timeouts. Concurrency is not an optimisation
    // here; it is what keeps the run inside the host's budget.
    [Fact]
    public async Task EntriesAreProcessedConcurrently()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        for (var i = 0; i < 8; i++)
            await repo.AppendAsync($"E{i}", "oxide", $"{i}-00-0", null, "op", "2020-01-01T00:00:00Z", default);

        var inFlight = 0; var peak = 0;
        var egress = new DelegateEgressClient(async (_, ct) =>
        {
            var n = Interlocked.Increment(ref inFlight);
            InterlockedExtensions.Max(ref peak, n);
            await Task.Delay(50, ct);
            Interlocked.Decrement(ref inFlight);
            return null;
        });

        await NewSweep(repo, egress).RunSweepAsync("2026-07-29T00:00:00Z", default);

        Assert.True(peak > 1, $"expected concurrent processing, peak in-flight was {peak}");
    }

    // Concurrency must not weaken the per-entry isolation the serial loop had.
    [Fact]
    public async Task OneThrowingEntryDoesNotStopTheOthers()
    {
        var store = new InMemoryMasterListStore();
        var repo = new MasterListRepo(store);
        await repo.AppendAsync("Boom", "x", "1-1-1", null, "op", "2020-01-01T00:00:00Z", default);
        for (var i = 0; i < 4; i++)
            await repo.AppendAsync($"E{i}", "oxide", $"{i}-00-0", null, "op", "2020-01-01T00:00:00Z", default);

        var egress = new DelegateEgressClient((u, _) =>
            u.Host.Contains("boom") ? throw new InvalidOperationException("supplier exploded")
                                    : Task.FromResult<EgressResult?>(null));

        await NewSweep(repo, egress).RunSweepAsync("2026-07-29T00:00:00Z", default);

        var all = await store.ListAllAsync(default);
        Assert.All(all, e => Assert.Equal(SdsStatus.Failed, e.Status));   // every entry got its attempt
    }
```

Add the helper the tests need (`src/Smx.Functions.Tests/Fakes/InterlockedExtensions.cs`):

```csharp
namespace Smx.Functions.Tests.Fakes;

public static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter SdsSweepTests`
Expected: FAIL — `peak` is 1; the loop is serial.

- [ ] **Step 3: Implement bounded parallelism**

Replace the `foreach (var entry in due)` body in `RunSweepAsync` with a semaphore-bounded fan-out. Extract the existing per-entry body verbatim into `ProcessEntryAsync(MasterListEntry entry, string nowUtc, CancellationToken ct)` — including its two `catch` blocks, which are what keep one bad supplier from costing the batch — then:

```csharp
        var gate = new SemaphoreSlim(_opts.SweepConcurrency);
        var work = due.Select(async entry =>
        {
            await gate.WaitAsync(ct);
            try { await ProcessEntryAsync(entry, nowUtc, ct); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(work);
```

Add to `SdsOptions`: `public int SweepConcurrency { get; init; } = 5;` bound from `SDS_SWEEP_CONCURRENCY`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln --filter SdsSweepTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "perf(sds): the sweep stops running out the clock"
```

---

## Phase 2 — Sourcing: content decides, not provenance

### Task 5: Drop the domain gate

**Files:**
- Modify: `src/Smx.Functions/Sds/Sourcing/NatEgressClient.cs`
- Modify: `src/Smx.Functions/Sds/Ingestion/SdsValidator.cs`
- Modify: `src/Smx.Functions/Sds/Ingestion/IngestionPipeline.cs` (drop the `allowlistDomains` ctor arg)
- Test: `src/Smx.Functions.Tests/SdsValidatorTests.cs`, `src/Smx.Functions.Tests/NatEgressClientTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    // The load-bearing inversion of this whole change: a sheet from a host nobody curated is a valid
    // sheet if its content proves it. Provenance is recorded, not enforced.
    [Fact]
    public void AValidSheetFromAnUncuratedHostIsAccepted()
    {
        var result = new SdsValidator(10).Validate(TenSectionSheetFor("1310-73-2"), "1310-73-2");
        Assert.True(result.Ok, result.Reason);
    }

    [Fact]
    public void TheWrongSubstanceIsStillRejectedHoweverReputableTheHost()
    {
        var result = new SdsValidator(10).Validate(TenSectionSheetFor("7440-25-7"), "1310-73-2");
        Assert.False(result.Ok);
        Assert.Contains("1310-73-2", result.Reason);
    }
```

And for egress — the rails that survive:

```csharp
    [Fact]
    public async Task PlainHttpIsRefused()
        => Assert.Null(await Client().FetchAsync(new Uri("http://example.com/sds.pdf"), default));

    [Fact]
    public async Task ADenylistedHostIsRefused()
        => Assert.Null(await Client(deny: ["tarpit.example"]).FetchAsync(
            new Uri("https://tarpit.example/sds.pdf"), default));

    [Fact]
    public async Task AnUncuratedHostIsFetched()
    {
        var client = Client(respondWith: PdfBytes());
        Assert.NotNull(await client.FetchAsync(new Uri("https://never-curated.example/sds.pdf"), default));
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/Smx.Functions.sln --filter "SdsValidatorTests|NatEgressClientTests"`
Expected: FAIL — `Validate` still takes `sourceDomain` and `allowlistDomains`; egress blocks the uncurated host.

- [ ] **Step 3: Implement**

`SdsValidator.Validate(string text, string requestedCas)` — delete the domain parameters and the domain check entirely. Keep the GHS-section count and the CAS-presence check unchanged.

`NatEgressClient` — delete the `_allowlistDomains` field and its check. Add, before the request:

```csharp
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        { _log.LogWarning("Egress refused: {Url} is not https", url); return null; }

        var host = url.Host.ToLowerInvariant();
        if (_denylist.Any(d => host == d || host.EndsWith("." + d)))
        { _log.LogWarning("Egress refused: {Host} is denylisted", host); return null; }
```

Add `public IReadOnlyList<string> Denylist { get; init; } = [];` to `SdsOptions`, bound from a comma-separated `SDS_DENYLIST`.

Update `IngestionPipeline`'s constructor and `Program.cs` DI to stop passing `AllowlistProvider.Domains`.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test src/Smx.Functions.sln`
Expected: PASS. Existing validator tests asserting domain rejection must be deleted — they assert the behaviour being removed.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): the document decides, not the domain"
```

---

### Task 6: The web-search abstraction

**Files:**
- Create: `src/Smx.Functions/Sds/Sourcing/ISdsWebSearch.cs`, `BraveSdsWebSearch.cs`, `DryRunSdsWebSearch.cs`
- Test: `src/Smx.Functions.Tests/BraveSdsWebSearchTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task ResultsAreParsedFromTheProviderPayload()
    {
        var handler = new StubHandler("""
            {"web":{"results":[
              {"url":"https://a.example/x.pdf","title":"Sodium hydroxide SDS"},
              {"url":"https://b.example/y","title":"Product page"}]}}
            """);
        var search = new BraveSdsWebSearch(new HttpClient(handler), "key", NullLogger<BraveSdsWebSearch>.Instance);

        var hits = await search.SearchAsync("1310-73-2 safety data sheet", 5, default);

        Assert.Equal(2, hits.Count);
        Assert.Equal("https://a.example/x.pdf", hits[0].Url.ToString());
    }

    // A search outage must degrade discovery, never fail the fetch: curated strategies still have work
    // to do, and an exception here would abort the whole ensure call.
    [Fact]
    public async Task AProviderFailureYieldsNoHitsRatherThanThrowing()
    {
        var search = new BraveSdsWebSearch(
            new HttpClient(new StubHandler("", HttpStatusCode.ServiceUnavailable)), "key",
            NullLogger<BraveSdsWebSearch>.Instance);

        Assert.Empty(await search.SearchAsync("anything", 5, default));
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter BraveSdsWebSearchTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Smx.Functions.Sds.Sourcing;

public sealed record WebHit(Uri Url, string Title);

/// SDS URL discovery. One method, so a fake is a lambda.
///
/// This is deliberately NOT routed through the Search Proxy. The proxy's k-anonymity exists to hide which
/// chemistry a live client project is evaluating; a query keyed by a CAS number from a public catalog
/// carries no project identity, so cover batches would quadruple volume against a 5,000/month cap and put
/// a second Function App in the critical path of every fetch, to protect information the request does not
/// contain. See D4 in the 2026-07-29 spec.
public interface ISdsWebSearch
{
    Task<IReadOnlyList<WebHit>> SearchAsync(string query, int maxResults, CancellationToken ct);
}
```

`BraveSdsWebSearch` posts to the Brave API with `X-Subscription-Token`, deserializes `web.results[].url/title`, and catches every exception into an empty list with a warning. `DryRunSdsWebSearch` returns `[]` and logs — used when `SDS_DRY_RUN=true` or no key is configured.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln --filter BraveSdsWebSearchTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): a way to look for a sheet nobody curated"
```

---

### Task 7: `WebDiscoveryStrategy`

**Files:**
- Create: `src/Smx.Functions/Sds/Sourcing/WebDiscoveryStrategy.cs`
- Test: `src/Smx.Functions.Tests/WebDiscoveryStrategyTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
public class WebDiscoveryStrategyTests
{
    private static SubstanceKey Key => new("Zr", "TMHD complex", "18865-74-2");

    [Fact]
    public async Task TheQueryCarriesTheCasAndFormAndNothingElse()
    {
        string? seen = null;
        var strat = new WebDiscoveryStrategy(new FakeSearch((q, _, _) => { seen = q; return []; }));

        await strat.ResolveAsync(Entry(), Key, NoFetch, default);

        Assert.Contains("18865-74-2", seen);
        Assert.Contains("safety data sheet", seen, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PdfResultsRankAheadOfPageResults()
    {
        var strat = new WebDiscoveryStrategy(new FakeSearch((_, _, _) =>
        [
            new(new Uri("https://a.example/page"), "SDS page"),
            new(new Uri("https://b.example/sheet.pdf"), "SDS pdf"),
        ]));

        var candidates = await strat.ResolveAsync(Entry(), Key, NoFetch, default);

        Assert.Equal("https://b.example/sheet.pdf", candidates[0].Url.ToString());
    }

    [Fact]
    public async Task TheSupplierIsTheHostBecauseNobodyCuratedAName()
    {
        var strat = new WebDiscoveryStrategy(new FakeSearch((_, _, _) =>
            [new(new Uri("https://chem.example/sheet.pdf"), "SDS")]));

        var candidates = await strat.ResolveAsync(Entry(), Key, NoFetch, default);

        Assert.Equal("chem.example", candidates[0].Supplier);
        Assert.Equal("chem.example", candidates[0].Domain);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter WebDiscoveryStrategyTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

```csharp
public sealed class WebDiscoveryStrategy(ISdsWebSearch search, int maxCandidates = 5) : ISourceStrategy
{
    public string Name => "webDiscovery";

    public async Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        // Chemistry only. There is no field here a project identity could travel in, and that is the
        // point: `ensure` is keyed by substance, never by project.
        var query = $"\"{key.Cas}\" {key.Element} {key.Form} safety data sheet SDS filetype:pdf";
        var hits = await search.SearchAsync(query, maxCandidates * 2, ct);

        return hits
            .OrderByDescending(h => h.Url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            .Take(maxCandidates)
            .Select(h => new SourceCandidate(h.Url.Host, h.Url.Host, h.Url))
            .ToList();
    }
}
```

`WebDiscoveryStrategy` is registered as an `ISourceStrategy` but has no allowlist row, so `SourceResolver` must run it once after the curated walk rather than per-entry. Change `SourceResolver.ResolveAsync` to append `await webDiscovery.ResolveAsync(SyntheticEntry, key, fetch, ct)` after the loop, and only when the curated walk produced nothing — discovery is a fallback, not an addition.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln --filter "WebDiscoveryStrategyTests|SourceResolverTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): find the sheet nobody curated"
```

---

### Task 8: Suppliers become runtime data

**Files:**
- Create: `src/Smx.Functions/Sds/Config/SupplierStore.cs`
- Modify: `src/Smx.Functions/Program.cs`
- Test: `src/Smx.Functions.Tests/SupplierStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task AnEmptyContainerIsSeededFromTheBundledFile()
    {
        var store = new InMemorySupplierStore();
        var provider = await SupplierStore.LoadAsync(store, BundledJson, default);

        Assert.Equal(3, provider.Ordered.Count);
        Assert.Equal(3, (await store.ListAllAsync(default)).Count);   // seeding persisted
    }

    [Fact]
    public async Task StoredSuppliersWinOverTheBundledFile()
    {
        var store = new InMemorySupplierStore();
        await store.UpsertAsync(new AllowlistEntry("Operator Added", "new.example", 1, "casTemplate",
            "https://new.example/{cas}.pdf", null, null), default);

        var provider = await SupplierStore.LoadAsync(store, BundledJson, default);

        Assert.Equal("Operator Added", provider.Ordered[0].Supplier);   // priority 1 sorts first
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter SupplierStoreTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

`ISupplierStore` (`GetAsync`/`UpsertAsync`/`ListAllAsync`) + `CosmosSupplierStore` over a new `sds-suppliers` container (partition key `/domain`), following `CosmosMasterListStore` exactly. `SupplierStore.LoadAsync` lists; if empty, parses the bundled JSON, upserts each entry, and returns an `AllowlistProvider` over the result.

Add `POST /api/sds/suppliers` accepting an `AllowlistEntry` so an operator can add one without a deploy — this is the gate removal, and without the endpoint the Cosmos move buys nothing.

Change `Program.cs` to resolve `AllowlistProvider` from the store rather than `FromFile`.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln --filter SupplierStoreTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): a supplier is data, not a deploy"
```

---

## Phase 3 — Acquisition API

### Task 9: `SdsAcquirer` — the one shared code path

**Files:**
- Create: `src/Smx.Functions/Sds/Acquisition/SdsAcquirer.cs`
- Test: `src/Smx.Functions.Tests/SdsAcquirerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    // The cache hit must be free. Asserting only the return value would pass even if it fetched
    // anyway — so assert that egress was never touched.
    [Fact]
    public async Task AnExistingSheetReturnsWithoutAnyEgress()
    {
        var egress = new CountingEgressClient();
        var acquirer = NewAcquirer(egress, existingSheetFor: "1310-73-2");

        var result = await acquirer.EnsureAsync(new("Na", "hydroxide", "1310-73-2"), force: false, default);

        Assert.Equal(EnsureStatus.AlreadyHad, result.Status);
        Assert.Equal(0, egress.Calls);
    }

    [Fact]
    public async Task ForceRefetchesEvenWhenASheetExists()
    {
        var egress = new CountingEgressClient(respondWith: ValidSheet("1310-73-2"));
        var acquirer = NewAcquirer(egress, existingSheetFor: "1310-73-2");

        var result = await acquirer.EnsureAsync(new("Na", "hydroxide", "1310-73-2"), force: true, default);

        Assert.Equal(EnsureStatus.Fetched, result.Status);
        Assert.True(egress.Calls > 0);
    }

    [Fact]
    public async Task AnUnknownSubstanceIsAppendedToTheLedger()
    {
        var store = new InMemoryMasterListStore();
        var acquirer = NewAcquirer(new CountingEgressClient(), masterList: store);

        await acquirer.EnsureAsync(new("Xx", "novel form", "99-99-9"), force: false, default);

        Assert.Single(await store.ListAllAsync(default));
    }

    // When it cannot be had, the caller is told what was tried — not merely that it failed.
    [Fact]
    public async Task AnUnavailableSheetReportsEveryCandidateAndWhyItFailed()
    {
        var acquirer = NewAcquirer(new CountingEgressClient(respondWith: NotAnSds()));

        var result = await acquirer.EnsureAsync(new("Zr", "TMHD complex", "18865-74-2"), false, default);

        Assert.Equal(EnsureStatus.Unavailable, result.Status);
        Assert.NotEmpty(result.Attempted);
        Assert.All(result.Attempted, a => Assert.False(string.IsNullOrWhiteSpace(a.Outcome)));
    }

    [Fact]
    public async Task AFailedEnsureLeavesTheEntryRetryableNotParked()
    {
        var store = new InMemoryMasterListStore();
        var acquirer = NewAcquirer(new CountingEgressClient(respondWith: NotAnSds()), masterList: store);

        await acquirer.EnsureAsync(new("Zr", "TMHD complex", "18865-74-2"), false, default);

        var entry = (await store.ListAllAsync(default)).Single();
        Assert.Equal(SdsStatus.Failed, entry.Status);
        Assert.NotNull(entry.NextAttemptUtc);
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test src/Smx.Functions.sln --filter SdsAcquirerTests`
Expected: FAIL — `SdsAcquirer` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Smx.Functions.Sds.Acquisition;

public static class EnsureStatus
{
    public const string AlreadyHad = "already-had";
    public const string Fetched = "fetched";
    public const string Unavailable = "unavailable";
}

public sealed record AttemptRecord(string Url, string Supplier, string Outcome);

public sealed record EnsureResult(
    string Status, string? RegistryId, string? Supplier, string? RevisionDate,
    string? Reason, IReadOnlyList<AttemptRecord> Attempted);
```

`SdsAcquirer.EnsureAsync(SubstanceKey key, bool force, CancellationToken ct)`:
1. Unless `force`, ask `RegistryRepo` for a current sheet by CAS; if found return `AlreadyHad` with **no** resolver or egress call.
2. `MasterListRepo.AppendAsync(...)` with `addedBy: "ensure"` — ignoring the `false` return that means it was already there.
3. Resolve candidates, then for each in order: fetch → ingest → on `Ok` mark fetched and return `Fetched`; otherwise append an `AttemptRecord` carrying the rejection reason and continue.
4. Exhausted → `RecordFailureAsync` and return `Unavailable` with the full `Attempted` list.

Wrap the whole candidate loop in a `CancellationTokenSource` linked to `ct` with `CancelAfter(_opts.EnsureBudgetSeconds)` (default 45).

Then rewrite `SdsSweep.ProcessEntryAsync` to call `EnsureAsync` so the timer, the manual sync and the agent all run the same code.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): one acquisition path, three doors"
```

---

### Task 10: `POST /api/sds/ensure` and `POST /api/sds/sync`

**Files:**
- Create: `src/Smx.Functions/Sds/Triggers/EnsureSds.cs`, `src/Smx.Functions/Sds/Triggers/RunSdsSync.cs`
- Test: `src/Smx.Functions.Tests/EnsureSdsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task ARequestWithoutACasIsRejected()
    {
        var resp = await NewTrigger().Run(Request(new { element = "Na", form = "hydroxide" }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task AnUnavailableSheetIsStillATwoHundred()
    {
        // "We tried and could not get it" is a successful answer to a question, not a server error.
        // A 5xx would make every agent tool call look like an outage.
        var resp = await NewTrigger(unavailable: true).Run(Request(new { cas = "18865-74-2" }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Functions.sln --filter EnsureSdsTests`
Expected: FAIL — trigger does not exist.

- [ ] **Step 3: Implement**

`EnsureSds` — `POST sds/ensure`, deserialize `{cas, element, form, force}`, 400 on a missing/malformed CAS, else call `SdsAcquirer.EnsureAsync` and return the `EnsureResult` as JSON with 200 regardless of outcome.

`RunSdsSync` — `POST sds/sync`, reads optional `{maxEntries, maxDurationSeconds}` (defaults 100 / 600), calls the shared sweep core bounded by both, returns `{examined, fetched, failed, remaining}`. Bounded because the 07-16 full sweep took 27 minutes against a 30-minute host timeout.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Functions.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(sds): fetch it now, or sweep it now"
```

---

## Phase 4 — Consumers

### Task 11: `SdsAcquisitionClient` in the backend

**Files:**
- Create: `src/Smx.Infrastructure/Sds/SdsAcquisitionClient.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/SdsAcquisitionClientTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task TheCasIsSentAndTheResultIsParsed()
    {
        var handler = new StubHandler("""{"status":"fetched","registryId":"r1","supplier":"Acme"}""");
        var client = new SdsAcquisitionClient(new HttpClient(handler) { BaseAddress = new("https://rs.example") },
            new FakeCredential(), "api://regsync", NullLogger<SdsAcquisitionClient>.Instance);

        var result = await client.EnsureAsync("1310-73-2", "Na", "hydroxide", default);

        Assert.Equal("fetched", result.Status);
        Assert.Contains("1310-73-2", handler.LastRequestBody);
    }

    // regsync being down must degrade the agent's turn, never end it.
    [Fact]
    public async Task ATransportFailureBecomesAnUnavailableResultNotAnException()
    {
        var client = new SdsAcquisitionClient(new HttpClient(new ThrowingHandler()) { BaseAddress = new("https://rs.example") },
            new FakeCredential(), "api://regsync", NullLogger<SdsAcquisitionClient>.Instance);

        var result = await client.EnsureAsync("1310-73-2", "Na", "hydroxide", default);

        Assert.Equal("unavailable", result.Status);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter SdsAcquisitionClientTests`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

Define the interface the consumers in Tasks 12 and 13 depend on, in `src/Smx.Domain/Tools/ISdsAcquisition.cs` (Domain, not Infrastructure — `ToolBox` and `PipelineRunner` must not reference the HTTP client directly):

```csharp
namespace Smx.Domain.Tools;

public sealed record SdsEnsureResult(
    string Status, string? RegistryId, string? Supplier, string? RevisionDate,
    string? Reason, IReadOnlyList<SdsAttempt> Attempted);

public sealed record SdsAttempt(string Url, string Supplier, string Outcome);

/// The backend's half of SDS acquisition. `regsync` does the fetching (it owns the only NAT'd subnet
/// and the corpus write grants); this is the line to it.
public interface ISdsAcquisition
{
    Task<SdsEnsureResult> EnsureAsync(string cas, string? element, string? form, CancellationToken ct);
    Task AppendAsync(string element, string form, string cas, CancellationToken ct);
}
```

`SdsAcquisitionClient : ISdsAcquisition` copies the shape of `src/Smx.Infrastructure/Search/SearchProxyClient.cs` exactly — same token acquisition, same audience handling, same logging. `EnsureAsync` catches every transport exception into `Status = "unavailable"` with the exception message as `Reason`; `AppendAsync` catches and logs, returning normally, because a ledger append must never fail a caller (Task 13 depends on this).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter SdsAcquisitionClientTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(backend): a line to the sheet fetcher"
```

---

### Task 12: `ensure_sds` on the Regulatory and chat surfaces

**Files:**
- Modify: `src/Smx.Backend/Agents/ToolBox.cs`
- Test: `src/Smx.Backend.Tests/ToolBoxTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void TheRegulatoryAgentCanFetchAMissingSheet()
        => Assert.Contains(NewToolBox().RegulatoryTools(), t => t.Name == "ensure_sds");

    // The rule that chat turns never trigger egress is deliberately bent for this one tool: an SDS fetch
    // keyed by CAS reveals no project, and "get me the sheet for X" is what an operator says out loud.
    [Fact]
    public void TheChatSurfaceCanFetchAMissingSheetToo()
        => Assert.Contains(NewToolBox().ReadToolsFor(Stages.Regulatory), t => t.Name == "ensure_sds");

    // Bending it for ensure_sds must not smuggle a general web tool into chat.
    [Fact]
    public void ChatStillHasNoWebSearch()
        => Assert.DoesNotContain(NewToolBox().ReadToolsFor(Stages.Discovery), t => t.Name == "search_web");
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter ToolBoxTests`
Expected: FAIL — no `ensure_sds` tool.

- [ ] **Step 3: Implement**

Add an `ISdsAcquisition` constructor dependency to `ToolBox` and a tool:

```csharp
    private AITool EnsureSds() => AIFunctionFactory.Create(
        EnsureSdsAsync, "ensure_sds",
        "Fetch and index the safety data sheet for a CAS the corpus does not have. Call this when " +
        "search_sds returns nothing for a substance you need hazard data for. Returns immediately with " +
        "what it tried if no sheet can be obtained — it never blocks and never parks the stage. " +
        "Takes a CAS and, if you know them, the element and form.");
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter ToolBoxTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(agents): an agent that needs a sheet can go and get it"
```

---

### Task 13: The ledger fills itself

**Files:**
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs`
- Test: `src/Smx.Backend.Tests/PipelineRunnerLedgerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task EveryDiscoveredCandidateIsAppendedToTheSdsLedger()
    {
        var acquisition = new RecordingSdsAcquisition();
        await RunDiscoveryStage(acquisition, candidates: [("Zr", "TMHD complex", "18865-74-2")]);
        Assert.Contains(acquisition.Appended, a => a.Cas == "18865-74-2");
    }

    // A ledger append is bookkeeping. It must never be able to fail a stage.
    [Fact]
    public async Task AnAppendFailureDoesNotFailTheStage()
    {
        var acquisition = new ThrowingSdsAcquisition();
        var result = await RunDiscoveryStage(acquisition, candidates: [("Zr", "TMHD", "1-1-1")]);
        Assert.True(result.Ok);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter PipelineRunnerLedgerTests`
Expected: FAIL — nothing appends.

- [ ] **Step 3: Implement**

After Discovery persists candidates and after Dosing persists selected markers, call `ISdsAcquisition.AppendAsync(element, form, cas)` per distinct CAS inside a `try/catch` that logs and swallows.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter PipelineRunnerLedgerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(pipeline): a substance in play is a substance we need a sheet for"
```

---

### Task 14: The signature goes, the gate stays

**Files:**
- Modify: `src/Smx.Domain/Records/MsdsRegistryDoc.cs`, `src/Smx.Backend/Api/KnowledgeEndpoints.cs`, `src/Smx.Backend/Api/DecisionEndpoints.cs`
- Test: `src/Smx.Backend.Tests/DecisionEndpointsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task AnOrderReleasesOnAValidatedSheetWithNoSignature()
    {
        await GivenSheetInCorpus(cas: "1310-73-2");            // fetched + indexed, never signed
        var resp = await _client.PostAsync($"/projects/{Id}/orders/1310-73-2", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // The gate itself survives: procurement still cannot run without a sheet.
    [Fact]
    public async Task AnOrderIsStillRefusedWithNoSheetAtAll()
    {
        var resp = await _client.PostAsync($"/projects/{Id}/orders/99-99-9", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, resp.StatusCode);
        Assert.Contains("no safety sheet", await resp.Content.ReadAsStringAsync());
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter DecisionEndpointsTests`
Expected: FAIL — the gate still requires `ReviewStatus == Reviewed`.

- [ ] **Step 3: Implement**

Delete `ReviewStatus`/`ReviewedAt` from `MsdsRegistryDoc` and the `MsdsReviewStatus` constants. Delete `POST /msds-registry/{cas}/review`. In `DecisionEndpoints`, replace the `msds?.ReviewStatus != Reviewed` check with a corpus lookup for a current indexed sheet, and reword the 422 to name the fix.

- [ ] **Step 4: Run the full suite**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS. Tests asserting the review requirement are asserting removed behaviour and must be deleted.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(decision): the gate asks for a sheet, not a signature"
```

---

### Task 15: The operator can act

**Files:**
- Modify: `src/smx-web/src/routes/MsdsRegistry.tsx`, `src/smx-web/src/routes/Documents.tsx`, `src/smx-web/src/routes/DocumentView.tsx`
- Test: `src/smx-web/src/routes/MsdsRegistry.test.tsx`

- [ ] **Step 1: Write the failing test**

```tsx
it('offers to fetch a sheet that is missing', async () => {
  renderWithRouter(<MsdsRegistry />)
  expect(await screen.findByRole('button', { name: /fetch now/i })).toBeInTheDocument()
})

it('no longer offers a review signature', async () => {
  renderWithRouter(<MsdsRegistry />)
  await screen.findByRole('button', { name: /fetch now/i })
  expect(screen.queryByRole('button', { name: /review/i })).not.toBeInTheDocument()
})
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd src/smx-web && npm test -- MsdsRegistry`
Expected: FAIL — no "Fetch now" button.

- [ ] **Step 3: Implement**

Replace the review action with a **Fetch now** button posting to a new backend passthrough `POST /msds/{cas}/fetch`; show a pending state while it runs and refresh the row on completion. On gap rows in `Documents.tsx` / `DocumentView.tsx`, render the same control and replace the "awaiting operator upload" subtitle with `next attempt <date>`. Add an upload control posting a PDF.

- [ ] **Step 4: Run to verify it passes**

Run: `cd src/smx-web && npm test`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(web): a missing sheet is now something the operator can do something about"
```

---

### Task 16: Infrastructure

**Files:**
- Modify: `infra/modules/functions.bicep`, `infra/single-rg/modules/functions.bicep`, `infra/modules/data.bicep`

- [ ] **Step 1: Make the changes**

- `sdsSweepCron` default `'0 0 3 * * 1'` → `'0 0 3 * * *'` (daily); delete `sdsRetryCap`/`SDS_RETRY_CAP`; add `SDS_SWEEP_CONCURRENCY`, `SDS_DENYLIST`, `SDS_ENSURE_BUDGET_SECONDS`, `SDS_SEARCH_API_KEY` (Key Vault reference to the existing search-key secret).
- Grant the `regsync` UAMI `get` on that secret.
- Add the `sds-suppliers` container (partition key `/domain`) alongside the other `sds-*` containers.
- **Both variants** — `infra/` and `infra/single-rg/` are twins; fixing one and not the other is the documented failure mode.

- [ ] **Step 2: Validate both compile**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```
Expected: no output, exit 0.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "infra(sds): daily sweep, no retry cap, suppliers container"
```

---

## Verification

- [ ] `dotnet build src/Smx.Functions.sln && dotnet test src/Smx.Functions.sln`
- [ ] `dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln`
- [ ] `cd src/smx-web && npm run build && npm test`
- [ ] `az bicep build` on both variants
- [ ] **The measure that counts:** deploy, `POST /api/sds/migrate-parked`, then `POST /api/sds/sync`, and report coverage against the **13-of-53 baseline**. A green test run is not evidence that coverage improved.

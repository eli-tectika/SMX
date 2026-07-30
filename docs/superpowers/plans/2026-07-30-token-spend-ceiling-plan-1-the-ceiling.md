# Token Spend Ceiling — Plan 1: the ceiling

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the backend starting a new pipeline stage once the day's model token spend has reached a configured dollar ceiling ($200 dev / $1000 prod).

**Architecture:** A `DelegatingChatClient` inserted into the one place every model client is built (`FoundryChatClientFactory.CreateAsync`) records input/output tokens per deployment into a Cosmos `spend` container, one document per (UTC day, deployment), incremented atomically. `PipelineRunner.ExecuteAsync` — the one place a run is opened — prices the day's tokens in code and refuses to start a stage over the ceiling, parking it as `awaiting-budget`. In-flight work is never interrupted. The gate **fails closed**: a spend total that cannot be read is not `$0`.

**Tech Stack:** .NET 8, xUnit, Microsoft.Extensions.AI 10.6.0 (`DelegatingChatClient`, `ChatResponse.Usage`), Azure Cosmos DB .NET SDK (`PatchOperation.Increment`), Bicep.

**Spec:** `docs/superpowers/specs/2026-07-30-token-spend-ceiling-design.md`

**Deliberately deferred to Plan 2** (so this is not read as dropped scope): per-stage/per-project cost attribution, the `GET /spend` read surface, the frontend rendering of `awaiting-budget`, embedding metering, and the per-environment TPM capacity plumbing. This plan ends with a working, deployable ceiling.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Spend/TokenPrices.cs` (create) | Pure pricing: deployment name → $/MTok, and token counts → dollars. No I/O, no clock. |
| `src/Smx.Domain/Spend/token-prices.json` (create) | The git-versioned price table, embedded as a resource. |
| `src/Smx.Domain/Spend/SpendDoc.cs` (create) | The (day, deployment) token-count document. |
| `src/Smx.Domain/Spend/ISpendStore.cs` (create) | Store interface: add tokens, read a day. |
| `src/Smx.Domain/Spend/ISpendMeter.cs` (create) | `RecordAsync` + `ReadAsync`; `SpendStatus` record. |
| `src/Smx.Domain/Spend/SpendMeter.cs` (create) | Prices a day's docs, compares against the ceiling. Takes `TimeProvider`. |
| `src/Smx.Infrastructure/CosmosSpendStore.cs` (create) | Atomic increment against the `spend` container. |
| `src/Smx.Infrastructure/BufferedSpendStore.cs` (create) | Holds failed writes for retry; leaves reads to propagate. |
| `src/Smx.Infrastructure/MeteredChatClient.cs` (create) | `DelegatingChatClient` that records `ChatResponse.Usage`. |
| `src/Smx.Infrastructure/FoundryChatClientFactory.cs` (modify) | Insert the decorator on both provider paths. |
| `src/Smx.Infrastructure/BackendOptions.cs` (modify) | `SpendCeilingUsdPerDay`, `SpendContainer`. |
| `src/Smx.Domain/Records/ProjectDoc.cs` (modify) | `StageStatus.AwaitingBudget`. |
| `src/Smx.Domain/Records/RunDoc.cs` (modify) | `RunOutcome.Blocked` (return-only signal, never persisted). |
| `src/Smx.Backend/Pipeline/PipelineRunner.cs` (modify) | The gate, at the top of `ExecuteAsync`. |
| `src/Smx.Backend/Program.cs` (modify) | DI wiring. |
| `infra/modules/data.bicep`, `infra/single-rg/modules/data.bicep` (modify) | The `spend` container. |
| `infra/modules/compute.bicep`, `infra/single-rg/modules/compute.bicep` (modify) | `SPEND_CEILING_USD_PER_DAY`. |
| `infra/main.bicep`, `infra/single-rg/main.bicep` (modify) | Per-env ceiling value. |

---

### Task 1: The pricing table

Pure arithmetic, no I/O. The critical property: an **unknown deployment name must throw**, never price at zero — a zero price is not a cheap model, it is no ceiling.

**Files:**
- Create: `src/Smx.Domain/Spend/TokenPrices.cs`
- Create: `src/Smx.Domain/Spend/token-prices.json`
- Modify: `src/Smx.Domain/Smx.Domain.csproj`
- Test: `src/Smx.Domain.Tests/TokenPricesTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Domain.Tests/TokenPricesTests.cs`:

```csharp
using Smx.Domain.Spend;

namespace Smx.Domain.Tests;

public class TokenPricesTests
{
    [Fact]
    public void Prices_input_and_output_at_different_rates()
    {
        var prices = TokenPrices.Load("""
            { "claude-opus-4-7": { "inputUsdPerMillion": 5.0, "outputUsdPerMillion": 25.0 } }
            """);

        // 1M in at $5 + 1M out at $25 = $30. Pricing both at the input rate would give $10,
        // which is the mistake that makes an output-heavy day look affordable.
        Assert.Equal(30.0m, prices.Cost("claude-opus-4-7", 1_000_000, 1_000_000));
    }

    [Fact]
    public void Prices_partial_millions_proportionally()
    {
        var prices = TokenPrices.Load("""
            { "claude-opus-4-7": { "inputUsdPerMillion": 5.0, "outputUsdPerMillion": 25.0 } }
            """);

        Assert.Equal(0.0075m, prices.Cost("claude-opus-4-7", 1_000, 100));
    }

    // The whole point of the type. An unpriced model must be LOUD: priced at zero it would
    // silently uncap the ceiling for exactly the model nobody remembered to price.
    [Fact]
    public void An_unknown_deployment_throws_rather_than_costing_nothing()
    {
        var prices = TokenPrices.Load("""
            { "claude-opus-4-7": { "inputUsdPerMillion": 5.0, "outputUsdPerMillion": 25.0 } }
            """);

        var e = Assert.Throws<UnknownDeploymentPriceException>(
            () => prices.Cost("claude-opus-5", 1_000, 100));
        Assert.Contains("claude-opus-5", e.Message);
    }

    // The shipped table must know every deployment the templates actually create.
    [Theory]
    [InlineData("claude-opus-4-7")]
    [InlineData("gpt-5-mini")]
    [InlineData("gpt-4o")]
    [InlineData("text-embedding-3-large")]
    public void The_shipped_table_prices_every_deployed_model(string deployment)
    {
        Assert.True(TokenPrices.Shipped.Cost(deployment, 1_000, 1_000) > 0m);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~TokenPricesTests`
Expected: FAIL — `The type or namespace name 'Spend' does not exist`.

- [ ] **Step 3: Write the price table**

Create `src/Smx.Domain/Spend/token-prices.json`:

```json
{
  "claude-opus-4-7": { "inputUsdPerMillion": 5.0, "outputUsdPerMillion": 25.0 },
  "gpt-5-mini": { "inputUsdPerMillion": 0.25, "outputUsdPerMillion": 2.0 },
  "gpt-4o": { "inputUsdPerMillion": 2.5, "outputUsdPerMillion": 10.0 },
  "text-embedding-3-large": { "inputUsdPerMillion": 0.13, "outputUsdPerMillion": 0.0 }
}
```

> **These rates must be verified against the Azure pricing page before this ships.** Claude Opus 4.7 at $5/$25 is authoritative (Foundry bills Claude at standard Anthropic rates). The three Azure-sold models are placeholders of the right order of magnitude and are *not* verified — an over-estimate is safe (the ceiling binds early), an under-estimate is not. Record the verification date in a comment when you confirm them.

- [ ] **Step 4: Write the implementation**

Create `src/Smx.Domain/Spend/TokenPrices.cs`:

```csharp
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smx.Domain.Spend;

/// Thrown when a deployment has no price. Deliberately fatal rather than defaulting to zero:
/// a zero price does not mean a free model, it means no ceiling at all for that model.
public sealed class UnknownDeploymentPriceException(string deployment)
    : Exception($"No token price is configured for deployment '{deployment}'. " +
                "Add it to src/Smx.Domain/Spend/token-prices.json — an unpriced model would " +
                "silently spend without counting against the daily ceiling.")
{
    public string Deployment { get; } = deployment;
}

public sealed record TokenPrice(
    [property: JsonPropertyName("inputUsdPerMillion")] decimal InputUsdPerMillion,
    [property: JsonPropertyName("outputUsdPerMillion")] decimal OutputUsdPerMillion);

/// Deployment name → price. Pure: no I/O, no clock, decimal money (never double — binary
/// floating point cannot represent 0.1, and a ceiling is a comparison against money).
public sealed class TokenPrices
{
    private readonly IReadOnlyDictionary<string, TokenPrice> _prices;

    private TokenPrices(IReadOnlyDictionary<string, TokenPrice> prices) => _prices = prices;

    public static TokenPrices Load(string json) =>
        new(JsonSerializer.Deserialize<Dictionary<string, TokenPrice>>(json)
            ?? throw new InvalidOperationException("token price table is empty"));

    /// The table shipped in the assembly. Lazy so a malformed table fails at first use with a
    /// readable error rather than inside a static constructor's TypeInitializationException.
    private static readonly Lazy<TokenPrices> _shipped = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Smx.Domain.Spend.token-prices.json")
            ?? throw new InvalidOperationException("token-prices.json is not embedded in Smx.Domain");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    });

    public static TokenPrices Shipped => _shipped.Value;

    public bool Knows(string deployment) => _prices.ContainsKey(deployment);

    public IEnumerable<string> Deployments => _prices.Keys;

    public decimal Cost(string deployment, long inputTokens, long outputTokens)
    {
        if (!_prices.TryGetValue(deployment, out var p))
            throw new UnknownDeploymentPriceException(deployment);
        return (inputTokens * p.InputUsdPerMillion + outputTokens * p.OutputUsdPerMillion) / 1_000_000m;
    }
}
```

- [ ] **Step 5: Embed the JSON**

In `src/Smx.Domain/Smx.Domain.csproj`, inside the existing top-level `<Project>` element, add:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Spend/token-prices.json" LogicalName="Smx.Domain.Spend.token-prices.json" />
  </ItemGroup>
```

The explicit `LogicalName` matters — the default resource name derives from the root namespace and folder and is easy to get subtly wrong, producing a null stream at runtime rather than a build error.

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~TokenPricesTests`
Expected: PASS, 6 tests.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Domain/Spend/ src/Smx.Domain/Smx.Domain.csproj src/Smx.Domain.Tests/TokenPricesTests.cs
git commit -m "feat(domain): price tokens in dollars, and refuse to price a model we do not know"
```

---

### Task 2: The spend document and store interface

One document per (UTC day, deployment). Both counters are `long`, which is what makes them
increment-safe in Cosmos — a per-model *map* inside one document would not be.

**Files:**
- Create: `src/Smx.Domain/Spend/SpendDoc.cs`
- Create: `src/Smx.Domain/Spend/ISpendStore.cs`
- Test: `src/Smx.Domain.Tests/SpendDocTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Domain.Tests/SpendDocTests.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Spend;

namespace Smx.Domain.Tests;

public class SpendDocTests
{
    [Fact]
    public void Id_pairs_the_day_with_the_deployment()
    {
        Assert.Equal("2026-07-30|claude-opus-4-7", SpendDoc.MakeId("2026-07-30", "claude-opus-4-7"));
    }

    // Round-trips through the SAME serializer options Cosmos is configured with, so the wire
    // names the patch operations below target ("/inputTokens") are the names actually written.
    [Fact]
    public void Round_trips_camelCase_through_the_cosmos_serializer_options()
    {
        var doc = new SpendDoc
        {
            Id = SpendDoc.MakeId("2026-07-30", "gpt-5-mini"),
            Day = "2026-07-30",
            Deployment = "gpt-5-mini",
            InputTokens = 12,
            OutputTokens = 34,
        };

        var json = JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"inputTokens\":12", json);
        Assert.Contains("\"outputTokens\":34", json);
        Assert.Contains("\"day\":\"2026-07-30\"", json);

        var back = JsonSerializer.Deserialize<SpendDoc>(json, Json.Options)!;
        Assert.Equal(12, back.InputTokens);
        Assert.Equal(34, back.OutputTokens);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~SpendDocTests`
Expected: FAIL — `SpendDoc` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/Spend/SpendDoc.cs`:

```csharp
namespace Smx.Domain.Spend;

/// One document per (UTC day, deployment), partitioned by /day.
///
/// TOKENS, not dollars, and one document per model rather than a map inside one document.
/// Both choices exist to make the write atomic: Cosmos `PatchOperation.Increment` works on a
/// numeric path, so two `long` counters can be incremented server-side with no read, no ETag
/// retry and no lost update under the regulatory fan-out's concurrency. A `Dictionary<string,
/// long>` keyed by model would need the key to already exist, and money as `decimal` cannot be
/// incremented by the patch API at all. Dollars are computed at read time from TokenPrices —
/// which also means a corrected price re-prices history rather than baking a mistake in.
public sealed class SpendDoc
{
    public string Id { get; set; } = "";
    /// The partition key: UTC date, "yyyy-MM-dd".
    public string Day { get; set; } = "";
    public string Deployment { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    public static string MakeId(string day, string deployment) => $"{day}|{deployment}";
}
```

Create `src/Smx.Domain/Spend/ISpendStore.cs`:

```csharp
namespace Smx.Domain.Spend;

public interface ISpendStore
{
    /// Adds tokens to (day, deployment). MUST be atomic against concurrent callers — the
    /// regulatory stage fans out REGULATORY_PARALLELISM ways and every child records here.
    Task AddAsync(string day, string deployment, long inputTokens, long outputTokens, CancellationToken ct = default);

    /// Every deployment's counters for one day. Throws if the day cannot be read — callers
    /// gate on this and MUST NOT read a failure as zero spend.
    Task<IReadOnlyList<SpendDoc>> ReadDayAsync(string day, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~SpendDocTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Spend/SpendDoc.cs src/Smx.Domain/Spend/ISpendStore.cs src/Smx.Domain.Tests/SpendDocTests.cs
git commit -m "feat(domain): a per-day, per-model token counter shaped for atomic increment"
```

---

### Task 3: The spend meter

Turns the day's token counts into dollars and answers "are we over?". Takes `TimeProvider` so
the day boundary is testable — the domain has no ambient clock, the same rule `RunDoc.StartedAt`
follows.

**Files:**
- Create: `src/Smx.Domain/Spend/ISpendMeter.cs`
- Create: `src/Smx.Domain/Spend/SpendMeter.cs`
- Test: `src/Smx.Domain.Tests/SpendMeterTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Domain.Tests/SpendMeterTests.cs`:

```csharp
using Smx.Domain.Spend;

namespace Smx.Domain.Tests;

public class SpendMeterTests
{
    private sealed class InMemorySpendStore : ISpendStore
    {
        public readonly Dictionary<string, SpendDoc> Docs = [];
        public Exception? ReadThrows;

        public Task AddAsync(string day, string deployment, long inTok, long outTok, CancellationToken ct = default)
        {
            var id = SpendDoc.MakeId(day, deployment);
            if (!Docs.TryGetValue(id, out var d))
                Docs[id] = d = new SpendDoc { Id = id, Day = day, Deployment = deployment };
            d.InputTokens += inTok;
            d.OutputTokens += outTok;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SpendDoc>> ReadDayAsync(string day, CancellationToken ct = default) =>
            ReadThrows is not null
                ? Task.FromException<IReadOnlyList<SpendDoc>>(ReadThrows)
                : Task.FromResult<IReadOnlyList<SpendDoc>>(Docs.Values.Where(d => d.Day == day).ToList());
    }

    private static readonly TokenPrices Prices = TokenPrices.Load("""
        {
          "claude-opus-4-7": { "inputUsdPerMillion": 5.0, "outputUsdPerMillion": 25.0 },
          "gpt-5-mini":      { "inputUsdPerMillion": 0.25, "outputUsdPerMillion": 2.0 }
        }
        """);

    private static (SpendMeter Meter, InMemorySpendStore Store, FakeTimeProvider Clock) Sut(decimal ceiling = 200m)
    {
        var store = new InMemorySpendStore();
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-07-30T12:00:00Z"));
        return (new SpendMeter(store, Prices, ceiling, clock), store, clock);
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Sums_dollars_across_every_model_used_today()
    {
        var (meter, _, _) = Sut();
        await meter.RecordAsync("claude-opus-4-7", 1_000_000, 1_000_000); // $30
        await meter.RecordAsync("gpt-5-mini", 1_000_000, 1_000_000);      // $2.25

        var status = await meter.ReadAsync();
        Assert.Equal(32.25m, status.UsdSpent);
        Assert.False(status.OverCeiling);
    }

    [Fact]
    public async Task Is_over_the_ceiling_at_exactly_the_ceiling()
    {
        // A ceiling of $200 means $200 is spent, not $200 is still available.
        var (meter, _, _) = Sut(ceiling: 30m);
        await meter.RecordAsync("claude-opus-4-7", 1_000_000, 1_000_000); // exactly $30

        Assert.True((await meter.ReadAsync()).OverCeiling);
    }

    [Fact]
    public async Task Yesterdays_spend_does_not_count_against_todays_ceiling()
    {
        var (meter, _, clock) = Sut(ceiling: 10m);
        await meter.RecordAsync("claude-opus-4-7", 1_000_000, 1_000_000); // $30 on the 30th
        Assert.True((await meter.ReadAsync()).OverCeiling);

        clock.Now = DateTimeOffset.Parse("2026-07-31T00:00:01Z");
        var status = await meter.ReadAsync();
        Assert.Equal(0m, status.UsdSpent);
        Assert.False(status.OverCeiling);
    }

    // The day boundary is UTC, and 23:59Z and 00:01Z are different days even though they are
    // ninety seconds apart. Asserted because "midnight" is the kind of thing that quietly
    // becomes machine-local.
    [Fact]
    public async Task Attributes_spend_to_the_UTC_day_not_the_local_one()
    {
        var (meter, store, clock) = Sut();
        clock.Now = DateTimeOffset.Parse("2026-07-30T23:59:00Z");
        await meter.RecordAsync("gpt-5-mini", 100, 0);
        clock.Now = DateTimeOffset.Parse("2026-07-31T00:01:00Z");
        await meter.RecordAsync("gpt-5-mini", 100, 0);

        Assert.Contains(store.Docs.Values, d => d.Day == "2026-07-30");
        Assert.Contains(store.Docs.Values, d => d.Day == "2026-07-31");
    }

    // FAIL CLOSED. The caller gates on this; swallowing the failure and returning $0 would
    // turn every Cosmos blip into an uncapped day.
    [Fact]
    public async Task A_read_failure_propagates_rather_than_reading_as_zero_spend()
    {
        var (meter, store, _) = Sut();
        store.ReadThrows = new InvalidOperationException("cosmos unreachable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => meter.ReadAsync());
    }

    // An unpriced model in the store must not silently drop out of the total.
    [Fact]
    public async Task An_unpriced_model_in_the_days_spend_throws_rather_than_being_skipped()
    {
        var (meter, store, _) = Sut();
        await store.AddAsync("2026-07-30", "some-model-nobody-priced", 1_000_000, 0);

        await Assert.ThrowsAsync<UnknownDeploymentPriceException>(() => meter.ReadAsync());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~SpendMeterTests`
Expected: FAIL — `SpendMeter` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/Spend/ISpendMeter.cs`:

```csharp
namespace Smx.Domain.Spend;

/// <param name="UsdSpent">today's model spend so far, in USD</param>
/// <param name="CeilingUsd">the configured daily ceiling</param>
/// <param name="OverCeiling">true at OR above the ceiling — a ceiling is a limit reached, not a limit passed</param>
public sealed record SpendStatus(decimal UsdSpent, decimal CeilingUsd, bool OverCeiling);

public interface ISpendMeter
{
    Task RecordAsync(string deployment, long inputTokens, long outputTokens, CancellationToken ct = default);

    /// Today's spend against the ceiling. THROWS if it cannot be determined — callers must fail
    /// closed rather than treat an unreadable total as zero.
    Task<SpendStatus> ReadAsync(CancellationToken ct = default);
}
```

Create `src/Smx.Domain/Spend/SpendMeter.cs`:

```csharp
namespace Smx.Domain.Spend;

/// Prices the day's token counts and compares them to the ceiling.
///
/// `TimeProvider` rather than `DateTimeOffset.UtcNow`: the day boundary is a behaviour worth
/// testing, and this codebase already refuses ambient clocks in the domain (see RunDoc.StartedAt).
public sealed class SpendMeter(
    ISpendStore store,
    TokenPrices prices,
    decimal ceilingUsdPerDay,
    TimeProvider clock) : ISpendMeter
{
    private string Today => clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd");

    public Task RecordAsync(string deployment, long inputTokens, long outputTokens, CancellationToken ct = default) =>
        inputTokens == 0 && outputTokens == 0
            ? Task.CompletedTask
            : store.AddAsync(Today, deployment, inputTokens, outputTokens, ct);

    public async Task<SpendStatus> ReadAsync(CancellationToken ct = default)
    {
        var docs = await store.ReadDayAsync(Today, ct);
        var spent = docs.Sum(d => prices.Cost(d.Deployment, d.InputTokens, d.OutputTokens));
        return new SpendStatus(spent, ceilingUsdPerDay, spent >= ceilingUsdPerDay);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~SpendMeterTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Spend/ISpendMeter.cs src/Smx.Domain/Spend/SpendMeter.cs src/Smx.Domain.Tests/SpendMeterTests.cs
git commit -m "feat(domain): price a day's tokens against a ceiling, and fail closed on an unreadable total"
```

---

### Task 4: The Cosmos store

**Files:**
- Create: `src/Smx.Infrastructure/CosmosSpendStore.cs`
- Test: `src/Smx.Backend.Tests/CosmosSpendStoreTests.cs`

The existing `CosmosPartitionKeyTests` and `CosmosQueryTextTests` establish the pattern: assert
the partition key and the emitted query text, because a wrong partition key or a hand-written
SQL string that bypasses the camelCase serializer is a silent empty result, not an error.

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/CosmosSpendStoreTests.cs`:

```csharp
using System.Net;
using Microsoft.Azure.Cosmos;
using Smx.Domain.Spend;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

public class CosmosSpendStoreTests
{
    // The patch paths are WIRE names. If SpendDoc's properties are ever renamed without
    // updating them, the patch silently increments nothing that is read back.
    [Fact]
    public void Increment_paths_match_the_serialized_wire_names()
    {
        Assert.Equal("/inputTokens", CosmosSpendStore.InputTokensPath);
        Assert.Equal("/outputTokens", CosmosSpendStore.OutputTokensPath);

        var json = System.Text.Json.JsonSerializer.Serialize(
            new SpendDoc { InputTokens = 1, OutputTokens = 2 }, Smx.Domain.Json.Options);
        Assert.Contains(CosmosSpendStore.InputTokensPath.TrimStart('/'), json);
        Assert.Contains(CosmosSpendStore.OutputTokensPath.TrimStart('/'), json);
    }

    [Fact]
    public void Partitions_by_day()
    {
        Assert.Equal(new PartitionKey("2026-07-30"), CosmosSpendStore.Key("2026-07-30"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~CosmosSpendStoreTests`
Expected: FAIL — `CosmosSpendStore` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Infrastructure/CosmosSpendStore.cs`:

```csharp
using System.Net;
using Microsoft.Azure.Cosmos;
using Smx.Domain.Spend;

namespace Smx.Infrastructure;

/// The `spend` container, partitioned by /day.
///
/// Increment-then-create, not read-modify-write: `PatchOperation.Increment` is applied
/// server-side, so N concurrent regulatory children incrementing the same (day, model) document
/// all land. A read-modify-write would lose all but one of them, and the loss would be silent —
/// the ceiling would simply take longer to arrive than it should.
public sealed class CosmosSpendStore(Container container) : ISpendStore
{
    public const string InputTokensPath = "/inputTokens";
    public const string OutputTokensPath = "/outputTokens";

    public static PartitionKey Key(string day) => new(day);

    public async Task AddAsync(
        string day, string deployment, long inputTokens, long outputTokens, CancellationToken ct = default)
    {
        var id = SpendDoc.MakeId(day, deployment);
        var ops = new[]
        {
            PatchOperation.Increment(InputTokensPath, inputTokens),
            PatchOperation.Increment(OutputTokensPath, outputTokens),
        };

        try
        {
            await container.PatchItemAsync<SpendDoc>(id, Key(day), ops, cancellationToken: ct);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // First write of the day for this model. Create it with the counts already in place,
            // then let a lost race fall back to the patch: two callers can reach here together,
            // and the 409 loser must still have its tokens counted rather than dropped.
            try
            {
                await container.CreateItemAsync(
                    new SpendDoc
                    {
                        Id = id, Day = day, Deployment = deployment,
                        InputTokens = inputTokens, OutputTokens = outputTokens,
                    },
                    Key(day), cancellationToken: ct);
            }
            catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
            {
                await container.PatchItemAsync<SpendDoc>(id, Key(day), ops, cancellationToken: ct);
            }
        }
    }

    /// LINQ, not a hand-written SQL string — the SDK's LINQ provider is wired to the same
    /// camelCase serializer the documents are written with (see CosmosRunStore's note), and a
    /// raw query string would reintroduce the silent-empty-result trap CosmosQueryTextTests exists
    /// to catch. A silent empty result here reads as "nothing spent today".
    public async Task<IReadOnlyList<SpendDoc>> ReadDayAsync(string day, CancellationToken ct = default)
    {
        var query = container
            .GetItemLinqQueryable<SpendDoc>(requestOptions: new QueryRequestOptions { PartitionKey = Key(day) })
            .ToFeedIterator();

        var results = new List<SpendDoc>();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~CosmosSpendStoreTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Infrastructure/CosmosSpendStore.cs src/Smx.Backend.Tests/CosmosSpendStoreTests.cs
git commit -m "feat(infra): accumulate day spend with server-side increments, not lost updates"
```

---

### Task 5: The metered chat client

**Files:**
- Create: `src/Smx.Infrastructure/MeteredChatClient.cs`
- Modify: `src/Smx.Infrastructure/FoundryChatClientFactory.cs:70-73` and `:91-96`
- Test: `src/Smx.Backend.Tests/MeteredChatClientTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/MeteredChatClientTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Smx.Domain.Spend;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

public class MeteredChatClientTests
{
    private sealed class RecordingMeter : ISpendMeter
    {
        public readonly List<(string Deployment, long In, long Out)> Records = [];
        public Task RecordAsync(string d, long i, long o, CancellationToken ct = default)
        {
            Records.Add((d, i, o));
            return Task.CompletedTask;
        }
        public Task<SpendStatus> ReadAsync(CancellationToken ct = default) =>
            Task.FromResult(new SpendStatus(0m, 200m, false));
    }

    private sealed class StubChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            Task.FromResult(response);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatResponse ResponseWith(UsageDetails? usage) =>
        new(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = usage };

    [Fact]
    public async Task Records_the_tokens_a_call_reported()
    {
        var meter = new RecordingMeter();
        var client = new MeteredChatClient(
            new StubChatClient(ResponseWith(new UsageDetails { InputTokenCount = 120, OutputTokenCount = 34 })),
            meter, "claude-opus-4-7");

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal(("claude-opus-4-7", 120L, 34L), meter.Records.Single());
    }

    // A response with no usage block is a call we cannot price. It must NOT be recorded as
    // zero-cost — that would let an unmeterable path spend without moving the ceiling.
    [Fact]
    public async Task Throws_when_a_response_carries_no_usage_rather_than_recording_nothing()
    {
        var meter = new RecordingMeter();
        var client = new MeteredChatClient(
            new StubChatClient(ResponseWith(null)), meter, "claude-opus-4-7");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
        Assert.Empty(meter.Records);
    }

    [Fact]
    public async Task Returns_the_inner_response_unchanged()
    {
        var inner = ResponseWith(new UsageDetails { InputTokenCount = 1, OutputTokenCount = 1 });
        var client = new MeteredChatClient(new StubChatClient(inner), new RecordingMeter(), "gpt-5-mini");

        Assert.Same(inner, await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~MeteredChatClientTests`
Expected: FAIL — `MeteredChatClient` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Infrastructure/MeteredChatClient.cs`:

```csharp
using Microsoft.Extensions.AI;
using Smx.Domain.Spend;

namespace Smx.Infrastructure;

/// Records every chat call's token usage against the daily ceiling.
///
/// Inserted in FoundryChatClientFactory, which is the ONE place an IChatClient is constructed for
/// either provider — so every agent turn on every path passes through here, including the
/// regulatory fan-out's children, without any caller having to remember to meter.
///
/// A response with no `Usage` THROWS rather than recording zero. The alternative is a model path
/// that spends real money and never moves the ceiling, which is the failure this whole feature
/// exists to prevent; better a loud stage failure than a silent hole in the accounting.
public sealed class MeteredChatClient(IChatClient inner, ISpendMeter meter, string deployment)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        var response = await base.GetResponseAsync(messages, options, ct);
        await RecordAsync(response.Usage, ct);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, ct))
        {
            updates.Add(update);
            yield return update;
        }
        // Usage arrives on the final update(s); ToChatResponse folds them into one Usage block.
        await RecordAsync(updates.ToChatResponse().Usage, ct);
    }

    private Task RecordAsync(UsageDetails? usage, CancellationToken ct)
    {
        if (usage is null)
            throw new InvalidOperationException(
                $"Deployment '{deployment}' returned a response with no token usage, so its cost " +
                "cannot be counted against the daily spend ceiling. Refusing to record it as free.");

        return meter.RecordAsync(deployment, usage.InputTokenCount ?? 0, usage.OutputTokenCount ?? 0, ct);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~MeteredChatClientTests`
Expected: PASS, 3 tests. If `updates.ToChatResponse()` does not resolve, the extension lives in
`Microsoft.Extensions.AI` — confirm the `using` is present before changing the approach.

- [ ] **Step 5: Wire it into the factory**

In `src/Smx.Infrastructure/FoundryChatClientFactory.cs`, add `ISpendMeter? meter = null` as the
last parameter of **both** `CreateAsync` and `CreateOpenAi`, and insert the decorator into both
builder chains.

Anthropic path (currently lines 70–73):

```csharp
        return client.AsIChatClient(opts.ClaudeDeployment)
            .AsBuilder()
            // Metering goes INSIDE function invocation, so a turn that calls five tools records
            // five model calls rather than one. Outside it, a tool-heavy turn would under-count.
            .Use(inner => meter is null ? inner : new MeteredChatClient(inner, meter, opts.ClaudeDeployment))
            .UseFunctionInvocation()
            .Build();
```

OpenAI path (currently lines 91–96):

```csharp
        return azure.GetResponsesClient()
            .AsIChatClient(opts.OpenAiDeployment)
#pragma warning restore OPENAI001
            .AsBuilder()
            .Use(inner => meter is null ? inner : new MeteredChatClient(inner, meter, opts.OpenAiDeployment))
            .UseFunctionInvocation()
            .Build();
```

Update the call in `CreateAsync` to forward the meter: `return CreateOpenAi(opts, credential, meter);`

- [ ] **Step 6: Verify the whole solution still builds and passes**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS. The default `meter = null` keeps every existing call site and test compiling and
behaving exactly as before.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Infrastructure/MeteredChatClient.cs src/Smx.Infrastructure/FoundryChatClientFactory.cs src/Smx.Backend.Tests/MeteredChatClientTests.cs
git commit -m "feat(infra): meter every model call at the one place a chat client is built"
```

---

### Task 6: The stage status and the run signal

**Files:**
- Modify: `src/Smx.Domain/Records/ProjectDoc.cs:30`
- Modify: `src/Smx.Domain/Records/RunDoc.cs:13`
- Test: `src/Smx.Domain.Tests/StageStatusTests.cs`

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Domain.Tests/StageStatusTests.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class StageStatusTests
{
    // The wire value the frontend's StageStatus union and PARKED record must both learn.
    [Fact]
    public void AwaitingBudget_is_an_awaiting_park()
    {
        Assert.Equal("awaiting-budget", StageStatus.AwaitingBudget);
        Assert.StartsWith("awaiting-", StageStatus.AwaitingBudget);
    }

    [Fact]
    public void Blocked_is_distinct_from_every_other_run_outcome()
    {
        Assert.Equal("blocked", RunOutcome.Blocked);
        Assert.NotEqual(RunOutcome.Done, RunOutcome.Blocked);
        Assert.NotEqual(RunOutcome.Cancelled, RunOutcome.Blocked);
        Assert.NotEqual(RunOutcome.Failed, RunOutcome.Blocked);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~StageStatusTests`
Expected: FAIL — `AwaitingBudget` does not exist.

- [ ] **Step 3: Add the constants**

In `src/Smx.Domain/Records/ProjectDoc.cs`, after `AwaitingConfirmation` (line 30):

```csharp
    /// The day's model spend has reached the ceiling. Any stage can park here — it is the only
    /// park that is not about a named human, and the only one that clears on its own (at UTC
    /// midnight) as well as by an operator action (raising SPEND_CEILING_USD_PER_DAY).
    ///
    /// It is an `awaiting-*` on purpose: the frontend's ParkedStatus is derived from the union,
    /// so this value cannot be added without the compiler demanding it be given a home in
    /// PARKED, stageIcon, pillClass, whatsBlocking and foldStatus. A spend park that rendered as
    /// "not started" would be the fifth instance of a bug family this repo has now shipped four
    /// times.
    public const string AwaitingBudget = "awaiting-budget";
```

In `src/Smx.Domain/Records/RunDoc.cs`, after `Interrupted` (line 13):

```csharp
    /// NEVER persisted on a RunDoc. This is PipelineRunner.ExecuteAsync's return-only signal that
    /// the stage was refused before any run was opened — there is no run to give an outcome to,
    /// and RunAsync stops on anything that is not `done`. It is a named constant rather than a
    /// reused `failed`/`cancelled` because both of those would be a lie in the trail.
    public const string Blocked = "blocked";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~StageStatusTests`
Expected: PASS, 2 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/ProjectDoc.cs src/Smx.Domain/Records/RunDoc.cs src/Smx.Domain.Tests/StageStatusTests.cs
git commit -m "feat(domain): a spend park, and a run signal that is not a lie about why"
```

---

### Task 7: The gate

**Files:**
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs:46-59` (constructor) and `:125-146` (`ExecuteAsync`)
- Test: `src/Smx.Backend.Tests/PipelineRunnerSpendCeilingTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/PipelineRunnerSpendCeilingTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Backend.Knowledge;
using Smx.Backend.Pipeline;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Spend;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class PipelineRunnerSpendCeilingTests
{
    private sealed class StubMeter(SpendStatus status, Exception? readThrows = null) : ISpendMeter
    {
        public Task RecordAsync(string d, long i, long o, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SpendStatus> ReadAsync(CancellationToken ct = default) =>
            readThrows is not null
                ? Task.FromException<SpendStatus>(readThrows)
                : Task.FromResult(status);
    }

    private static (PipelineRunner Runner, InMemoryRecordStore Store, InMemoryRunStore Runs)
        Sut(ISpendMeter? meter)
    {
        var store = new InMemoryRecordStore();
        var runs = new InMemoryRunStore();
        var conclusions = new LearnedConclusionWriter(
            new InMemoryKnowledgeStore(), new FakeLearnedConclusionsIndex(), new FakeEmbedder(),
            NullLogger<LearnedConclusionWriter>.Instance);
        return (new PipelineRunner(store, runs, new FakeAgentRuns(), new ThreadEventHub(), conclusions,
                    regulatoryParallelism: 2, spend: meter),
                store, runs);
    }

    private static async Task<ProjectDoc> Seed(InMemoryRecordStore store)
    {
        var doc = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(doc);
        return doc;
    }

    [Fact]
    public async Task Over_the_ceiling_the_first_stage_parks_as_awaiting_budget_and_opens_no_run()
    {
        var (runner, store, runs) = Sut(new StubMeter(new SpendStatus(212.40m, 200m, OverCeiling: true)));
        await Seed(store);

        await runner.RunAsync("p1", CancellationToken.None);

        var project = await store.GetProjectAsync("p1");
        Assert.Equal(StageStatus.AwaitingBudget, project!.Stages[Stages.Intake].Status);
        // No run doc: the gate sits before the run is opened, so there is no empty group in the trail.
        Assert.Empty(await runs.ListAsync("p1", null));
    }

    [Fact]
    public async Task Over_the_ceiling_the_park_says_what_was_spent_and_what_the_ceiling_is()
    {
        var (runner, store, _) = Sut(new StubMeter(new SpendStatus(212.40m, 200m, OverCeiling: true)));
        await Seed(store);

        await runner.RunAsync("p1", CancellationToken.None);

        var error = (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Error;
        Assert.Contains("212.40", error);
        Assert.Contains("200.00", error);
    }

    // The whole pipeline stops — not just the gated stage. A later stage running over the
    // ceiling would make the ceiling advisory.
    [Fact]
    public async Task Over_the_ceiling_no_later_stage_starts()
    {
        var (runner, store, _) = Sut(new StubMeter(new SpendStatus(500m, 200m, OverCeiling: true)));
        await Seed(store);

        await runner.RunAsync("p1", CancellationToken.None);

        var project = await store.GetProjectAsync("p1");
        Assert.Equal(StageStatus.Pending, project!.Stages[Stages.Discovery].Status);
        Assert.Equal(StageStatus.Pending, project.Stages[Stages.Regulatory].Status);
    }

    // FAIL CLOSED — the decision confirmed in the spec review.
    [Fact]
    public async Task An_unreadable_spend_total_parks_rather_than_running_uncounted()
    {
        var (runner, store, runs) = Sut(
            new StubMeter(new SpendStatus(0m, 200m, false), new InvalidOperationException("cosmos unreachable")));
        await Seed(store);

        await runner.RunAsync("p1", CancellationToken.None);

        var stage = (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake];
        Assert.Equal(StageStatus.AwaitingBudget, stage.Status);
        Assert.Contains("unverifiable", stage.Error);
        Assert.Empty(await runs.ListAsync("p1", null));
    }

    [Fact]
    public async Task Under_the_ceiling_the_pipeline_runs_exactly_as_before()
    {
        var (runner, store, runs) = Sut(new StubMeter(new SpendStatus(12.50m, 200m, OverCeiling: false)));
        await Seed(store);

        await runner.RunAsync("p1", CancellationToken.None);

        Assert.NotEqual(StageStatus.AwaitingBudget, (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
        Assert.NotEmpty(await runs.ListAsync("p1", null));
    }

    // No meter configured (local dev, and every pre-existing test) ⇒ no ceiling, no behaviour change.
    [Fact]
    public async Task With_no_meter_configured_there_is_no_ceiling()
    {
        var (runner, store, runs) = Sut(meter: null);
        await Seed(store);

        await runner.RunAsync("p1", CancellationToken.None);

        Assert.NotEqual(StageStatus.AwaitingBudget, (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
        Assert.NotEmpty(await runs.ListAsync("p1", null));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~PipelineRunnerSpendCeilingTests`
Expected: FAIL — `PipelineRunner` has no `spend` parameter.

- [ ] **Step 3: Add the optional dependency**

In `src/Smx.Backend/Pipeline/PipelineRunner.cs`, add a parameter after `regulatoryAutoApprove`
(line 59), matching the existing optional-dependency idiom used by `knowledge`, `catalog` and `sds`:

```csharp
    bool regulatoryAutoApprove = false,
    // The daily spend ceiling. OPTIONAL for the same reason the other three are: when it is null
    // there is no ceiling and every stage runs exactly as before — which is what local dev and
    // every pre-existing test rely on.
    ISpendMeter? spend = null)
```

Add `using Smx.Domain.Spend;` to the file's usings.

- [ ] **Step 4: Add the gate**

In `ExecuteAsync`, insert this as the **first** statement of the method body (before the
`ordinal` line at 131), so no run doc is created for a refused stage:

```csharp
        // THE CEILING. Checked at stage entry, which is exactly the agreed behaviour: refuse new
        // stage starts, never interrupt work already running. RunAsync walks stages sequentially
        // per project, so "in flight" is the current stage — a regulatory fan-out already going
        // finishes rather than being cut off half way through a fourteen-substance screen.
        if (spend is not null)
        {
            SpendStatus status;
            try
            {
                status = await spend.ReadAsync(hostToken);
            }
            catch (Exception e)
            {
                // FAIL CLOSED. A total we cannot read is not zero spend. This costs nothing in
                // practice: the store is Cosmos, and a pipeline that cannot reach Cosmos could not
                // have read the project record either.
                await StampAsync(projectId, stage, StageStatus.AwaitingBudget,
                    $"spend ceiling unverifiable: {e.Message}", hostToken);
                return RunOutcome.Blocked;
            }

            if (status.OverCeiling)
            {
                await StampAsync(projectId, stage, StageStatus.AwaitingBudget,
                    $"today's model spend of ${status.UsdSpent:F2} has reached the " +
                    $"${status.CeilingUsd:F2} daily ceiling", hostToken);
                return RunOutcome.Blocked;
            }
        }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~PipelineRunnerSpendCeilingTests`
Expected: PASS, 6 tests.

- [ ] **Step 6: Verify nothing else regressed**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS — every pre-existing `PipelineRunnerTests` case still passes, because they
construct the runner without a meter.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Backend/Pipeline/PipelineRunner.cs src/Smx.Backend.Tests/PipelineRunnerSpendCeilingTests.cs
git commit -m "feat(backend): refuse to start a stage once the day's ceiling is reached"
```

---

### Task 8: Configuration and wiring

**Files:**
- Modify: `src/Smx.Infrastructure/BackendOptions.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/BackendOptionsTests.cs` (add cases)

- [ ] **Step 1: Write the failing tests**

Append to `src/Smx.Backend.Tests/BackendOptionsTests.cs` (inside the existing test class; match
its existing helper for building an `IConfiguration` from a dictionary):

```csharp
    [Fact]
    public void SpendCeiling_defaults_to_none()
    {
        // Absent config ⇒ no ceiling. Program.cs warns loudly; it must not fail startup, because
        // a host with no Cosmos configured still has to boot (see FOUNDRY_ENDPOINT's note).
        Assert.Null(Options([]).SpendCeilingUsdPerDay);
    }

    [Fact]
    public void SpendCeiling_reads_a_decimal()
    {
        Assert.Equal(200m, Options(new() { ["SPEND_CEILING_USD_PER_DAY"] = "200" }).SpendCeilingUsdPerDay);
        Assert.Equal(1000.50m, Options(new() { ["SPEND_CEILING_USD_PER_DAY"] = "1000.50" }).SpendCeilingUsdPerDay);
    }

    // A garbled value must NOT silently become "no ceiling" — that is the one guardrail the
    // company asked for, disabled by a typo.
    [Fact]
    public void An_unparseable_SpendCeiling_throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => Options(new() { ["SPEND_CEILING_USD_PER_DAY"] = "two hundred" }));
    }

    [Fact]
    public void SpendContainer_defaults_to_spend()
    {
        Assert.Equal("spend", Options([]).SpendContainer);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~BackendOptionsTests`
Expected: FAIL — `SpendCeilingUsdPerDay` does not exist.

- [ ] **Step 3: Add the options**

In `src/Smx.Infrastructure/BackendOptions.cs`, add two init properties alongside `RunContainer`
(after line 65):

```csharp
    /// The daily model-spend ceiling in USD. NULL means no ceiling — see the note in Program.cs,
    /// which warns loudly rather than failing startup, because a host with no Cosmos configured
    /// must still boot.
    public decimal? SpendCeilingUsdPerDay { get; init; }

    /// The daily spend container. Separate from `record` and from `runs` — a day's spend is
    /// neither project state nor run telemetry. Provisioned in Bicep: the workload identity holds
    /// Cosmos data-plane rights only and cannot create it at runtime.
    public string SpendContainer { get; init; } = "spend";
```

And in the `From` object initializer (after line 136), add:

```csharp
        SpendCeilingUsdPerDay = ParseCeiling(c["SPEND_CEILING_USD_PER_DAY"]),
        SpendContainer = c["SPEND_CONTAINER"] ?? "spend",
```

Add this private static helper to the class:

```csharp
    /// A garbled ceiling THROWS rather than falling back to "no ceiling": a typo in the one
    /// configured guardrail must not silently switch it off.
    private static decimal? ParseCeiling(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null
        : decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                           System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : throw new InvalidOperationException(
                $"SPEND_CEILING_USD_PER_DAY is '{raw}', which is not a positive decimal. " +
                "Leave it unset for no ceiling, or set a value like 200.");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~BackendOptionsTests`
Expected: PASS.

- [ ] **Step 5: Wire the DI**

In `src/Smx.Backend/Program.cs`, immediately after the `IRunStore` registration (line 165–166):

```csharp
        services.AddSingleton<ISpendStore>(sp => new CosmosSpendStore(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.SpendContainer)));

        // The ceiling. Registered only when configured: an absent ceiling means PipelineRunner
        // gets a null meter and behaves exactly as it did before this feature existed.
        if (opts.SpendCeilingUsdPerDay is { } ceiling)
        {
            // Fail startup on an unpriced deployment rather than discovering it at the ceiling.
            // A model the table cannot price would spend without counting, which is not a cheap
            // model — it is no ceiling for that model.
            foreach (var deployment in new[] { opts.ClaudeDeployment, opts.OpenAiDeployment, opts.EmbeddingDeployment })
                if (!TokenPrices.Shipped.Knows(deployment))
                    throw new InvalidOperationException(
                        $"Deployment '{deployment}' has no entry in src/Smx.Domain/Spend/token-prices.json, " +
                        "so its spend could not be counted against SPEND_CEILING_USD_PER_DAY. " +
                        "Add its price, or unset SPEND_CEILING_USD_PER_DAY to run without a ceiling.");

            services.AddSingleton<ISpendMeter>(sp => new SpendMeter(
                sp.GetRequiredService<ISpendStore>(), TokenPrices.Shipped, ceiling, TimeProvider.System));
        }
        else
        {
            logger.LogWarning(
                "SPEND_CEILING_USD_PER_DAY is not set — model spend is UNCAPPED in this process.");
        }
```

Add `using Smx.Domain.Spend;` to the file's usings. Use whichever logger instance is already in
scope in `ConfigureServices`; if none is, emit the warning via `Console.Error.WriteLine` rather
than adding a logger just for this.

Then thread the meter into the two consumers. Where `PipelineRunner` is registered, add
`sp.GetService<ISpendMeter>()` as the `spend` argument (`GetService`, not `GetRequiredService` —
null is the valid "no ceiling" case). Where `FoundryChatClientFactory.CreateAsync` is called, pass
`sp.GetService<ISpendMeter>()` as the new final argument.

- [ ] **Step 6: Verify wiring**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS, including `BackendHostWiringTests`.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Infrastructure/BackendOptions.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/BackendOptionsTests.cs
git commit -m "feat(backend): configure the ceiling, and refuse to boot with an unpriced model"
```

---

### Task 9: Never lose a recorded call

The spec requires that a call whose cost cannot be persisted "is logged as an error and held in
memory for retry" — never dropped. As written so far, a Cosmos write failure propagates out of
`MeteredChatClient` and fails the whole stage, which is loud but wrong: it turns a bookkeeping
outage into a work outage, and a Cosmos **write** 429 under RU pressure is an ordinary event that
a **read** may well survive. That combination — writes failing, reads succeeding — is the one that
matters: spend would accumulate uncounted while the gate happily reports we are under the ceiling.

**Files:**
- Create: `src/Smx.Infrastructure/BufferedSpendStore.cs`
- Modify: `src/Smx.Backend/Program.cs` (wrap the `ISpendStore` registration)
- Test: `src/Smx.Backend.Tests/BufferedSpendStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/BufferedSpendStoreTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Smx.Domain.Spend;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

public class BufferedSpendStoreTests
{
    private sealed class FlakyStore : ISpendStore
    {
        public bool Fail;
        public readonly List<(string Day, string Deployment, long In, long Out)> Writes = [];

        public Task AddAsync(string day, string dep, long i, long o, CancellationToken ct = default)
        {
            if (Fail) return Task.FromException(new InvalidOperationException("429"));
            Writes.Add((day, dep, i, o));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SpendDoc>> ReadDayAsync(string day, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SpendDoc>>([]);
    }

    private static BufferedSpendStore Sut(FlakyStore inner) =>
        new(inner, NullLogger<BufferedSpendStore>.Instance);

    // A failed write must NOT throw into the model call — the stage keeps working.
    [Fact]
    public async Task A_failed_write_does_not_fail_the_caller()
    {
        var inner = new FlakyStore { Fail = true };
        await Sut(inner).AddAsync("2026-07-30", "gpt-5-mini", 10, 1);
        Assert.Empty(inner.Writes);
    }

    // ...but it must not be lost either. The next successful write flushes what was held.
    [Fact]
    public async Task A_failed_write_is_replayed_on_the_next_successful_one()
    {
        var inner = new FlakyStore { Fail = true };
        var store = Sut(inner);

        await store.AddAsync("2026-07-30", "gpt-5-mini", 10, 1);
        await store.AddAsync("2026-07-30", "gpt-5-mini", 20, 2);

        inner.Fail = false;
        await store.AddAsync("2026-07-30", "gpt-5-mini", 5, 0);

        // The two buffered calls plus the live one: 35 in, 3 out, in some order.
        Assert.Equal(35, inner.Writes.Sum(w => w.In));
        Assert.Equal(3, inner.Writes.Sum(w => w.Out));
    }

    // Buffered entries keep their own day: a call held across midnight must not be re-attributed
    // to the day it was finally written on.
    [Fact]
    public async Task A_buffered_call_keeps_the_day_it_happened_on()
    {
        var inner = new FlakyStore { Fail = true };
        var store = Sut(inner);

        await store.AddAsync("2026-07-30", "gpt-5-mini", 10, 1);
        inner.Fail = false;
        await store.AddAsync("2026-07-31", "gpt-5-mini", 5, 0);

        Assert.Contains(inner.Writes, w => w is { Day: "2026-07-30", In: 10 });
        Assert.Contains(inner.Writes, w => w is { Day: "2026-07-31", In: 5 });
    }

    [Fact]
    public async Task Read_failures_still_propagate()
    {
        // The GATE reads. Buffering must never turn an unreadable total into a readable zero —
        // that is the fail-closed guarantee, and it belongs on the read path untouched.
        var store = Sut(new ThrowingReadStore());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadDayAsync("2026-07-30"));
    }

    private sealed class ThrowingReadStore : ISpendStore
    {
        public Task AddAsync(string d, string dep, long i, long o, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<IReadOnlyList<SpendDoc>> ReadDayAsync(string day, CancellationToken ct = default) =>
            Task.FromException<IReadOnlyList<SpendDoc>>(new InvalidOperationException("cosmos unreachable"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~BufferedSpendStoreTests`
Expected: FAIL — `BufferedSpendStore` does not exist.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Infrastructure/BufferedSpendStore.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Smx.Domain.Spend;

namespace Smx.Infrastructure;

/// Holds writes that failed and replays them on the next successful one.
///
/// WRITES are buffered; READS are not touched. That asymmetry is the design: a write failure must
/// not fail the model call that triggered it (a bookkeeping outage is not a work outage), but a
/// read failure MUST still propagate, because the gate reads and fail-closed depends on it. An
/// unreadable total is not zero spend.
///
/// The buffer is in-memory and therefore lost on restart — accepted deliberately. Making it
/// durable would mean a second store with the same failure mode as the one it exists to survive.
/// What it buys is resilience to a transient write 429, which is the realistic case; a process
/// that dies mid-outage loses at most the calls it had not yet flushed, and logs each one.
public sealed class BufferedSpendStore(ISpendStore inner, ILogger<BufferedSpendStore> logger) : ISpendStore
{
    private readonly record struct Pending(string Day, string Deployment, long InputTokens, long OutputTokens);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Pending> _pending = new();

    public async Task AddAsync(
        string day, string deployment, long inputTokens, long outputTokens, CancellationToken ct = default)
    {
        await FlushAsync(ct);

        try
        {
            await inner.AddAsync(day, deployment, inputTokens, outputTokens, ct);
        }
        catch (Exception e)
        {
            _pending.Enqueue(new Pending(day, deployment, inputTokens, outputTokens));
            logger.LogError(e,
                "Could not record {In} input / {Out} output tokens for {Deployment} on {Day} against the " +
                "daily spend ceiling. Held for retry; {Depth} call(s) now pending.",
                inputTokens, outputTokens, deployment, day, _pending.Count);
        }
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        while (_pending.TryDequeue(out var p))
        {
            try
            {
                await inner.AddAsync(p.Day, p.Deployment, p.InputTokens, p.OutputTokens, ct);
            }
            catch
            {
                // Still failing. Put it back and stop — draining the whole queue against a store
                // that is down would just move the outage into the caller's latency.
                _pending.Enqueue(p);
                return;
            }
        }
    }

    /// Deliberately NOT buffered or swallowed — see the class note.
    public Task<IReadOnlyList<SpendDoc>> ReadDayAsync(string day, CancellationToken ct = default) =>
        inner.ReadDayAsync(day, ct);
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/Smx.Backend.sln --filter FullyQualifiedName~BufferedSpendStoreTests`
Expected: PASS, 4 tests.

- [ ] **Step 5: Wrap the registration**

In `src/Smx.Backend/Program.cs`, change the `ISpendStore` registration added in Task 8 to:

```csharp
        services.AddSingleton<ISpendStore>(sp => new BufferedSpendStore(
            new CosmosSpendStore(
                sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.SpendContainer)),
            sp.GetRequiredService<ILogger<BufferedSpendStore>>()));
```

- [ ] **Step 6: Verify**

Run: `dotnet test src/Smx.Backend.sln`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Infrastructure/BufferedSpendStore.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/BufferedSpendStoreTests.cs
git commit -m "feat(infra): a write outage must not lose spend, or stop work"
```

---

### Task 10: Infrastructure — the container and the ceiling value

The `spend` container **must** exist before the backend runs: the workload identity has Cosmos
data-plane rights only and cannot create it (`infra/modules/data.bicep:164`). Because the gate
fails closed, a missing container stops every project rather than degrading quietly. This is the
single most likely way to ship this feature broken.

**Files:**
- Modify: `infra/modules/data.bicep`, `infra/single-rg/modules/data.bicep`
- Modify: `infra/modules/compute.bicep`, `infra/single-rg/modules/compute.bicep`
- Modify: `infra/main.bicep`, `infra/single-rg/main.bicep`

- [ ] **Step 1: Add the container to both data.bicep twins**

After the `intakeSessions` resource in each file:

```bicep
// The daily model-spend ledger, partitioned by /day. A THIRD container on purpose: a day's spend
// is neither project state (`record`) nor run telemetry (`runs`). Provisioned here because the
// workload identity holds data-plane rights only — and because the ceiling's gate fails closed,
// a container that exists only in code stops every project on first write.
resource spend 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'spend'
  properties: {
    resource: {
      id: 'spend'
      partitionKey: { paths: [ '/day' ], kind: 'Hash' }
    }
  }
}
```

- [ ] **Step 2: Add the ceiling parameter to both compute.bicep twins**

Add a parameter alongside the existing ones:

```bicep
@description('Daily model-spend ceiling in USD. Empty string = no ceiling (the backend warns loudly).')
param spendCeilingUsdPerDay string
```

And add to the shared env array, beside `WEB_SEARCH_MAX_PER_STAGE`:

```bicep
  { name: 'SPEND_CEILING_USD_PER_DAY', value: spendCeilingUsdPerDay }
```

- [ ] **Step 3: Set the per-env value in both main.bicep twins**

In the `compute` module invocation, following the existing `env == 'prod' ? … : …` idiom used by
`searchSku` (`infra/main.bicep:107`):

```bicep
    // Dev gets the lower ceiling because dev is where a runaway loop actually happens. These are
    // the CEILING, not the quota — see the spend-ceiling design: the TPM quota that would enforce
    // these arithmetically is below the TPM a single RAG request needs, so the code meter is the
    // enforcement and the quota is a blast-radius limiter.
    spendCeilingUsdPerDay: env == 'prod' ? '1000' : '200'
```

- [ ] **Step 4: Verify both variants compile**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: both silent (exit 0). Any output is an error.

- [ ] **Step 5: Commit**

```bash
git add infra/
git commit -m "infra: provision the spend ledger and set the per-env ceiling"
```

---

### Task 11: Full verification

- [ ] **Step 1: Build and test the whole backend**

```bash
dotnet build src/Smx.Backend.sln
dotnet test src/Smx.Backend.sln
```

Expected: build clean, all tests pass.

- [ ] **Step 2: Confirm the Functions solution is untouched**

```bash
dotnet build src/Smx.Functions.sln
```

Expected: PASS. Nothing in this plan touches it; this catches an accidental shared-project break.

- [ ] **Step 3: Confirm both Bicep variants still compile**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

Expected: both silent.

- [ ] **Step 4: Commit any fixes and push**

```bash
git push
```

---

## Deployment note (not a code step)

`seed-reference-data.sh` and friends run against containers created by `deploy.sh`. Because the
`spend` container is new, **`deploy.sh` must run before the new backend image is deployed** — a
backend that starts against a Cosmos database without a `spend` container will fail closed and
park every project on its first stage. That is the designed behaviour, and it is also exactly what
a wrong deploy order looks like, so check the container exists before concluding the ceiling is
misconfigured:

```bash
az cosmosdb sql container show -g <rg> -a <cosmos-account> -d smx -n spend --query id -o tsv
```

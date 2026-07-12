# Chemistry Backend — Plan 3a: Knowledge Layer (read side + cross-project containers) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the three cross-project knowledge containers (**Learned Conclusions**, **Marker Library**, **MSDS Registry**), the agent **read-tools** that let Intake/Discovery retrieve prior knowledge, and the cross-project **browse endpoints** — all **cold-start-safe** (an empty store returns "no matches — do not invent," never an error). This is the read/storage foundation; the **write** paths land later: revise-with-reason writes a Learned Conclusion (**Plan 3b**, which also adds the AI Search push/embedding client), and Marker-Library/close-writes land in **Plan 5**.

**Architecture:** Three new Cosmos containers **outside** the per-project `record` change-feed bus (cross-project, partition keys chosen for query shape — NOT `/projectId`). A new `IKnowledgeStore` port (mirroring `IRecordStore`) fronts all three, with a `CosmosKnowledgeStore` (three injected containers) and an `InMemoryKnowledgeStore` fake. Agents read the knowledge layer through **tools** (`search_marker_library` over Cosmos, `search_learned_conclusions` over the `learned-conclusions` AI Search index), exactly like the existing `ref-*`/RAG tools. The thin backend exposes structured **browse** reads over Cosmos. This is Plan 3a of the Plan-3 trio (design: [`2026-07-12-chemistry-backend-end-to-end-design.md`](../specs/2026-07-12-chemistry-backend-end-to-end-design.md) §6, §7, §10).

**Tech Stack:** .NET 8, xUnit, Azure Cosmos DB (NoSQL, serverless), Azure AI Search (read-only `SearchClient`), ASP.NET Core minimal API, `WebApplicationFactory` for endpoint tests, Bicep.

**Scope guard — what this plan does NOT build (kept explicit so scope stays honest):**
- **No writes triggered by agents/gates.** No revise-with-reason (Plan 3b), no VP-close writes (Plan 5). Stores gain `Upsert*`/`Get*`/`Query*` methods, but the only callers in this plan are **tests** (which seed docs to exercise reads). This is deliberate: the read side must be proven independently first.
- **No AI Search index creation or push.** The `learned-conclusions` index is queried read-only here; its `EnsureIndexAsync`/`PushAsync` + the embeddings client land in **Plan 3b** (where the first write happens). The read tool is cold-start-safe against a not-yet-created/empty index.
- **No chat.** The `chat-message`/`chat-reply` thread + interactive dispatch is **Plan 3c**.
- **No agent-behavior/LLM changes beyond registering tools.** Intake/Discovery gain the tools in their tool lists + a line in their `Instructions`; deterministic tests assert the tools are *registered*, not that the LLM *uses* them (that's golden-eval territory, Plan 5).
- **Deliberate resequencing (honest against the spec):** the design (§9.5) sequences the broad "cross-project read surfaces (§7)" in **Plan 5**. This plan pulls **only** the three *knowledge* browse reads (`GET /marker-library`, `/learned-conclusions`, `/msds-registry`) + the `POST /msds-registry/{cas}/review` action forward into 3a, because they are thin reads directly over the containers 3a creates and the review action feeds the MSDS-before-order precondition — co-locating them with the store keeps the knowledge layer cohesive and independently demoable. The **rest of §7 stays in Plan 5**: the projects list, the dashboard aggregation, the per-stage reads (`candidates`/`verdicts`/`dosing`/`cost`/`decision`), and the compliance-package/elements-to-check artifacts.

**Key patterns to mirror (from the current post-Plan-2 code — read these first):**
- Record doc convention: `sealed class` in `src/Smx.Domain/Records/`, `required string Id`, a partition-key field, `string Type` discriminator defaulted to a constant; nested payloads are `sealed record`s; lists default `= []`. See `src/Smx.Domain/Records/GateDoc.cs`, `CandidatesDoc.cs`, `ConstraintsDoc.cs`.
- Store: `IRecordStore` + `CosmosRecordStore(Container container)` with generic `ReadAsync<T>(id, pk, ct)` / `Upsert<T>(doc, pk, ct)` helpers + a LINQ `GetItemLinqQueryable<T>(… PartitionKey …)` list query; `InMemoryRecordStore` fake = `ConcurrentDictionary<string, object> _docs` keyed by `doc.Id`.
- Cross-project multi-container DI: `src/Smx.Orchestrator/Program.cs` wires `ICatalogLookup`/`ICompatibilityLookup` by resolving a **named** container off the shared `CosmosClient` (`GetContainer(opts.CosmosDatabase, opts.CatalogContainer)`); names come from `BackendOptions` (`c["CATALOG_CONTAINER"] ?? "ref-catalog"`).
- Tools: interfaces in `src/Smx.Domain/Tools/ITools.cs`; AI-Search readers in `src/Smx.Infrastructure/Search/SearchTools.cs` (`SearchToolBase(SearchClient client, string sourceName)` + one-line subclasses); Cosmos lookups in `src/Smx.Infrastructure/Search/CatalogLookup.cs`; agent exposure in `src/Smx.Orchestrator/Agents/ToolBox.cs` via `AIFunctionFactory.Create(method, "name", "desc")`, each tool method returning a JSON string with a `"no matches — do not invent"` sentinel on empty results; fakes in `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`.
- Endpoints: `src/Smx.Backend/Api/ProjectEndpoints.cs` (`MapProjectEndpoints(this IEndpointRouteBuilder app)`, per-endpoint DI params); tests use `WebApplicationFactory<Program>` + `ConfigureServices(s => s.AddSingleton<IX>(fake))`.
- Infra: `infra/modules/data.bicep` (containers via a `var xContainers = [...]` + `[for c in xContainers: {...}]` loop) and its **byte-identical twin** `infra/single-rg/modules/data.bicep`; env vars in `infra/modules/compute.bicep` `sharedEnv` + twin.

**Build/test commands** (from repo root):
- Build: `dotnet build src/Smx.Backend.sln`
- Test all: `dotnet test src/Smx.Backend.sln`
- Test one: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~<Class>.<Method>"`
- Infra: `az bicep build --file infra/main.bicep --stdout > /dev/null` and `az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null`

The whole solution builds and stays green after **every** task (additive plan — no red zone). Baseline entering this plan: 101 tests green (Domain 26, Eval 4, Orchestrator 47, Backend 24).

---

## File Structure

**Domain (`src/Smx.Domain/`)**
- `Records/LearnedConclusionDoc.cs` — **create**: the accumulation-layer doc (+ `ConclusionScope`, `ConclusionProvenance` nested records).
- `Records/MarkerLibraryDoc.cs` — **create**: approved-code reuse doc (+ `MarkerComposition`, `ValidatedFor` nested records).
- `Records/MsdsRegistryDoc.cs` — **create**: procurement-governance doc.
- `Records/KnowledgeIds.cs` — **create**: `KnowledgeTypes` (record discriminators) + `KnowledgeKinds` (learned-conclusion kinds) + `KnowledgeIds` deterministic id builders.
- `IKnowledgeStore.cs` — **create**: the cross-project store port (get/upsert/query for the three docs).
- `Tools/ITools.cs` — **modify**: add `ILearnedConclusionsSearch` (reuses `RetrievedChunk`).

**Infrastructure (`src/Smx.Infrastructure/`)**
- `CosmosKnowledgeStore.cs` — **create**: three-container Cosmos implementation of `IKnowledgeStore`.
- `Search/SearchTools.cs` — **modify**: add `LearnedConclusionsSearchTool`.
- `BackendOptions.cs` — **modify**: three container-name + one index-name settings.

**Orchestrator (`src/Smx.Orchestrator/`)**
- `Agents/ToolBox.cs` — **modify**: `search_marker_library` + `search_learned_conclusions` tool methods; add them to `IntakeTools()`/`DiscoveryTools()`; take `IKnowledgeStore` + `ILearnedConclusionsSearch` in the ctor.
- `Agents/IntakeAgent.cs`, `Agents/DiscoveryAgent.cs` — **modify**: one `Instructions` line each naming the new tool(s).
- `Program.cs` — **modify**: register `IKnowledgeStore` (`CosmosKnowledgeStore`, three containers), `ILearnedConclusionsSearch`, and pass both into `ToolBox`.

**Backend (`src/Smx.Backend/`)**
- `Api/KnowledgeEndpoints.cs` — **create**: `GET /marker-library`, `GET /learned-conclusions`, `GET /msds-registry`, `POST /msds-registry/{cas}/review`.
- `Program.cs` — **modify**: register `IKnowledgeStore` (conditional on Cosmos config, like `IRecordStore`); call `app.MapKnowledgeEndpoints()`.

**Tests**
- `Smx.Domain.Tests/KnowledgeDocsTests.cs` — **create**: doc ids/types/round-trip.
- `Smx.Domain.Tests/Fakes/InMemoryKnowledgeStore.cs` — **create**: the store fake.
- `Smx.Domain.Tests/InMemoryKnowledgeStoreTests.cs` — **create**: round-trip + query.
- `Smx.Orchestrator.Tests/Fakes/FakeTools.cs` — **modify**: `FakeLearnedConclusionsSearch`.
- `Smx.Orchestrator.Tests/ToolBoxTests.cs` — **modify**: assert the new tools are registered + cold-start "no matches".
- `Smx.Backend.Tests/KnowledgeEndpointsTests.cs` — **create**: the four browse endpoints.

**Infra**
- `infra/modules/data.bicep` + `infra/single-rg/modules/data.bicep` — three containers.
- `infra/modules/compute.bicep` + `infra/single-rg/modules/compute.bicep` — env vars.

---

## Task 1: Knowledge record docs + ids

**Files:**
- Create: `src/Smx.Domain/Records/LearnedConclusionDoc.cs`, `MarkerLibraryDoc.cs`, `MsdsRegistryDoc.cs`, `KnowledgeIds.cs`
- Test: `src/Smx.Domain.Tests/KnowledgeDocsTests.cs`

- [ ] **Step 1: Write the failing test** — create `src/Smx.Domain.Tests/KnowledgeDocsTests.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class KnowledgeDocsTests
{
    [Fact]
    public void LearnedConclusion_HasDeterministicId_KindPk_AndRoundTrips()
    {
        var c = new LearnedConclusionDoc
        {
            Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.RegulatoryJudgment, "ba|label|eu"),
            Kind = KnowledgeKinds.RegulatoryJudgment,
            Scope = new ConclusionScope(Element: "Ba", Form: null, Material: "label", Application: null, Market: "EU", Substance: null),
            Finding = "Ba tier-B for labels in EU: overlaps Ti Kα.",
            Confidence = 0.8,
            Provenance = new ConclusionProvenance(["p1"], ["p1|discovery|revise"]),
            CreatedAt = "2026-07-12T00:00:00Z",
        };
        Assert.Equal("regulatory-judgment|ba|label|eu", c.Id);
        Assert.Equal(KnowledgeTypes.LearnedConclusion, c.Type);
        Assert.Equal(KnowledgeKinds.RegulatoryJudgment, c.Kind);
        var back = JsonSerializer.Deserialize<LearnedConclusionDoc>(JsonSerializer.Serialize(c, Json.Options), Json.Options)!;
        Assert.Equal("Ba", back.Scope.Element);
        Assert.Equal(0.8, back.Confidence);
        Assert.Equal(["p1"], back.Provenance.SourceProjects);
    }

    [Fact]
    public void MarkerLibrary_HasDeterministicId_AndDefaults()
    {
        var m = new MarkerLibraryDoc
        {
            Id = KnowledgeIds.Marker("acme-anti-counterfeit-label"),
            Composition = new MarkerComposition(["Zr", "Y"], 250, "2:1"),
            ValidatedFor = new ValidatedFor(Application: "anti-counterfeit", Material: "label", Objective: "overt"),
            SourceProject = "p1",
            Status = "approved",
            CreatedAt = "2026-07-12T00:00:00Z",
        };
        Assert.Equal("marker|acme-anti-counterfeit-label", m.Id);
        Assert.Equal(KnowledgeTypes.MarkerLibrary, m.Type);
        Assert.Equal(0, m.ReuseCount);
    }

    [Fact]
    public void MsdsRegistry_KeyedByCas_WithDefaults()
    {
        var s = new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("13463-67-7"), Cas = "13463-67-7",
            Supplier = "Acme", Version = "3", Date = "2025-01-01",
        };
        Assert.Equal("msds|13463-67-7", s.Id);
        Assert.Equal(KnowledgeTypes.MsdsRegistry, s.Type);
        Assert.Equal("unreviewed", s.ReviewStatus);
        Assert.Empty(s.LinkedProjects);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~KnowledgeDocsTests"`
Expected: FAIL — the doc types / `KnowledgeIds` / `KnowledgeKinds` don't exist.

- [ ] **Step 3: Create the docs + ids.**

`src/Smx.Domain/Records/KnowledgeIds.cs`:

```csharp
namespace Smx.Domain.Records;

/// Record-type discriminators for the cross-project knowledge containers (parallel to RecordTypes,
/// which is for the per-project `record` change-feed bus). These docs live OUTSIDE that bus.
public static class KnowledgeTypes
{
    public const string LearnedConclusion = "learned-conclusion";
    public const string MarkerLibrary = "marker-library";
    public const string MsdsRegistry = "msds-registry";
}

/// The kind of a Learned Conclusion — also its Cosmos partition key (/kind). Distinct from the
/// record-type discriminator above (which is always "learned-conclusion").
public static class KnowledgeKinds
{
    public const string Material = "material";
    public const string XrfBackground = "xrf-background";
    public const string RegulatoryJudgment = "regulatory-judgment";
}

public static class KnowledgeIds
{
    public static string LearnedConclusion(string kind, string scopeKey) => $"{kind}|{scopeKey}";
    public static string Marker(string key) => $"marker|{key}";
    public static string Msds(string cas) => $"msds|{cas}";
}
```

`src/Smx.Domain/Records/LearnedConclusionDoc.cs`:

```csharp
namespace Smx.Domain.Records;

/// One accumulated finding with provenance + confidence (design §6.1). Authoritative in the
/// `learned-conclusions` Cosmos container (PK /kind); also pushed into the AI Search index (Plan 3b).
public sealed class LearnedConclusionDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.LearnedConclusion;
    public required string Kind { get; set; }              // KnowledgeKinds.* — the partition key
    public required ConclusionScope Scope { get; set; }
    public required string Finding { get; set; }
    public double Confidence { get; set; }
    public required ConclusionProvenance Provenance { get; set; }
    public string? Supersedes { get; set; }                // id of a conclusion this refines
    public required string CreatedAt { get; set; }         // ISO-8601 (caller-supplied; time is not available in domain)
}

public sealed record ConclusionScope(
    string? Element, string? Form, string? Material, string? Application, string? Market, string? Substance);

public sealed record ConclusionProvenance(
    IReadOnlyList<string> SourceProjects, IReadOnlyList<string> Decisions);
```

`src/Smx.Domain/Records/MarkerLibraryDoc.cs`:

```csharp
namespace Smx.Domain.Records;

/// An approved final code, reusable across projects (design §6.2). Structured store; PK /id.
public sealed class MarkerLibraryDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.MarkerLibrary;
    public required MarkerComposition Composition { get; set; }
    public required ValidatedFor ValidatedFor { get; set; }
    public required string SourceProject { get; set; }
    public string Status { get; set; } = "approved";
    public int ReuseCount { get; set; }
    public required string CreatedAt { get; set; }
}

public sealed record MarkerComposition(IReadOnlyList<string> Markers, double Ppm, string Ratio);
public sealed record ValidatedFor(string Application, string Material, string Objective);
```

`src/Smx.Domain/Records/MsdsRegistryDoc.cs`:

```csharp
namespace Smx.Domain.Records;

/// Thin curated governance layer over the SDS corpus (design §6.3). PK /cas. References the
/// indexed SDS; does not duplicate the corpus. Backs the MSDS-before-order precondition (Plan 5).
public sealed class MsdsRegistryDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.MsdsRegistry;
    public required string Cas { get; set; }               // the partition key
    public required string Supplier { get; set; }
    public required string Version { get; set; }
    public required string Date { get; set; }              // SDS revision date (ISO-8601)
    public string ReviewStatus { get; set; } = "unreviewed"; // "unreviewed" | "reviewed"
    public List<string> LinkedProjects { get; set; } = [];
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~KnowledgeDocsTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/LearnedConclusionDoc.cs src/Smx.Domain/Records/MarkerLibraryDoc.cs src/Smx.Domain/Records/MsdsRegistryDoc.cs src/Smx.Domain/Records/KnowledgeIds.cs src/Smx.Domain.Tests/KnowledgeDocsTests.cs
git commit -m "feat(domain): knowledge-layer docs + ids (learned-conclusion, marker-library, msds-registry)"
```

---

## Task 2: IKnowledgeStore + InMemoryKnowledgeStore fake

**Files:**
- Create: `src/Smx.Domain/IKnowledgeStore.cs`
- Create: `src/Smx.Domain.Tests/Fakes/InMemoryKnowledgeStore.cs`
- Test: `src/Smx.Domain.Tests/InMemoryKnowledgeStoreTests.cs`

The port fronts all three containers. `Query*` methods are the browse reads (a case-insensitive substring match over the doc's searchable text) used by the backend endpoints and the `search_marker_library` tool; `Get*` are point reads; `Upsert*` exist for later writers (and tests seed through them here).

- [ ] **Step 1: Write the failing test** — create `src/Smx.Domain.Tests/InMemoryKnowledgeStoreTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Domain.Tests;

public class InMemoryKnowledgeStoreTests
{
    private static LearnedConclusionDoc Conclusion(string scopeKey, string finding) => new()
    {
        Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Material, scopeKey), Kind = KnowledgeKinds.Material,
        Scope = new ConclusionScope("Zr", null, "bottle", null, null, null), Finding = finding,
        Confidence = 0.9, Provenance = new ConclusionProvenance(["p1"], []), CreatedAt = "t",
    };

    private static MarkerLibraryDoc Marker(string key, string application) => new()
    {
        Id = KnowledgeIds.Marker(key), Composition = new MarkerComposition(["Zr"], 200, "1:0"),
        ValidatedFor = new ValidatedFor(application, "label", "overt"), SourceProject = "p1", CreatedAt = "t",
    };

    [Fact]
    public async Task LearnedConclusion_RoundTrips_AndQueryMatchesFinding()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertLearnedConclusionAsync(Conclusion("zr|bottle", "Zr neodecanoate is the preferred bottle form."));
        Assert.Equal("Zr neodecanoate is the preferred bottle form.",
            (await store.GetLearnedConclusionAsync(KnowledgeKinds.Material, "zr|bottle"))!.Finding);
        Assert.Single(await store.QueryLearnedConclusionsAsync("neodecanoate"));
        Assert.Empty(await store.QueryLearnedConclusionsAsync("cadmium"));
    }

    [Fact]
    public async Task Marker_RoundTrips_AndQueryMatchesValidatedFor()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMarkerAsync(Marker("m1", "anti-counterfeit"));
        Assert.Equal("anti-counterfeit", (await store.GetMarkerAsync(KnowledgeIds.Marker("m1")))!.ValidatedFor.Application);
        Assert.Single(await store.QueryMarkersAsync("anti-counterfeit"));
        Assert.Empty(await store.QueryMarkersAsync("banknote"));
    }

    [Fact]
    public async Task Msds_RoundTrips_ByCas()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMsdsAsync(new MsdsRegistryDoc { Id = KnowledgeIds.Msds("c1"), Cas = "c1", Supplier = "Acme", Version = "1", Date = "d" });
        Assert.Equal("Acme", (await store.GetMsdsAsync("c1"))!.Supplier);
        Assert.Single(await store.QueryMsdsAsync(null));
        Assert.Null(await store.GetMsdsAsync("nope"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~InMemoryKnowledgeStoreTests"`
Expected: FAIL — `IKnowledgeStore`/`InMemoryKnowledgeStore` don't exist.

- [ ] **Step 3: Create the port + fake.**

`src/Smx.Domain/IKnowledgeStore.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// Cross-project knowledge layer (design §6). Separate from IRecordStore: these containers are NOT
/// on the per-project `record` change-feed bus. `Query*` is a case-insensitive substring browse.
public interface IKnowledgeStore
{
    Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default);
    Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default);
    Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default);

    Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default);
    Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default);

    Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default);
    Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default);
    Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default);
}
```

`src/Smx.Domain.Tests/Fakes/InMemoryKnowledgeStore.cs`:

```csharp
using System.Collections.Concurrent;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// In-memory IKnowledgeStore for tests + WebApplicationFactory DI swaps. Mirrors InMemoryRecordStore.
public sealed class InMemoryKnowledgeStore : Smx.Domain.IKnowledgeStore
{
    private readonly ConcurrentDictionary<string, LearnedConclusionDoc> _conclusions = new();
    private readonly ConcurrentDictionary<string, MarkerLibraryDoc> _markers = new();
    private readonly ConcurrentDictionary<string, MsdsRegistryDoc> _msds = new();

    private static bool Match(string? search, params string?[] fields) =>
        string.IsNullOrWhiteSpace(search) ||
        fields.Any(f => f is not null && f.Contains(search, StringComparison.OrdinalIgnoreCase));

    public Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default) =>
        Task.FromResult(_conclusions.TryGetValue(KnowledgeIds.LearnedConclusion(kind, scopeKey), out var d) ? d : null);
    public Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LearnedConclusionDoc>>(_conclusions.Values
            .Where(c => Match(search, c.Finding, c.Scope.Element, c.Scope.Material, c.Scope.Application, c.Scope.Market, c.Scope.Substance)).ToList());
    public Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default) { _conclusions[doc.Id] = doc; return Task.CompletedTask; }

    public Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_markers.TryGetValue(id, out var d) ? d : null);
    public Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarkerLibraryDoc>>(_markers.Values
            .Where(m => Match(search, m.ValidatedFor.Application, m.ValidatedFor.Material, m.ValidatedFor.Objective)).ToList());
    public Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default) { _markers[doc.Id] = doc; return Task.CompletedTask; }

    public Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default) =>
        Task.FromResult(_msds.TryGetValue(KnowledgeIds.Msds(cas), out var d) ? d : null);
    public Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MsdsRegistryDoc>>(_msds.Values.Where(m => Match(search, m.Cas, m.Supplier)).ToList());
    public Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default) { _msds[doc.Id] = doc; return Task.CompletedTask; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~InMemoryKnowledgeStoreTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/IKnowledgeStore.cs src/Smx.Domain.Tests/Fakes/InMemoryKnowledgeStore.cs src/Smx.Domain.Tests/InMemoryKnowledgeStoreTests.cs
git commit -m "feat(store): IKnowledgeStore + in-memory fake (3 cross-project containers)"
```

---

## Task 3: CosmosKnowledgeStore (three containers)

**Files:**
- Create: `src/Smx.Infrastructure/CosmosKnowledgeStore.cs`

No unit test (it needs a live Cosmos emulator, out of scope for the suite — the `InMemoryKnowledgeStore` proves the contract, exactly as `CosmosRecordStore` has no unit test and `InMemoryRecordStore` proves `IRecordStore`). Verification is a compile + the mirrored query shape.

- [ ] **Step 1: Create the implementation.** `src/Smx.Infrastructure/CosmosKnowledgeStore.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

/// IKnowledgeStore over three cross-project Cosmos containers. PKs: learned-conclusions /kind,
/// marker-library /id, msds-registry /cas. Query* does a Cosmos CONTAINS (case-insensitive) browse.
public sealed class CosmosKnowledgeStore(Container conclusions, Container markers, Container msds) : IKnowledgeStore
{
    public Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default) =>
        ReadAsync<LearnedConclusionDoc>(conclusions, KnowledgeIds.LearnedConclusion(kind, scopeKey), kind, ct);
    public async Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default)
    {
        // Cosmos NoSQL has no `??`; CONTAINS on an absent/undefined path yields undefined, which the OR
        // correctly treats as non-matching — so the missing-scope-field case needs no coalesce.
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? "SELECT * FROM c WHERE c.type = @t"
            : "SELECT * FROM c WHERE c.type = @t AND (CONTAINS(c.finding, @s, true) OR CONTAINS(c.scope.element, @s, true) OR CONTAINS(c.scope.material, @s, true) OR CONTAINS(c.scope.application, @s, true) OR CONTAINS(c.scope.market, @s, true) OR CONTAINS(c.scope.substance, @s, true))")
            .WithParameter("@t", KnowledgeTypes.LearnedConclusion).WithParameter("@s", search ?? "");
        return await RunAsync<LearnedConclusionDoc>(conclusions, q, ct);
    }
    public Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default) =>
        conclusions.UpsertItemAsync(doc, new PartitionKey(doc.Kind), cancellationToken: ct);

    public Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default) =>
        ReadAsync<MarkerLibraryDoc>(markers, id, id, ct);
    public async Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default)
    {
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? "SELECT * FROM c WHERE c.type = @t"
            : "SELECT * FROM c WHERE c.type = @t AND (CONTAINS(c.validatedFor.application, @s, true) OR CONTAINS(c.validatedFor.material, @s, true) OR CONTAINS(c.validatedFor.objective, @s, true))")
            .WithParameter("@t", KnowledgeTypes.MarkerLibrary).WithParameter("@s", search ?? "");
        return await RunAsync<MarkerLibraryDoc>(markers, q, ct);
    }
    public Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default) =>
        markers.UpsertItemAsync(doc, new PartitionKey(doc.Id), cancellationToken: ct);

    public Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default) =>
        ReadAsync<MsdsRegistryDoc>(msds, KnowledgeIds.Msds(cas), cas, ct);
    public async Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default)
    {
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? "SELECT * FROM c WHERE c.type = @t"
            : "SELECT * FROM c WHERE c.type = @t AND (CONTAINS(c.cas, @s, true) OR CONTAINS(c.supplier, @s, true))")
            .WithParameter("@t", KnowledgeTypes.MsdsRegistry).WithParameter("@s", search ?? "");
        return await RunAsync<MsdsRegistryDoc>(msds, q, ct);
    }
    public Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default) =>
        msds.UpsertItemAsync(doc, new PartitionKey(doc.Cas), cancellationToken: ct);

    private static async Task<T?> ReadAsync<T>(Container c, string id, string pk, CancellationToken ct) where T : class
    {
        try { return (await c.ReadItemAsync<T>(id, new PartitionKey(pk), cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private static async Task<IReadOnlyList<T>> RunAsync<T>(Container c, QueryDefinition q, CancellationToken ct)
    {
        var results = new List<T>();
        using var it = c.GetItemQueryIterator<T>(q);
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}
```

- [ ] **Step 2: Verify it compiles.**

Run: `dotnet build src/Smx.Infrastructure/Smx.Infrastructure.csproj`
Expected: succeeds, 0 errors. (Property names in the SQL are camelCase because the `record` Cosmos client uses `SystemTextJsonCosmosSerializer(Json.Options)` — Web defaults → camelCase; the knowledge containers are wired with the same serializer in Task 7.)

- [ ] **Step 3: Commit**

```bash
git add src/Smx.Infrastructure/CosmosKnowledgeStore.cs
git commit -m "feat(store): CosmosKnowledgeStore over 3 cross-project containers"
```

---

## Task 4: Learned-conclusions AI Search read tool + fake

**Files:**
- Modify: `src/Smx.Domain/Tools/ITools.cs`
- Modify: `src/Smx.Infrastructure/Search/SearchTools.cs`
- Modify: `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`
- Test: `src/Smx.Orchestrator.Tests/FakeToolsTests.cs` (create — a tiny test that the fake honors `top`)

The read side of the `learned-conclusions` index. Same `SearchToolBase` pattern as `sds`/`regulatory`/`reference`. (The index's creation + push land in Plan 3b; here we only read, and a not-yet-created/empty index simply yields no chunks — cold-start-safe.)

- [ ] **Step 1: Write the failing test** — create `src/Smx.Orchestrator.Tests/FakeToolsTests.cs`:

```csharp
using Smx.Domain.Tools;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class FakeToolsTests
{
    [Fact]
    public async Task FakeLearnedConclusionsSearch_RecordsQuery_AndHonorsTop()
    {
        var fake = new FakeLearnedConclusionsSearch
        {
            Results = { new RetrievedChunk("learned-conclusions", "learned-conclusions/1", "a", 0.9),
                        new RetrievedChunk("learned-conclusions", "learned-conclusions/2", "b", 0.8) },
        };
        var got = await fake.SearchAsync("zr bottle", top: 1);
        Assert.Single(got);
        Assert.Equal("zr bottle", fake.Queries.Single());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~FakeToolsTests"`
Expected: FAIL — `ILearnedConclusionsSearch` / `FakeLearnedConclusionsSearch` don't exist.

- [ ] **Step 3: Add the interface, the Azure tool, and the fake.**

In `src/Smx.Domain/Tools/ITools.cs`, add alongside the other `I*Search` interfaces:

```csharp
public interface ILearnedConclusionsSearch { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }
```

In `src/Smx.Infrastructure/Search/SearchTools.cs`, add a **resilient** reader (NOT a plain `SearchToolBase` subclass). Unlike `sds`/`regulatory`/`reference` — which are always seeded — the `learned-conclusions` index has no writer until Plan 3b, so until then it does not exist; a `SearchClient` query against a missing index throws `RequestFailedException` (404). Cold-start safety (design §6) requires that to degrade to "no matches," not an agent-breaking error. So give this one a guarded body (add `using Azure;` to the file for `RequestFailedException`):

```csharp
public sealed class LearnedConclusionsSearchTool(SearchClient client) : ILearnedConclusionsSearch
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        try
        {
            var response = await client.SearchAsync<Dictionary<string, object>>(query, new SearchOptions { Size = top }, ct);
            var results = new List<RetrievedChunk>();
            await foreach (var r in response.Value.GetResultsAsync())
            {
                var doc = r.Document;
                var id = doc.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
                var content = doc.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                results.Add(new RetrievedChunk("learned-conclusions", $"{client.IndexName}/{id}", content, r.Score ?? 0));
            }
            return results;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            // Index not created yet (no conclusions written — Plan 3b creates it on first push). Cold-start → no matches.
            return [];
        }
    }
}
```

In `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`, add a dedicated fake (kept separate from `FakeSearch` so a test can seed conclusions independently):

```csharp
public sealed class FakeLearnedConclusionsSearch : ILearnedConclusionsSearch
{
    public List<string> Queries { get; } = [];
    public List<RetrievedChunk> Results { get; } = [];
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Results.Take(top).ToList());
    }
}
```

(Ensure `FakeTools.cs` has `using Smx.Domain.Tools;` — it already does for the existing fakes.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~FakeToolsTests"` → PASS.
Also `dotnet build src/Smx.Infrastructure/Smx.Infrastructure.csproj` → succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Tools/ITools.cs src/Smx.Infrastructure/Search/SearchTools.cs src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs src/Smx.Orchestrator.Tests/FakeToolsTests.cs
git commit -m "feat(tools): learned-conclusions AI Search read tool + fake"
```

---

## Task 5: ToolBox — search_marker_library + search_learned_conclusions

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/ToolBox.cs`
- Test: `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`

Two new tool methods. `search_marker_library` reads Cosmos via `IKnowledgeStore.QueryMarkersAsync`; `search_learned_conclusions` reads the index via `ILearnedConclusionsSearch`. Both return a JSON string with the same **`"no matches — do not invent"`** cold-start sentinel the existing tools use (design §6: an empty read must not induce fabrication).

- [ ] **Step 1: Write the failing test** — add to `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`. First, find the existing `Box()` helper (it constructs a `ToolBox` with the 5 fakes) and extend it to pass the two new dependencies; then add the tests. If the helper currently reads:

```csharp
private static ToolBox Box(FakeCatalogLookup? catalog = null, FakeCompatibilityLookup? compat = null, FakeSearch? search = null) =>
    new(catalog ?? new(), compat ?? new(), search ?? new(), search ?? new(), search ?? new());
```

change it to also thread a knowledge store + a learned-conclusions search (add optional params, defaulting to fresh fakes):

```csharp
private static ToolBox Box(FakeCatalogLookup? catalog = null, FakeCompatibilityLookup? compat = null,
    FakeSearch? search = null, Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore? knowledge = null,
    FakeLearnedConclusionsSearch? learned = null)
{
    var s = search ?? new();
    return new(catalog ?? new(), compat ?? new(), s, s, s, knowledge ?? new(), learned ?? new());
}
```

(Match the ToolBox ctor parameter ORDER you set in Step 3. The `Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore` type comes from the `Smx.Domain.Tests` project — confirm `Smx.Orchestrator.Tests.csproj` references `Smx.Domain.Tests`; the router/dispatcher endpoint tests already reuse `InMemoryRecordStore` from there, so the reference exists. If it does NOT, add a `ProjectReference` to `Smx.Domain.Tests.csproj` in `Smx.Orchestrator.Tests.csproj`.)

Then add:

```csharp
[Fact]
public void IntakeTools_IncludeMarkerLibraryAndLearnedConclusions()
{
    var names = Box().IntakeTools().Select(t => t.Name).OrderBy(x => x).ToArray();
    Assert.Contains("search_marker_library", names);
    Assert.Contains("search_learned_conclusions", names);
}

[Fact]
public void DiscoveryTools_IncludeLearnedConclusions()
{
    var names = Box().DiscoveryTools().Select(t => t.Name).ToArray();
    Assert.Contains("search_learned_conclusions", names);
}

[Fact]
public async Task SearchMarkerLibrary_EmptyStore_ReturnsNoMatchesSentinel()
{
    var json = await Box().SearchMarkerLibraryAsync("anti-counterfeit", default);
    Assert.Contains("no matches", json);
}

[Fact]
public async Task SearchLearnedConclusions_EmptyIndex_ReturnsNoMatchesSentinel()
{
    var json = await Box().SearchLearnedConclusionsAsync("zr bottle", default);
    Assert.Contains("no matches", json);
}

[Fact]
public async Task SearchMarkerLibrary_ReturnsSeededMatch()
{
    var knowledge = new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore();
    await knowledge.UpsertMarkerAsync(new Smx.Domain.Records.MarkerLibraryDoc
    {
        Id = Smx.Domain.Records.KnowledgeIds.Marker("m1"),
        Composition = new(["Zr"], 200, "1:0"), ValidatedFor = new("anti-counterfeit", "label", "overt"),
        SourceProject = "p1", CreatedAt = "t",
    });
    var json = await Box(knowledge: knowledge).SearchMarkerLibraryAsync("anti-counterfeit", default);
    Assert.Contains("anti-counterfeit", json);
    Assert.DoesNotContain("no matches", json);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ToolBoxTests"`
Expected: FAIL — the ctor arity is wrong and the tool methods don't exist.

- [ ] **Step 3: Add the ctor deps + tool methods.** In `src/Smx.Orchestrator/Agents/ToolBox.cs`:

Extend the primary constructor to take the two new deps (append them so existing positional call sites are easy to update):

```csharp
public sealed class ToolBox(
    ICatalogLookup catalog, ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory, ISdsSearch sds, IReferenceSearch reference,
    IKnowledgeStore knowledge, ILearnedConclusionsSearch learnedConclusions)
{
```

(Add `using Smx.Domain;` if not present — `IKnowledgeStore` is in that namespace; `ILearnedConclusionsSearch` is in `Smx.Domain.Tools`, already imported.)

Add `search_marker_library` to `IntakeTools()` and `search_learned_conclusions` to both `IntakeTools()` and `DiscoveryTools()` (keep the existing entries; append):

```csharp
    public IList<AITool> IntakeTools() =>
    [
        // ... existing search_regulatory, search_reference ...
        AIFunctionFactory.Create(SearchMarkerLibraryAsync, "search_marker_library",
            "Search the cross-project Marker Library for a previously approved code that fits this application/material/objective. Prefer reusing a validated code over inventing a new one; cite the source project."),
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Search accumulated Learned Conclusions (prior material/regulatory findings with confidence + provenance) relevant to this intake. Treat them as prior evidence, not fact; a higher-confidence, more recent conclusion supersedes an older one."),
    ];

    public IList<AITool> DiscoveryTools() =>
    [
        // ... existing search_catalog, lookup_compatibility, search_reference ...
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Search accumulated Learned Conclusions relevant to tiering this element/form (e.g. a prior overlap or a preferred form). Treat as prior evidence with confidence, not fact."),
    ];
```

Add the two tool methods (place them beside the existing `SearchCatalogAsync`/`SearchRegulatoryAsync` methods):

```csharp
    public async Task<string> SearchMarkerLibraryAsync(string query, CancellationToken ct)
    {
        var markers = await knowledge.QueryMarkersAsync(query, ct);
        return markers.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — no prior approved code fits; proceed without reuse, do not invent one\"}"
            : JsonSerializer.Serialize(new { results = markers }, Json.Options);
    }

    public async Task<string> SearchLearnedConclusionsAsync(string query, CancellationToken ct)
    {
        var chunks = await learnedConclusions.SearchAsync(query, 5, ct);
        return chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — no prior conclusions on this; reason from primary sources, do not fabricate a prior finding\"}"
            : JsonSerializer.Serialize(new { results = chunks }, Json.Options);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ToolBoxTests"`
Expected: PASS. (This will not build until Task 7 updates `Program.cs`'s `ToolBox` registration — but `ToolBoxTests` construct `ToolBox` directly via `Box()`, and the orchestrator app project only needs to compile, which it does since the ctor change is source-compatible with a not-yet-updated `Program.cs` ONLY IF `Program.cs` is updated too. To keep the solution green, do Task 7's `Program.cs` `ToolBox` registration edit as part of THIS task's Step 3 if the solution build breaks — the two are coupled by the ctor signature. Simplest: make the `Program.cs` `ToolBox` registration change now, and Task 7 covers the remaining DI.)

> **Coupling note:** changing the `ToolBox` ctor arity breaks `src/Smx.Orchestrator/Program.cs` where `ToolBox` is registered. To keep the build green in this task, also register `IKnowledgeStore` + `ILearnedConclusionsSearch` and pass them into `ToolBox` in `Program.cs` now (the exact lines are in Task 7 Step 3). Then Task 7 is a no-op verification for that part and only adds the `BackendOptions` settings + backend registration. If you prefer, fold Task 7's `Program.cs`/`BackendOptions` edits into this task and renumber — the reviewer only cares that the solution is green at each commit.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ToolBox.cs src/Smx.Orchestrator.Tests/ToolBoxTests.cs src/Smx.Orchestrator/Program.cs src/Smx.Infrastructure/BackendOptions.cs
git commit -m "feat(tools): search_marker_library + search_learned_conclusions (cold-start-safe)"
```

---

## Task 6: Wire the tools into the Intake + Discovery agent instructions

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/IntakeAgent.cs`
- Modify: `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/{IntakeAgentTests,DiscoveryAgentTests}.cs` (assert unchanged-green; add nothing behavioral)

Task 5 registered the tools in the tool *lists*. This task adds one `Instructions` line to each agent naming the tool and when to use it, so the LLM actually reaches for it. Deterministic tests don't exercise the LLM, so this is behavior-neutral for the suite — the point is the prompt, and we assert the existing agent tests stay green (the `Instructions` string change must not break the validators or the scripted-response parsing).

- [ ] **Step 1: Confirm the existing agent tests are green (baseline).**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~IntakeAgentTests|FullyQualifiedName~DiscoveryAgentTests"`
Expected: PASS (all existing).

- [ ] **Step 2: Add the instruction lines.** In `src/Smx.Orchestrator/Agents/IntakeAgent.cs`, inside the `Instructions` string, add (near where the other tools are described — keep the exact surrounding prose, insert one bullet):

```
- Before proposing scope, call search_marker_library with the application/material/objective to find a prior approved code to reuse; if one fits, note it as a reuse candidate with its source project. Call search_learned_conclusions for prior findings on these materials/markets; treat any hit as prior evidence with confidence + provenance, never as ground truth, and never invent a conclusion if the tool returns no matches.
```

In `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`, inside its `Instructions` string, add:

```
- Call search_learned_conclusions when tiering an element/form (e.g. to reuse a prior overlap finding or a preferred-form conclusion). A higher-confidence, more recent conclusion supersedes an older one. If the tool returns no matches, tier from the primary sources (catalog + compatibility + reference) — do not fabricate a prior finding.
```

- [ ] **Step 3: Run the agent tests to verify still green.**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~IntakeAgentTests|FullyQualifiedName~DiscoveryAgentTests"`
Expected: PASS (unchanged count). If any validator test asserts an exact `Instructions` substring and now fails, that is a signal the assertion was over-specified — fix the test to assert the behavior, not the prompt prose (do NOT weaken a real validation).

- [ ] **Step 4: Full orchestrator suite green.**

Run: `dotnet test src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/IntakeAgent.cs src/Smx.Orchestrator/Agents/DiscoveryAgent.cs
git commit -m "feat(agents): Intake/Discovery instructions reach for the knowledge-layer tools"
```

---

## Task 7: DI wiring — BackendOptions settings + orchestrator + backend registration

**Files:**
- Modify: `src/Smx.Infrastructure/BackendOptions.cs`
- Modify: `src/Smx.Orchestrator/Program.cs` (if not already done in Task 5)
- Modify: `src/Smx.Backend/Program.cs`

Adds the container/index name settings and registers `IKnowledgeStore` in **both** hosts (the orchestrator needs it for `ToolBox`/tools; the backend needs it for the browse endpoints), plus `ILearnedConclusionsSearch` in the orchestrator. Backend registration is **conditional on Cosmos config**, mirroring `IRecordStore`, so tests inject the in-memory fake.

- [ ] **Step 1: Add the settings.** In `src/Smx.Infrastructure/BackendOptions.cs`, add fields to the options record + the `From(IConfiguration c)` factory (mirror the existing `CatalogContainer` / `SdsIndex` lines):

```csharp
    // ... existing fields ...
    string LearnedConclusionsContainer,
    string MarkerLibraryContainer,
    string MsdsRegistryContainer,
    string LearnedConclusionsIndex,
```

and in `From`:

```csharp
        LearnedConclusionsContainer: c["LEARNED_CONCLUSIONS_CONTAINER"] ?? "learned-conclusions",
        MarkerLibraryContainer: c["MARKER_LIBRARY_CONTAINER"] ?? "marker-library",
        MsdsRegistryContainer: c["MSDS_REGISTRY_CONTAINER"] ?? "msds-registry",
        LearnedConclusionsIndex: c["LEARNED_CONCLUSIONS_SEARCH_INDEX"] ?? "learned-conclusions",
```

- [ ] **Step 2: Register in the orchestrator.** In `src/Smx.Orchestrator/Program.cs`, after the `ICatalogLookup` registration, add (if not already added in Task 5):

```csharp
builder.Services.AddSingleton<IKnowledgeStore>(sp =>
{
    var cosmos = sp.GetRequiredService<CosmosClient>();
    return new CosmosKnowledgeStore(
        cosmos.GetContainer(opts.CosmosDatabase, opts.LearnedConclusionsContainer),
        cosmos.GetContainer(opts.CosmosDatabase, opts.MarkerLibraryContainer),
        cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer));
});
builder.Services.AddSingleton<ILearnedConclusionsSearch>(new LearnedConclusionsSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.LearnedConclusionsIndex, credential)));
```

Then update the `ToolBox` registration to pass the two new deps (if `ToolBox` is registered via `AddSingleton<ToolBox>()` with constructor injection, the container resolves them automatically once they're registered — verify whether `ToolBox` is `AddSingleton<ToolBox>()` (auto-wire, no change needed) or `AddSingleton(sp => new ToolBox(...))` (explicit — add the two args)).

- [ ] **Step 3: Register in the backend (conditional on Cosmos config).** In `src/Smx.Backend/Program.cs`, find the block that conditionally registers `CosmosClient` + `IRecordStore` when `COSMOS_ACCOUNT_ENDPOINT` is set, and add `IKnowledgeStore` in the same block:

```csharp
    builder.Services.AddSingleton<IKnowledgeStore>(sp =>
    {
        var cosmos = sp.GetRequiredService<CosmosClient>();
        var opts = BackendOptions.From(builder.Configuration);
        return new CosmosKnowledgeStore(
            cosmos.GetContainer(opts.CosmosDatabase, opts.LearnedConclusionsContainer),
            cosmos.GetContainer(opts.CosmosDatabase, opts.MarkerLibraryContainer),
            cosmos.GetContainer(opts.CosmosDatabase, opts.MsdsRegistryContainer));
    });
```

(Match however the existing block obtains `BackendOptions`/`CosmosClient` — reuse the same locals, don't re-create the client. Add `using Smx.Infrastructure;` / `using Smx.Domain;` if the file lacks them.)

- [ ] **Step 4: Build the full solution.**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors (the 2 pre-existing `ManagedIdentityCredential` obsolete warnings are expected; no NEW warnings).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Infrastructure/BackendOptions.cs src/Smx.Orchestrator/Program.cs src/Smx.Backend/Program.cs
git commit -m "feat(di): register IKnowledgeStore (both hosts) + learned-conclusions search (orchestrator)"
```

---

## Task 8: Backend browse endpoints — marker-library + learned-conclusions

**Files:**
- Create: `src/Smx.Backend/Api/KnowledgeEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs` (call `app.MapKnowledgeEndpoints()`)
- Test: `src/Smx.Backend.Tests/KnowledgeEndpointsTests.cs` (create)

Structured browse reads over Cosmos (design §7). Thin: query the store, return JSON. The backend does NOT touch AI Search — semantic retrieval is the agent's job; the backend browse is a substring query over the authoritative Cosmos store.

- [ ] **Step 1: Write the failing test** — create `src/Smx.Backend.Tests/KnowledgeEndpointsTests.cs`:

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

public class KnowledgeEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryKnowledgeStore _knowledge = new();
    private readonly HttpClient _client;

    public KnowledgeEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IKnowledgeStore>(_knowledge))).CreateClient();
    }

    [Fact]
    public async Task GetMarkerLibrary_ReturnsMatches_AndEmptyArrayOnColdStart()
    {
        var empty = await _client.GetFromJsonAsync<JsonElement>("/marker-library?search=anything");
        Assert.Equal(0, empty.GetArrayLength());

        await _knowledge.UpsertMarkerAsync(new MarkerLibraryDoc
        {
            Id = KnowledgeIds.Marker("m1"), Composition = new(["Zr"], 200, "1:0"),
            ValidatedFor = new("anti-counterfeit", "label", "overt"), SourceProject = "p1", CreatedAt = "t",
        });
        var hit = await _client.GetFromJsonAsync<JsonElement>("/marker-library?search=anti-counterfeit");
        Assert.Equal(1, hit.GetArrayLength());
    }

    [Fact]
    public async Task GetLearnedConclusions_FiltersBySearch()
    {
        await _knowledge.UpsertLearnedConclusionAsync(new LearnedConclusionDoc
        {
            Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Material, "zr|bottle"), Kind = KnowledgeKinds.Material,
            Scope = new("Zr", null, "bottle", null, null, null), Finding = "Zr neodecanoate preferred.",
            Confidence = 0.9, Provenance = new(["p1"], []), CreatedAt = "t",
        });
        var hit = await _client.GetFromJsonAsync<JsonElement>("/learned-conclusions?search=neodecanoate");
        Assert.Equal(1, hit.GetArrayLength());
        var miss = await _client.GetFromJsonAsync<JsonElement>("/learned-conclusions?search=cadmium");
        Assert.Equal(0, miss.GetArrayLength());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~KnowledgeEndpointsTests.GetMarkerLibrary_ReturnsMatches_AndEmptyArrayOnColdStart"`
Expected: FAIL — the routes don't exist (404).

- [ ] **Step 3: Create the endpoints + wire them.** Create `src/Smx.Backend/Api/KnowledgeEndpoints.cs`:

```csharp
using Smx.Domain;

namespace Smx.Backend.Api;

public static class KnowledgeEndpoints
{
    public static void MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/marker-library", async (string? search, IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryMarkersAsync(search, ct), Json.Options));

        app.MapGet("/learned-conclusions", async (string? search, IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryLearnedConclusionsAsync(search, ct), Json.Options));

        // /msds-registry (GET + review) added in Task 9.
    }
}
```

In `src/Smx.Backend/Program.cs`, add the call next to `app.MapProjectEndpoints();`:

```csharp
app.MapProjectEndpoints();
app.MapKnowledgeEndpoints();
```

(Add `using Smx.Backend.Api;` if not already present.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~KnowledgeEndpointsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/KnowledgeEndpoints.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/KnowledgeEndpointsTests.cs
git commit -m "feat(api): GET /marker-library + /learned-conclusions browse reads"
```

---

## Task 9: MSDS Registry endpoints — GET + review action

**Files:**
- Modify: `src/Smx.Backend/Api/KnowledgeEndpoints.cs`
- Test: `src/Smx.Backend.Tests/KnowledgeEndpointsTests.cs`

`GET /msds-registry` browses; `POST /msds-registry/{cas}/review` is the operator action that flips `ReviewStatus` to `reviewed` (feeds the MSDS-before-order precondition consumed in Plan 5). 404 if the CAS isn't registered.

- [ ] **Step 1: Write the failing test** — add to `KnowledgeEndpointsTests`:

```csharp
    [Fact]
    public async Task Msds_Review_FlipsStatus_And404ForUnknown()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("13463-67-7"), Cas = "13463-67-7", Supplier = "Acme", Version = "3", Date = "2025-01-01",
        });
        var ok = await _client.PostAsJsonAsync("/msds-registry/13463-67-7/review", new { });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("reviewed", (await _knowledge.GetMsdsAsync("13463-67-7"))!.ReviewStatus);

        var missing = await _client.PostAsJsonAsync("/msds-registry/nope/review", new { });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task GetMsds_BrowsesAll()
    {
        await _knowledge.UpsertMsdsAsync(new MsdsRegistryDoc
        {
            Id = KnowledgeIds.Msds("c1"), Cas = "c1", Supplier = "Acme", Version = "1", Date = "d",
        });
        var all = await _client.GetFromJsonAsync<JsonElement>("/msds-registry");
        Assert.Equal(1, all.GetArrayLength());
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~KnowledgeEndpointsTests.Msds_Review_FlipsStatus_And404ForUnknown"`
Expected: FAIL — the routes don't exist.

- [ ] **Step 3: Add the endpoints.** In `src/Smx.Backend/Api/KnowledgeEndpoints.cs`, replace the `// /msds-registry ...` comment with:

```csharp
        app.MapGet("/msds-registry", async (string? search, IKnowledgeStore store, CancellationToken ct) =>
            Results.Json(await store.QueryMsdsAsync(search, ct), Json.Options));

        app.MapPost("/msds-registry/{cas}/review", async (string cas, IKnowledgeStore store, CancellationToken ct) =>
        {
            if (await store.GetMsdsAsync(cas, ct) is not { } m)
                return Results.NotFound();
            m.ReviewStatus = "reviewed";
            await store.UpsertMsdsAsync(m, ct);
            return Results.Ok(new { m.Cas, m.ReviewStatus });
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~KnowledgeEndpointsTests"`
Expected: PASS (4 tests total in the class).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/KnowledgeEndpoints.cs src/Smx.Backend.Tests/KnowledgeEndpointsTests.cs
git commit -m "feat(api): GET /msds-registry + POST /msds-registry/{cas}/review"
```

---

## Task 10: Infra — three Cosmos containers + env vars (both topologies)

**Files:**
- Modify: `infra/modules/data.bicep`, `infra/single-rg/modules/data.bicep`
- Modify: `infra/modules/compute.bicep`, `infra/single-rg/modules/compute.bicep`

Both `data.bicep` files and both `compute.bicep` files are **byte-identical twins today** — apply the identical edit to each of the pair, per the CLAUDE.md twin rule.

- [ ] **Step 1: Add the containers.** In `infra/modules/data.bicep`, next to the existing `refContainers` loop, add a knowledge-containers loop:

```bicep
var knowledgeContainers = [
  { name: 'learned-conclusions', pk: '/kind' }
  { name: 'marker-library', pk: '/id' }
  { name: 'msds-registry', pk: '/cas' }
]
resource knowledgeCosmosContainers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for c in knowledgeContainers: {
  parent: cosmosDb
  name: c.name
  properties: {
    resource: {
      id: c.name
      partitionKey: { paths: [ c.pk ], kind: 'Hash' }
    }
  }
}]
```

Apply the **identical** block to `infra/single-rg/modules/data.bicep`.

- [ ] **Step 2: Add the env vars.** In `infra/modules/compute.bicep`, extend the `sharedEnv` array (applied to both backend + orchestrator) with the container + index names:

```bicep
  { name: 'LEARNED_CONCLUSIONS_CONTAINER', value: 'learned-conclusions' }
  { name: 'MARKER_LIBRARY_CONTAINER', value: 'marker-library' }
  { name: 'MSDS_REGISTRY_CONTAINER', value: 'msds-registry' }
  { name: 'LEARNED_CONCLUSIONS_SEARCH_INDEX', value: 'learned-conclusions' }
```

Apply the **identical** addition to `infra/single-rg/modules/compute.bicep`.

(Setting these explicitly — even though `BackendOptions` defaults match — follows the more-explicit Functions-app convention and avoids the `regulatory-index`/`regulatory-corpus`-class name-drift bug.)

- [ ] **Step 3: Validate both topologies compile.**

Run:
```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```
Expected: both exit 0 (the one pre-existing `ai.bicep` `modelProviderData` warning is expected; no new errors).

- [ ] **Step 4: Confirm the twins stayed identical.**

Run:
```bash
diff infra/modules/data.bicep infra/single-rg/modules/data.bicep
diff infra/modules/compute.bicep infra/single-rg/modules/compute.bicep
```
Expected: no differences (both pairs remain byte-identical).

- [ ] **Step 5: Commit**

```bash
git add infra/modules/data.bicep infra/single-rg/modules/data.bicep infra/modules/compute.bicep infra/single-rg/modules/compute.bicep
git commit -m "infra: 3 knowledge Cosmos containers + app env vars (both topologies)"
```

---

## Task 11: Full green + cold-start integration sanity

**Files:** none (verification task).

- [ ] **Step 1: Full solution build.**

Run: `dotnet build src/Smx.Backend.sln`
Expected: 0 errors, no NEW warnings (only the 2 pre-existing `ManagedIdentityCredential` obsolete-ctor warnings).

- [ ] **Step 2: Full test run.**

Run: `dotnet test src/Smx.Backend.sln`
Expected: ALL pass. New test classes green: `KnowledgeDocsTests`, `InMemoryKnowledgeStoreTests`, `FakeToolsTests`, `KnowledgeEndpointsTests`, plus the extended `ToolBoxTests`. Baseline was 101; expect ~+18 tests.

- [ ] **Step 3: Cold-start assertion (the design's load-bearing safety property).**

Confirm the empty-read behavior is covered end-to-end: `ToolBoxTests.SearchMarkerLibrary_EmptyStore_ReturnsNoMatchesSentinel`, `ToolBoxTests.SearchLearnedConclusions_EmptyIndex_ReturnsNoMatchesSentinel`, and `KnowledgeEndpointsTests.GetMarkerLibrary_ReturnsMatches_AndEmptyArrayOnColdStart` all pass. These prove an empty knowledge layer returns "no matches — do not invent" (tools) / an empty array (endpoints), never an error — the cold-start-safe requirement (design §6, Decision #6).

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~SearchMarkerLibrary_EmptyStore|FullyQualifiedName~SearchLearnedConclusions_EmptyIndex|FullyQualifiedName~GetMarkerLibrary_ReturnsMatches"`
Expected: PASS (3).

- [ ] **Step 4: Confirm the eval harness still builds/passes (unaffected).**

Run: `dotnet build tools/Smx.Eval/Smx.Eval.csproj && dotnet test tools/Smx.Eval.Tests/Smx.Eval.Tests.csproj`
Expected: build succeeds, eval tests PASS (this plan adds read-only surfaces; the pipeline/eval is untouched).

- [ ] **Step 5: Commit any final cleanup (only if needed; otherwise skip).**

```bash
git add -A && git commit -m "chore: plan 3a final green" || echo "nothing to commit"
```

---

## Notes for the implementer

- **Additive plan — green after every task.** Run `dotnet test src/Smx.Backend.sln` freely between tasks. The one coupling to watch is the `ToolBox` ctor arity (Task 5) which forces the `Program.cs` registration edit (Task 7 Step 2) in the same commit — see the Task 5 coupling note.
- **Cross-project ≠ record bus.** These three containers are NOT on the `record` change feed and have NO `Type`-discriminated router entry — do not add them to `RecordDocRouter`/`StageDispatcher`. They are read/written directly through `IKnowledgeStore`, never dispatched.
- **Read-only in this plan.** The only writers of knowledge docs here are TESTS. The real writers are Plan 3b (revise → Learned Conclusion, plus the AI Search push/embed client) and Plan 5 (VP-close → Marker Library + Learned Conclusions; MSDS entries). Do not build those here.
- **Cold-start sentinel is load-bearing, not decoration.** Every knowledge read tool returns the `"no matches — do not invent"` note on empty, mirroring the existing `search_catalog`/`lookup_compatibility` tools. This is the anti-fabrication guard (design §6). Keep the exact sentinel substring `"no matches"` so the `ToolBoxTests` assertions hold and the agent prompt can rely on it.
- **camelCase in Cosmos SQL.** `CosmosKnowledgeStore`'s `CONTAINS(c.finding, …)` etc. use camelCase property names because the containers are wired with `SystemTextJsonCosmosSerializer(Json.Options)` (Web defaults → camelCase), same as the `record` container. If a query returns nothing against a real emulator, first suspect a casing mismatch.
- **No `ai.bicep` change.** AI Search indexes in this repo are **code-created** (`EnsureIndexAsync`), not bicep resources — so the `learned-conclusions` index needs no bicep. Its creation + push land in Plan 3b (the first writer). The read tool tolerates the not-yet-created index as an empty result (cold-start).
- **This is Plan 3a of 3.** Next: **Plan 3b** — revise-with-reason endpoint + chat-parity tool, the dispatcher re-run, the Learned-Conclusion write (Cosmos + the new AI Search push/embedding client that this plan deliberately deferred), and the **write→read round-trip proof** (a revise writes a conclusion that a later Discovery read retrieves). Then **Plan 3c** — the `chat-message`/`chat-reply` thread + interactive dispatch + `apply_revision`/`record_answer` tools + the "chat never signs a gate" guardrail.
```

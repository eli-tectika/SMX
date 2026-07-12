# Chemistry Backend — Plan 1: Per-Stage Refactor + Discovery — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split the folded `Screening` agent into a per-stage **Discovery** agent (element pools → tiered candidate substances) and a **Regulatory** agent (the 3-dimension battery → verdicts), flip the intake contract from handed-in substances to **element pools**, and run the pipeline straight-through **Intake → Discovery → Regulatory → Matrix** — while the matrix/xlsx export and the eval harness keep working.

**Architecture:** Record-as-bus over the Cosmos `record` container (change-feed dispatch); `Smx.Domain` holds records + pure logic, `Smx.Orchestrator` hosts the MAF agents + tools + dispatcher, `Smx.Backend` is the thin API front door. This plan is the first of five (design: [`2026-07-12-chemistry-backend-end-to-end-design.md`](../specs/2026-07-12-chemistry-backend-end-to-end-design.md) §9). It establishes the correct per-stage structure the remaining plans build on. **Compatibility stops being a verdict dimension** and becomes a Discovery tiering input (via `lookup_compatibility`); Regulatory owns exactly `ElementGate`, `ApplicationCheck`, `Hazard`.

**Tech Stack:** .NET 8, xUnit, Microsoft Agent Framework (MAF) via `Microsoft.Extensions.AI`, Azure Cosmos DB (`Microsoft.Azure.Cosmos`), Azure AI Search (`Azure.Search.Documents`), ClosedXML.

**Key design decision — the eval seam (known-candidate mode):** Discovery is an LLM stage, so its candidate picks are non-deterministic. To keep the eval's reasoning track graded against fixed `(CAS, component)` cells, the POST payload may carry explicit `candidates`. When present, the dispatcher writes them as the `candidates` doc verbatim and **skips the Discovery agent**; when absent, the Discovery agent generates candidates from the element pools. Production uses element pools; the eval + integration tests use known-candidate mode.

---

## File Structure

**Domain (`src/Smx.Domain/`)**
- `Records/ConstraintsDoc.cs` — **modify**: add `ElementPool`, `CandidateSubstance` records; `ConstraintsDoc` gains `ElementPools` + `ProvidedCandidates`, drops `Substances`.
- `Records/CandidatesDoc.cs` — **create**: the Discovery output doc.
- `Records/RecordIds.cs` — **modify**: add `Candidates` type + id builder; replace `Screening` stage with `Discovery` + `Regulatory`.
- `Records/ProjectDoc.cs` — **modify**: seed `intake/discovery/regulatory/matrix` stages.
- `IRecordStore.cs` — **modify**: add `GetCandidatesAsync` / `UpsertCandidatesAsync`.
- `MatrixAssembler.cs` — **modify**: assemble from candidates (non-`C`), not `constraints.Substances`.
- `Tools/ITools.cs` — **modify**: add `ICatalogLookup` + `CatalogCard`.

**Infrastructure (`src/Smx.Infrastructure/`)**
- `CosmosRecordStore.cs` — **modify**: candidates get/upsert.
- `Search/CatalogLookup.cs` — **create**: `CosmosCatalogLookup` over `ref-catalog` (PK `/element`).
- `BackendOptions.cs` — **modify**: add `CatalogContainer` (default `ref-catalog`).

**Orchestrator (`src/Smx.Orchestrator/`)**
- `Agents/DiscoveryAgent.cs` — **create**.
- `Agents/RegulatoryAgent.cs` — **create** (replaces `ScreeningAgent.cs`, which is deleted).
- `Agents/ToolBox.cs` — **modify**: `DiscoveryTools()` + `RegulatoryTools()` (drop `ScreeningTools`); add `search_catalog`.
- `Dispatch/StageDispatcher.cs` — **modify**: Discovery + Regulatory stages, assemble from candidates.
- `Dispatch/AgentRuns.cs` — **modify**: `RunDiscoveryAsync` + `RunRegulatoryAsync`.
- `Dispatch/RecordDocRouter.cs` — **modify**: route `candidates`.
- `Program.cs` — **modify**: DI for `ICatalogLookup`.

**Backend (`src/Smx.Backend/`)**
- `Api/CreateProjectRequest.cs` — **modify**: `ElementPools` + optional `Candidates`.

**Tests** — `Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, `Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`, `Smx.Orchestrator.Tests/Fakes/FakeTools.cs`, `StageDispatcherTests.cs`, `DiscoveryAgentTests.cs` (new), `RegulatoryAgentTests.cs` (replaces `ScreeningAgentTests.cs`), `ToolBoxTests.cs`, `Smx.Backend.Tests/ProjectEndpointsTests.cs`, and `tools/Smx.Eval/golden/starter.json`.

**Build/test commands** (from repo root):
- Build: `dotnet build src/Smx.Backend.sln`
- Test all: `dotnet test src/Smx.Backend.sln`
- Test one: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~<Class>.<Method>"`

---

## Task 1: Domain records — element pools & candidates

**Files:**
- Modify: `src/Smx.Domain/Records/ConstraintsDoc.cs`
- Create: `src/Smx.Domain/Records/CandidatesDoc.cs`
- Modify: `src/Smx.Domain/Records/RecordIds.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test** — append to `src/Smx.Domain.Tests/RecordDocsTests.cs`:

```csharp
[Fact]
public void CandidatesDoc_HasDeterministicId_AndCandidatesType()
{
    var doc = new CandidatesDoc
    {
        Id = RecordIds.Candidates("p1"), ProjectId = "p1",
        Substances = [new("bottle", "Y", "2-ethylhexanoate", "136-25-4", "sub-micron", "mineral spirits", true, "A", "clean XRF, catalog-available", [new Citation("catalog", "ref-catalog/product|Y|x", "t")])],
    };
    Assert.Equal("p1|candidates", doc.Id);
    Assert.Equal(RecordTypes.Candidates, doc.Type);
    Assert.Equal("A", doc.Substances[0].Tier);
    Assert.True(doc.Substances[0].Preferred);
}

[Fact]
public void ElementPool_CarriesComponentAndSignalNote()
{
    var pool = new ElementPool("liquid", "Sc", "Kα", "L", "small-amount peak");
    Assert.Equal("liquid", pool.Component);
    Assert.Equal("L", pool.Status);
    Assert.Equal("small-amount peak", pool.SignalNote);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RecordDocsTests.CandidatesDoc_HasDeterministicId_AndCandidatesType"`
Expected: FAIL — `CandidatesDoc`/`ElementPool` do not exist (compile error).

- [ ] **Step 3: Add the records.** In `src/Smx.Domain/Records/ConstraintsDoc.cs`, add these record types above `ConstraintsDoc` (keep `Citation`, `ComponentSpec`, `SubstanceSpec`, `AppliedList`):

```csharp
public sealed record ElementPool(string Component, string Element, string Line, string Status, string? SignalNote = null); // Status: "V" | "L"

public sealed record CandidateSubstance(
    string ComponentId, string Element, string Form, string Cas,
    string? ParticleSize, string? Solvent, bool Preferred, string Tier, string Rationale,
    IReadOnlyList<Citation> Citations); // Tier: "A" | "B" | "C"
```

Create `src/Smx.Domain/Records/CandidatesDoc.cs`:

```csharp
namespace Smx.Domain.Records;

public sealed class CandidatesDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Candidates;
    public List<CandidateSubstance> Substances { get; set; } = [];
}
```

In `src/Smx.Domain/Records/RecordIds.cs`, add `Candidates` to `RecordTypes` and a `Candidates` id builder:

```csharp
public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Candidates = "candidates";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
}
```

```csharp
public static string Candidates(string projectId) => $"{projectId}|candidates";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RecordDocsTests.CandidatesDoc_HasDeterministicId_AndCandidatesType|FullyQualifiedName~RecordDocsTests.ElementPool_CarriesComponentAndSignalNote"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/ConstraintsDoc.cs src/Smx.Domain/Records/CandidatesDoc.cs src/Smx.Domain/Records/RecordIds.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): add ElementPool + CandidateSubstance + CandidatesDoc"
```

---

## Task 2: Stage constants — discovery & regulatory

**Files:**
- Modify: `src/Smx.Domain/Records/RecordIds.cs`
- Modify: `src/Smx.Domain/Records/ProjectDoc.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test** — append to `RecordDocsTests.cs`:

```csharp
[Fact]
public void ProjectCreate_SeedsIntakeDiscoveryRegulatoryMatrix()
{
    var p = ProjectDoc.Create("p1", "Acme", "P", System.Text.Json.JsonDocument.Parse("{}").RootElement);
    Assert.True(p.Stages.ContainsKey(Stages.Intake));
    Assert.True(p.Stages.ContainsKey(Stages.Discovery));
    Assert.True(p.Stages.ContainsKey(Stages.Regulatory));
    Assert.True(p.Stages.ContainsKey(Stages.Matrix));
    Assert.False(p.Stages.ContainsKey("screening"));
    Assert.Equal(4, p.Stages.Count);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RecordDocsTests.ProjectCreate_SeedsIntakeDiscoveryRegulatoryMatrix"`
Expected: FAIL — `Stages.Discovery`/`Stages.Regulatory` do not exist (compile error).

- [ ] **Step 3: Replace the stage constants.** In `src/Smx.Domain/Records/RecordIds.cs`, replace the `Stages` class:

```csharp
public static class Stages
{
    public const string Intake = "intake";
    public const string Discovery = "discovery";
    public const string Regulatory = "regulatory";
    public const string Matrix = "matrix";
}
```

In `src/Smx.Domain/Records/ProjectDoc.cs`, replace the `Stages` dictionary in `Create`:

```csharp
        Stages = new()
        {
            [Records.Stages.Intake] = new StageState(),
            [Records.Stages.Discovery] = new StageState(),
            [Records.Stages.Regulatory] = new StageState(),
            [Records.Stages.Matrix] = new StageState(),
        },
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RecordDocsTests.ProjectCreate_SeedsIntakeDiscoveryRegulatoryMatrix"`
Expected: PASS. (Other projects won't compile yet — that's expected until Task 8+; run this filter against `Smx.Domain.Tests` only if the solution build blocks: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~ProjectCreate_SeedsIntakeDiscoveryRegulatoryMatrix"`.)

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/RecordIds.cs src/Smx.Domain/Records/ProjectDoc.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): replace screening stage with discovery + regulatory"
```

---

## Task 3: ConstraintsDoc — element pools in, substances out

**Files:**
- Modify: `src/Smx.Domain/Records/ConstraintsDoc.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test** — append to `RecordDocsTests.cs`:

```csharp
[Fact]
public void ConstraintsDoc_CarriesElementPools_AndProvidedCandidates()
{
    var c = new ConstraintsDoc
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "PET", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Y", "Kα", "V", null)],
        ProvidedCandidates = [new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "provided", [])],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };
    Assert.Single(c.ElementPools);
    Assert.Single(c.ProvidedCandidates);
    Assert.Equal("V", c.ElementPools[0].Status);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~ConstraintsDoc_CarriesElementPools_AndProvidedCandidates"`
Expected: FAIL — `ElementPools`/`ProvidedCandidates` members do not exist.

- [ ] **Step 3: Update `ConstraintsDoc`.** In `src/Smx.Domain/Records/ConstraintsDoc.cs`, replace the `Substances` line inside `ConstraintsDoc` with element pools + provided candidates:

```csharp
    public List<ComponentSpec> Components { get; set; } = [];
    public List<ElementPool> ElementPools { get; set; } = [];
    /// Known-candidate mode (eval/integration): when non-empty, Discovery is bypassed and these
    /// become the candidates doc verbatim. Empty ⇒ the Discovery agent generates candidates.
    public List<CandidateSubstance> ProvidedCandidates { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    public List<AppliedList> DerivedScope { get; set; } = [];
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~ConstraintsDoc_CarriesElementPools_AndProvidedCandidates"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/ConstraintsDoc.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): ConstraintsDoc carries element pools + provided candidates"
```

---

## Task 4: Record store — candidates get/upsert

**Files:**
- Modify: `src/Smx.Domain/IRecordStore.cs`
- Modify: `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`
- Modify: `src/Smx.Infrastructure/CosmosRecordStore.cs`
- Test: `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`

- [ ] **Step 1: Write the failing test** — append to `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`:

```csharp
[Fact]
public async Task Candidates_UpsertThenGet_RoundTrips()
{
    var store = new Smx.Domain.Tests.Fakes.InMemoryRecordStore();
    await store.UpsertCandidatesAsync(new CandidatesDoc
    {
        Id = RecordIds.Candidates("p1"), ProjectId = "p1",
        Substances = [new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "r", [])],
    });
    var got = await store.GetCandidatesAsync("p1");
    Assert.NotNull(got);
    Assert.Single(got!.Substances);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~Candidates_UpsertThenGet_RoundTrips"`
Expected: FAIL — `GetCandidatesAsync`/`UpsertCandidatesAsync` not on `IRecordStore`.

- [ ] **Step 3: Add the store methods.** In `src/Smx.Domain/IRecordStore.cs`, add to the interface:

```csharp
    Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default);
    Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default);
```

In `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, add:

```csharp
    public Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Candidates(projectId), out var d) ? (CandidatesDoc?)d : null);
    public Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
```

In `src/Smx.Infrastructure/CosmosRecordStore.cs`, add:

```csharp
    public Task<CandidatesDoc?> GetCandidatesAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<CandidatesDoc>(RecordIds.Candidates(projectId), projectId, ct);
    public Task UpsertCandidatesAsync(CandidatesDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~Candidates_UpsertThenGet_RoundTrips"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/IRecordStore.cs src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs src/Smx.Infrastructure/CosmosRecordStore.cs src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs
git commit -m "feat(store): candidates get/upsert"
```

---

## Task 5: MatrixAssembler over candidates (non-C)

**Files:**
- Modify: `src/Smx.Domain/MatrixAssembler.cs`
- Test: `src/Smx.Domain.Tests/MatrixAssemblerTests.cs`

The assembler now folds **candidates × their component** (not a full substance×component cross-product), skipping `C`-tier (excluded, never screened). Rows are the distinct substances; Columns are the supplied component ids.

- [ ] **Step 1: Write the failing test** — replace the body of `src/Smx.Domain.Tests/MatrixAssemblerTests.cs` with candidate-based tests:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class MatrixAssemblerTests
{
    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates("p1"), ProjectId = "p1",
        Substances =
        [
            new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "strong", []),
            new("bottle", "Zr", "neodec", "39049-04-2", null, null, false, "C", "excluded", []), // C: not screened
        ],
    };

    private static VerdictDoc Verdict(string cas, string comp, VerdictStatus s) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1", Cas = cas, ComponentId = comp,
        Element = "Y", Form = "2-EH",
        Dimensions = [new("ElementGate", s, [new Citation("regulatory", "x", "t")], 0.9, "r")],
    };

    [Fact]
    public void Cells_ExcludesCTier()
    {
        var cells = MatrixAssembler.Cells(Candidates()).ToList();
        Assert.Single(cells);
        Assert.Equal(("136-25-4", "bottle"), cells[0]);
    }

    [Fact]
    public void IsComplete_TrueOnlyWhenEveryNonCCellHasVerdict()
    {
        var c = Candidates();
        Assert.False(MatrixAssembler.IsComplete(c, []));
        Assert.True(MatrixAssembler.IsComplete(c, [Verdict("136-25-4", "bottle", VerdictStatus.Pass)]));
    }

    [Fact]
    public void Assemble_BuildsRowsColumnsCells()
    {
        var c = Candidates();
        var m = MatrixAssembler.Assemble(c, ["bottle"], [Verdict("136-25-4", "bottle", VerdictStatus.Pass)], "t");
        Assert.Equal(["bottle"], m.Columns);
        Assert.Single(m.Rows);
        Assert.Equal("136-25-4", m.Rows[0].Cas);
        Assert.Single(m.Cells);
        Assert.Equal(VerdictStatus.Pass, m.Cells[0].Overall);
    }

    [Fact]
    public void Assemble_ThrowsWhenIncomplete()
    {
        Assert.Throws<InvalidOperationException>(() => MatrixAssembler.Assemble(Candidates(), ["bottle"], [], "t"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~MatrixAssemblerTests"`
Expected: FAIL — `MatrixAssembler.Cells(CandidatesDoc)` / `Assemble(CandidatesDoc, ...)` overloads don't exist.

- [ ] **Step 3: Rewrite `MatrixAssembler`.** Replace `src/Smx.Domain/MatrixAssembler.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

public static class MatrixAssembler
{
    /// The screened cells: every non-C candidate paired with its component.
    public static IEnumerable<(string Cas, string ComponentId)> Cells(CandidatesDoc c) =>
        c.Substances.Where(s => s.Tier != "C").Select(s => (s.Cas, s.ComponentId));

    public static bool IsComplete(CandidatesDoc c, IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var have = verdicts.Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        return Cells(c).All(have.Contains);
    }

    public static MatrixDoc Assemble(
        CandidatesDoc c, IReadOnlyList<string> componentIds,
        IReadOnlyCollection<VerdictDoc> verdicts, string generatedAt)
    {
        if (!IsComplete(c, verdicts))
            throw new InvalidOperationException("matrix assembly requires a verdict for every non-excluded candidate×component cell");
        var byCell = verdicts.ToDictionary(v => (v.Cas, v.ComponentId));
        var rows = c.Substances.Where(s => s.Tier != "C")
            .GroupBy(s => s.Cas).Select(g => g.First())
            .Select(s => new SubstanceSpec(s.Element, s.Form, s.Cas)).ToList();
        return new MatrixDoc
        {
            Id = RecordIds.Matrix(c.ProjectId), ProjectId = c.ProjectId,
            Rows = rows,
            Columns = [.. componentIds],
            Cells = [.. Cells(c).Select(cell =>
            {
                var v = byCell[cell];
                return new MatrixCell(v.Cas, v.ComponentId, v.Overall, v.Dimensions);
            })],
            GeneratedAt = generatedAt,
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~MatrixAssemblerTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/MatrixAssembler.cs src/Smx.Domain.Tests/MatrixAssemblerTests.cs
git commit -m "feat(domain): assemble matrix from candidates, skip C-tier"
```

---

## Task 6: Catalog tool — `ICatalogLookup` + `CosmosCatalogLookup`

**Files:**
- Modify: `src/Smx.Domain/Tools/ITools.cs`
- Create: `src/Smx.Infrastructure/Search/CatalogLookup.cs`
- Modify: `src/Smx.Infrastructure/BackendOptions.cs`
- Test: `src/Smx.Domain.Tests/Tools/CatalogCardTests.cs` (create)

Discovery needs element → available forms/CAS from the seeded `ref-catalog` container (PK `/element`, docType `product`: `{ id, element, docType, compound, molecule, cas, purity, supplier, price, pack, source }`).

- [ ] **Step 1: Write the failing test** — create `src/Smx.Domain.Tests/Tools/CatalogCardTests.cs`:

```csharp
using Smx.Domain.Tools;

namespace Smx.Domain.Tests.Tools;

public class CatalogCardTests
{
    [Fact]
    public void CatalogCard_CarriesFormAndCas()
    {
        var card = new CatalogCard("Y", "Y(TMHD)3", "TMHD complex", "15632-39-0", "99.9%", "ProChem", "ref-catalog/product|Y|Y(TMHD)3|ProChem");
        Assert.Equal("Y", card.Element);
        Assert.Equal("15632-39-0", card.Cas);
        Assert.Equal("ProChem", card.Supplier);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~CatalogCardTests"`
Expected: FAIL — `CatalogCard`/`ICatalogLookup` don't exist.

- [ ] **Step 3: Add the tool interface + adapter + option.** In `src/Smx.Domain/Tools/ITools.cs`, add:

```csharp
/// One catalog product listing from ref-catalog (docType "product").
public sealed record CatalogCard(string Element, string Molecule, string Compound, string Cas, string? Purity, string Supplier, string RefId);

public interface ICatalogLookup
{
    /// All catalog products for an element (single-partition read of ref-catalog by /element).
    Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default);
}
```

Create `src/Smx.Infrastructure/Search/CatalogLookup.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Reads the ref-catalog container seeded by the reference-data subsystem (PK /element, docType "product").
public sealed class CosmosCatalogLookup(Container container) : ICatalogLookup
{
    private sealed record Row(string Id, string Element, string DocType, string? Molecule, string? Compound, string? Cas, string? Purity, string? Supplier);

    public async Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default)
    {
        var results = new List<CatalogCard>();
        var it = container.GetItemLinqQueryable<Row>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(element) })
            .Where(r => r.DocType == "product")
            .ToFeedIterator();
        while (it.HasMoreResults)
            foreach (var r in await it.ReadNextAsync(ct))
                results.Add(new CatalogCard(r.Element, r.Molecule ?? "", r.Compound ?? "", r.Cas ?? "",
                    r.Purity, r.Supplier ?? "", $"ref-catalog/{r.Id}"));
        return results;
    }
}
```

In `src/Smx.Infrastructure/BackendOptions.cs`, add `CatalogContainer` to the record (after `CompatibilityContainer`) and to `From`:

```csharp
    string CompatibilityContainer,
    string CatalogContainer,
```

```csharp
        CompatibilityContainer: c["COMPATIBILITY_CONTAINER"] ?? "ref-compatibility",
        CatalogContainer: c["CATALOG_CONTAINER"] ?? "ref-catalog",
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "FullyQualifiedName~CatalogCardTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Tools/ITools.cs src/Smx.Infrastructure/Search/CatalogLookup.cs src/Smx.Infrastructure/BackendOptions.cs src/Smx.Domain.Tests/Tools/CatalogCardTests.cs
git commit -m "feat(tools): ref-catalog lookup (ICatalogLookup + CosmosCatalogLookup)"
```

---

## Task 7: ToolBox — DiscoveryTools + RegulatoryTools

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/ToolBox.cs`
- Modify: `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`
- Modify: `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`

Discovery gets `search_catalog` + `lookup_compatibility` + `search_reference`. Regulatory gets `search_regulatory` + `search_sds` + `search_reference` (compatibility moved to Discovery). `ScreeningTools()` is removed.

- [ ] **Step 1: Write the failing test** — replace `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`:

```csharp
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ToolBoxTests
{
    private static ToolBox Box()
    {
        var search = new FakeSearch();
        return new ToolBox(new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search);
    }

    [Fact]
    public void DiscoveryTools_ExposeCatalogCompatibilityReference()
    {
        var names = Box().DiscoveryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["lookup_compatibility", "search_catalog", "search_reference"], names);
    }

    [Fact]
    public void RegulatoryTools_ExposeRegulatorySdsReference_NoCompatibility()
    {
        var names = Box().RegulatoryTools().Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["search_reference", "search_regulatory", "search_sds"], names);
        Assert.DoesNotContain("lookup_compatibility", names);
    }

    [Fact]
    public async Task SearchCatalog_RendersEmptyAsNoMatchNote()
    {
        var json = await Box().SearchCatalogAsync("Xx", default);
        Assert.Contains("no matches", json);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ToolBoxTests"`
Expected: FAIL — `ToolBox` ctor signature, `DiscoveryTools`/`RegulatoryTools`, `SearchCatalogAsync`, and `FakeCatalogLookup` don't exist.

- [ ] **Step 3: Add the fake + rewrite the ToolBox.** In `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`, add:

```csharp
public sealed class FakeCatalogLookup : ICatalogLookup
{
    public Dictionary<string, List<CatalogCard>> Cards { get; } = new();
    public List<string> Calls { get; } = [];
    public Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default)
    {
        Calls.Add(element);
        return Task.FromResult<IReadOnlyList<CatalogCard>>(Cards.TryGetValue(element, out var c) ? c : []);
    }
}
```

Replace `src/Smx.Orchestrator/Agents/ToolBox.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Agents;

public sealed class ToolBox(
    ICatalogLookup catalog,
    ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory,
    ISdsSearch sds,
    IReferenceSearch reference)
{
    public IList<AITool> DiscoveryTools() =>
    [
        AIFunctionFactory.Create(SearchCatalogAsync, "search_catalog",
            "List the catalog products (form, molecule, CAS, purity, supplier) available for an element from the SMX catalog. Use this to specify candidate forms and their CAS numbers; only propose candidates whose CAS you retrieved here."),
        AIFunctionFactory.Create(LookupCompatibilityAsync, "lookup_compatibility",
            "Exact tabulated element×substrate compatibility verdict. Use as a tiering signal — an incompatible substrate lowers a candidate's tier or excludes it."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "Search SMX reference prose: solubility, XRF cleanliness, marker forms, bibliography-backed notes. Use to justify form ranking and tiering."),
    ];

    public IList<AITool> RegulatoryTools() =>
    [
        AIFunctionFactory.Create(SearchRegulatoryAsync, "search_regulatory",
            "Search the official regulatory corpus (REACH/RoHS/PPWR/SVHC/Prop 65/EU Cosmetics/FDA...). Call this for the ElementGate and ApplicationCheck dimensions. Cite every returned reference you rely on."),
        AIFunctionFactory.Create(SearchSdsAsync, "search_sds",
            "Search safety-data-sheet (GHS) chunks by CAS or element: H-codes, CMR, hazard classifications. Call this for the Hazard dimension."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "Search SMX reference prose: solubility, XRF cleanliness, marker forms, bibliography-backed notes."),
    ];

    public IList<AITool> IntakeTools() =>
    [
        AIFunctionFactory.Create(SearchRegulatoryAsync, "search_regulatory",
            "Search the official regulatory corpus to confirm which regulation lists apply to a component given its application and target markets. Cite every list you include."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "Search SMX reference prose for material/application background."),
    ];

    public async Task<string> SearchCatalogAsync(string element, CancellationToken ct)
    {
        var cards = await catalog.LookupAsync(element, ct);
        return cards.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — do not invent CAS numbers; exclude this element or mark the candidate lower-confidence\"}"
            : JsonSerializer.Serialize(new { results = cards }, Json.Options);
    }

    public async Task<string> LookupCompatibilityAsync(string element, string substrate, CancellationToken ct)
    {
        var card = await compatibility.LookupAsync(element, substrate, ct);
        return card is null
            ? $"{{\"tabulated\":false,\"note\":\"{element}×{substrate} not tabulated — treat as a weak signal\"}}"
            : JsonSerializer.Serialize(new { tabulated = true, card }, Json.Options);
    }

    public async Task<string> SearchRegulatoryAsync(string query, CancellationToken ct) => Render(await regulatory.SearchAsync(query, ct: ct));
    public async Task<string> SearchSdsAsync(string query, CancellationToken ct) => Render(await sds.SearchAsync(query, ct: ct));
    public async Task<string> SearchReferenceAsync(string query, CancellationToken ct) => Render(await reference.SearchAsync(query, ct: ct));

    private static string Render(IReadOnlyList<RetrievedChunk> chunks) =>
        chunks.Count == 0
            ? "{\"results\":[],\"note\":\"no matches — do not invent facts; lower confidence or mark NeedsReview\"}"
            : JsonSerializer.Serialize(new { results = chunks }, Json.Options);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ToolBoxTests"`
Expected: PASS (3 tests). (The solution build still fails until Tasks 8–11 update the agents/dispatcher — if the solution won't build, defer this run to Step 4 of Task 11 and note it here.)

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/ToolBox.cs src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs src/Smx.Orchestrator.Tests/ToolBoxTests.cs
git commit -m "feat(tools): DiscoveryTools + RegulatoryTools; add search_catalog"
```

---

## Task 8: Discovery agent

**Files:**
- Create: `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/DiscoveryAgentTests.cs` (create)

- [ ] **Step 1: Write the failing test** — create `src/Smx.Orchestrator.Tests/DiscoveryAgentTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class DiscoveryAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "PET", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Y", "Kα", "V", null)],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "gate", new Citation("regulatory", "x", "t"))],
    };

    private const string Valid = """
    { "substances": [
      { "componentId": "bottle", "element": "Y", "form": "2-ethylhexanoate", "cas": "136-25-4",
        "particleSize": null, "solvent": "mineral spirits", "preferred": true, "tier": "A",
        "rationale": "clean XRF (V), catalog-available",
        "citations": [{ "source": "catalog", "reference": "ref-catalog/product|Y|x", "retrievedAt": "t" }] } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesCandidatesDoc()
    {
        var result = await DiscoveryAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|candidates", result.Output!.Id);
        Assert.Single(result.Output.Substances);
        Assert.Equal("A", result.Output.Substances[0].Tier);
    }

    [Fact]
    public async Task Candidate_ForUnknownComponent_IsRejected()
    {
        var bad = Valid.Replace("\"componentId\": \"bottle\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("unknown component", result.Error);
    }

    [Fact]
    public async Task Candidate_WithElementNotInPool_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Y\"", "\"element\": \"Cd\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("not in the element pool", result.Error);
    }

    [Fact]
    public async Task Candidate_WithoutCitation_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citations\": [{ \"source\": \"catalog\", \"reference\": \"ref-catalog/product|Y|x\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await DiscoveryAgent.RunAsync(agent, Constraints(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DiscoveryAgentTests"`
Expected: FAIL — `DiscoveryAgent` doesn't exist.

- [ ] **Step 3: Create the agent.** Create `src/Smx.Orchestrator/Agents/DiscoveryAgent.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class DiscoveryOutput
{
    public List<CandidateSubstance> Substances { get; set; } = [];
}

public static class DiscoveryAgent
{
    public const string AgentName = "discovery";

    public const string Instructions = """
        You are the SMX Discovery agent. For each component you receive its usable/conditional element POOL
        (V = clean, L = conditional) plus material, application and objective. Turn each pooled element into
        one or more FULLY-SPECIFIED candidate substances: element + molecular form + CAS + (particle size,
        solvent when known). You may only use facts from your tools:
        - search_catalog(element) FIRST — propose only forms/CAS you retrieved there; never invent a CAS.
        - search_reference for solubility / XRF cleanliness / form ranking evidence.
        - lookup_compatibility(element, substrate) as a tiering signal (incompatible ⇒ lower tier or C).
        Rank the forms and set preferred=true on the best one per element×component. Assign a tier with a
        one/two-sentence cited rationale:
        - A: strong (clean signal, catalog-available, no obvious blockers).
        - B: needs validation (e.g. limited use history, single form).
        - C: excluded (present in background, clearly regulated, or substrate-incompatible) — still list it,
          with the reason, so the exclusion is visible.
        EVERY candidate MUST carry at least one citation built from an actual tool result
        (source, reference, retrievedAt = now ISO 8601 UTC). Only propose candidates whose element is in that
        component's pool. Reply with ONLY a JSON object:
        { "substances": [{ "componentId", "element", "form", "cas", "particleSize", "solvent", "preferred",
          "tier" ("A"|"B"|"C"), "rationale", "citations": [{ "source", "reference", "retrievedAt" }] }] }
        """;

    public static async Task<AgentRunResult<CandidatesDoc>> RunAsync(ISmxAgent agent, ConstraintsDoc constraints, CancellationToken ct)
    {
        var prompt = JsonSerializer.Serialize(new
        {
            components = constraints.Components,
            elementPools = constraints.ElementPools,
        }, Json.Options);
        var result = await ValidatedAgentRunner.RunAsync<DiscoveryOutput>(agent,
            $"Discover candidate substances for these components and pools:\n{prompt}",
            o => Validate(o, constraints), ct);
        if (!result.Succeeded) return AgentRunResult<CandidatesDoc>.NeedsReview(result.Error!);
        return AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(constraints.ProjectId), ProjectId = constraints.ProjectId,
            Substances = result.Output!.Substances,
        });
    }

    internal static string? Validate(DiscoveryOutput o, ConstraintsDoc constraints)
    {
        if (o.Substances.Count == 0) return "at least one candidate substance is required";
        var componentIds = constraints.Components.Select(c => c.Id).ToHashSet();
        var poolByComponent = constraints.ElementPools
            .GroupBy(p => p.Component)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Element).ToHashSet());
        string[] tiers = ["A", "B", "C"];
        foreach (var s in o.Substances)
        {
            if (!componentIds.Contains(s.ComponentId)) return $"candidate references unknown component '{s.ComponentId}'";
            if (!poolByComponent.TryGetValue(s.ComponentId, out var pool) || !pool.Contains(s.Element))
                return $"candidate element '{s.Element}' is not in the element pool for component '{s.ComponentId}'";
            if (!tiers.Contains(s.Tier)) return $"candidate tier must be one of A|B|C; got '{s.Tier}'";
            if (string.IsNullOrWhiteSpace(s.Cas)) return $"candidate '{s.Element}/{s.Form}' is missing a CAS number";
            if (s.Citations.Count == 0 || s.Citations.Any(c => string.IsNullOrWhiteSpace(c.Source) || string.IsNullOrWhiteSpace(c.Reference)))
                return $"candidate '{s.Element}/{s.Form}' is missing a usable citation — every candidate must cite a retrieved source";
        }
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DiscoveryAgentTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/DiscoveryAgent.cs src/Smx.Orchestrator.Tests/DiscoveryAgentTests.cs
git commit -m "feat(agent): Discovery — element pools to tiered candidates"
```

---

## Task 9: Regulatory agent (replaces Screening; 3 dimensions)

**Files:**
- Create: `src/Smx.Orchestrator/Agents/RegulatoryAgent.cs`
- Delete: `src/Smx.Orchestrator/Agents/ScreeningAgent.cs`
- Create: `src/Smx.Orchestrator.Tests/RegulatoryAgentTests.cs`
- Delete: `src/Smx.Orchestrator.Tests/ScreeningAgentTests.cs`

Regulatory screens one **candidate** × its component across exactly `ElementGate`, `ApplicationCheck`, `Hazard`. No Compatibility dimension (that moved to Discovery).

- [ ] **Step 1: Write the failing test** — create `src/Smx.Orchestrator.Tests/RegulatoryAgentTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class RegulatoryAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "element gate",
            new Citation("regulatory", "regulatory-index/reach-17", "t"))],
    };

    private static CandidateSubstance Candidate() =>
        new("bottle", "Cd", "sulfide", "1306-23-6", null, null, true, "A", "provided", []);

    private const string Valid = """
    { "dimensions": [
      { "dimension": "ElementGate", "status": "Fail",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/reach-e23", "retrievedAt": "t" }],
        "confidence": 0.98, "rationale": "Cd restricted by REACH Annex XVII entry 23" },
      { "dimension": "ApplicationCheck", "status": "Fail",
        "citations": [{ "source": "regulatory", "reference": "regulatory-index/ppwr-hm", "retrievedAt": "t" }],
        "confidence": 0.95, "rationale": "PPWR heavy-metal cap" },
      { "dimension": "Hazard", "status": "Fail",
        "citations": [{ "source": "sds", "reference": "sds-index/cd-ghs", "retrievedAt": "t" }],
        "confidence": 0.97, "rationale": "carcinogenic H350" } ] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesVerdictDoc_ThreeDimensions()
    {
        var result = await RegulatoryAgent.RunAsync(new ScriptedAgent(Valid), Constraints(), Candidate(), default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|verdict|1306-23-6|bottle", result.Output!.Id);
        Assert.Equal(VerdictStatus.Fail, result.Output.Overall);
        Assert.Equal(3, result.Output.Dimensions.Count);
    }

    [Fact]
    public async Task IncludingCompatibilityDimension_IsRejected()
    {
        var bad = Valid.Replace("\"dimension\": \"Hazard\"", "\"dimension\": \"Compatibility\"");
        var agent = new ScriptedAgent(bad, Valid);
        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), default);
        Assert.True(result.Succeeded);
        Assert.Contains("exactly the three dimensions", agent.Received[1]);
    }

    [Fact]
    public async Task UncitedDimension_IsRejected()
    {
        var bad = Valid.Replace(
            "\"citations\": [{ \"source\": \"sds\", \"reference\": \"sds-index/cd-ghs\", \"retrievedAt\": \"t\" }]",
            "\"citations\": []");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("citation", result.Error);
    }

    [Fact]
    public async Task PromptCarriesCandidate_ScopeAndRestrictedList()
    {
        var agent = new ScriptedAgent(Valid);
        await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), default);
        var prompt = agent.Received[0];
        Assert.Contains("1306-23-6", prompt);
        Assert.Contains("reach-annex-xvii", prompt);
        Assert.Contains("Pb", prompt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryAgentTests"`
Expected: FAIL — `RegulatoryAgent` doesn't exist.

- [ ] **Step 3: Create the agent, delete Screening.** Delete `src/Smx.Orchestrator/Agents/ScreeningAgent.cs` and `src/Smx.Orchestrator.Tests/ScreeningAgentTests.cs`. Create `src/Smx.Orchestrator/Agents/RegulatoryAgent.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class RegulatoryOutput
{
    public List<DimensionVerdict> Dimensions { get; set; } = [];
}

public static class RegulatoryAgent
{
    public const string AgentName = "regulatory";

    public const string Instructions = """
        You are the SMX Regulatory agent. You evaluate ONE candidate substance against ONE product component
        and return a verdict per dimension. Substrate compatibility is NOT your concern (Discovery handled it).
        You may only use facts obtained through your tools in this conversation — never from memory. Dimensions
        (all three, exactly once each):
        - ElementGate: product-wide lists from the provided scope (componentId "*") plus the client restricted
          list. Search the regulatory corpus for the element/substance against each list. A hit on any list = Fail.
        - ApplicationCheck: the component-scoped lists from the provided scope. A restriction that binds this
          component's application/markets = Fail; a cap/limit that constrains but permits = Conditional.
        - Hazard: search_sds for GHS data (H-codes, CMR, endocrine). CMR category 1A/1B = Fail; significant
          hazards that merit "not recommended" = Conditional.
        Statuses: Pass | Conditional | NeedsReview | Fail. EVERY dimension MUST carry at least one citation
        built from an actual tool result (source, reference, retrievedAt = now, ISO 8601 UTC). If your tools
        return nothing decisive for a dimension, the status is NeedsReview — never guess, never assume clean.
        Confidence is your calibrated 0..1 estimate. Rationale is one or two sentences.
        Reply with ONLY a JSON object: { "dimensions": [{ "dimension", "status", "citations":
        [{ "source", "reference", "retrievedAt" }], "confidence", "rationale" }] }
        """;

    public static async Task<AgentRunResult<VerdictDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, CandidateSubstance candidate, CancellationToken ct)
    {
        var component = constraints.Components.Single(c => c.Id == candidate.ComponentId);
        var scope = constraints.DerivedScope.Where(s => s.ComponentId is "*" || s.ComponentId == candidate.ComponentId).ToList();
        var prompt = JsonSerializer.Serialize(new
        {
            substance = new { candidate.Element, candidate.Form, candidate.Cas },
            component,
            applicableScope = scope,
            clientRestrictedList = constraints.ClientRestrictedList,
        }, Json.Options);

        var result = await ValidatedAgentRunner.RunAsync<RegulatoryOutput>(agent,
            $"Screen this cell:\n{prompt}", Validate, ct);
        if (!result.Succeeded) return AgentRunResult<VerdictDoc>.NeedsReview(result.Error!);
        return AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(constraints.ProjectId, candidate.Cas, candidate.ComponentId),
            ProjectId = constraints.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
            Element = candidate.Element, Form = candidate.Form,
            Dimensions = result.Output!.Dimensions,
        });
    }

    internal static string? Validate(RegulatoryOutput o)
    {
        string[] required = ["ElementGate", "ApplicationCheck", "Hazard"];
        var names = o.Dimensions.Select(d => d.Dimension).OrderBy(x => x).ToArray();
        if (!names.SequenceEqual(required.OrderBy(x => x)))
            return $"response must contain exactly the three dimensions {string.Join(", ", required)} once each; got [{string.Join(", ", names)}]";
        foreach (var d in o.Dimensions)
        {
            if (d.Citations.Count == 0 || d.Citations.Any(c =>
                    string.IsNullOrWhiteSpace(c.Source) || string.IsNullOrWhiteSpace(c.Reference)))
                return $"dimension '{d.Dimension}' is missing a usable citation — every dimension must cite an actual tool result";
            if (d.Confidence is < 0 or > 1) return $"dimension '{d.Dimension}' confidence must be within 0..1";
            if (string.IsNullOrWhiteSpace(d.Rationale)) return $"dimension '{d.Dimension}' needs a rationale";
        }
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegulatoryAgentTests"`
Expected: PASS (4 tests). (If the solution build blocks on the dispatcher, complete Task 10–11 first, then re-run.)

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/RegulatoryAgent.cs src/Smx.Orchestrator.Tests/RegulatoryAgentTests.cs
git rm src/Smx.Orchestrator/Agents/ScreeningAgent.cs src/Smx.Orchestrator.Tests/ScreeningAgentTests.cs
git commit -m "feat(agent): Regulatory (3-dim battery) replaces folded Screening"
```

---

## Task 10: AgentRuns + FakeAgentRuns — discovery & regulatory

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`
- Modify: `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`

- [ ] **Step 1: Write the failing test** — the `IAgentRuns` interface change is exercised by `StageDispatcherTests` (Task 11). Here, update the fake to the new interface. First add a placeholder test to `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRunsSmokeTests.cs` (create):

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class FakeAgentRunsSmokeTests
{
    [Fact]
    public async Task Fake_DefaultDiscovery_ReturnsOneCandidate()
    {
        var fake = new FakeAgentRuns();
        var c = new ConstraintsDoc { Id = RecordIds.Constraints("p1"), ProjectId = "p1",
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)] };
        var result = await ((Smx.Orchestrator.Dispatch.IAgentRuns)fake).RunDiscoveryAsync(c, default);
        Assert.True(result.Succeeded);
        Assert.Single(result.Output!.Substances);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~FakeAgentRunsSmokeTests"`
Expected: FAIL — `IAgentRuns.RunDiscoveryAsync` doesn't exist.

- [ ] **Step 3: Update the interface + real + fake.** In `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`, replace the interface and class:

```csharp
using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);
    Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, CancellationToken ct);
    Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, CancellationToken ct);
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(ConstraintsDoc constraints, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions, toolBox.DiscoveryTools()),
            constraints, ct);

    public Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, CancellationToken ct) =>
        RegulatoryAgent.RunAsync(
            new MafAgent(chatClient, RegulatoryAgent.AgentName, RegulatoryAgent.Instructions, toolBox.RegulatoryTools()),
            constraints, candidate, ct);
}
```

Replace `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Dispatch;

namespace Smx.Orchestrator.Tests.Fakes;

/// Bypasses LLM entirely: dispatcher tests exercise orchestration, not reasoning.
public sealed class FakeAgentRuns : IAgentRuns
{
    public Func<ProjectDoc, Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>>> Intake { get; set; } =
        p => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    public Func<ConstraintsDoc, Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>>> Discovery { get; set; } =
        c => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        }));

    public Func<ConstraintsDoc, CandidateSubstance, Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>>> Regulatory { get; set; } =
        (c, cand) => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, cand.Cas, cand.ComponentId), ProjectId = c.ProjectId,
            Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        }));

    public int IntakeCalls; public int DiscoveryCalls; public int RegulatoryCalls;

    Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>> IAgentRuns.RunIntakeAsync(ProjectDoc p, CancellationToken ct)
    { Interlocked.Increment(ref IntakeCalls); return Intake(p); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>> IAgentRuns.RunDiscoveryAsync(ConstraintsDoc c, CancellationToken ct)
    { Interlocked.Increment(ref DiscoveryCalls); return Discovery(c); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>> IAgentRuns.RunRegulatoryAsync(ConstraintsDoc c, CandidateSubstance cand, CancellationToken ct)
    { Interlocked.Increment(ref RegulatoryCalls); return Regulatory(c, cand); }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~FakeAgentRunsSmokeTests"`
Expected: PASS (after Task 11 makes the dispatcher compile; if the solution won't build yet, proceed to Task 11 then re-run).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Dispatch/AgentRuns.cs src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs src/Smx.Orchestrator.Tests/FakeAgentRunsSmokeTests.cs
git commit -m "feat(dispatch): IAgentRuns gains discovery + regulatory"
```

---

## Task 11: StageDispatcher — Intake → Discovery → Regulatory → Matrix

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Modify: `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Modify: `src/Smx.Orchestrator.Tests/StageDispatcherTests.cs`

The dispatcher now: on `constraints` → Discovery (bypass to `ProvidedCandidates` when present); on `candidates` → fan out Regulatory over non-`C` candidates; on `verdict` → assemble. Regulatory `needs_review` still writes a placeholder verdict so the matrix completes.

- [ ] **Step 1: Write the failing test** — replace `src/Smx.Orchestrator.Tests/StageDispatcherTests.cs`:

```csharp
using System.Text.Json;
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class StageDispatcherTests
{
    private static (StageDispatcher, InMemoryRecordStore, FakeAgentRuns) Sut(int parallelism = 2)
    {
        var store = new InMemoryRecordStore();
        var agents = new FakeAgentRuns();
        return (new StageDispatcher(store, agents, parallelism), store, agents);
    }

    private static async Task<ProjectDoc> Seed(InMemoryRecordStore store)
    {
        var doc = ProjectDoc.Create("p1", "Acme", "P", JsonDocument.Parse("{}").RootElement);
        await store.UpsertProjectAsync(doc);
        return doc;
    }

    [Fact]
    public async Task ProjectCreated_RunsIntake_WritesConstraints_MarksStageDone()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        Assert.Equal(1, agents.IntakeCalls);
        Assert.NotNull(await store.GetConstraintsAsync("p1"));
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task ConstraintsWritten_RunsDiscovery_WritesCandidates()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        Assert.Equal(1, agents.DiscoveryCalls);
        Assert.NotNull(await store.GetCandidatesAsync("p1"));
        Assert.Equal("done", (await store.GetProjectAsync("p1"))!.Stages[Stages.Discovery].Status);
    }

    [Fact]
    public async Task ConstraintsWithProvidedCandidates_BypassesDiscoveryAgent()
    {
        var (d, store, agents) = Sut();
        agents.Intake = p => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ProvidedCandidates = [new("bottle", "Zr", "neodec", "cas-zr", null, null, true, "A", "provided",
                [new Citation("catalog", "x", "t")])],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        Assert.Equal(0, agents.DiscoveryCalls);                 // bypassed
        Assert.Single((await store.GetCandidatesAsync("p1"))!.Substances);
    }

    [Fact]
    public async Task CandidatesWritten_FansOutRegulatory_ThenAssemblesMatrix()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);              // intake
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default); // discovery
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);  // regulatory fan-out
        Assert.Single(await store.GetVerdictsAsync("p1"));
        var last = (await store.GetVerdictsAsync("p1"))[0];
        await d.OnRecordChangedAsync(last, default);                            // verdict → assembly
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("done", proj!.Stages[Stages.Regulatory].Status);
        Assert.Equal("done", proj.Stages[Stages.Matrix].Status);
    }

    [Fact]
    public async Task RegulatoryFanOut_SkipsCellsThatAlreadyHaveVerdicts()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        var candidates = await store.GetCandidatesAsync("p1");
        await d.OnRecordChangedAsync(candidates!, default);
        var callsAfterFirst = agents.RegulatoryCalls;
        await d.OnRecordChangedAsync(candidates!, default);                     // redelivery
        Assert.Equal(callsAfterFirst, agents.RegulatoryCalls);
    }

    [Fact]
    public async Task DiscoveryNeedsReview_MarksStage_DoesNotCascade()
    {
        var (d, store, agents) = Sut();
        agents.Discovery = _ => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>.NeedsReview("no catalog hits"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("needs-review", proj!.Stages[Stages.Discovery].Status);
        Assert.Null(await store.GetCandidatesAsync("p1"));
    }

    [Fact]
    public async Task RegulatoryNeedsReview_WritesPlaceholderVerdict_MatrixStillAssembles()
    {
        var (d, store, agents) = Sut();
        agents.Regulatory = (c, cand) => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.NeedsReview("no retrieval"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        await d.OnRecordChangedAsync((await store.GetCandidatesAsync("p1"))!, default);
        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.Single(verdicts);
        Assert.Equal(VerdictStatus.NeedsReview, verdicts[0].Overall);
        await d.OnRecordChangedAsync(verdicts[0], default);
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        Assert.Equal("needs-review", (await store.GetProjectAsync("p1"))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task IntakeThrow_MarksStageFailed_WithErrorDetail()
    {
        var (d, store, agents) = Sut();
        agents.Intake = _ => throw new InvalidOperationException("foundry 500");
        await d.OnRecordChangedAsync(await Seed(store), default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("failed", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("foundry 500", proj.Stages[Stages.Intake].Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~StageDispatcherTests"`
Expected: FAIL — dispatcher has no Discovery/Regulatory handling.

- [ ] **Step 3: Rewrite the dispatcher + router.** Replace `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Dispatch;

/// Reacts to record changes. Change feed is at-least-once: every branch must be idempotent
/// (re-check store state before acting) and every write an upsert.
public sealed class StageDispatcher(IRecordStore store, IAgentRuns agents, int regulatoryParallelism)
{
    public async Task OnRecordChangedAsync(object doc, CancellationToken ct)
    {
        switch (doc)
        {
            case ProjectDoc p: await OnProjectAsync(p, ct); break;
            case ConstraintsDoc c: await OnConstraintsAsync(c, ct); break;
            case CandidatesDoc cd: await OnCandidatesAsync(cd, ct); break;
            case VerdictDoc v: await OnVerdictAsync(v, ct); break;
            case MatrixDoc: break; // terminal
        }
    }

    private async Task OnProjectAsync(ProjectDoc p, CancellationToken ct)
    {
        if (p.Stages[Stages.Intake].Status != "pending") return;
        if (await store.GetConstraintsAsync(p.ProjectId, ct) is not null) return;
        await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var result = await agents.RunIntakeAsync(p, ct);
            if (result.Succeeded)
            {
                await store.UpsertConstraintsAsync(result.Output!, ct);
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "done"; s.Error = null; }, ct);
            }
            else
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnConstraintsAsync(ConstraintsDoc c, CancellationToken ct)
    {
        if (await store.GetCandidatesAsync(c.ProjectId, ct) is not null) return; // idempotency
        await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            // Known-candidate mode: bypass the Discovery agent when the operator/eval supplied candidates.
            if (c.ProvidedCandidates.Count > 0)
            {
                await store.UpsertCandidatesAsync(new CandidatesDoc
                {
                    Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
                    Substances = [.. c.ProvidedCandidates],
                }, ct);
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "done"; s.Error = null; }, ct);
                return;
            }
            var result = await agents.RunDiscoveryAsync(c, ct);
            if (result.Succeeded)
            {
                await store.UpsertCandidatesAsync(result.Output!, ct);
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "done"; s.Error = null; }, ct);
            }
            else
                await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(c.ProjectId, Stages.Discovery, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnCandidatesAsync(CandidatesDoc cd, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(cd.ProjectId, ct);
        if (constraints is null) return;
        var existing = (await store.GetVerdictsAsync(cd.ProjectId, ct)).Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = cd.Substances.Where(s => s.Tier != "C" && !existing.Contains((s.Cas, s.ComponentId))).ToList();
        if (missing.Count == 0) { await TryAssembleAsync(cd.ProjectId, ct); return; }

        await SetStageAsync(cd.ProjectId, Stages.Regulatory, s => { s.Status = "running"; s.Attempts++; }, ct);
        using var gate = new SemaphoreSlim(regulatoryParallelism);
        var tasks = missing.Select(async candidate =>
        {
            await gate.WaitAsync(ct);
            try
            {
                try
                {
                    var result = await agents.RunRegulatoryAsync(constraints, candidate, ct);
                    var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                    {
                        Id = RecordIds.Verdict(cd.ProjectId, candidate.Cas, candidate.ComponentId),
                        ProjectId = cd.ProjectId, Cas = candidate.Cas, ComponentId = candidate.ComponentId,
                        Element = candidate.Element, Form = candidate.Form,
                        Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [],
                            0, $"agent could not produce a valid cited verdict: {result.Error}")],
                    };
                    await store.UpsertVerdictAsync(verdict, ct);
                }
                catch (Exception e)
                {
                    await SetStageAsync(cd.ProjectId, Stages.Regulatory, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        await TryAssembleAsync(cd.ProjectId, ct);
    }

    private Task OnVerdictAsync(VerdictDoc v, CancellationToken ct) => TryAssembleAsync(v.ProjectId, ct);

    private async Task TryAssembleAsync(string projectId, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        var candidates = await store.GetCandidatesAsync(projectId, ct);
        if (constraints is null || candidates is null) return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (!MatrixAssembler.IsComplete(candidates, verdicts)) return;

        var anyReview = verdicts.Any(v => v.Overall == VerdictStatus.NeedsReview);
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status != "failed") s.Status = anyReview ? "needs-review" : "done"; }, ct);

        if (await store.GetMatrixAsync(projectId, ct) is null)
        {
            var componentIds = constraints.Components.Select(k => k.Id).ToList();
            await store.UpsertMatrixAsync(
                MatrixAssembler.Assemble(candidates, componentIds, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
        }
        await SetStageAsync(projectId, Stages.Matrix, s => s.Status = "done", ct);
    }

    private async Task SetStageAsync(string projectId, string stage, Action<StageState> mutate, CancellationToken ct)
    {
        if (await store.GetProjectAsync(projectId, ct) is not { } p) return;
        mutate(p.Stages[stage]);
        await store.UpsertProjectAsync(p, ct);
    }
}
```

In `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`, add the `candidates` case:

```csharp
            RecordTypes.Constraints => element.Deserialize<ConstraintsDoc>(Json.Options),
            RecordTypes.Candidates => element.Deserialize<CandidatesDoc>(Json.Options),
            RecordTypes.Verdict => element.Deserialize<VerdictDoc>(Json.Options),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~StageDispatcherTests"`
Expected: PASS (8 tests). Also re-run the deferred tasks now that the solution builds:
`dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ToolBoxTests|FullyQualifiedName~DiscoveryAgentTests|FullyQualifiedName~RegulatoryAgentTests|FullyQualifiedName~FakeAgentRunsSmokeTests"`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Dispatch/StageDispatcher.cs src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs src/Smx.Orchestrator.Tests/StageDispatcherTests.cs
git commit -m "feat(dispatch): Intake to Discovery to Regulatory to Matrix pipeline"
```

---

## Task 12: Intake agent — echo element pools + provided candidates

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/IntakeAgent.cs`
- Modify: `src/Smx.Orchestrator.Tests/IntakeAgentTests.cs`

Intake now echoes `elementPools` + `providedCandidates` (instead of `substances`) and still derives the cited scope.

- [ ] **Step 1: Write the failing test** — replace the entire contents of `src/Smx.Orchestrator.Tests/IntakeAgentTests.cs` with the element-pool contract below (this is the complete file — the old substances-echo tests are superseded by `AlteredElementPool_IsRejected`):

```csharp
using System.Text.Json;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class IntakeAgentTests
{
    private static ProjectDoc Project()
    {
        var payload = JsonDocument.Parse("""
        { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "elementPools": [{ "component": "bottle", "element": "Y", "line": "Kα", "status": "V", "signalNote": null }],
          "providedCandidates": [],
          "clientRestrictedList": ["Pb"] }
        """).RootElement;
        return ProjectDoc.Create("p1", "Acme", "MUFE", payload);
    }

    private const string Valid = """
    { "components": [{ "id": "bottle", "material": "PET", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
      "elementPools": [{ "component": "bottle", "element": "Y", "line": "Kα", "status": "V", "signalNote": null }],
      "providedCandidates": [],
      "clientRestrictedList": ["Pb"],
      "derivedScope": [{ "listId": "reach-annex-xvii", "componentId": "*", "reason": "gate",
        "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "t" } }] }
    """;

    [Fact]
    public async Task ValidResponse_BecomesConstraintsDoc_WithElementPools()
    {
        var result = await IntakeAgent.RunAsync(new ScriptedAgent(Valid), Project(), default);
        Assert.True(result.Succeeded);
        Assert.Single(result.Output!.ElementPools);
        Assert.Equal("Y", result.Output.ElementPools[0].Element);
    }

    [Fact]
    public async Task AlteredElementPool_IsRejected()
    {
        var bad = Valid.Replace("\"element\": \"Y\"", "\"element\": \"Zr\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded);
        Assert.Contains("element pools must exactly echo", result.Error);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~IntakeAgentTests"`
Expected: FAIL — `IntakeOutput` has no `ElementPools`/`ProvidedCandidates`; `ConstraintsDoc` mapping differs.

- [ ] **Step 3: Update the Intake agent.** In `src/Smx.Orchestrator/Agents/IntakeAgent.cs`, replace `IntakeOutput`, the `ConstraintsDoc` construction, and `Validate`:

```csharp
public sealed class IntakeOutput
{
    public List<ComponentSpec> Components { get; set; } = [];
    public List<ElementPool> ElementPools { get; set; } = [];
    public List<CandidateSubstance> ProvidedCandidates { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    public List<AppliedList> DerivedScope { get; set; } = [];
}
```

In the `Instructions` string, change the schema/echo lines: replace every mention of `substances` with the pools contract. Use this instruction body:

```csharp
    public const string Instructions = """
        You are the SMX Constraint-Intake agent. You receive a project's raw constraints payload and must
        normalize it and DERIVE the regulatory scope. You never invent data: components, element pools,
        provided candidates and the client restricted list must EXACTLY echo the input. Your added value is
        `derivedScope`:
        - The product-wide element gate lists ALWAYS apply (componentId "*"): REACH Annex XVII, RoHS (if
          electronics), PPWR heavy-metal cap (if packaging), SVHC, Prop 65 (if US market), client restricted list.
        - Per-component application lists follow from application × target markets (e.g. EU Cosmetics for a
          skin-contact liquid in EU, migration/SML if food-contact, FDA regimes for US market).
        Use the search_regulatory tool to confirm each list applies and cite the retrieved reference in that
        entry's citation (source = the tool's source, reference = the returned reference id, retrievedAt = now,
        ISO 8601 UTC). Every derivedScope entry MUST carry a citation from an actual tool result. If retrieval
        gives you nothing for a list you believe applies, do not include it silently — include it only with a
        real citation, otherwise leave it out.
        Reply with ONLY a JSON object of shape:
        { "components": [...], "elementPools": [...], "providedCandidates": [...], "clientRestrictedList": [...],
          "derivedScope": [{ "listId", "componentId" ("*" for product-wide), "reason",
                             "citation": { "source", "reference", "retrievedAt" } }] }
        """;
```

Replace the `ConstraintsDoc` construction in `RunAsync`:

```csharp
        return AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(project.ProjectId), ProjectId = project.ProjectId,
            Components = o.Components, ElementPools = o.ElementPools,
            ProvidedCandidates = o.ProvidedCandidates,
            ClientRestrictedList = o.ClientRestrictedList, DerivedScope = o.DerivedScope,
        });
```

Replace `Validate` — echo components + element pools (by component+element+line), keep the scope checks:

```csharp
    internal static string? Validate(IntakeOutput o, ProjectDoc project)
    {
        var payload = JsonSerializer.Deserialize<IntakeOutput>(project.Payload.GetRawText(), Json.Options)!;
        if (o.Components.Count != payload.Components.Count ||
            !o.Components.Select(c => c.Id).OrderBy(x => x).SequenceEqual(payload.Components.Select(c => c.Id).OrderBy(x => x)))
            return "components must exactly echo the input payload (no additions/removals)";
        static IEnumerable<string> Keys(IEnumerable<ElementPool> ps) =>
            ps.Select(p => $"{p.Component}|{p.Element}|{p.Line}").OrderBy(x => x);
        if (!Keys(o.ElementPools).SequenceEqual(Keys(payload.ElementPools)))
            return "element pools must exactly echo the input payload (no additions/removals)";
        if (o.DerivedScope.Count == 0)
            return "derivedScope must not be empty — at minimum the product-wide element gate lists apply";
        var known = o.Components.Select(c => c.Id).Append("*").ToHashSet();
        foreach (var e in o.DerivedScope)
        {
            if (!known.Contains(e.ComponentId)) return $"derivedScope references unknown component '{e.ComponentId}'";
            if (string.IsNullOrWhiteSpace(e.Citation?.Source) || string.IsNullOrWhiteSpace(e.Citation?.Reference))
                return $"derivedScope entry '{e.ListId}' is missing its citation — every list must cite a retrieved source";
        }
        return null;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~IntakeAgentTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator/Agents/IntakeAgent.cs src/Smx.Orchestrator.Tests/IntakeAgentTests.cs
git commit -m "feat(agent): Intake echoes element pools + provided candidates"
```

---

## Task 13: Orchestrator DI — wire the catalog lookup

**Files:**
- Modify: `src/Smx.Orchestrator/Program.cs`

- [ ] **Step 1: Register `ICatalogLookup`.** In `src/Smx.Orchestrator/Program.cs`, directly **after** the existing `ICompatibilityLookup` registration (the block ending on the line `sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CompatibilityContainer)));`), insert:

```csharp
builder.Services.AddSingleton<ICatalogLookup>(sp => new CosmosCatalogLookup(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CatalogContainer)));
```

`ICatalogLookup` (namespace `Smx.Domain.Tools`) and `CosmosCatalogLookup` (namespace `Smx.Infrastructure.Search`) are both already imported by the file's existing `using` directives — no new `using` needed. `ToolBox` is registered as `AddSingleton<ToolBox>()`, so DI resolves its new 5th ctor arg from this registration automatically. The `StageDispatcher` registration (which passes `opts.ScreeningParallelism` positionally) is unaffected by the constructor parameter rename.

- [ ] **Step 2: Build to verify wiring.**

Run: `dotnet build src/Smx.Backend.sln`
Expected: Build succeeds (the `ToolBox` 5-arg ctor is now satisfiable).

- [ ] **Step 3: Full test run.**

Run: `dotnet test src/Smx.Backend.sln`
Expected: all Domain + Orchestrator tests PASS (Backend + Eval addressed in Tasks 14–15).

- [ ] **Step 4: Commit**

```bash
git add src/Smx.Orchestrator/Program.cs
git commit -m "chore(di): register CosmosCatalogLookup for Discovery"
```

---

## Task 14: Backend API — element pools payload

**Files:**
- Modify: `src/Smx.Backend/Api/CreateProjectRequest.cs`
- Modify: `src/Smx.Backend.Tests/ProjectEndpointsTests.cs`

- [ ] **Step 1: Write the failing test** — in `src/Smx.Backend.Tests/ProjectEndpointsTests.cs`, replace the request-construction helper and the validation test to the new contract. Add/adjust:

```csharp
[Fact]
public async Task Post_WithElementPools_Returns202_AndSeedsProject()
{
    var req = new CreateProjectRequest("Acme", "MUFE",
        Components: [new("bottle", "PET", "packaging", ["EU"], "brand")],
        ElementPools: [new("bottle", "Y", "Kα", "V", null)],
        Candidates: null,
        ClientRestrictedList: ["Pb"]);
    var resp = await _client.PostAsJsonAsync("/projects", req);
    Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);
}

[Fact]
public async Task Post_WithNeitherPoolsNorCandidates_Returns400()
{
    var req = new CreateProjectRequest("Acme", "MUFE",
        Components: [new("bottle", "PET", "packaging", ["EU"], "brand")],
        ElementPools: [], Candidates: null, ClientRestrictedList: null);
    var resp = await _client.PostAsJsonAsync("/projects", req);
    Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
}
```

(Update any other test in this file that constructs `CreateProjectRequest` with `Substances` to use `ElementPools`/`Candidates`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ProjectEndpointsTests.Post_WithElementPools_Returns202_AndSeedsProject"`
Expected: FAIL — `CreateProjectRequest` still requires `Substances`.

- [ ] **Step 3: Update the request record.** Replace `src/Smx.Backend/Api/CreateProjectRequest.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateProjectRequest(
    string Client, string Product,
    List<ComponentSpec> Components,
    List<ElementPool> ElementPools,
    List<CandidateSubstance>? Candidates,
    List<string>? ClientRestrictedList)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Client) || string.IsNullOrWhiteSpace(Product)) return "client and product are required";
        if (Components is not { Count: > 0 }) return "at least one component is required";
        if (Components.Select(c => c.Id).Distinct().Count() != Components.Count) return "component ids must be unique";
        var hasPools = ElementPools is { Count: > 0 };
        var hasCandidates = Candidates is { Count: > 0 };
        if (!hasPools && !hasCandidates) return "provide element pools (production) or explicit candidates (known-candidate mode)";
        var componentIds = Components.Select(c => c.Id).ToHashSet();
        if (hasPools && ElementPools.Any(p => !componentIds.Contains(p.Component)))
            return "every element pool must reference a declared component";
        // Anti-rubber-stamping (design §4): a conditional (L) pool entry must carry its signal-character note.
        if (hasPools && ElementPools.Any(p => p.Status == "L" && string.IsNullOrWhiteSpace(p.SignalNote)))
            return "each conditional (L) element pool entry must carry a signal-character note";
        if (hasCandidates && Candidates!.Any(c => !componentIds.Contains(c.ComponentId)))
            return "every candidate must reference a declared component";
        return null;
    }
}
```

The POST handler already serializes the whole request to the payload and Intake echoes it. But the payload keys must match `IntakeOutput` for the known-candidate path — the handler must also route `Candidates` → the payload's `providedCandidates`. Since `IntakeOutput` reads `providedCandidates`/`elementPools`, and `CreateProjectRequest` serializes `Candidates`/`ElementPools`, add `[JsonPropertyName]` to align, OR simplest: in `ProjectEndpoints.cs` build the payload explicitly. Update the POST handler in `src/Smx.Backend/Api/ProjectEndpoints.cs`:

```csharp
        app.MapPost("/projects", async (CreateProjectRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (req.Validate() is { } error) return Results.BadRequest(new { error });
            var projectId = $"proj-{Guid.NewGuid():N}"[..17];
            var payload = JsonSerializer.SerializeToElement(new
            {
                components = req.Components,
                elementPools = req.ElementPools,
                providedCandidates = req.Candidates ?? [],
                clientRestrictedList = req.ClientRestrictedList ?? [],
            }, Json.Options);
            var doc = ProjectDoc.Create(projectId, req.Client, req.Product, payload);
            doc.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertProjectAsync(doc, ct);
            return Results.Accepted($"/projects/{projectId}", new { projectId });
        });
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~ProjectEndpointsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend/Api/CreateProjectRequest.cs src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Backend.Tests/ProjectEndpointsTests.cs
git commit -m "feat(api): POST /projects takes element pools + optional candidates"
```

---

## Task 15: Eval golden — new payload shape (known-candidate mode)

**Files:**
- Modify: `tools/Smx.Eval/golden/starter.json`

The harness POSTs `projectPayload` verbatim and scores `(cas, componentId)` cells, so we only change the payload to the new contract using **known-candidate mode** (deterministic Regulatory grading). Keep the expected cells unchanged.

- [ ] **Step 1: Replace the file.** Overwrite `tools/Smx.Eval/golden/starter.json` with the following (same `components`, `clientRestrictedList`, and `expected` as before; `substances` → per-component `candidates` in known-candidate mode; minimal `elementPools` added):

```json
[
  {
    "name": "starter-eu-bottle-liquid",
    "projectPayload": {
      "client": "Golden", "product": "EU shampoo bottle",
      "components": [
        { "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"], "objective": "brand" },
        { "id": "liquid", "material": "aqueous surfactant", "application": "cosmetic", "markets": ["EU"], "objective": "brand" }
      ],
      "elementPools": [
        { "component": "bottle", "element": "Cd", "line": "Kα", "status": "V", "signalNote": null },
        { "component": "bottle", "element": "Pb", "line": "Lα", "status": "V", "signalNote": null },
        { "component": "bottle", "element": "Zr", "line": "Kα", "status": "V", "signalNote": null },
        { "component": "liquid", "element": "Cd", "line": "Kα", "status": "V", "signalNote": null },
        { "component": "liquid", "element": "Pb", "line": "Lα", "status": "V", "signalNote": null },
        { "component": "liquid", "element": "Zr", "line": "Kα", "status": "V", "signalNote": null }
      ],
      "candidates": [
        { "componentId": "bottle", "element": "Cd", "form": "sulfide", "cas": "1306-23-6", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "known-candidate (eval)", "citations": [{ "source": "catalog", "reference": "eval/known", "retrievedAt": "t" }] },
        { "componentId": "liquid", "element": "Cd", "form": "sulfide", "cas": "1306-23-6", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "known-candidate (eval)", "citations": [{ "source": "catalog", "reference": "eval/known", "retrievedAt": "t" }] },
        { "componentId": "bottle", "element": "Pb", "form": "2-ethylhexanoate", "cas": "301-08-6", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "known-candidate (eval)", "citations": [{ "source": "catalog", "reference": "eval/known", "retrievedAt": "t" }] },
        { "componentId": "liquid", "element": "Pb", "form": "2-ethylhexanoate", "cas": "301-08-6", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "known-candidate (eval)", "citations": [{ "source": "catalog", "reference": "eval/known", "retrievedAt": "t" }] },
        { "componentId": "bottle", "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "known-candidate (eval)", "citations": [{ "source": "catalog", "reference": "eval/known", "retrievedAt": "t" }] },
        { "componentId": "liquid", "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2", "particleSize": null, "solvent": null, "preferred": true, "tier": "A", "rationale": "known-candidate (eval)", "citations": [{ "source": "catalog", "reference": "eval/known", "retrievedAt": "t" }] }
      ],
      "clientRestrictedList": []
    },
    "expected": [
      { "cas": "1306-23-6", "componentId": "bottle", "expected": "Fail", "track": "reasoning" },
      { "cas": "1306-23-6", "componentId": "liquid", "expected": "Fail", "track": "reasoning" },
      { "cas": "301-08-6",  "componentId": "bottle", "expected": "Fail", "track": "reasoning" },
      { "cas": "301-08-6",  "componentId": "liquid", "expected": "Fail", "track": "reasoning" }
    ]
  }
]
```

Note: the harness deserializes `projectPayload` into `CreateProjectRequest` when it POSTs; the `elementPools`/`candidates`/`components` field names above match that record (Task 14). If the harness instead re-serializes a typed payload, confirm its payload model carries these fields — otherwise it forwards the JSON verbatim and no code change is needed.

- [ ] **Step 2: Build the eval to confirm the JSON still deserializes.**

Run: `dotnet build tools/Smx.Eval/Smx.Eval.csproj`
Expected: Build succeeds. (The eval models are payload-agnostic; this just confirms the file is valid JSON.)

- [ ] **Step 3: Run the eval unit tests.**

Run: `dotnet test tools/Smx.Eval.Tests/Smx.Eval.Tests.csproj`
Expected: PASS (metric tests are payload-independent).

- [ ] **Step 4: Commit**

```bash
git add tools/Smx.Eval/golden/starter.json
git commit -m "test(eval): golden payload uses element pools + known candidates"
```

---

## Task 16: Full green + integration sanity

**Files:** none (verification task).

- [ ] **Step 1: Full solution build.**

Run: `dotnet build src/Smx.Backend.sln`
Expected: Build succeeds, zero warnings introduced by this plan.

- [ ] **Step 2: Full test run.**

Run: `dotnet test src/Smx.Backend.sln`
Expected: ALL tests PASS. Confirm these classes are green: `RecordDocsTests`, `MatrixAssemblerTests`, `InMemoryRecordStoreTests`, `CatalogCardTests`, `ToolBoxTests`, `DiscoveryAgentTests`, `RegulatoryAgentTests`, `StageDispatcherTests`, `IntakeAgentTests`, `ProjectEndpointsTests`.

- [ ] **Step 3: Confirm no stale `screening` references remain.**

Run: `grep -rniE '\bscreening\b|ScreeningAgent|ScreeningTools|RunScreeningAsync|Stages\.Screening' src/ tools/ --include=*.cs`
Expected: no matches (all renamed to discovery/regulatory).

- [ ] **Step 4: Confirm the eval project builds against the solution.**

Run: `dotnet build tools/Smx.Eval/Smx.Eval.csproj && dotnet test tools/Smx.Eval.Tests/Smx.Eval.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit any final cleanup (if grep found stragglers; otherwise skip).**

```bash
git add -A && git commit -m "chore: finish per-stage refactor cleanup" || echo "nothing to commit"
```

---

## Notes for the implementer

- **Compatibility dimension:** the `VerdictDimension` enum still lists `Compatibility`, but Regulatory never emits it and its `Validate` requires exactly the three regulatory dimensions. Leaving the enum value is intentional (zero-churn; Discovery may reference it as a signal name) — do **not** spend time removing it.
- **`ref-catalog` already exists** (seeded by the reference-data subsystem) — this plan adds no Cosmos container and no Bicep change. It only wires a lookup against the existing container.
- **Known-candidate mode is a test/eval seam**, not the production path. Production callers send `elementPools` only; Discovery generates candidates. Keep that distinction clear in any follow-on.
- **Ordering:** Tasks 1–6 (domain/infra) leave the solution temporarily non-building until Tasks 7–14 update the orchestrator/backend. Prefer running per-project test filters (e.g. `Smx.Domain.Tests`) for Tasks 1–6, then the solution-wide run from Task 11 onward. The commits stay small; the green bar returns at Task 11 for the orchestrator and Task 16 overall.
- This plan is **Plan 1 of 5**. Next: Plan 2 (gates + async loop + operator-entry API). Do not start it until this one is merged and green.

# Agent Backend (MAF + Claude on Foundry + RAG) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and deploy the SMX reasoning backend — two ACA apps (API + agent orchestrator) that ingest project constraints, screen candidate substances against compatibility + regulatory + hazard constraints via Claude Opus 4.7 on Azure AI Foundry with RAG over AI Search, and emit the Excel-style compatibility matrix, proven by an eval harness.

**Architecture:** Record-as-bus: the API writes a `project` doc to Cosmos; the orchestrator's change-feed processor dispatches MAF agents (Constraint-Intake, Screening) whose only fact sources are tools (AI Search indexes + deterministic Cosmos lookups); agents write `constraints`/`verdict` docs back; a deterministic assembler folds verdicts into a `matrix` doc; the API serves it as JSON/xlsx. Spec: `docs/superpowers/specs/2026-07-08-agent-backend-design.md`.

**Tech Stack:** .NET 8 (`net8.0`), ASP.NET Core minimal API, Microsoft Agent Framework (`Microsoft.Agents.AI`), `Microsoft.Extensions.AI`, Anthropic C# SDK (`Anthropic` + `Anthropic.Foundry`, `AsIChatClient`), `Microsoft.Azure.Cosmos` (change feed processor), `Azure.Search.Documents`, ClosedXML, xUnit with hand-written fakes, Bicep (both `infra/` and `infra/single-rg/` variants).

**Refinement over spec §2 (locked here):** shared Azure adapters (Cosmos record store, search clients, Foundry chat-client factory) live in a fourth project `src/Smx.Infrastructure/`, referenced by both apps — so `Smx.Backend` and `Smx.Orchestrator` never reference each other and `Smx.Domain` stays Azure-free.

**Environment notes for the engineer:**
- Work happens in the worktree `.claude/worktrees/agent-backend` (branch `worktree-agent-backend`). Never touch the main checkout — another session owns it.
- Only the .NET **10** SDK is installed locally; projects target `net8.0`. Executable/test projects need `<RollForward>Major</RollForward>` to run locally (same trick as `Smx.Functions.Tests`). Container images use real 8.0 runtimes.
- Keyless-by-default estate: managed identity everywhere; any unavoidable key lives in Key Vault.
- Every Bicep edit is made **twice**: `infra/modules/X.bicep` and `infra/single-rg/modules/X.bicep` (bodies are kept byte-identical), plus the corresponding `main.bicep` wiring in each variant.
- Build/test: `dotnet build src/Smx.Backend.sln`, `dotnet test src/Smx.Backend.sln`. Never build `Smx.Functions.sln` artifacts into this plan's commits.

---

## Record & API contracts (single source of truth for all tasks)

**Cosmos:** database `smx`, container `record` (PK `/projectId`), container `record-leases` (PK `/id`). All documents carry `id`, `projectId`, `type`. camelCase JSON.

| type | id convention | producer |
|---|---|---|
| `project` | `{projectId}` | API |
| `constraints` | `{projectId}\|constraints` | Intake agent |
| `verdict` | `{projectId}\|verdict\|{cas}\|{componentId}` | Screening agent |
| `matrix` | `{projectId}\|matrix` | assembler |

**Stages** on the project doc: `intake`, `screening`, `matrix`; statuses: `pending`, `running`, `failed`, `needs-review`, `done`. Overall verdict folding order (worst wins): `Fail` > `NeedsReview` > `Conditional` > `Pass`.

**API payload** (`POST /projects`):

```json
{
  "client": "Acme",
  "product": "Shampoo bottle",
  "components": [
    { "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"], "objective": "brand" }
  ],
  "substances": [
    { "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2" }
  ],
  "clientRestrictedList": ["Pb"]
}
```

**Verdict dimensions:** `Compatibility`, `ElementGate`, `ApplicationCheck`, `Hazard`.

---

### Task 1: Solution scaffold (4 src projects, 3 test projects)

**Files:**
- Create: `src/Smx.Backend.sln`
- Create: `src/Smx.Domain/Smx.Domain.csproj`
- Create: `src/Smx.Infrastructure/Smx.Infrastructure.csproj`
- Create: `src/Smx.Backend/Smx.Backend.csproj`
- Create: `src/Smx.Orchestrator/Smx.Orchestrator.csproj`
- Create: `src/Smx.Domain.Tests/Smx.Domain.Tests.csproj`
- Create: `src/Smx.Backend.Tests/Smx.Backend.Tests.csproj`
- Create: `src/Smx.Orchestrator.Tests/Smx.Orchestrator.Tests.csproj`

- [ ] **Step 1: Create projects and solution**

```bash
cd src
dotnet new classlib  -n Smx.Domain -f net8.0
dotnet new classlib  -n Smx.Infrastructure -f net8.0
dotnet new web       -n Smx.Backend -f net8.0
dotnet new worker    -n Smx.Orchestrator -f net8.0
dotnet new xunit     -n Smx.Domain.Tests -f net8.0
dotnet new xunit     -n Smx.Backend.Tests -f net8.0
dotnet new xunit     -n Smx.Orchestrator.Tests -f net8.0
dotnet new sln       -n Smx.Backend
dotnet sln Smx.Backend.sln add Smx.Domain Smx.Infrastructure Smx.Backend Smx.Orchestrator Smx.Domain.Tests Smx.Backend.Tests Smx.Orchestrator.Tests
rm Smx.Domain/Class1.cs Smx.Infrastructure/Class1.cs
```

- [ ] **Step 2: Wire references and packages**

```bash
cd src
dotnet add Smx.Infrastructure reference Smx.Domain
dotnet add Smx.Backend        reference Smx.Domain Smx.Infrastructure
dotnet add Smx.Orchestrator   reference Smx.Domain Smx.Infrastructure
dotnet add Smx.Domain.Tests        reference Smx.Domain
dotnet add Smx.Backend.Tests       reference Smx.Backend
dotnet add Smx.Orchestrator.Tests  reference Smx.Orchestrator

# Domain stays dependency-light (ClosedXML-free; xlsx lives in Smx.Backend)
dotnet add Smx.Infrastructure package Microsoft.Azure.Cosmos --version 3.43.0
dotnet add Smx.Infrastructure package Azure.Search.Documents --version 11.6.0
dotnet add Smx.Infrastructure package Azure.Identity
dotnet add Smx.Infrastructure package Azure.Security.KeyVault.Secrets
dotnet add Smx.Infrastructure package Anthropic
dotnet add Smx.Infrastructure package Anthropic.Foundry
dotnet add Smx.Infrastructure package Microsoft.Extensions.AI
dotnet add Smx.Orchestrator  package Microsoft.Agents.AI
dotnet add Smx.Orchestrator  package Azure.Monitor.OpenTelemetry.Exporter
dotnet add Smx.Orchestrator  package OpenTelemetry.Extensions.Hosting
dotnet add Smx.Backend       package ClosedXML --version 0.104.2
dotnet add Smx.Backend       package Azure.Monitor.OpenTelemetry.AspNetCore
dotnet add Smx.Backend.Tests package Microsoft.AspNetCore.Mvc.Testing --version 8.0.*
```

Version pins: match the repo where a package already exists (`Microsoft.Azure.Cosmos 3.43.0`, `Azure.Search.Documents 11.6.0` — same as `Smx.Functions`). For `Anthropic`/`Anthropic.Foundry`/`Microsoft.Agents.AI`/`Microsoft.Extensions.AI`, take the latest stable/beta on NuGet at execution time (`dotnet package search <name> --take 1`) and record the chosen versions in the commit message. If `Microsoft.Agents.AI` needs a prerelease flag, add `--prerelease`.

- [ ] **Step 3: Add `RollForward` to every test + executable project**

In each of `Smx.Backend.csproj`, `Smx.Orchestrator.csproj`, `Smx.Domain.Tests.csproj`, `Smx.Backend.Tests.csproj`, `Smx.Orchestrator.Tests.csproj`, inside the first `<PropertyGroup>`:

```xml
<RollForward>Major</RollForward>
```

- [ ] **Step 4: Build and run the (empty) test suite**

Run: `dotnet test src/Smx.Backend.sln`
Expected: build succeeds; 3 test assemblies, each 1 placeholder `UnitTest1` passing. If `Microsoft.Agents.AI` fails to restore under net8.0, check its TFMs (`dotnet package search Microsoft.Agents.AI --exact-match --verbosity detailed`) and if it requires net9+, retarget `Smx.Orchestrator` (only) and its test project to the required TFM — record the deviation in the commit message.

- [ ] **Step 5: Delete placeholder tests, commit**

```bash
rm src/Smx.Domain.Tests/UnitTest1.cs src/Smx.Backend.Tests/UnitTest1.cs src/Smx.Orchestrator.Tests/UnitTest1.cs
git add src/ && git commit -m "chore(backend): scaffold Smx.Backend solution (Domain/Infrastructure/Backend/Orchestrator + tests)"
```

---

### Task 2: Domain — record documents, citations, verdict folding

**Files:**
- Create: `src/Smx.Domain/Records/RecordIds.cs`
- Create: `src/Smx.Domain/Records/ProjectDoc.cs`
- Create: `src/Smx.Domain/Records/ConstraintsDoc.cs`
- Create: `src/Smx.Domain/Records/VerdictDoc.cs`
- Create: `src/Smx.Domain/Records/MatrixDoc.cs`
- Create: `src/Smx.Domain/Json.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class RecordDocsTests
{
    [Fact]
    public void VerdictId_IsDeterministic_AndPipeDelimited()
    {
        Assert.Equal("p1|verdict|39049-04-2|bottle", RecordIds.Verdict("p1", "39049-04-2", "bottle"));
        Assert.Equal("p1|constraints", RecordIds.Constraints("p1"));
        Assert.Equal("p1|matrix", RecordIds.Matrix("p1"));
    }

    [Fact]
    public void ProjectDoc_SerializesCamelCase_WithTypeDiscriminator()
    {
        var doc = ProjectDoc.Create("p1", "Acme", "Shampoo bottle", JsonDocument.Parse("{}").RootElement);
        var json = JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"type\":\"project\"", json);
        Assert.Contains("\"projectId\":\"p1\"", json);
        Assert.Contains("\"intake\"", json); // stages seeded
        var back = JsonSerializer.Deserialize<ProjectDoc>(json, Json.Options)!;
        Assert.Equal("pending", back.Stages["intake"].Status);
    }

    [Theory]
    [InlineData(new[] { "Pass", "Pass" }, "Pass")]
    [InlineData(new[] { "Pass", "Conditional" }, "Conditional")]
    [InlineData(new[] { "Conditional", "NeedsReview" }, "NeedsReview")]
    [InlineData(new[] { "NeedsReview", "Fail" }, "Fail")]
    public void Verdict_Overall_IsWorstOfDimensions(string[] statuses, string expected)
    {
        var dims = statuses.Select((s, i) => new DimensionVerdict(
            Dimension: ((VerdictDimension)i).ToString(),
            Status: Enum.Parse<VerdictStatus>(s),
            Citations: [new Citation("reg-index", "doc-1#chunk-3", "2026-07-08T00:00:00Z")],
            Confidence: 0.9,
            Rationale: "r")).ToList();
        Assert.Equal(Enum.Parse<VerdictStatus>(expected), VerdictDoc.Fold(dims));
    }

    [Fact]
    public void Verdict_RoundTrips()
    {
        var v = new VerdictDoc
        {
            Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "neodecanoate",
            Dimensions = [new DimensionVerdict("ElementGate", VerdictStatus.Pass,
                [new Citation("reg-index", "reach-annex17#e23", "2026-07-08T00:00:00Z")], 0.95, "not listed")],
        };
        var back = JsonSerializer.Deserialize<VerdictDoc>(JsonSerializer.Serialize(v, Json.Options), Json.Options)!;
        Assert.Equal(VerdictStatus.Pass, back.Overall);
        Assert.Single(back.Dimensions[0].Citations);
    }
}
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `dotnet test src/Smx.Domain.Tests -v q`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement the domain types**

`src/Smx.Domain/Json.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smx.Domain;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
```

`src/Smx.Domain/Records/RecordIds.cs`:

```csharp
namespace Smx.Domain.Records;

public static class RecordTypes
{
    public const string Project = "project";
    public const string Constraints = "constraints";
    public const string Verdict = "verdict";
    public const string Matrix = "matrix";
}

public static class Stages
{
    public const string Intake = "intake";
    public const string Screening = "screening";
    public const string Matrix = "matrix";
}

public static class RecordIds
{
    public static string Constraints(string projectId) => $"{projectId}|constraints";
    public static string Verdict(string projectId, string cas, string componentId) => $"{projectId}|verdict|{cas}|{componentId}";
    public static string Matrix(string projectId) => $"{projectId}|matrix";
}
```

`src/Smx.Domain/Records/ProjectDoc.cs`:

```csharp
using System.Text.Json;

namespace Smx.Domain.Records;

public sealed class StageState
{
    public string Status { get; set; } = "pending"; // pending|running|failed|needs-review|done
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

public sealed class ProjectDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Project;
    public required string Client { get; set; }
    public required string Product { get; set; }
    public JsonElement Payload { get; set; } // the POST /projects body, verbatim
    public Dictionary<string, StageState> Stages { get; set; } = new();
    public string CreatedAt { get; set; } = "";

    public static ProjectDoc Create(string projectId, string client, string product, JsonElement payload) => new()
    {
        Id = projectId, ProjectId = projectId, Client = client, Product = product,
        Payload = payload.Clone(),
        Stages = new()
        {
            [Records.Stages.Intake] = new StageState(),
            [Records.Stages.Screening] = new StageState(),
            [Records.Stages.Matrix] = new StageState(),
        },
    };
}
```

`src/Smx.Domain/Records/ConstraintsDoc.cs`:

```csharp
namespace Smx.Domain.Records;

public sealed record Citation(string Source, string Reference, string RetrievedAt, string? Snippet = null);

public sealed record ComponentSpec(string Id, string Material, string Application, IReadOnlyList<string> Markets, string Objective);
public sealed record SubstanceSpec(string Element, string Form, string Cas);
public sealed record AppliedList(string ListId, string ComponentId, string Reason, Citation Citation);

public sealed class ConstraintsDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Constraints;
    public List<ComponentSpec> Components { get; set; } = [];
    public List<SubstanceSpec> Substances { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    /// Derived regulatory scope: which lists apply, per component (element gate entries use ComponentId="*").
    public List<AppliedList> DerivedScope { get; set; } = [];
}
```

`src/Smx.Domain/Records/VerdictDoc.cs`:

```csharp
namespace Smx.Domain.Records;

public enum VerdictStatus { Pass, Conditional, NeedsReview, Fail }
public enum VerdictDimension { Compatibility, ElementGate, ApplicationCheck, Hazard }

public sealed record DimensionVerdict(
    string Dimension, VerdictStatus Status, IReadOnlyList<Citation> Citations, double Confidence, string Rationale);

public sealed class VerdictDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Verdict;
    public required string Cas { get; set; }
    public required string ComponentId { get; set; }
    public required string Element { get; set; }
    public required string Form { get; set; }
    public List<DimensionVerdict> Dimensions { get; set; } = [];
    public VerdictStatus Overall => Fold(Dimensions);

    public static VerdictStatus Fold(IReadOnlyList<DimensionVerdict> dims) =>
        dims.Count == 0 ? VerdictStatus.NeedsReview : dims.Max(d => d.Status);
}
```

(Note the enum order `Pass < Conditional < NeedsReview < Fail` makes `Max` the worst-wins fold.)

`src/Smx.Domain/Records/MatrixDoc.cs`:

```csharp
namespace Smx.Domain.Records;

public sealed record MatrixCell(string Cas, string ComponentId, VerdictStatus Overall, List<DimensionVerdict> Dimensions);

public sealed class MatrixDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Matrix;
    public List<SubstanceSpec> Rows { get; set; } = [];      // substances
    public List<string> Columns { get; set; } = [];          // component ids
    public List<MatrixCell> Cells { get; set; } = [];
    public string GeneratedAt { get; set; } = "";
}
```

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test src/Smx.Domain.Tests -v q`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Domain.Tests
git commit -m "feat(domain): record documents, citations, worst-wins verdict folding (TDD)"
```

### Task 3: Domain — matrix assembler

**Files:**
- Create: `src/Smx.Domain/MatrixAssembler.cs`
- Test: `src/Smx.Domain.Tests/MatrixAssemblerTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class MatrixAssemblerTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand"), new("liquid", "aqueous", "cosmetic", ["EU"], "brand")],
        Substances = [new("Zr", "neodecanoate", "cas-zr"), new("Cd", "sulfide", "cas-cd")],
    };

    private static VerdictDoc V(string cas, string comp, VerdictStatus s) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1", Cas = cas, ComponentId = comp,
        Element = cas == "cas-zr" ? "Zr" : "Cd", Form = "f",
        Dimensions = [new("ElementGate", s, [new Citation("reg-index", "r", "t")], 0.9, "r")],
    };

    [Fact]
    public void IsComplete_FalseUntilEveryCellHasAVerdict()
    {
        var c = Constraints();
        Assert.False(MatrixAssembler.IsComplete(c, [V("cas-zr", "bottle", VerdictStatus.Pass)]));
        VerdictDoc[] all = [V("cas-zr", "bottle", VerdictStatus.Pass), V("cas-zr", "liquid", VerdictStatus.Pass),
                            V("cas-cd", "bottle", VerdictStatus.Fail), V("cas-cd", "liquid", VerdictStatus.Fail)];
        Assert.True(MatrixAssembler.IsComplete(c, all));
    }

    [Fact]
    public void Assemble_ProducesRowPerSubstance_ColumnPerComponent_CellPerPair()
    {
        var c = Constraints();
        VerdictDoc[] all = [V("cas-zr", "bottle", VerdictStatus.Pass), V("cas-zr", "liquid", VerdictStatus.Conditional),
                            V("cas-cd", "bottle", VerdictStatus.Fail), V("cas-cd", "liquid", VerdictStatus.Fail)];
        var m = MatrixAssembler.Assemble(c, all, "2026-07-08T00:00:00Z");
        Assert.Equal("p1|matrix", m.Id);
        Assert.Equal(2, m.Rows.Count);
        Assert.Equal(["bottle", "liquid"], m.Columns);
        Assert.Equal(4, m.Cells.Count);
        Assert.Equal(VerdictStatus.Conditional, m.Cells.Single(x => x.Cas == "cas-zr" && x.ComponentId == "liquid").Overall);
    }

    [Fact]
    public void Assemble_Throws_WhenIncomplete()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MatrixAssembler.Assemble(Constraints(), [V("cas-zr", "bottle", VerdictStatus.Pass)], "t"));
    }
}
```

- [ ] **Step 2: Run tests, verify fail**

Run: `dotnet test src/Smx.Domain.Tests -v q` — Expected: FAIL (`MatrixAssembler` missing).

- [ ] **Step 3: Implement**

`src/Smx.Domain/MatrixAssembler.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

public static class MatrixAssembler
{
    public static IEnumerable<(string Cas, string ComponentId)> Cells(ConstraintsDoc c) =>
        c.Substances.SelectMany(s => c.Components.Select(k => (s.Cas, k.Id)));

    public static bool IsComplete(ConstraintsDoc c, IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var have = verdicts.Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        return Cells(c).All(have.Contains);
    }

    public static MatrixDoc Assemble(ConstraintsDoc c, IReadOnlyCollection<VerdictDoc> verdicts, string generatedAt)
    {
        if (!IsComplete(c, verdicts))
            throw new InvalidOperationException("matrix assembly requires a verdict for every substance×component cell");
        var byCell = verdicts.ToDictionary(v => (v.Cas, v.ComponentId));
        return new MatrixDoc
        {
            Id = RecordIds.Matrix(c.ProjectId), ProjectId = c.ProjectId,
            Rows = [.. c.Substances],
            Columns = [.. c.Components.Select(k => k.Id)],
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

- [ ] **Step 4: Run tests, verify pass**

Run: `dotnet test src/Smx.Domain.Tests -v q` — Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Domain.Tests
git commit -m "feat(domain): deterministic matrix assembler with completeness check (TDD)"
```

---

### Task 4: Record store — interface, in-memory fake, Cosmos implementation

**Files:**
- Create: `src/Smx.Domain/IRecordStore.cs`
- Create: `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs` (shared fake — Backend/Orchestrator test projects link this file)
- Create: `src/Smx.Infrastructure/CosmosRecordStore.cs`
- Test: `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`

- [ ] **Step 1: Define the interface** (interface-first; the fake is the testable contract)

`src/Smx.Domain/IRecordStore.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

public interface IRecordStore
{
    Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default);
    Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default);
    Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default);
    Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default);

    Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default);
    Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default);
    Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default);
    Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing fake tests**

`src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Domain.Tests.Fakes;

namespace Smx.Domain.Tests;

public class InMemoryRecordStoreTests
{
    [Fact]
    public async Task Upserts_AreIdempotent_ByDocumentId()
    {
        var store = new InMemoryRecordStore();
        var v = new VerdictDoc { Id = RecordIds.Verdict("p1", "c1", "bottle"), ProjectId = "p1",
            Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" };
        await store.UpsertVerdictAsync(v);
        await store.UpsertVerdictAsync(v); // redelivery must be harmless
        Assert.Single(await store.GetVerdictsAsync("p1"));
    }

    [Fact]
    public async Task Queries_AreScopedToProject()
    {
        var store = new InMemoryRecordStore();
        await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("p1", "c1", "bottle"),
            ProjectId = "p1", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" });
        await store.UpsertVerdictAsync(new VerdictDoc { Id = RecordIds.Verdict("p2", "c1", "bottle"),
            ProjectId = "p2", Cas = "c1", ComponentId = "bottle", Element = "Zr", Form = "f" });
        Assert.Single(await store.GetVerdictsAsync("p1"));
        Assert.Null(await store.GetMatrixAsync("p1"));
    }
}
```

- [ ] **Step 3: Run tests, verify fail** — `dotnet test src/Smx.Domain.Tests -v q` → FAIL.

- [ ] **Step 4: Implement the fake**

`src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`:

```csharp
using System.Collections.Concurrent;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly ConcurrentDictionary<string, object> _docs = new();
    public IReadOnlyCollection<object> Documents => (IReadOnlyCollection<object>)_docs.Values;

    public Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(projectId, out var d) ? (ProjectDoc?)d : null);
    public Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Constraints(projectId), out var d) ? (ConstraintsDoc?)d : null);
    public Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(RecordIds.Matrix(projectId), out var d) ? (MatrixDoc?)d : null);
    public Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VerdictDoc>>(
            _docs.Values.OfType<VerdictDoc>().Where(v => v.ProjectId == projectId).ToList());

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) { _docs[doc.Id] = doc; return Task.CompletedTask; }
}
```

Link the fake into the other test projects — add to `Smx.Backend.Tests.csproj` and `Smx.Orchestrator.Tests.csproj`:

```xml
<ItemGroup>
  <Compile Include="../Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs" Link="Fakes/InMemoryRecordStore.cs" />
</ItemGroup>
```

(Those two test projects also need `<ProjectReference Include="../Smx.Domain/Smx.Domain.csproj" />` if not already transitively available — add it explicitly.)

- [ ] **Step 5: Run tests, verify pass** — `dotnet test src/Smx.Domain.Tests -v q` → PASS.

- [ ] **Step 6: Implement the Cosmos store** (no unit tests — thin adapter, same style as `Smx.Functions/Sds/Data`; exercised by the deploy smoke)

`src/Smx.Infrastructure/CosmosRecordStore.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

public sealed class CosmosRecordStore(Container container) : IRecordStore
{
    public Task<ProjectDoc?> GetProjectAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<ProjectDoc>(projectId, projectId, ct);
    public Task<ConstraintsDoc?> GetConstraintsAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<ConstraintsDoc>(RecordIds.Constraints(projectId), projectId, ct);
    public Task<MatrixDoc?> GetMatrixAsync(string projectId, CancellationToken ct = default) =>
        ReadAsync<MatrixDoc>(RecordIds.Matrix(projectId), projectId, ct);

    public async Task<IReadOnlyList<VerdictDoc>> GetVerdictsAsync(string projectId, CancellationToken ct = default)
    {
        var results = new List<VerdictDoc>();
        var query = container.GetItemLinqQueryable<VerdictDoc>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(projectId) })
            .Where(d => d.Type == RecordTypes.Verdict)
            .ToFeedIterator();
        while (query.HasMoreResults)
            results.AddRange(await query.ReadNextAsync(ct));
        return results;
    }

    public Task UpsertProjectAsync(ProjectDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertConstraintsAsync(ConstraintsDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertVerdictAsync(VerdictDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);
    public Task UpsertMatrixAsync(MatrixDoc doc, CancellationToken ct = default) => Upsert(doc, doc.ProjectId, ct);

    private async Task<T?> ReadAsync<T>(string id, string pk, CancellationToken ct) where T : class
    {
        try { return (await container.ReadItemAsync<T>(id, new PartitionKey(pk), cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private Task Upsert<T>(T doc, string pk, CancellationToken ct) =>
        container.UpsertItemAsync(doc, new PartitionKey(pk), cancellationToken: ct);
}
```

The `CosmosClient` must be constructed with the same camelCase serialization the repo already uses — reuse the pattern from `src/Smx.Functions/Program.cs:38` (`CosmosClientOptions { SerializerOptions = new() { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase } }`). Note: `VerdictDoc.Overall` is a computed get-only property — Cosmos/STJ will serialize it but never needs to round-trip it; that is fine.

- [ ] **Step 7: Build + commit**

Run: `dotnet build src/Smx.Backend.sln` — Expected: success.

```bash
git add src/Smx.Domain src/Smx.Domain.Tests src/Smx.Infrastructure src/Smx.Backend.Tests src/Smx.Orchestrator.Tests
git commit -m "feat(record): IRecordStore + in-memory fake (TDD) + Cosmos implementation"
```

### Task 5: Backend API — endpoints over IRecordStore

**Files:**
- Create: `src/Smx.Backend/Api/ProjectEndpoints.cs`
- Create: `src/Smx.Backend/Api/CreateProjectRequest.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/ProjectEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

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

public class ProjectEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemoryRecordStore _store = new();
    private readonly HttpClient _client;

    public ProjectEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IRecordStore>(_store))).CreateClient();
    }

    private static readonly object ValidBody = new
    {
        client = "Acme", product = "Shampoo bottle",
        components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "brand" } },
        substances = new[] { new { element = "Zr", form = "neodecanoate", cas = "39049-04-2" } },
        clientRestrictedList = new[] { "Pb" },
    };

    [Fact]
    public async Task PostProjects_Returns202_AndSeedsProjectDoc()
    {
        var resp = await _client.PostAsJsonAsync("/projects", ValidBody);
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("projectId").GetString()!;
        var doc = await _store.GetProjectAsync(id);
        Assert.NotNull(doc);
        Assert.Equal("pending", doc!.Stages[Stages.Intake].Status);
    }

    [Fact]
    public async Task PostProjects_Rejects_EmptyComponentsOrSubstances()
    {
        var resp = await _client.PostAsJsonAsync("/projects", new { client = "A", product = "P",
            components = Array.Empty<object>(), substances = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetProject_ReportsStageStatuses()
    {
        var post = await _client.PostAsJsonAsync("/projects", ValidBody);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        var status = await _client.GetFromJsonAsync<JsonElement>($"/projects/{id}");
        Assert.Equal("pending", status.GetProperty("stages").GetProperty("intake").GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetMatrix_404UntilAssembled_ThenReturnsJson()
    {
        var post = await _client.PostAsJsonAsync("/projects", ValidBody);
        var id = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{id}/matrix")).StatusCode);

        await _store.UpsertMatrixAsync(new MatrixDoc { Id = RecordIds.Matrix(id), ProjectId = id,
            Columns = ["bottle"], GeneratedAt = "t" });
        var resp = await _client.GetAsync($"/projects/{id}/matrix");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("bottle", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Healthz_Returns200()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/healthz")).StatusCode);
    }
}
```

- [ ] **Step 2: Run tests, verify fail** — `dotnet test src/Smx.Backend.Tests -v q` → FAIL (endpoints missing; `Program` not visible).

- [ ] **Step 3: Implement**

`src/Smx.Backend/Api/CreateProjectRequest.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public sealed record CreateProjectRequest(
    string Client, string Product,
    List<ComponentSpec> Components, List<SubstanceSpec> Substances,
    List<string>? ClientRestrictedList)
{
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Client) || string.IsNullOrWhiteSpace(Product)) return "client and product are required";
        if (Components is not { Count: > 0 }) return "at least one component is required";
        if (Substances is not { Count: > 0 }) return "at least one candidate substance is required";
        if (Components.Select(c => c.Id).Distinct().Count() != Components.Count) return "component ids must be unique";
        if (Substances.Select(s => s.Cas).Distinct().Count() != Substances.Count) return "substance CAS numbers must be unique";
        return null;
    }
}
```

`src/Smx.Backend/Api/ProjectEndpoints.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/projects", async (CreateProjectRequest req, IRecordStore store, CancellationToken ct) =>
        {
            if (req.Validate() is { } error) return Results.BadRequest(new { error });
            var projectId = $"proj-{Guid.NewGuid():N}"[..17];
            var payload = JsonSerializer.SerializeToElement(req, Json.Options);
            var doc = ProjectDoc.Create(projectId, req.Client, req.Product, payload);
            doc.CreatedAt = DateTimeOffset.UtcNow.ToString("O");
            await store.UpsertProjectAsync(doc, ct);
            return Results.Accepted($"/projects/{projectId}", new { projectId });
        });

        app.MapGet("/projects/{projectId}", async (string projectId, IRecordStore store, CancellationToken ct) =>
            await store.GetProjectAsync(projectId, ct) is { } doc
                ? Results.Json(new { doc.ProjectId, doc.Client, doc.Product, doc.Stages }, Json.Options)
                : Results.NotFound());

        app.MapGet("/projects/{projectId}/matrix",
            async (string projectId, string? format, IRecordStore store, CancellationToken ct) =>
        {
            if (await store.GetMatrixAsync(projectId, ct) is not { } matrix) return Results.NotFound();
            if (!string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
                return Results.Json(matrix, Json.Options);
            var bytes = MatrixXlsxWriter.Write(matrix); // Task 6
            return Results.File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"{projectId}-compatibility-matrix.xlsx");
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
    }
}
```

`src/Smx.Backend/Program.cs` (full file at this task; Foundry/Cosmos DI arrives in Task 12):

```csharp
using System.Text.Json.Serialization;
using Smx.Backend.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
// IRecordStore is registered in Task 12 (Cosmos) and overridden in tests.

var app = builder.Build();
app.MapProjectEndpoints();
app.Run();

public partial class Program { } // WebApplicationFactory hook
```

Until Task 6 exists, add a temporary compiling stub so this task stands alone:

`src/Smx.Backend/Api/MatrixXlsxWriter.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class MatrixXlsxWriter
{
    public static byte[] Write(MatrixDoc matrix) => throw new NotImplementedException("Task 6");
}
```

- [ ] **Step 4: Run tests, verify pass** — `dotnet test src/Smx.Backend.Tests -v q` → PASS (5 tests; none exercise xlsx yet).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend src/Smx.Backend.Tests
git commit -m "feat(api): POST /projects, status, matrix (json) endpoints over IRecordStore (TDD)"
```

---

### Task 6: Xlsx export of the matrix

**Files:**
- Modify: `src/Smx.Backend/Api/MatrixXlsxWriter.cs` (replace stub)
- Test: `src/Smx.Backend.Tests/MatrixXlsxWriterTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using ClosedXML.Excel;
using Smx.Backend.Api;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class MatrixXlsxWriterTests
{
    private static MatrixDoc Matrix() => new()
    {
        Id = "p1|matrix", ProjectId = "p1",
        Rows = [new("Zr", "neodecanoate", "cas-zr"), new("Cd", "sulfide", "cas-cd")],
        Columns = ["bottle", "liquid"],
        Cells =
        [
            new("cas-zr", "bottle", VerdictStatus.Pass, []),
            new("cas-zr", "liquid", VerdictStatus.Conditional, []),
            new("cas-cd", "bottle", VerdictStatus.Fail,
                [new("ElementGate", VerdictStatus.Fail, [new Citation("reg-index", "reach-annex17#e23", "t")], 0.99, "Cd restricted")]),
            new("cas-cd", "liquid", VerdictStatus.Fail, []),
        ],
        GeneratedAt = "2026-07-08T00:00:00Z",
    };

    [Fact]
    public void Write_ProducesMatrixSheet_RowsSubstances_ColumnsComponents()
    {
        using var wb = new XLWorkbook(new MemoryStream(MatrixXlsxWriter.Write(Matrix())));
        var ws = wb.Worksheet("Matrix");
        Assert.Equal("bottle", ws.Cell(1, 4).GetString());   // headers: Element|Form|CAS|<components...>
        Assert.Equal("Zr", ws.Cell(2, 1).GetString());
        Assert.Equal("Pass", ws.Cell(2, 4).GetString());
        Assert.Equal("Fail", ws.Cell(3, 4).GetString());
    }

    [Fact]
    public void Write_ProducesCitationsSheet_OneRowPerDimensionCitation()
    {
        using var wb = new XLWorkbook(new MemoryStream(MatrixXlsxWriter.Write(Matrix())));
        var ws = wb.Worksheet("Citations");
        // header + 1 citation row from the cas-cd/bottle ElementGate dimension
        Assert.Equal("cas-cd", ws.Cell(2, 1).GetString());
        Assert.Equal("reach-annex17#e23", ws.Cell(2, 5).GetString());
    }
}
```

- [ ] **Step 2: Run tests, verify fail** — `dotnet test src/Smx.Backend.Tests -v q` → FAIL (`NotImplementedException`).

- [ ] **Step 3: Implement**

Replace `src/Smx.Backend/Api/MatrixXlsxWriter.cs`:

```csharp
using ClosedXML.Excel;
using Smx.Domain.Records;

namespace Smx.Backend.Api;

public static class MatrixXlsxWriter
{
    public static byte[] Write(MatrixDoc matrix)
    {
        using var wb = new XLWorkbook();

        var ws = wb.AddWorksheet("Matrix");
        ws.Cell(1, 1).Value = "Element"; ws.Cell(1, 2).Value = "Form"; ws.Cell(1, 3).Value = "CAS";
        for (var c = 0; c < matrix.Columns.Count; c++) ws.Cell(1, 4 + c).Value = matrix.Columns[c];
        var byCell = matrix.Cells.ToDictionary(x => (x.Cas, x.ComponentId));
        for (var r = 0; r < matrix.Rows.Count; r++)
        {
            var sub = matrix.Rows[r];
            ws.Cell(2 + r, 1).Value = sub.Element; ws.Cell(2 + r, 2).Value = sub.Form; ws.Cell(2 + r, 3).Value = sub.Cas;
            for (var c = 0; c < matrix.Columns.Count; c++)
            {
                var cell = ws.Cell(2 + r, 4 + c);
                var status = byCell[(sub.Cas, matrix.Columns[c])].Overall;
                cell.Value = status.ToString();
                cell.Style.Fill.BackgroundColor = status switch
                {
                    VerdictStatus.Pass => XLColor.FromHtml("#c6efce"),
                    VerdictStatus.Conditional => XLColor.FromHtml("#ffeb9c"),
                    VerdictStatus.NeedsReview => XLColor.FromHtml("#d9d2e9"),
                    _ => XLColor.FromHtml("#ffc7ce"),
                };
            }
        }
        ws.Columns().AdjustToContents();

        var cit = wb.AddWorksheet("Citations");
        string[] headers = ["CAS", "Component", "Dimension", "Source", "Reference", "RetrievedAt", "Status", "Confidence", "Rationale"];
        for (var i = 0; i < headers.Length; i++) cit.Cell(1, i + 1).Value = headers[i];
        var row = 2;
        foreach (var cell in matrix.Cells)
        foreach (var dim in cell.Dimensions)
        foreach (var c in dim.Citations)
        {
            cit.Cell(row, 1).Value = cell.Cas; cit.Cell(row, 2).Value = cell.ComponentId;
            cit.Cell(row, 3).Value = dim.Dimension; cit.Cell(row, 4).Value = c.Source;
            cit.Cell(row, 5).Value = c.Reference; cit.Cell(row, 6).Value = c.RetrievedAt;
            cit.Cell(row, 7).Value = dim.Status.ToString(); cit.Cell(row, 8).Value = dim.Confidence;
            cit.Cell(row, 9).Value = dim.Rationale;
            row++;
        }
        cit.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
```

- [ ] **Step 4: Run tests, verify pass** — `dotnet test src/Smx.Backend.Tests -v q` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend src/Smx.Backend.Tests
git commit -m "feat(api): Excel-style matrix export (Matrix + Citations sheets, TDD)"
```

### Task 7: Infrastructure — options + Foundry `IChatClient` factory

**Files:**
- Create: `src/Smx.Infrastructure/BackendOptions.cs`
- Create: `src/Smx.Infrastructure/FoundryChatClientFactory.cs`
- Test: `src/Smx.Infrastructure` gets no test project; the options parsing test lives in `src/Smx.Orchestrator.Tests/BackendOptionsTests.cs`

- [ ] **Step 1: Write the failing options test**

`src/Smx.Orchestrator.Tests/BackendOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Smx.Infrastructure;

namespace Smx.Orchestrator.Tests;

public class BackendOptionsTests
{
    [Fact]
    public void From_ReadsEnvironmentShapedConfig_WithDefaults()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FOUNDRY_ENDPOINT"] = "https://aif-smx-dev.services.ai.azure.com",
            ["COSMOS_ACCOUNT_ENDPOINT"] = "https://cosmos-smx-dev.documents.azure.com:443/",
            ["SEARCH_ENDPOINT"] = "https://srch-smx-dev.search.windows.net",
            ["UAMI_CLIENT_ID"] = "client-id",
        }).Build();

        var o = BackendOptions.From(config);
        Assert.Equal("claude-opus-4-7", o.ClaudeDeployment);           // default
        Assert.Equal("smx", o.CosmosDatabase);                          // default
        Assert.Equal("record", o.RecordContainer);                      // default
        Assert.Equal("sds-index", o.SdsIndex);                          // default
        Assert.Equal("smx-reference", o.ReferenceIndex);                // default
        Assert.Equal("regulatory-index", o.RegulatoryIndex);            // default; overridden when team schema lands
        Assert.Equal("ref-compatibility", o.CompatibilityContainer);    // default
        Assert.Equal(4, o.ScreeningParallelism);                        // default
        Assert.Equal("https://aif-smx-dev.services.ai.azure.com/anthropic/v1", o.AnthropicBaseUrl);
    }
}
```

- [ ] **Step 2: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 3: Implement options + factory**

`src/Smx.Infrastructure/BackendOptions.cs` (same env-var options style as `SdsOptions`):

```csharp
using Microsoft.Extensions.Configuration;

namespace Smx.Infrastructure;

public sealed record BackendOptions(
    string FoundryEndpoint,
    string ClaudeDeployment,
    string CosmosAccountEndpoint,
    string CosmosDatabase,
    string RecordContainer,
    string LeaseContainer,
    string CompatibilityContainer,
    string SearchEndpoint,
    string SdsIndex,
    string ReferenceIndex,
    string RegulatoryIndex,
    string? UamiClientId,
    string? FoundryApiKey,           // local-dev only; production resolves Entra first, then Key Vault
    string? KeyVaultUri,
    int ScreeningParallelism)
{
    public string AnthropicBaseUrl => $"{FoundryEndpoint.TrimEnd('/')}/anthropic/v1";

    // FOUNDRY_ENDPOINT / SEARCH_ENDPOINT default to "" (the API host doesn't use them);
    // the components that actually need them throw — see FoundryChatClientFactory and the
    // orchestrator Program.cs guard in Task 13.
    public static BackendOptions From(IConfiguration c) => new(
        FoundryEndpoint: c["FOUNDRY_ENDPOINT"] ?? "",
        ClaudeDeployment: c["CLAUDE_DEPLOYMENT"] ?? "claude-opus-4-7",
        CosmosAccountEndpoint: c["COSMOS_ACCOUNT_ENDPOINT"] ?? throw new InvalidOperationException("COSMOS_ACCOUNT_ENDPOINT missing"),
        CosmosDatabase: c["COSMOS_DATABASE"] ?? "smx",
        RecordContainer: c["RECORD_CONTAINER"] ?? "record",
        LeaseContainer: c["RECORD_LEASE_CONTAINER"] ?? "record-leases",
        CompatibilityContainer: c["COMPATIBILITY_CONTAINER"] ?? "ref-compatibility",
        SearchEndpoint: c["SEARCH_ENDPOINT"] ?? "",
        SdsIndex: c["SDS_SEARCH_INDEX"] ?? "sds-index",
        ReferenceIndex: c["REFERENCE_SEARCH_INDEX"] ?? "smx-reference",
        RegulatoryIndex: c["REGULATORY_SEARCH_INDEX"] ?? "regulatory-index",
        UamiClientId: c["UAMI_CLIENT_ID"],
        FoundryApiKey: c["FOUNDRY_API_KEY"],
        KeyVaultUri: c["KEYVAULT_URI"],
        ScreeningParallelism: int.TryParse(c["SCREENING_PARALLELISM"], out var p) ? p : 4);
}
```

`src/Smx.Infrastructure/FoundryChatClientFactory.cs`:

```csharp
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.AI;

namespace Smx.Infrastructure;

/// <summary>
/// Builds the IChatClient that MAF agents consume: Anthropic C# SDK Foundry client →
/// AsIChatClient(deployment) → function-invocation pipeline.
/// Credential resolution order: (1) Entra bearer via TokenCredential if the Foundry Anthropic
/// surface accepts it, (2) key from Key Vault secret `foundry-anthropic-key`, (3) FOUNDRY_API_KEY
/// env var (local dev only).
/// </summary>
public static class FoundryChatClientFactory
{
    public const string KeySecretName = "foundry-anthropic-key";

    public static async Task<IChatClient> CreateAsync(BackendOptions opts, TokenCredential credential, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(opts.FoundryEndpoint))
            throw new InvalidOperationException("FOUNDRY_ENDPOINT missing — required for the agent host");
        var apiKey = opts.FoundryApiKey;
        if (apiKey is null && opts.KeyVaultUri is not null)
        {
            var secrets = new SecretClient(new Uri(opts.KeyVaultUri), credential);
            apiKey = (await secrets.GetSecretAsync(KeySecretName, cancellationToken: ct)).Value.Value;
        }

        // NOTE (pin at execution): the Anthropic.Foundry package exposes AnthropicFoundryClient with
        // DefaultAnthropicFoundryCredentials.FromEnv() or explicit credentials. Prefer an
        // Entra/TokenCredential overload if the installed version has one (check with:
        //   strings ~/.nuget/packages/anthropic.foundry/*/lib/*/Anthropic.Foundry.dll | grep -i credential
        // ); otherwise construct with the API key + base URL below. Compile errors here are expected
        // to be resolved against the installed package surface, not by changing the design.
        var client = new Anthropic.Foundry.AnthropicFoundryClient(
            new Anthropic.Foundry.AnthropicFoundryApiKeyCredentials(apiKey
                ?? throw new InvalidOperationException("No Foundry Anthropic credential available")))
            { BaseUrl = opts.AnthropicBaseUrl };

        return client.AsIChatClient(opts.ClaudeDeployment)
            .AsBuilder()
            .UseFunctionInvocation()
            .Build();
    }
}
```

- [ ] **Step 4: Run tests + build** — `dotnet test src/Smx.Orchestrator.Tests -v q` → options test PASS; `dotnet build src/Smx.Backend.sln` → success. If the `Anthropic.Foundry` constructor/property names differ from the sketch, fix from compiler errors against the installed package (the class/credentials names above come from the official docs; the exact option-setting shape may vary).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Infrastructure src/Smx.Orchestrator.Tests
git commit -m "feat(infra): BackendOptions + Foundry Anthropic IChatClient factory (Entra->KV->env credential order)"
```

---

### Task 8: Tools — interfaces, Azure implementations, AIFunction exposure

**Files:**
- Create: `src/Smx.Domain/Tools/ITools.cs`
- Create: `src/Smx.Infrastructure/Search/SearchTools.cs`
- Create: `src/Smx.Infrastructure/Search/CompatibilityLookup.cs`
- Create: `src/Smx.Orchestrator/Agents/ToolBox.cs`
- Test: `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`
- Test fakes: `src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`

- [ ] **Step 1: Define the tool interfaces** (Domain — Azure-free)

`src/Smx.Domain/Tools/ITools.cs`:

```csharp
namespace Smx.Domain.Tools;

/// A retrieved chunk with everything needed to build a Citation.
public sealed record RetrievedChunk(string Source, string Reference, string Content, double Score);

/// Exact tabulated verdict from ref-compatibility; null when the pair is not tabulated.
public sealed record CompatibilityCard(string Element, string Substrate, string Verdict, string? Notes, string RefId);

public interface ICompatibilityLookup
{
    Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default);
}

public interface IRegulatorySearch { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }
public interface ISdsSearch        { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }
public interface IReferenceSearch  { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }
```

- [ ] **Step 2: Write the failing ToolBox tests**

`src/Smx.Orchestrator.Tests/Fakes/FakeTools.cs`:

```csharp
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class FakeCompatibilityLookup : ICompatibilityLookup
{
    public Dictionary<(string, string), CompatibilityCard> Cards { get; } = new();
    public List<(string Element, string Substrate)> Calls { get; } = [];
    public Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default)
    {
        Calls.Add((element, substrate));
        return Task.FromResult(Cards.TryGetValue((element, substrate), out var c) ? c : (CompatibilityCard?)null);
    }
}

public sealed class FakeSearch : IRegulatorySearch, ISdsSearch, IReferenceSearch
{
    public List<string> Queries { get; } = [];
    public List<RetrievedChunk> Results { get; set; } = [];
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Results.Take(top).ToList());
    }
}
```

`src/Smx.Orchestrator.Tests/ToolBoxTests.cs`:

```csharp
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ToolBoxTests
{
    [Fact]
    public void ScreeningTools_ExposeFourNamedFunctions()
    {
        var box = new ToolBox(new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(), new FakeSearch());
        var names = box.ScreeningTools().Select(t => t.Name).ToList();
        Assert.Equal(["lookup_compatibility", "search_regulatory", "search_sds", "search_reference"], names);
    }

    [Fact]
    public void IntakeTools_ExposeRegulatoryAndReferenceOnly()
    {
        var box = new ToolBox(new FakeCompatibilityLookup(), new FakeSearch(), new FakeSearch(), new FakeSearch());
        Assert.Equal(["search_regulatory", "search_reference"], box.IntakeTools().Select(t => t.Name).ToList());
    }

    [Fact]
    public async Task LookupCompatibility_DelegatesToLookup_AndReportsUntabulated()
    {
        var lookup = new FakeCompatibilityLookup();
        var box = new ToolBox(lookup, new FakeSearch(), new FakeSearch(), new FakeSearch());
        var result = await box.LookupCompatibilityAsync("Zr", "HDPE", default);
        Assert.Contains("not tabulated", result);
        Assert.Single(lookup.Calls);
    }
}
```

- [ ] **Step 3: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 4: Implement ToolBox** (AIFunctions the agents receive)

`src/Smx.Orchestrator/Agents/ToolBox.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Tools;

namespace Smx.Orchestrator.Agents;

public sealed class ToolBox(
    ICompatibilityLookup compatibility,
    IRegulatorySearch regulatory,
    ISdsSearch sds,
    IReferenceSearch reference)
{
    public IList<AITool> ScreeningTools() =>
    [
        AIFunctionFactory.Create(LookupCompatibilityAsync, "lookup_compatibility",
            "Exact tabulated element×substrate compatibility verdict from the SMX knowledge base. Call this FIRST for the Compatibility dimension; only reason from search results when the pair is not tabulated."),
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

    public async Task<string> LookupCompatibilityAsync(string element, string substrate, CancellationToken ct)
    {
        var card = await compatibility.LookupAsync(element, substrate, ct);
        return card is null
            ? $"{{\"tabulated\":false,\"note\":\"{element}×{substrate} not tabulated — reason from search results and mark confidence accordingly\"}}"
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

If the installed `Microsoft.Extensions.AI` version's `AIFunctionFactory.Create` overload takes `(Delegate method, string? name, string? description)` differently (e.g. an options object), adapt from compiler errors — keep the names/descriptions exactly as written.

- [ ] **Step 5: Run, verify pass** — `dotnet test src/Smx.Orchestrator.Tests -v q` → PASS.

- [ ] **Step 6: Implement the Azure adapters** (thin; no unit tests — deploy smoke exercises them)

`src/Smx.Infrastructure/Search/SearchTools.cs`:

```csharp
using Azure.Search.Documents;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// One class per index so DI can register each interface against its own SearchClient.
public abstract class SearchToolBase(SearchClient client, string sourceName)
{
    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        var options = new SearchOptions { Size = top };
        // Text search is the lowest common denominator across the three index schemas; hybrid/vector
        // upgrades happen per-index once schemas are unified (regulatory schema arrives from the team).
        var response = await client.SearchAsync<Dictionary<string, object>>(query, options, ct);
        var results = new List<RetrievedChunk>();
        await foreach (var r in response.Value.GetResultsAsync())
        {
            var doc = r.Document;
            var id = doc.TryGetValue("id", out var i) ? i?.ToString() ?? "?" : "?";
            var content = doc.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
            results.Add(new RetrievedChunk(sourceName, $"{client.IndexName}/{id}", content, r.Score ?? 0));
        }
        return results;
    }
}

public sealed class RegulatorySearchTool(SearchClient client) : SearchToolBase(client, "regulatory"), IRegulatorySearch;
public sealed class SdsSearchTool(SearchClient client) : SearchToolBase(client, "sds"), ISdsSearch;
public sealed class ReferenceSearchTool(SearchClient client) : SearchToolBase(client, "reference"), IReferenceSearch;
```

`src/Smx.Infrastructure/Search/CompatibilityLookup.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Reads the ref-compatibility container seeded by the reference-data subsystem (PK /element).
public sealed class CosmosCompatibilityLookup(Container container) : ICompatibilityLookup
{
    private sealed record Row(string Id, string Element, string Substrate, string Verdict, string? Notes, string? RefId);

    public async Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default)
    {
        var it = container.GetItemLinqQueryable<Row>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(element) })
            .Where(r => r.Substrate == substrate)
            .Take(1).ToFeedIterator();
        while (it.HasMoreResults)
            foreach (var row in await it.ReadNextAsync(ct))
                return new CompatibilityCard(row.Element, row.Substrate, row.Verdict, row.Notes, row.RefId ?? row.Id);
        return null;
    }
}
```

**Schema note:** field names (`substrate`, `verdict`, `notes`, `refId`) follow the reference-data design (`docs/superpowers/specs/2026-07-08-reference-data-subsystem-design.md`); when the parallel session's seed code merges, diff this record against their actual document shape and adjust in this one file.

- [ ] **Step 7: Build + commit**

```bash
dotnet build src/Smx.Backend.sln
git add src/Smx.Domain src/Smx.Infrastructure src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(tools): tool interfaces, AIFunction toolbox (TDD), AI Search + ref-compatibility adapters"
```

### Task 9: Agent core — `ISmxAgent`, MAF adapter, validated runner (retry-with-feedback)

**Files:**
- Create: `src/Smx.Orchestrator/Agents/ISmxAgent.cs`
- Create: `src/Smx.Orchestrator/Agents/MafAgent.cs`
- Create: `src/Smx.Orchestrator/Agents/ValidatedAgentRunner.cs`
- Test: `src/Smx.Orchestrator.Tests/ValidatedAgentRunnerTests.cs`
- Test fake: `src/Smx.Orchestrator.Tests/Fakes/ScriptedAgent.cs`

- [ ] **Step 1: Define the seam** — our code never depends on MAF types directly; agents are `ISmxAgent`, MAF lives behind one adapter. Tests use a scripted fake.

`src/Smx.Orchestrator/Agents/ISmxAgent.cs`:

```csharp
namespace Smx.Orchestrator.Agents;

public interface ISmxAgent
{
    string Name { get; }
    /// Starts a fresh conversation. Subsequent SendAsync calls on the returned thread continue
    /// the same conversation (used to feed validation errors back to the agent).
    Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct);
}

public interface ISmxAgentThread
{
    Task<string> SendAsync(string message, CancellationToken ct);
}
```

- [ ] **Step 2: Write the failing runner tests**

`src/Smx.Orchestrator.Tests/Fakes/ScriptedAgent.cs`:

```csharp
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests.Fakes;

public sealed class ScriptedAgent(params string[] responses) : ISmxAgent, ISmxAgentThread
{
    private int _i;
    public string Name => "scripted";
    public List<string> Received { get; } = [];
    public Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct) => Task.FromResult<ISmxAgentThread>(this);
    public Task<string> SendAsync(string message, CancellationToken ct)
    {
        Received.Add(message);
        return Task.FromResult(responses[Math.Min(_i++, responses.Length - 1)]);
    }
}
```

`src/Smx.Orchestrator.Tests/ValidatedAgentRunnerTests.cs`:

```csharp
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ValidatedAgentRunnerTests
{
    private sealed record Out(string Value);
    private static string? RequireAbc(Out o) => o.Value == "abc" ? null : $"value must be 'abc', got '{o.Value}'";

    [Fact]
    public async Task ValidOnFirstTry_ReturnsParsedOutput()
    {
        var agent = new ScriptedAgent("""{"value":"abc"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
        Assert.Equal("abc", result.Output!.Value);
        Assert.Single(agent.Received);
    }

    [Fact]
    public async Task InvalidThenValid_FeedsValidationErrorBack_SameThread()
    {
        var agent = new ScriptedAgent("""{"value":"xyz"}""", """{"value":"abc"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
        Assert.Equal(2, agent.Received.Count);
        Assert.Contains("value must be 'abc'", agent.Received[1]); // feedback carried the validator message
    }

    [Fact]
    public async Task UnparseableJson_GetsParseFeedback()
    {
        var agent = new ScriptedAgent("not json at all", """{"value":"abc"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
        Assert.Contains("valid JSON", agent.Received[1]);
    }

    [Fact]
    public async Task ThreeInvalidAttempts_ReturnsNeedsReview_WithLastError()
    {
        var agent = new ScriptedAgent("""{"value":"x"}""", """{"value":"y"}""", """{"value":"z"}""");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.False(result.Succeeded);
        Assert.Contains("value must be 'abc'", result.Error);
        Assert.Equal(3, agent.Received.Count); // initial + 2 retries, then give up
    }

    [Fact]
    public async Task JsonFence_IsStripped()
    {
        var agent = new ScriptedAgent("Here you go:\n```json\n{\"value\":\"abc\"}\n```");
        var result = await ValidatedAgentRunner.RunAsync<Out>(agent, "prompt", RequireAbc, default);
        Assert.True(result.Succeeded);
    }
}
```

- [ ] **Step 3: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 4: Implement the runner**

`src/Smx.Orchestrator/Agents/ValidatedAgentRunner.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;

namespace Smx.Orchestrator.Agents;

public sealed record AgentRunResult<T>(bool Succeeded, T? Output, string? Error)
{
    public static AgentRunResult<T> Ok(T output) => new(true, output, null);
    public static AgentRunResult<T> NeedsReview(string error) => new(false, default, error);
}

public static class ValidatedAgentRunner
{
    private const int MaxRetries = 2; // spec: 2 failed retries (3 attempts) → needs_review

    /// <param name="validate">returns null when valid, else a human-readable error fed back to the agent</param>
    public static async Task<AgentRunResult<T>> RunAsync<T>(
        ISmxAgent agent, string prompt, Func<T, string?> validate, CancellationToken ct)
    {
        var thread = await agent.StartThreadAsync(ct);
        var message = prompt;
        string lastError = "no attempts made";
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            var text = await thread.SendAsync(message, ct);
            string? error;
            T? parsed = default;
            try
            {
                parsed = JsonSerializer.Deserialize<T>(StripFence(text), Json.Options);
                error = parsed is null ? "response deserialized to null" : validate(parsed);
            }
            catch (JsonException e)
            {
                error = $"response was not valid JSON matching the required schema: {e.Message}. " +
                        "Reply with ONLY the JSON object, no prose, no code fences.";
            }
            if (error is null) return AgentRunResult<T>.Ok(parsed!);
            lastError = error;
            message = $"Your previous response was rejected: {error}\n" +
                      "Correct the response. Reply with ONLY the corrected JSON object.";
        }
        return AgentRunResult<T>.NeedsReview(lastError);
    }

    internal static string StripFence(string text)
    {
        var t = text.Trim();
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        return start >= 0 && end > start ? t[start..(end + 1)] : t;
    }
}
```

- [ ] **Step 5: Run, verify pass** — `dotnet test src/Smx.Orchestrator.Tests -v q` → PASS.

- [ ] **Step 6: Implement the MAF adapter** (compile-verified; behavior exercised in the deploy smoke)

`src/Smx.Orchestrator/Agents/MafAgent.cs`:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Smx.Orchestrator.Agents;

/// Wraps a MAF ChatClientAgent (over our Foundry IChatClient) behind ISmxAgent.
public sealed class MafAgent : ISmxAgent
{
    private readonly AIAgent _agent;
    public string Name { get; }

    public MafAgent(IChatClient chatClient, string name, string instructions, IList<AITool> tools)
    {
        Name = name;
        // Pin at execution against the installed Microsoft.Agents.AI version. Documented surface:
        // extension `chatClient.CreateAIAgent(...)` / `AsAIAgent(...)` or `new ChatClientAgent(chatClient, options)`.
        _agent = chatClient.CreateAIAgent(name: name, instructions: instructions, tools: [.. tools]);
    }

    public Task<ISmxAgentThread> StartThreadAsync(CancellationToken ct) =>
        Task.FromResult<ISmxAgentThread>(new Thread(_agent));

    private sealed class Thread(AIAgent agent) : ISmxAgentThread
    {
        private readonly AgentThread _thread = agent.GetNewThread();
        public async Task<string> SendAsync(string message, CancellationToken ct)
        {
            var response = await agent.RunAsync(message, _thread, cancellationToken: ct);
            return response.Text;
        }
    }
}
```

If `CreateAIAgent`/`GetNewThread`/`RunAsync` signatures differ in the installed version, adjust **only inside this file** from compiler errors (`strings ~/.nuget/packages/microsoft.agents.ai/*/lib/*/Microsoft.Agents.AI.dll | grep -iE 'AIAgent|AgentThread|CreateAIAgent'` locates names fast). The rest of the codebase touches only `ISmxAgent`.

- [ ] **Step 7: Build + commit**

```bash
dotnet build src/Smx.Backend.sln
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): ISmxAgent seam, validated runner with retry-feedback->needs-review (TDD), MAF adapter"
```

---

### Task 10: Constraint-Intake agent

**Files:**
- Create: `src/Smx.Orchestrator/Agents/IntakeAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/IntakeAgentTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class IntakeAgentTests
{
    private static ProjectDoc Project()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""
        {
          "client": "Acme", "product": "Shampoo bottle",
          "components": [{ "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
          "substances": [{ "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2" }],
          "clientRestrictedList": ["Pb"]
        }
        """);
        return ProjectDoc.Create("p1", "Acme", "Shampoo bottle", payload);
    }

    private const string ValidResponse = """
    {
      "components": [{ "id": "bottle", "material": "HDPE", "application": "packaging", "markets": ["EU"], "objective": "brand" }],
      "substances": [{ "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2" }],
      "clientRestrictedList": ["Pb"],
      "derivedScope": [
        { "listId": "reach-annex-xvii", "componentId": "*", "reason": "element gate always applies in EU",
          "citation": { "source": "regulatory", "reference": "regulatory-index/reach-17", "retrievedAt": "2026-07-08T00:00:00Z" } },
        { "listId": "ppwr-heavy-metals", "componentId": "bottle", "reason": "packaging application, EU market",
          "citation": { "source": "regulatory", "reference": "regulatory-index/ppwr-1", "retrievedAt": "2026-07-08T00:00:00Z" } }
      ]
    }
    """;

    [Fact]
    public async Task ValidResponse_BecomesConstraintsDoc()
    {
        var result = await IntakeAgent.RunAsync(new ScriptedAgent(ValidResponse), Project(), default);
        Assert.True(result.Succeeded);
        var doc = result.Output!;
        Assert.Equal(RecordIds.Constraints("p1"), doc.Id);
        Assert.Equal(2, doc.DerivedScope.Count);
        Assert.Equal("*", doc.DerivedScope[0].ComponentId);
    }

    [Fact]
    public async Task ScopeEntry_ForUnknownComponent_IsRejected_ThenRetried()
    {
        var bad = ValidResponse.Replace("\"componentId\": \"bottle\"", "\"componentId\": \"lid\"");
        var agent = new ScriptedAgent(bad, ValidResponse);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.True(result.Succeeded);
        Assert.Contains("unknown component", agent.Received[1]);
    }

    [Fact]
    public async Task ScopeEntry_WithoutCitation_IsRejected()
    {
        var bad = ValidResponse.Replace("\"source\": \"regulatory\"", "\"source\": \"\"");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.False(result.Succeeded); // 3 attempts, all uncited → needs review
    }

    [Fact]
    public async Task SubstancesMustEchoInput_NoInventedCandidates()
    {
        var bad = ValidResponse.Replace("39049-04-2", "999-99-9");
        var agent = new ScriptedAgent(bad, ValidResponse);
        var result = await IntakeAgent.RunAsync(agent, Project(), default);
        Assert.True(result.Succeeded);
        Assert.Contains("must exactly echo", agent.Received[1]);
    }
}
```

- [ ] **Step 2: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 3: Implement**

`src/Smx.Orchestrator/Agents/IntakeAgent.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class IntakeOutput
{
    public List<ComponentSpec> Components { get; set; } = [];
    public List<SubstanceSpec> Substances { get; set; } = [];
    public List<string> ClientRestrictedList { get; set; } = [];
    public List<AppliedList> DerivedScope { get; set; } = [];
}

public static class IntakeAgent
{
    public const string AgentName = "constraint-intake";

    public const string Instructions = """
        You are the SMX Constraint-Intake agent. You receive a project's raw constraints payload and must
        normalize it and DERIVE the regulatory scope. You never invent data: components, substances and the
        client restricted list must exactly echo the input. Your added value is `derivedScope`:
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
        { "components": [...], "substances": [...], "clientRestrictedList": [...],
          "derivedScope": [{ "listId", "componentId" ("*" for product-wide), "reason",
                             "citation": { "source", "reference", "retrievedAt" } }] }
        """;

    public static async Task<AgentRunResult<ConstraintsDoc>> RunAsync(ISmxAgent agent, ProjectDoc project, CancellationToken ct)
    {
        var prompt = $"Project constraints payload:\n{JsonSerializer.Serialize(project.Payload, Json.Options)}";
        var result = await ValidatedAgentRunner.RunAsync<IntakeOutput>(agent, prompt, o => Validate(o, project), ct);
        if (!result.Succeeded) return AgentRunResult<ConstraintsDoc>.NeedsReview(result.Error!);
        var o = result.Output!;
        return AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(project.ProjectId), ProjectId = project.ProjectId,
            Components = o.Components, Substances = o.Substances,
            ClientRestrictedList = o.ClientRestrictedList, DerivedScope = o.DerivedScope,
        });
    }

    internal static string? Validate(IntakeOutput o, ProjectDoc project)
    {
        var payload = JsonSerializer.Deserialize<IntakeOutput>(project.Payload.GetRawText(), Json.Options)!;
        if (o.Components.Count != payload.Components.Count ||
            !o.Components.Select(c => c.Id).OrderBy(x => x).SequenceEqual(payload.Components.Select(c => c.Id).OrderBy(x => x)))
            return "components must exactly echo the input payload (no additions/removals)";
        if (!o.Substances.Select(s => s.Cas).OrderBy(x => x).SequenceEqual(payload.Substances.Select(s => s.Cas).OrderBy(x => x)))
            return "substances must exactly echo the input payload (no invented candidates)";
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
}
```

(The `IntakeOutput` payload-echo validation reuses `IntakeOutput` to parse the original payload — the request shape matches by construction of `CreateProjectRequest`.)

- [ ] **Step 4: Run, verify pass** — `dotnet test src/Smx.Orchestrator.Tests -v q` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): constraint-intake agent — echo-validated normalization + cited derived scope (TDD)"
```

### Task 11: Screening agent (per substance × component cell)

**Files:**
- Create: `src/Smx.Orchestrator/Agents/ScreeningAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/ScreeningAgentTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

public class ScreeningAgentTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        Substances = [new("Cd", "sulfide", "1306-23-6")],
        ClientRestrictedList = ["Pb"],
        DerivedScope = [new("reach-annex-xvii", "*", "element gate",
            new Citation("regulatory", "regulatory-index/reach-17", "t"))],
    };

    private const string ValidResponse = """
    {
      "dimensions": [
        { "dimension": "Compatibility", "status": "Pass",
          "citations": [{ "source": "reference", "reference": "ref-compatibility/cd-hdpe", "retrievedAt": "t" }],
          "confidence": 0.9, "rationale": "tabulated compatible" },
        { "dimension": "ElementGate", "status": "Fail",
          "citations": [{ "source": "regulatory", "reference": "regulatory-index/reach-e23", "retrievedAt": "t" }],
          "confidence": 0.98, "rationale": "Cd restricted by REACH Annex XVII entry 23" },
        { "dimension": "ApplicationCheck", "status": "Fail",
          "citations": [{ "source": "regulatory", "reference": "regulatory-index/ppwr-hm", "retrievedAt": "t" }],
          "confidence": 0.95, "rationale": "PPWR heavy-metal cap" },
        { "dimension": "Hazard", "status": "Fail",
          "citations": [{ "source": "sds", "reference": "sds-index/cd-ghs", "retrievedAt": "t" }],
          "confidence": 0.97, "rationale": "carcinogenic H350" }
      ]
    }
    """;

    [Fact]
    public async Task ValidResponse_BecomesVerdictDoc_WithDeterministicId()
    {
        var result = await ScreeningAgent.RunAsync(new ScriptedAgent(ValidResponse), Constraints(),
            new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        Assert.True(result.Succeeded);
        Assert.Equal("p1|verdict|1306-23-6|bottle", result.Output!.Id);
        Assert.Equal(VerdictStatus.Fail, result.Output.Overall);
        Assert.Equal(4, result.Output.Dimensions.Count);
    }

    [Fact]
    public async Task MissingDimension_IsRejected_ThenRetried()
    {
        var bad = ValidResponse.Replace("\"dimension\": \"Hazard\"", "\"dimension\": \"Compatibility\"");
        var agent = new ScriptedAgent(bad, ValidResponse);
        var result = await ScreeningAgent.RunAsync(agent, Constraints(),
            new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        Assert.True(result.Succeeded);
        Assert.Contains("exactly the four dimensions", agent.Received[1]);
    }

    [Fact]
    public async Task NonFailDimension_WithoutCitation_IsRejected()
    {
        // A Fail without citation is also invalid, but the sharper rule: nothing passes uncited.
        var bad = ValidResponse.Replace(
            """"citations": [{ "source": "reference", "reference": "ref-compatibility/cd-hdpe", "retrievedAt": "t" }]""",
            """"citations": []""");
        var agent = new ScriptedAgent(bad, bad, bad);
        var result = await ScreeningAgent.RunAsync(agent, Constraints(),
            new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        Assert.False(result.Succeeded); // needs_review after 3 uncited attempts
        Assert.Contains("citation", result.Error);
    }

    [Fact]
    public async Task PromptCarriesCell_ScopeAndRestrictedList()
    {
        var agent = new ScriptedAgent(ValidResponse);
        await ScreeningAgent.RunAsync(agent, Constraints(), new SubstanceSpec("Cd", "sulfide", "1306-23-6"), "bottle", default);
        var prompt = agent.Received[0];
        Assert.Contains("1306-23-6", prompt);
        Assert.Contains("HDPE", prompt);
        Assert.Contains("reach-annex-xvii", prompt);
        Assert.Contains("Pb", prompt); // client restricted list travels with the prompt
    }
}
```

- [ ] **Step 2: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 3: Implement**

`src/Smx.Orchestrator/Agents/ScreeningAgent.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Agents;

public sealed class ScreeningOutput
{
    public List<DimensionVerdict> Dimensions { get; set; } = [];
}

public static class ScreeningAgent
{
    public const string AgentName = "screening";

    public const string Instructions = """
        You are the SMX Screening agent. You evaluate ONE candidate substance against ONE product component
        and return a verdict per dimension. You may only use facts you obtained through your tools in this
        conversation — never from memory. Dimensions (all four, exactly once each):
        - Compatibility: call lookup_compatibility(element, substrate) FIRST. If tabulated, the tabulated
          verdict decides (Pass for compatible, Fail for incompatible, Conditional where the table says
          conditional) and you cite the returned refId. If not tabulated, reason from search_reference
          results with lowered confidence, or return NeedsReview if retrieval is inconclusive.
        - ElementGate: product-wide lists from the provided scope (componentId "*") plus the client
          restricted list. Search the regulatory corpus for the element/substance against each list.
          A hit on any list = Fail.
        - ApplicationCheck: the component-scoped lists from the provided scope. A restriction that binds
          this component's application/markets = Fail; a cap/limit that constrains but permits = Conditional.
        - Hazard: search_sds for GHS data (H-codes, CMR, endocrine). CMR category 1A/1B = Fail;
          significant hazards that merit "not recommended" = Conditional.
        Statuses: Pass | Conditional | NeedsReview | Fail. EVERY dimension MUST carry at least one citation
        built from an actual tool result (source, reference, retrievedAt = now, ISO 8601 UTC). If your tools
        return nothing decisive for a dimension, the status is NeedsReview — never guess, never assume clean.
        Confidence is your calibrated 0..1 estimate. Rationale is one or two sentences.
        Reply with ONLY a JSON object: { "dimensions": [{ "dimension", "status", "citations":
        [{ "source", "reference", "retrievedAt" }], "confidence", "rationale" }] }
        """;

    public static async Task<AgentRunResult<VerdictDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints, SubstanceSpec substance, string componentId, CancellationToken ct)
    {
        var component = constraints.Components.Single(c => c.Id == componentId);
        var scope = constraints.DerivedScope.Where(s => s.ComponentId is "*" || s.ComponentId == componentId).ToList();
        var prompt = JsonSerializer.Serialize(new
        {
            substance,
            component,
            applicableScope = scope,
            clientRestrictedList = constraints.ClientRestrictedList,
        }, Json.Options);

        var result = await ValidatedAgentRunner.RunAsync<ScreeningOutput>(agent,
            $"Screen this cell:\n{prompt}", Validate, ct);
        if (!result.Succeeded) return AgentRunResult<VerdictDoc>.NeedsReview(result.Error!);
        return AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(constraints.ProjectId, substance.Cas, componentId),
            ProjectId = constraints.ProjectId, Cas = substance.Cas, ComponentId = componentId,
            Element = substance.Element, Form = substance.Form,
            Dimensions = result.Output!.Dimensions,
        });
    }

    internal static string? Validate(ScreeningOutput o)
    {
        string[] required = ["Compatibility", "ElementGate", "ApplicationCheck", "Hazard"];
        var names = o.Dimensions.Select(d => d.Dimension).OrderBy(x => x).ToArray();
        if (!names.SequenceEqual(required.OrderBy(x => x)))
            return $"response must contain exactly the four dimensions {string.Join(", ", required)} once each; got [{string.Join(", ", names)}]";
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

- [ ] **Step 4: Run, verify pass** — `dotnet test src/Smx.Orchestrator.Tests -v q` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): screening agent — 4-dimension cited verdicts, uncited=rejected (TDD)"
```

---

### Task 12: Dispatcher — stage decisions + execution (idempotent, bounded, poison-safe)

**Files:**
- Create: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Test: `src/Smx.Orchestrator.Tests/StageDispatcherTests.cs`
- Test fake: `src/Smx.Orchestrator.Tests/Fakes/FakeAgents.cs`

- [ ] **Step 1: Write the failing tests**

`src/Smx.Orchestrator.Tests/Fakes/FakeAgents.cs`:

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
            Substances = [new("Zr", "neodecanoate", "cas-zr")],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    public Func<ConstraintsDoc, SubstanceSpec, string, Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>>> Screen { get; set; } =
        (c, s, comp) => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, s.Cas, comp), ProjectId = c.ProjectId,
            Cas = s.Cas, ComponentId = comp, Element = s.Element, Form = s.Form,
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        }));

    public int IntakeCalls; public int ScreenCalls;

    Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>> IAgentRuns.RunIntakeAsync(ProjectDoc p, CancellationToken ct)
    { Interlocked.Increment(ref IntakeCalls); return Intake(p); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>> IAgentRuns.RunScreeningAsync(ConstraintsDoc c, SubstanceSpec s, string comp, CancellationToken ct)
    { Interlocked.Increment(ref ScreenCalls); return Screen(c, s, comp); }
}
```

`src/Smx.Orchestrator.Tests/StageDispatcherTests.cs`:

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
    public async Task ProjectChange_WithIntakeAlreadyDone_DoesNotRerunIntake()
    {
        var (d, store, agents) = Sut();
        var doc = await Seed(store);
        await d.OnRecordChangedAsync(doc, default);
        await d.OnRecordChangedAsync((await store.GetProjectAsync("p1"))!, default); // change-feed redelivery
        Assert.Equal(1, agents.IntakeCalls);
    }

    [Fact]
    public async Task ConstraintsWritten_FansOutScreening_PerCell_ThenAssemblesMatrix()
    {
        var (d, store, _) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);              // intake
        var constraints = await store.GetConstraintsAsync("p1");
        await d.OnRecordChangedAsync(constraints!, default);                    // screening fan-out
        Assert.Single(await store.GetVerdictsAsync("p1"));                      // 1 substance × 1 component
        var last = (await store.GetVerdictsAsync("p1"))[0];
        await d.OnRecordChangedAsync(last, default);                            // verdict arrival → assembly
        Assert.NotNull(await store.GetMatrixAsync("p1"));
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("done", proj!.Stages[Stages.Screening].Status);
        Assert.Equal("done", proj.Stages[Stages.Matrix].Status);
    }

    [Fact]
    public async Task ScreeningFanOut_SkipsCellsThatAlreadyHaveVerdicts()
    {
        var (d, store, agents) = Sut();
        await d.OnRecordChangedAsync(await Seed(store), default);
        var constraints = await store.GetConstraintsAsync("p1");
        await d.OnRecordChangedAsync(constraints!, default);
        var callsAfterFirst = agents.ScreenCalls;
        await d.OnRecordChangedAsync(constraints!, default); // redelivery
        Assert.Equal(callsAfterFirst, agents.ScreenCalls);
    }

    [Fact]
    public async Task IntakeNeedsReview_MarksStage_DoesNotCascade()
    {
        var (d, store, agents) = Sut();
        agents.Intake = _ => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.NeedsReview("uncited scope"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("needs-review", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("uncited scope", proj.Stages[Stages.Intake].Error);
        Assert.Null(await store.GetConstraintsAsync("p1"));
    }

    [Fact]
    public async Task AgentThrow_MarksStageFailed_WithErrorDetail()
    {
        var (d, store, agents) = Sut();
        agents.Intake = _ => throw new InvalidOperationException("foundry 500");
        await d.OnRecordChangedAsync(await Seed(store), default);
        var proj = await store.GetProjectAsync("p1");
        Assert.Equal("failed", proj!.Stages[Stages.Intake].Status);
        Assert.Contains("foundry 500", proj.Stages[Stages.Intake].Error);
        Assert.Equal(1, proj.Stages[Stages.Intake].Attempts);
    }

    [Fact]
    public async Task NeedsReviewVerdict_StillCountsTowardCompletion_ScreeningStageEndsNeedsReview()
    {
        var (d, store, agents) = Sut();
        agents.Screen = (c, s, comp) => Task.FromResult(
            Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.NeedsReview("no retrieval"));
        await d.OnRecordChangedAsync(await Seed(store), default);
        await d.OnRecordChangedAsync((await store.GetConstraintsAsync("p1"))!, default);
        var verdicts = await store.GetVerdictsAsync("p1");
        Assert.Single(verdicts);                                   // placeholder NeedsReview verdict written
        Assert.Equal(VerdictStatus.NeedsReview, verdicts[0].Overall);
        await d.OnRecordChangedAsync(verdicts[0], default);
        Assert.NotNull(await store.GetMatrixAsync("p1"));          // matrix still assembles (cells say NeedsReview)
        Assert.Equal("needs-review", (await store.GetProjectAsync("p1"))!.Stages[Stages.Screening].Status);
    }
}
```

- [ ] **Step 2: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 3: Implement**

`src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);
    Task<AgentRunResult<VerdictDoc>> RunScreeningAsync(ConstraintsDoc constraints, SubstanceSpec substance, string componentId, CancellationToken ct);
}

/// Reacts to record changes. Change feed is at-least-once: every branch here must be
/// idempotent (re-checking store state before acting) and every write an upsert.
public sealed class StageDispatcher(IRecordStore store, IAgentRuns agents, int screeningParallelism)
{
    public async Task OnRecordChangedAsync(object doc, CancellationToken ct)
    {
        switch (doc)
        {
            case ProjectDoc p: await OnProjectAsync(p, ct); break;
            case ConstraintsDoc c: await OnConstraintsAsync(c, ct); break;
            case VerdictDoc v: await OnVerdictAsync(v, ct); break;
            case MatrixDoc: break; // terminal
        }
    }

    private async Task OnProjectAsync(ProjectDoc p, CancellationToken ct)
    {
        if (p.Stages[Stages.Intake].Status != "pending") return;                 // idempotency gate
        if (await store.GetConstraintsAsync(p.ProjectId, ct) is not null) return; // belt-and-braces
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
            {
                await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
            }
        }
        catch (Exception e)
        {
            await SetStageAsync(p.ProjectId, Stages.Intake, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }

    private async Task OnConstraintsAsync(ConstraintsDoc c, CancellationToken ct)
    {
        var existing = (await store.GetVerdictsAsync(c.ProjectId, ct)).Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        var missing = MatrixAssembler.Cells(c).Where(cell => !existing.Contains(cell)).ToList();
        if (missing.Count == 0) { await TryAssembleAsync(c.ProjectId, ct); return; }

        await SetStageAsync(c.ProjectId, Stages.Screening, s => { s.Status = "running"; s.Attempts++; }, ct);
        using var gate = new SemaphoreSlim(screeningParallelism);
        var tasks = missing.Select(async cell =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var substance = c.Substances.Single(s => s.Cas == cell.Cas);
                try
                {
                    var result = await agents.RunScreeningAsync(c, substance, cell.ComponentId, ct);
                    // needs_review is a first-class outcome: write a placeholder verdict so the
                    // matrix can complete and the operator sees exactly which cells need eyes.
                    var verdict = result.Succeeded ? result.Output! : new VerdictDoc
                    {
                        Id = RecordIds.Verdict(c.ProjectId, cell.Cas, cell.ComponentId),
                        ProjectId = c.ProjectId, Cas = cell.Cas, ComponentId = cell.ComponentId,
                        Element = substance.Element, Form = substance.Form,
                        Dimensions = [new("ElementGate", VerdictStatus.NeedsReview, [],
                            0, $"agent could not produce a valid cited verdict: {result.Error}")],
                    };
                    await store.UpsertVerdictAsync(verdict, ct);
                }
                catch (Exception e)
                {
                    await SetStageAsync(c.ProjectId, Stages.Screening, s => { s.Status = "failed"; s.Error = e.Message; }, ct);
                }
            }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        await TryAssembleAsync(c.ProjectId, ct);
    }

    private Task OnVerdictAsync(VerdictDoc v, CancellationToken ct) => TryAssembleAsync(v.ProjectId, ct);

    private async Task TryAssembleAsync(string projectId, CancellationToken ct)
    {
        var constraints = await store.GetConstraintsAsync(projectId, ct);
        if (constraints is null) return;
        var verdicts = await store.GetVerdictsAsync(projectId, ct);
        if (!MatrixAssembler.IsComplete(constraints, verdicts)) return;

        var anyReview = verdicts.Any(v => v.Overall == VerdictStatus.NeedsReview);
        await SetStageAsync(projectId, Stages.Screening,
            s => { if (s.Status != "failed") s.Status = anyReview ? "needs-review" : "done"; }, ct);

        if (await store.GetMatrixAsync(projectId, ct) is null)
        {
            await store.UpsertMatrixAsync(
                MatrixAssembler.Assemble(constraints, verdicts, DateTimeOffset.UtcNow.ToString("O")), ct);
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

- [ ] **Step 4: Run, verify pass** — `dotnet test src/Smx.Orchestrator.Tests -v q` → PASS (7 dispatcher tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(orchestrator): stage dispatcher — idempotent change handling, bounded fan-out, needs-review placeholders (TDD)"
```

### Task 13: Hosts — orchestrator change-feed worker + backend DI, OTel on both

**Files:**
- Create: `src/Smx.Orchestrator/Dispatch/AgentRuns.cs`
- Create: `src/Smx.Orchestrator/Dispatch/ChangeFeedWorker.cs`
- Create: `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Modify: `src/Smx.Orchestrator/Program.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Orchestrator.Tests/RecordDocRouterTests.cs`

- [ ] **Step 1: Write the failing router test** (the change feed yields raw JSON; routing to typed docs is the testable logic)

```csharp
using System.Text.Json;
using Smx.Domain.Records;
using Smx.Orchestrator.Dispatch;

namespace Smx.Orchestrator.Tests;

public class RecordDocRouterTests
{
    [Theory]
    [InlineData("project", typeof(ProjectDoc))]
    [InlineData("constraints", typeof(ConstraintsDoc))]
    [InlineData("verdict", typeof(VerdictDoc))]
    [InlineData("matrix", typeof(MatrixDoc))]
    public void Route_DeserializesByTypeDiscriminator(string type, Type expected)
    {
        var json = type switch
        {
            "project" => """{"id":"p1","projectId":"p1","type":"project","client":"A","product":"P","payload":{},"stages":{}}""",
            "constraints" => """{"id":"p1|constraints","projectId":"p1","type":"constraints"}""",
            "verdict" => """{"id":"p1|verdict|c|b","projectId":"p1","type":"verdict","cas":"c","componentId":"b","element":"E","form":"f"}""",
            _ => """{"id":"p1|matrix","projectId":"p1","type":"matrix"}""",
        };
        var doc = RecordDocRouter.Route(JsonDocument.Parse(json).RootElement);
        Assert.IsType(expected, doc);
    }

    [Fact]
    public void Route_UnknownType_ReturnsNull()
    {
        Assert.Null(RecordDocRouter.Route(JsonDocument.Parse("""{"id":"x","type":"lease-ish"}""").RootElement));
    }
}
```

- [ ] **Step 2: Run, verify fail** — `dotnet test src/Smx.Orchestrator.Tests -v q` → FAIL.

- [ ] **Step 3: Implement router + worker + composition**

`src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`:

```csharp
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Dispatch;

public static class RecordDocRouter
{
    public static object? Route(JsonElement element) =>
        element.TryGetProperty("type", out var t) ? t.GetString() switch
        {
            RecordTypes.Project => element.Deserialize<ProjectDoc>(Json.Options),
            RecordTypes.Constraints => element.Deserialize<ConstraintsDoc>(Json.Options),
            RecordTypes.Verdict => element.Deserialize<VerdictDoc>(Json.Options),
            RecordTypes.Matrix => element.Deserialize<MatrixDoc>(Json.Options),
            _ => null,
        } : null;
}
```

`src/Smx.Orchestrator/Dispatch/AgentRuns.cs` (binds `IAgentRuns` to the two MAF agents):

```csharp
using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<VerdictDoc>> RunScreeningAsync(ConstraintsDoc constraints, SubstanceSpec substance, string componentId, CancellationToken ct) =>
        ScreeningAgent.RunAsync(
            new MafAgent(chatClient, ScreeningAgent.AgentName, ScreeningAgent.Instructions, toolBox.ScreeningTools()),
            constraints, substance, componentId, ct);
}
```

`src/Smx.Orchestrator/Dispatch/ChangeFeedWorker.cs`:

```csharp
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Smx.Infrastructure;

namespace Smx.Orchestrator.Dispatch;

public sealed class ChangeFeedWorker(
    CosmosClient cosmos, BackendOptions opts, StageDispatcher dispatcher,
    ILogger<ChangeFeedWorker> logger) : BackgroundService
{
    private ChangeFeedProcessor? _processor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = cosmos.GetDatabase(opts.CosmosDatabase);
        var monitored = db.GetContainer(opts.RecordContainer);
        var leases = db.GetContainer(opts.LeaseContainer);

        _processor = monitored
            .GetChangeFeedProcessorBuilder<JsonElement>("smx-orchestrator", HandleChangesAsync)
            .WithInstanceName(Environment.MachineName)
            .WithLeaseContainer(leases)
            .WithStartTime(DateTime.MinValue.ToUniversalTime()) // process history on first start
            .Build();

        await _processor.StartAsync();
        logger.LogInformation("change feed processor started on {Container}", opts.RecordContainer);
        try { await Task.Delay(Timeout.Infinite, stoppingToken); }
        catch (OperationCanceledException) { /* shutting down */ }
        await _processor.StopAsync();
    }

    private async Task HandleChangesAsync(IReadOnlyCollection<JsonElement> changes, CancellationToken ct)
    {
        foreach (var change in changes)
        {
            var doc = RecordDocRouter.Route(change);
            if (doc is null) continue;
            try
            {
                await dispatcher.OnRecordChangedAsync(doc, ct);
            }
            catch (Exception e)
            {
                // Dispatcher already persists per-stage failure states; this catch is the last-resort
                // guard so one bad document cannot wedge the feed. Log and move on.
                logger.LogError(e, "dispatch failed for record change {Id}",
                    change.TryGetProperty("id", out var id) ? id.GetString() : "?");
            }
        }
    }
}
```

`src/Smx.Orchestrator/Program.cs` (full file):

```csharp
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Azure.Search.Documents;
using Microsoft.Azure.Cosmos;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Smx.Domain;
using Smx.Domain.Tools;
using Smx.Infrastructure;
using Smx.Infrastructure.Search;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;

var builder = Host.CreateApplicationBuilder(args);
var opts = BackendOptions.From(builder.Configuration);
Azure.Core.TokenCredential credential = opts.UamiClientId is { } id
    ? new ManagedIdentityCredential(id)
    : new DefaultAzureCredential();

builder.Services.AddSingleton(opts);
builder.Services.AddSingleton(credential);
builder.Services.AddSingleton(new CosmosClient(opts.CosmosAccountEndpoint, credential, new CosmosClientOptions
{
    SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
}));
builder.Services.AddSingleton<IRecordStore>(sp => new CosmosRecordStore(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RecordContainer)));
builder.Services.AddSingleton<ICompatibilityLookup>(sp => new CosmosCompatibilityLookup(
    sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.CompatibilityContainer)));
builder.Services.AddSingleton<IRegulatorySearch>(new RegulatorySearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.RegulatoryIndex, credential)));
builder.Services.AddSingleton<ISdsSearch>(new SdsSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.SdsIndex, credential)));
builder.Services.AddSingleton<IReferenceSearch>(new ReferenceSearchTool(
    new SearchClient(new Uri(opts.SearchEndpoint), opts.ReferenceIndex, credential)));
builder.Services.AddSingleton<ToolBox>();
builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(sp =>
    FoundryChatClientFactory.CreateAsync(opts, credential).GetAwaiter().GetResult());
builder.Services.AddSingleton<IAgentRuns, AgentRuns>();
builder.Services.AddSingleton(sp => new StageDispatcher(
    sp.GetRequiredService<IRecordStore>(), sp.GetRequiredService<IAgentRuns>(), opts.ScreeningParallelism));
builder.Services.AddHostedService<ChangeFeedWorker>();

if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 } aiConn)
{
    builder.Services.AddOpenTelemetry()
        .WithTracing(t => t
            .AddSource("*") // MAF + Azure SDK activity sources
            .AddHttpClientInstrumentation()
            .AddAzureMonitorTraceExporter(o => o.ConnectionString = aiConn))
        .WithMetrics(m => m.AddAzureMonitorMetricExporter(o => o.ConnectionString = aiConn));
}

await builder.Build().RunAsync();
```

(`AddHttpClientInstrumentation` needs package `OpenTelemetry.Instrumentation.Http` — add it to `Smx.Orchestrator`. If MAF exposes a named ActivitySource, replace `AddSource("*")` with the specific names at execution time.)

Backend `src/Smx.Backend/Program.cs` — extend the Task-5 file with production DI (test override still wins):

```csharp
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Azure.Cosmos;
using Smx.Backend.Api;
using Smx.Domain;
using Smx.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Production wiring only when configured; tests inject InMemoryRecordStore instead.
if (builder.Configuration["COSMOS_ACCOUNT_ENDPOINT"] is { Length: > 0 })
{
    var opts = BackendOptions.From(builder.Configuration);
    Azure.Core.TokenCredential credential = opts.UamiClientId is { } id
        ? new ManagedIdentityCredential(id)
        : new DefaultAzureCredential();
    builder.Services.AddSingleton(new CosmosClient(opts.CosmosAccountEndpoint, credential, new CosmosClientOptions
    {
        SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase },
    }));
    builder.Services.AddSingleton<IRecordStore>(sp => new CosmosRecordStore(
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.RecordContainer)));
}
if (builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"] is { Length: > 0 })
    builder.Services.AddOpenTelemetry().UseAzureMonitor();

var app = builder.Build();
app.MapProjectEndpoints();
app.Run();

public partial class Program { }
```

**Note:** `BackendOptions.From` deliberately defaults `FOUNDRY_ENDPOINT`/`SEARCH_ENDPOINT` to `""` (Task 7) because the API host doesn't use them. The orchestrator must fail fast instead: add a guard right after `var opts = BackendOptions.From(builder.Configuration);` in the orchestrator `Program.cs`:

```csharp
if (string.IsNullOrEmpty(opts.SearchEndpoint))
    throw new InvalidOperationException("SEARCH_ENDPOINT missing — required for the agent host");
// FoundryChatClientFactory guards FOUNDRY_ENDPOINT itself.
```

- [ ] **Step 4: Run all tests + build** — `dotnet test src/Smx.Backend.sln -v q` → all PASS (Backend tests confirm production wiring doesn't break the in-memory test path because no `COSMOS_ACCOUNT_ENDPOINT` is set in tests).

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Backend src/Smx.Orchestrator.Tests
git commit -m "feat(hosts): change-feed worker + DI composition + OTel wiring for both apps"
```

---

### Task 14: Sync the worktree branch with local `main` (reference-data landed)

The reference-data subsystem merged to **local `main`** after this branch was cut (from `origin/main`). It adds the `ref-*` Cosmos containers to `data.bicep` (both variants), the `smx-reference` index code, and seed JSON — all consumed by later tasks.

**Files:** none created — git operation.

- [ ] **Step 1: Rebase this branch onto local main**

```bash
git fetch . main   # no-op sanity; main is local
git rebase main
```

Expected: clean rebase (our commits touch only `src/Smx.Backend*`, `src/Smx.Domain*`, `src/Smx.Infrastructure*`, `src/Smx.Orchestrator*`, and two docs files — no overlap with reference-data's files). If conflicts appear, stop and resolve with the user.

- [ ] **Step 2: Verify the reference-data artifacts are now visible**

```bash
ls src/Smx.Functions/Reference/Seed/ && grep -n "ref-compatibility" infra/modules/data.bicep | head -3
```

Expected: seed JSON files listed; `ref-compatibility` container present in `data.bicep`.

- [ ] **Step 3: Reconcile `CosmosCompatibilityLookup` with the actual seed shape**

Open one document in `src/Smx.Functions/Reference/Seed/*.json` for the compatibility dataset and diff its field names against the `Row` record in `src/Smx.Infrastructure/Search/CompatibilityLookup.cs` (Task 8). Adjust `Row`'s property names to match exactly (only that record changes). Re-run `dotnet test src/Smx.Backend.sln -v q` → PASS.

- [ ] **Step 4: Full suite + commit any reconciliation**

```bash
dotnet test src/Smx.Backend.sln
git add -A && git commit -m "fix(tools): align ref-compatibility lookup with seeded document shape" || echo "nothing to reconcile"
```

### Task 15: Infra — `record` + `record-leases` containers (both variants)

**Files:**
- Modify: `infra/modules/data.bicep` (after the `sdsRegistry` resource, before outputs)
- Modify: `infra/single-rg/modules/data.bicep` (identical edit — keep bodies byte-identical)

- [ ] **Step 1: Add the containers + endpoint output**

Append after the `sdsRegistry` resource (and after any `ref-*` containers that arrived with the Task-14 rebase):

```bicep
// Agent-backend structured record — the record-as-bus. One container, discriminated
// document types, partitioned by project.
resource record 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'record'
  properties: {
    resource: {
      id: 'record'
      partitionKey: { paths: [ '/projectId' ], kind: 'Hash' }
    }
  }
}

// Change-feed processor leases for the orchestrator.
resource recordLeases 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = {
  parent: cosmosDb
  name: 'record-leases'
  properties: {
    resource: {
      id: 'record-leases'
      partitionKey: { paths: [ '/id' ], kind: 'Hash' }
    }
  }
}
```

And add to the outputs block:

```bicep
output recordContainer string = record.name
output cosmosDocumentEndpoint string = cosmos.properties.documentEndpoint
```

Apply the identical edit to `infra/single-rg/modules/data.bicep`.

- [ ] **Step 2: Lint both variants**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null`
Expected: exit 0 (warnings ok, no errors).

- [ ] **Step 3: Verify byte-identity + commit**

```bash
diff infra/modules/data.bicep infra/single-rg/modules/data.bicep && echo IDENTICAL
git add infra/ && git commit -m "feat(infra): record + record-leases Cosmos containers (both variants)"
```

---

### Task 16: Infra — Claude Opus 4.7 deployment on Foundry + roles (both variants)

**Files:**
- Modify: `infra/modules/ai.bicep` + `infra/single-rg/modules/ai.bicep`
- Modify: `infra/main.bicep` + `infra/single-rg/main.bicep` (params)

- [ ] **Step 1: Preflight — verify Anthropic model availability (do this before writing Bicep)**

```bash
az cognitiveservices model list -l swedencentral \
  --query "[?contains(name.name, 'claude')].{name:name.name, version:name.version, format:name.format, skus:model.skus[].name}" -o table 2>/dev/null \
  || az cognitiveservices model list -l swedencentral -o json | python3 -c "import json,sys; [print(m) for m in json.load(sys.stdin) if 'claude' in json.dumps(m).lower()]"
```

Record the exact `format` (expected `Anthropic`), model `name` (expected `claude-opus-4-7`), `version`, and available SKU (expected `GlobalStandard`; Anthropic models on Foundry are typically Global-only). **If no Claude models list for `swedencentral`:** re-run with `-l eastus2` and `-l swedencentral` removed; if Claude is available in another region only, raise it with the user — the documented fallback is a second, minimal AIServices account in a supported region behind a `foundryAnthropicEndpoint` override param, and this task's Bicep goes there instead. Do not guess: the values below get corrected to whatever this command returns.

- [ ] **Step 2: Add params + deployment + role to `ai.bicep` (both variants)**

After the `gpt4oCapacity` param block:

```bicep
@description('Deploy the Claude Opus 4.7 reasoning model (Anthropic on Foundry). ON by default — named by the SOW. Verify availability with `az cognitiveservices model list` first.')
param deployClaude bool = true

@description('Claude deployment capacity. GlobalStandard capacity unit; kept minimal.')
param claudeCapacity int = 1

@description('Claude model version as listed by `az cognitiveservices model list` (empty = provider default).')
param claudeModelVersion string = ''
```

After the `openAiUserRoleId` var:

```bicep
// Cognitive Services User — required for Entra auth against non-OpenAI (Anthropic) surfaces.
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
```

After the `embedding` resource (deployments on one account must be serialized — same pattern as the existing `dependsOn`):

```bicep
// Claude Opus 4.7 (reasoning for the agent backend). Anthropic models on Foundry use the
// Anthropic format and (typically) GlobalStandard SKU — correct from the preflight listing.
resource claude 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (deployClaude) {
  parent: foundry
  name: 'claude-opus-4-7'
  sku: {
    name: 'GlobalStandard'
    capacity: claudeCapacity
  }
  dependsOn: [
    embedding
  ]
  properties: {
    model: {
      format: 'Anthropic'
      name: 'claude-opus-4-7'
      version: empty(claudeModelVersion) ? null : claudeModelVersion
    }
  }
}
```

After the `foundryRole` resource:

```bicep
resource foundryCogUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundry.id, uamiPrincipalId, cognitiveServicesUserRoleId)
  scope: foundry
  properties: {
    principalId: uamiPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalType: 'ServicePrincipal'
  }
}
```

(If `version: null` fails lint on this API version, use the `union()` trick: build `properties.model` as `union({ format: 'Anthropic', name: 'claude-opus-4-7' }, empty(claudeModelVersion) ? {} : { version: claudeModelVersion })`.)

- [ ] **Step 3: Wire params through both `main.bicep`s**

Grep first: `grep -n "deployGpt4o\|gpt4oCapacity" infra/main.bicep infra/single-rg/main.bicep` — add `deployClaude`, `claudeCapacity`, `claudeModelVersion` params beside them (same defaults) and pass them into the `ai` module call exactly the way `deployGpt4o`/`gpt4oCapacity` are passed in each variant.

- [ ] **Step 4: Lint + identity check + commit**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
diff infra/modules/ai.bicep infra/single-rg/modules/ai.bicep && echo IDENTICAL
git add infra/ && git commit -m "feat(infra): claude-opus-4-7 Foundry deployment (param-gated) + Cognitive Services User role (both variants)"
```

---

### Task 17: Infra — compute.bicep gets real apps (ACR, env vars, probes) + first-deploy fix-ups

The main checkout carries three deliberately-uncommitted first-deploy fix-ups to `compute.bicep`; this task **incorporates them** so they finally land in git: (a) placeholder image → `mcr.microsoft.com/azuredocs/containerapps-helloworld:latest`, (b) `allowInsecure: true` (HTTP gateway→ACA inside the private VNet; HTTPS deferred per Plan-3 Decision F), (c) frontend `minReplicas: 1` (gateway health probe needs a warm backend). Coordinate with the user before merging if the main checkout still holds those edits, to avoid a textual conflict.

**Files:**
- Modify: `infra/modules/compute.bicep` + `infra/single-rg/modules/compute.bicep` (full rewrite below — keep byte-identical)
- Modify: `infra/main.bicep` + `infra/single-rg/main.bicep` (module wiring)

- [ ] **Step 1: Replace the body of both `compute.bicep` files**

Keep the existing header params (`namePrefix`…`includeDedicatedProfile`) and the `cae` resource exactly as they are; change `placeholderImage`'s default and replace everything from the `apps` var to the outputs with:

```bicep
@description('ACR login server (empty = no registry wiring; placeholder images only).')
param acrLoginServer string = ''

@description('Backend API image (empty = placeholder).')
param backendImage string = ''

@description('Orchestrator image (empty = placeholder).')
param orchestratorImage string = ''

@description('Client ID of the workload UAMI (env var for ManagedIdentityCredential).')
param uamiClientId string = ''

@description('Foundry account endpoint, e.g. https://aif-....cognitiveservices.azure.com/ — the app derives the /anthropic/v1 base itself from the services.ai host.')
param foundryEndpoint string = ''

@description('Cosmos account document endpoint.')
param cosmosEndpoint string = ''

@description('AI Search endpoint.')
param searchEndpoint string = ''

@description('App Insights connection string (empty = telemetry off).')
param appInsightsConnectionString string = ''

@description('Key Vault URI for the Foundry Anthropic key fallback (empty = Entra-only).')
param keyVaultUri string = ''

var sharedEnv = [
  { name: 'UAMI_CLIENT_ID', value: uamiClientId }
  { name: 'FOUNDRY_ENDPOINT', value: foundryEndpoint }
  { name: 'COSMOS_ACCOUNT_ENDPOINT', value: cosmosEndpoint }
  { name: 'SEARCH_ENDPOINT', value: searchEndpoint }
  { name: 'KEYVAULT_URI', value: keyVaultUri }
  { name: 'CLAUDE_DEPLOYMENT', value: 'claude-opus-4-7' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
]

var registries = empty(acrLoginServer) ? [] : [
  {
    server: acrLoginServer
    identity: uamiId
  }
]

// name/image/port/ingress/env per app; probes only where there is an HTTP surface.
var apps = [
  {
    name: 'frontend'
    image: placeholderImage
    hasIngress: true
    targetPort: 80
    minReplicas: 1 // gateway backend probe needs a warm replica
    env: []
    probes: []
  }
  {
    name: 'backend'
    image: empty(backendImage) ? placeholderImage : backendImage
    hasIngress: true
    targetPort: empty(backendImage) ? 80 : 8080 // aspnet:8.0 default port
    minReplicas: 0
    env: sharedEnv
    probes: empty(backendImage) ? [] : [
      {
        type: 'Readiness'
        httpGet: { path: '/healthz', port: 8080 }
        initialDelaySeconds: 5
        periodSeconds: 10
      }
    ]
  }
  {
    name: 'orchestrator'
    image: empty(orchestratorImage) ? placeholderImage : orchestratorImage
    hasIngress: empty(orchestratorImage) // placeholder needs ingress to be healthy; real worker has none
    targetPort: 80
    minReplicas: empty(orchestratorImage) ? 0 : 1 // change-feed processor must be running to dispatch
    env: sharedEnv
    probes: []
  }
]

resource containerApps 'Microsoft.App/containerApps@2024-03-01' = [for app in apps: {
  name: 'ca-${namePrefix}-${env}-${app.name}-${regionShort}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${uamiId}': {}
    }
  }
  properties: {
    managedEnvironmentId: cae.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: app.hasIngress ? {
        external: false
        targetPort: app.targetPort
        transport: 'auto'
        allowInsecure: true // HTTP end-to-end inside the private VNet; HTTPS deferred (Decision F)
      } : null
      registries: registries
    }
    template: {
      containers: [
        {
          name: app.name
          image: app.image
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: app.env
          probes: app.probes
        }
      ]
      scale: {
        minReplicas: app.minReplicas
        maxReplicas: 2
      }
    }
  }
}]

output envId string = cae.id
output envStaticIp string = cae.properties.staticIp
output envDefaultDomain string = cae.properties.defaultDomain
output frontendFqdn string = containerApps[0].properties.configuration.ingress.fqdn
output frontendAppName string = containerApps[0].name
output backendAppName string = containerApps[1].name
output orchestratorAppName string = containerApps[2].name
```

Also change the existing param default at the top of the file:

```bicep
param placeholderImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
```

- [ ] **Step 2: Wire new module params in both `main.bicep`s**

Locate the compute module call (`grep -n "modules/compute.bicep" infra/main.bicep infra/single-rg/main.bicep`) and add arguments, sourcing each from the existing module outputs in that file (grep for the output names to find the module symbol names used in each variant — they differ):

```bicep
    acrLoginServer: acr.outputs.loginServer            // grep: "output loginServer" in acr.bicep
    backendImage: backendImage                          // new top-level param, default ''
    orchestratorImage: orchestratorImage                // new top-level param, default ''
    uamiClientId: security.outputs.uamiClientId         // add this output to security.bicep if absent
    foundryEndpoint: ai.outputs.foundryEndpoint
    cosmosEndpoint: data.outputs.cosmosDocumentEndpoint // added in Task 15
    searchEndpoint: 'https://${ai.outputs.searchName}.search.windows.net'
    appInsightsConnectionString: /* the same value the functions module already receives — reuse that expression */
    keyVaultUri: security.outputs.keyVaultUri           // add this output to security.bicep if absent
```

Add the two top-level params next to `placeholderImage`-style params:

```bicep
@description('Backend API image (ACR path incl. tag). Empty = placeholder.')
param backendImage string = ''

@description('Orchestrator image (ACR path incl. tag). Empty = placeholder.')
param orchestratorImage string = ''
```

If `security.bicep` lacks `uamiClientId`/`keyVaultUri` outputs, add them (both variants):

```bicep
output uamiClientId string = uami.properties.clientId
output keyVaultUri string = keyVault.properties.vaultUri
```

(match the actual resource symbol names in that file — grep for `Microsoft.ManagedIdentity/userAssignedIdentities` and `Microsoft.KeyVault/vaults`).

- [ ] **Step 3: AcrPull sanity** — `grep -n "AcrPull\|7f951dda" infra/modules/acr.bicep` — the AcrPull role assignment to the UAMI already exists per the repo map; if the grep comes up empty, add the standard role assignment (`7f951dda-4ed3-4680-a7ca-43fe172d538d`) to `acr.bicep` scoped to the registry.

- [ ] **Step 4: Lint + identity check + commit**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
diff infra/modules/compute.bicep infra/single-rg/modules/compute.bicep && echo IDENTICAL
git add infra/ && git commit -m "feat(infra): compute apps get ACR wiring, env vars, probes; land first-deploy fix-ups (both variants)"
```

---

### Task 18: Dockerfiles + `build-images.sh`

**Files:**
- Create: `src/Smx.Backend/Dockerfile`
- Create: `src/Smx.Orchestrator/Dockerfile`
- Create: `infra/scripts/build-images.sh`

- [ ] **Step 1: Write the Dockerfiles**

`src/Smx.Backend/Dockerfile` (build context is `src/`):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Smx.Domain/ Smx.Domain/
COPY Smx.Infrastructure/ Smx.Infrastructure/
COPY Smx.Backend/ Smx.Backend/
RUN dotnet publish Smx.Backend/Smx.Backend.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS run
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Smx.Backend.dll"]
```

`src/Smx.Orchestrator/Dockerfile` (build context is `src/`):

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Smx.Domain/ Smx.Domain/
COPY Smx.Infrastructure/ Smx.Infrastructure/
COPY Smx.Orchestrator/ Smx.Orchestrator/
RUN dotnet publish Smx.Orchestrator/Smx.Orchestrator.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS run
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Smx.Orchestrator.dll"]
```

- [ ] **Step 2: Write `infra/scripts/build-images.sh`** (mirror the header/arg style of `infra/scripts/publish-functions.sh`; uses `az acr build` so no local Docker is needed)

```bash
#!/usr/bin/env bash
# Build + push the backend and orchestrator images in ACR (cloud build, no local docker).
# Usage: build-images.sh <env> [tag]
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "${SCRIPT_DIR}/lib.sh"

ENV_NAME="${1:?usage: build-images.sh <env> [tag]}"
TAG="${2:-$(git -C "${SCRIPT_DIR}/../.." rev-parse --short HEAD)}"
ACR_NAME="$(az acr list --query "[?starts_with(name, 'acrsmx${ENV_NAME}')].name | [0]" -o tsv)"
[[ -n "${ACR_NAME}" ]] || { echo "no ACR found for env ${ENV_NAME}" >&2; exit 1; }
SRC_DIR="${SCRIPT_DIR}/../../src"

for app in backend orchestrator; do
  proj="Smx.$(python3 -c "print('${app}'.capitalize())")"
  echo ">> az acr build ${app} -> smx-${app}:${TAG}"
  az acr build --registry "${ACR_NAME}" \
    --image "smx-${app}:${TAG}" \
    --file "${SRC_DIR}/${proj}/Dockerfile" \
    "${SRC_DIR}"
done

echo "images: ${ACR_NAME}.azurecr.io/smx-backend:${TAG}  ${ACR_NAME}.azurecr.io/smx-orchestrator:${TAG}"
echo "roll out with: az deployment ... -p backendImage=... orchestratorImage=...  (or swap-images.sh)"
```

```bash
chmod +x infra/scripts/build-images.sh
```

Check `lib.sh`'s actual helper names first (`grep -n "^[a-z_]*()" infra/scripts/lib.sh`) and use its logging helpers if they exist instead of bare `echo`, matching the other scripts. Also confirm the ACR naming query matches the actual convention (`acr<prefix><env><suffix>` per `acr.bicep` — adjust the `starts_with` accordingly).

- [ ] **Step 3: Verify the Dockerfiles build (cloud, dev ACR) or defer**

If the dev ACR is reachable: `infra/scripts/build-images.sh dev` → both builds succeed. If not logged in to Azure, mark this deferred to the deploy runbook (Task 20) and continue.

- [ ] **Step 4: Commit**

```bash
git add src/Smx.Backend/Dockerfile src/Smx.Orchestrator/Dockerfile infra/scripts/build-images.sh
git commit -m "feat(infra): Dockerfiles + az-acr-build script for backend/orchestrator images"
```

### Task 19: Eval harness (`tools/Smx.Eval`) — metrics TDD + starter golden set

**Files:**
- Create: `tools/Smx.Eval/Smx.Eval.csproj` (console, net8.0, `RollForward=Major`; add to `src/Smx.Backend.sln`)
- Create: `tools/Smx.Eval/EvalModels.cs`
- Create: `tools/Smx.Eval/EvalMetrics.cs`
- Create: `tools/Smx.Eval/Program.cs`
- Create: `tools/Smx.Eval/golden/starter.json`
- Create: `tools/Smx.Eval.Tests/Smx.Eval.Tests.csproj` (xunit, references Smx.Eval; add to sln)
- Test: `tools/Smx.Eval.Tests/EvalMetricsTests.cs`

- [ ] **Step 1: Create the projects**

```bash
mkdir -p tools && cd tools
dotnet new console -n Smx.Eval -f net8.0
dotnet new xunit  -n Smx.Eval.Tests -f net8.0
dotnet add Smx.Eval reference ../src/Smx.Domain
dotnet add Smx.Eval.Tests reference Smx.Eval
cd .. && dotnet sln src/Smx.Backend.sln add tools/Smx.Eval tools/Smx.Eval.Tests
```

Add `<RollForward>Major</RollForward>` to both csproj files, and to `Smx.Eval.csproj`:

```xml
<ItemGroup>
  <None Include="golden/**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

- [ ] **Step 2: Write the failing metrics tests**

`tools/Smx.Eval.Tests/EvalMetricsTests.cs`:

```csharp
using Smx.Domain.Records;
using Smx.Eval;

namespace Smx.Eval.Tests;

public class EvalMetricsTests
{
    private static ExpectedCell E(string cas, string comp, VerdictStatus s, string track) => new(cas, comp, s, track);
    private static MatrixCell A(string cas, string comp, VerdictStatus s, bool cited = true) => new(cas, comp, s,
        [new DimensionVerdict("ElementGate", s,
            cited ? [new Citation("regulatory", "x", "t")] : [], 0.9, "r")]);

    [Fact]
    public void Agreement_IsComputedPerTrack()
    {
        var report = EvalMetrics.Score(
            [E("c1", "b", VerdictStatus.Fail, "plumbing"), E("c2", "b", VerdictStatus.Pass, "reasoning")],
            [A("c1", "b", VerdictStatus.Fail), A("c2", "b", VerdictStatus.Conditional)]);
        Assert.Equal(1.0, report.Tracks["plumbing"].Agreement);
        Assert.Equal(0.0, report.Tracks["reasoning"].Agreement);
    }

    [Fact]
    public void FalsePass_IsCountedSeparately_AsTheHarmMetric()
    {
        var report = EvalMetrics.Score(
            [E("c1", "b", VerdictStatus.Fail, "reasoning"), E("c2", "b", VerdictStatus.Fail, "reasoning")],
            [A("c1", "b", VerdictStatus.Pass), A("c2", "b", VerdictStatus.Fail)]);
        Assert.Equal(1, report.FalsePassCount); // predicted clean where expected Fail
        Assert.Equal(0.5, report.Tracks["reasoning"].Agreement);
    }

    [Fact]
    public void UncitedCell_CountsAsFailure_EvenWhenVerdictAgrees()
    {
        var report = EvalMetrics.Score(
            [E("c1", "b", VerdictStatus.Fail, "reasoning")],
            [A("c1", "b", VerdictStatus.Fail, cited: false)]);
        Assert.Equal(0.0, report.Tracks["reasoning"].Agreement);
        Assert.Equal(1, report.UncitedCount);
    }

    [Fact]
    public void MissingCell_CountsAsDisagreement()
    {
        var report = EvalMetrics.Score([E("c1", "b", VerdictStatus.Fail, "reasoning")], []);
        Assert.Equal(0.0, report.Tracks["reasoning"].Agreement);
        Assert.Equal(1, report.MissingCount);
    }
}
```

- [ ] **Step 3: Run, verify fail** — `dotnet test tools/Smx.Eval.Tests -v q` → FAIL.

- [ ] **Step 4: Implement models + metrics**

`tools/Smx.Eval/EvalModels.cs`:

```csharp
using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Eval;

/// One golden case = one project payload + expected overall verdict per cell.
public sealed class GoldenCase
{
    public required string Name { get; set; }
    public required JsonElement ProjectPayload { get; set; } // the exact POST /projects body
    public List<ExpectedCell> Expected { get; set; } = [];
}

/// track: "plumbing" (answerable via ref-compatibility lookup) or "reasoning" (requires retrieval+judgment)
public sealed record ExpectedCell(string Cas, string ComponentId, VerdictStatus Expected, string Track);

public sealed class TrackScore
{
    public int Total { get; set; }
    public int Agreed { get; set; }
    public double Agreement => Total == 0 ? 1.0 : (double)Agreed / Total;
}

public sealed class EvalReport
{
    public Dictionary<string, TrackScore> Tracks { get; } = new(); // collection expressions don't cover Dictionary
    public int FalsePassCount { get; set; }
    public int UncitedCount { get; set; }
    public int MissingCount { get; set; }
    public List<string> Failures { get; } = [];
}
```

`tools/Smx.Eval/EvalMetrics.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Eval;

public static class EvalMetrics
{
    public static EvalReport Score(IReadOnlyList<ExpectedCell> expected, IReadOnlyList<MatrixCell> actual)
    {
        var report = new EvalReport();
        var byCell = actual.ToDictionary(c => (c.Cas, c.ComponentId));
        foreach (var e in expected)
        {
            var track = report.Tracks.TryGetValue(e.Track, out var t) ? t : report.Tracks[e.Track] = new TrackScore();
            track.Total++;

            if (!byCell.TryGetValue((e.Cas, e.ComponentId), out var cell))
            {
                report.MissingCount++;
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: no cell in matrix (expected {e.Expected})");
                continue;
            }
            var uncited = cell.Dimensions.Any(d => d.Citations.Count == 0);
            if (uncited)
            {
                report.UncitedCount++;
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: uncited dimension — counts as failure");
            }
            var agreed = cell.Overall == e.Expected && !uncited;
            if (agreed) track.Agreed++;
            else if (cell.Overall != e.Expected)
                report.Failures.Add($"{e.Cas}×{e.ComponentId}: expected {e.Expected}, got {cell.Overall}");
            // the harm metric: model said usable where the golden answer is Fail
            if (e.Expected == VerdictStatus.Fail && cell.Overall is VerdictStatus.Pass or VerdictStatus.Conditional)
                report.FalsePassCount++;
        }
        return report;
    }
}
```

- [ ] **Step 5: Run, verify pass** — `dotnet test tools/Smx.Eval.Tests -v q` → PASS.

- [ ] **Step 6: Implement the runner** (thin HTTP loop; no unit tests — exercised live)

`tools/Smx.Eval/Program.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Eval;

var baseUrl = args.Length > 0 ? args[0] : Environment.GetEnvironmentVariable("SMX_API_URL")
    ?? throw new InvalidOperationException("usage: Smx.Eval <api-base-url> [golden.json] — or set SMX_API_URL");
var goldenPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "golden", "starter.json");
var cases = JsonSerializer.Deserialize<List<GoldenCase>>(File.ReadAllText(goldenPath), Json.Options)!;
using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };

var overall = new EvalReport();
foreach (var gc in cases)
{
    Console.WriteLine($"== case: {gc.Name}");
    var post = await http.PostAsJsonAsync("/projects", gc.ProjectPayload);
    post.EnsureSuccessStatusCode();
    var projectId = (await post.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("projectId").GetString()!;

    MatrixDoc? matrix = null;
    var deadline = DateTimeOffset.UtcNow.AddMinutes(20); // agent runs take minutes; poll patiently
    while (DateTimeOffset.UtcNow < deadline)
    {
        var resp = await http.GetAsync($"/projects/{projectId}/matrix");
        if (resp.IsSuccessStatusCode)
        {
            matrix = JsonSerializer.Deserialize<MatrixDoc>(await resp.Content.ReadAsStringAsync(), Json.Options);
            break;
        }
        var status = await http.GetFromJsonAsync<JsonElement>($"/projects/{projectId}");
        var stages = status.GetProperty("stages");
        Console.WriteLine($"   waiting... intake={S(stages, "intake")} screening={S(stages, "screening")} matrix={S(stages, "matrix")}");
        if (S(stages, "intake") == "failed" || S(stages, "screening") == "failed") break;
        await Task.Delay(TimeSpan.FromSeconds(15));
    }
    if (matrix is null) { Console.WriteLine($"   NO MATRIX for {gc.Name} — counting all cells as missing"); }

    var report = EvalMetrics.Score(gc.Expected, matrix?.Cells ?? []);
    Merge(overall, report);
    Print(gc.Name, report);
}
Print("TOTAL", overall);
File.WriteAllText("eval-report.json", JsonSerializer.Serialize(overall, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
Console.WriteLine("wrote eval-report.json");
return overall.FalsePassCount == 0 ? 0 : 2; // false-pass is the harm case: non-zero exit

static string S(JsonElement stages, string k) => stages.GetProperty(k).GetProperty("status").GetString()!;

static void Merge(EvalReport into, EvalReport from)
{
    foreach (var (k, v) in from.Tracks)
    {
        var t = into.Tracks.TryGetValue(k, out var e) ? e : into.Tracks[k] = new TrackScore();
        t.Total += v.Total; t.Agreed += v.Agreed;
    }
    into.FalsePassCount += from.FalsePassCount;
    into.UncitedCount += from.UncitedCount;
    into.MissingCount += from.MissingCount;
    into.Failures.AddRange(from.Failures);
}

static void Print(string name, EvalReport r)
{
    Console.WriteLine($"-- {name}");
    foreach (var (track, s) in r.Tracks)
        Console.WriteLine($"   {track}: {s.Agreed}/{s.Total} = {s.Agreement:P0}");
    Console.WriteLine($"   false-pass: {r.FalsePassCount}  uncited: {r.UncitedCount}  missing: {r.MissingCount}");
    foreach (var f in r.Failures) Console.WriteLine($"   ! {f}");
}
```

- [ ] **Step 7: Write the starter golden set** (defensible, well-known cases; the reasoning track expects Fail on notorious restricted elements — expand with the user during the proof)

`tools/Smx.Eval/golden/starter.json`:

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
      "substances": [
        { "element": "Cd", "form": "sulfide", "cas": "1306-23-6" },
        { "element": "Pb", "form": "2-ethylhexanoate", "cas": "301-08-6" },
        { "element": "Zr", "form": "neodecanoate", "cas": "39049-04-2" }
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

Rationale: Cd and Pb are restricted under REACH Annex XVII (entries 23 and 63) and capped by the PPWR heavy-metals rule; Pb compounds are CMR — any correctly-reasoning screen must Fail them for EU packaging/cosmetic. The Zr cells are deliberately **not** asserted in the starter set (their golden answer depends on KB content — curate with the operator; plumbing-track cells get added the same way once the seeded `ref-compatibility` rows are inspected).

- [ ] **Step 8: Full suite + commit**

```bash
dotnet test src/Smx.Backend.sln
git add tools/ src/Smx.Backend.sln
git commit -m "feat(eval): eval harness — per-track agreement, false-pass harm metric (TDD), runner + starter golden set"
```

---

### Task 20: Deploy & proof runbook (gated on live Azure access)

No code beyond one gateway edit — an ordered checklist executed with the user, mirroring how the SDS deploy was gated. **Stop and confirm with the user before starting this task.**

**Files:**
- Modify: `infra/modules/gateway.bicep` + `infra/single-rg/modules/gateway.bicep` (path-based routing)

- [ ] **Step 1: Route `/api/*` to the backend app through the App Gateway**

The backend has internal-only ingress; the gateway is the sole public inbound. Add path-based routing so `/api/*` reaches the backend while `/` keeps hitting the frontend. In both `gateway.bicep` variants: add a `backendFqdn` param, a second backend pool + HTTP settings (host header = `backendFqdn`, probe path `/api/healthz` → rewrite or use `/healthz` with its own probe), and replace the basic `requestRoutingRules` entry with a `PathBasedRouting` rule + `urlPathMaps` (`/api/*` → backend pool). App Gateway does not strip the matched path, so the API must serve under `/api` — add to `src/Smx.Backend/Program.cs`, immediately before `app.MapProjectEndpoints();`:

```csharp
if (app.Configuration["PATH_BASE"] is { Length: > 0 } pathBase)
    app.UsePathBase(pathBase);
```

and set `{ name: 'PATH_BASE', value: '/api' }` in the backend app's env in `compute.bicep` (both variants). Adapt symbol names to the file's existing structure (`grep -n "requestRoutingRules\|backendAddressPools\|backendHttpSettings" infra/modules/gateway.bicep`) and wire `backendFqdn: compute.outputs.<backend fqdn output>` in both `main.bicep`s (add `output backendFqdn string = containerApps[1].properties.configuration.ingress.fqdn` to `compute.bicep`, both variants). Keep both variants byte-identical; lint both mains.

**Security note (recorded, accepted for this milestone):** this exposes the unauthenticated API through the gateway on dev. The spec defers API auth; if the user prefers, restrict with a WAF custom rule / NSG to the operator's IP during the proof window.

```bash
git add infra/ src/Smx.Backend && git commit -m "feat(infra): gateway path-based routing /api/* -> backend (+PATH_BASE support)"
```

- [ ] **Step 2: Preflight** — `infra/scripts/preflight.sh` passes; Claude model listing (Task 16 Step 1) re-run against the live subscription; confirm `deployClaude`/version params match reality.

- [ ] **Step 3: Deploy infra** — run the variant-appropriate deploy (`infra/scripts/deploy.sh dev` or the single-rg equivalent the user actually uses — ask). This is **Plan 3's first live deploy** plus this plan's deltas. Then the established SDS/reference sequence if not already done on this sub: `publish-functions.sh dev`, `configure-auth.sh dev`, `seed-reference-data.sh dev`.

- [ ] **Step 4: Resolve the Foundry Anthropic credential path** — from a shell with the deployer IP allowlisted: obtain an Entra token for the UAMI-equivalent scope and call the Anthropic endpoint once:

```bash
FOUNDRY=$(az cognitiveservices account show -n <aif-name> -g <rg> --query properties.endpoint -o tsv)
TOKEN=$(az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv)
curl -sS "${FOUNDRY%/}/anthropic/v1/messages" -H "Authorization: Bearer $TOKEN" -H "content-type: application/json" \
  -d '{"model":"claude-opus-4-7","max_tokens":32,"messages":[{"role":"user","content":"ping"}]}' | head -c 400
```

If Entra works (JSON message response): done — the factory's TokenCredential path is primary; **extend `FoundryChatClientFactory` to send the bearer token** (add a small `TokenCredential`-based auth handler if the `Anthropic.Foundry` client lacks one — an `AuthToken`/custom-header option on the client; adjust per installed SDK). If Entra is rejected (401 with key-only error): fetch an account key (`az cognitiveservices account keys list`), store it as Key Vault secret `foundry-anthropic-key`, and the factory's existing KV path takes over. Record which path won in the runbook commit message.

- [ ] **Step 5: Build + roll out images** — `infra/scripts/build-images.sh dev`, then redeploy with `-p backendImage=<acr>/smx-backend:<tag> -p orchestratorImage=<acr>/smx-orchestrator:<tag>` (or `swap-images.sh`).

- [ ] **Step 6: End-to-end smoke** — through the gateway public IP: `curl http://<gw-ip>/api/healthz` → 200; POST the starter golden payload to `/api/projects`; watch `GET /api/projects/{id}` progress to `matrix: done`; download `?format=xlsx` and open it.

- [ ] **Step 7: Run the eval** — `dotnet run --project tools/Smx.Eval -- http://<gw-ip>/api` → report printed + `eval-report.json`; exit code 0 (no false-pass). Review the reasoning-track failures (if any) with the user; curate additional golden cases (plumbing track from seeded `ref-compatibility` rows; more reasoning cases from known determinations) and re-run.

- [ ] **Step 8: Harden** — re-run `infra/scripts/harden.sh dev` if this deploy re-opened anything; verify the eval still passes from an allowlisted path (gateway remains the public inbound).

---

### Task 21: Docs — CLAUDE.md + README

**Files:**
- Modify: `CLAUDE.md` (Application code section)
- Modify: `README.md` (if it lists build commands)

- [ ] **Step 1: Extend the CLAUDE.md "Application code" section** — append after the SDS bullet:

```markdown
- **Agent backend** (`src/Smx.Backend.sln`: `Smx.Domain`, `Smx.Infrastructure`, `Smx.Backend` API,
  `Smx.Orchestrator` agent host; deployed as the `backend` + `orchestrator` Container Apps) — the SMX
  reasoning layer: MAF agents on Claude Opus 4.7 (Foundry, Anthropic-native endpoint) with RAG tools over
  the three AI Search indexes + deterministic `ref-*` lookups, record-as-bus in the Cosmos `record`
  container (change-feed dispatch), Excel-style compatibility matrix output. Design + plan:
  `docs/superpowers/specs/2026-07-08-agent-backend-design.md`,
  `docs/superpowers/plans/2026-07-08-agent-backend.md`.
  - Build: `dotnet build src/Smx.Backend.sln` · Test: `dotnet test src/Smx.Backend.sln`
  - Images: `infra/scripts/build-images.sh <env>` (az acr build) · Eval: `dotnet run --project tools/Smx.Eval -- <api-url>`
```

- [ ] **Step 2: Check README** — `grep -n "dotnet" README.md`; if build/test commands are listed there, mirror the two new commands. Otherwise skip.

- [ ] **Step 3: Full suite, final commit**

```bash
dotnet test src/Smx.Backend.sln && dotnet test src/Smx.Functions.sln
git add CLAUDE.md README.md
git commit -m "docs: agent backend build/test/eval commands"
```

---

## Execution order & gates

1. Tasks 1–13 — pure code, no Azure needed, TDD throughout (any order violations noted per-task; 5 depends on 4, 9–11 depend on 7–8, 12–13 depend on 9–11).
2. Task 14 — rebase gate (coordinate: local `main` must be quiescent).
3. Tasks 15–19 — infra + eval, lint-verified only.
4. Task 20 — **live-deploy gate: confirm with the user first** (subscription access, Claude quota, cost).
5. Task 21 — docs.









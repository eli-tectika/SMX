# Chemistry Backend — Plan 4: Dosing & Codes + Cost

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the operator-approved compliant set into a **dosable, orderable answer** — per-component ppm windows with a measured detection floor, 2–3-marker codes with a ratio signature, order amounts in grams, and a per-molecule supplier/cost audit — where **every number traces to a measurement, a table, or a cited listing, and nothing is ever estimated into existence.**

**Architecture:** Two new stages on the existing record bus. `dosing` is *mixed*: deterministic calculators (detection floor, order amount, ratio signature) fenced around an agent that only picks a ppm **inside** a window the code computed. `cost` is *purely deterministic* — no agent. Both hang off the change feed exactly like every other stage: writing a doc IS the dispatch.

**Tech Stack:** .NET 8 (`Smx.Backend.Tests` is `net10.0`), xUnit, Cosmos change feed, Microsoft Agent Framework on Claude via Foundry, Bicep.

---

## Read this before you touch anything

- `docs/superpowers/specs/2026-07-12-chemistry-backend-end-to-end-design.md` **§3.3 (Dosing)** and **§3.4 (Cost)**.
- `project_files/SMX_Marker_System_UX_Spec.md` **§4.5, §4.6**.
- `CLAUDE.md` — the interaction laws. Four govern this plan:
  - **Law 4 — no direct edits to agent output.** Dosing is revisable: changes go through the agent, with a reason, and earn a Learned Conclusion.
  - **Law 5 — per-stage isolated agents.** Dosing gets its own agent, its own tools, its own record inputs.
  - **Law 9 — gates are operator-signed.** Dosing's checkpoint is **soft** (a recorded review note), not a gate. It does not block. Do not make it block.
  - **Deterministic-first.** Anything that is a formula or a table lookup is *code*, not a model. The agent's only real judgment here is *where inside a computed window* to sit, and *which 2–3 markers* combine into a code.

**Correctness is the primary design driver. The headline harm is a false pass.**

**Baseline: 401 tests green** (`dotnet test src/Smx.Backend.sln` — Domain 144, Orchestrator 194, Backend 59, Eval 4).
Also: `dotnet test src/Smx.Functions.sln` → 70, must not regress.

### Five traps this codebase has already sprung. Do not spring them again.

1. **`[FromServices]` is mandatory on every store param in a minimal-API handler.** Miss it and routing breaks for the **whole app**, `/healthz` included. See the comment atop `src/Smx.Backend/Api/ProjectEndpoints.cs`.
2. **`AIFunctionFactory` schemas can lie.** A parameter without a default is emitted `"required"`. **Test every agent tool by invoking the real `AIFunction` via `InvokeAsync`**, never the C# method.
3. **Cosmos LINQ takes member names from `SystemTextJsonCosmosSerializer.SerializeMemberName`** (it derives from `CosmosLinqSerializer` — **do not change that base class**). A PascalCase query matches **zero** documents in Azure, silently, with a green suite. **Every new LINQ query goes into `src/Smx.Orchestrator.Tests/CosmosQueryTextTests.cs`.**
4. **Azure/Cosmos failures are silent.** A missing container 404s and looks like an empty knowledge layer.
5. **Test-project fakes are shared by source-link** (`<Compile Include="../Smx.Domain.Tests/Fakes/X.cs" Link="Fakes/X.cs" />`), never `ProjectReference` (CS0433).

### And one this plan will spring the moment you add a stage constant

**`Stages.All` is reflection-pinned to the stage constants** (`ChatEndpointsTests.Stages_All_ListsEveryStageConstantOnTheClass`), and **`ProjectDoc.Create`'s stage dictionary is pinned to `Stages.All`**. So the instant you add `Stages.Dosing`, three things become live *in the same commit*:

- `POST /projects/{id}/stages/dosing/chat` starts accepting messages, and the dispatcher will run a chat turn with **zero tools** over `"{}"` stage inputs — a confident conversation about nothing.
- `ProjectDoc.Create` must gain the stage or its test goes red.
- `RevisionEffects` must answer for it or it throws.

**Task 8 does all of this together, deliberately.** Do not add a stage constant in any other task.

---

## The five design decisions that shape this plan

### 1. The detection floor is MEASURED. It is never inferred, and Dosing parks rather than guess it.

The floor is the **binding constraint** on the whole product. A recommended ppm below the true floor means the marker is **physically unreadable in the field** — SMX ships a taggant nobody can detect, and nobody finds out until deployment. There is no downstream check that catches this.

So the floor is computed **deterministically** from data a human measured:

```
detectionFloor(component, element)      = backgroundLevel + 3  × deviceLod    // IUPAC 3σ
quantificationFloor(component, element) = backgroundLevel + 10 × deviceLod    // IUPAC 10σ
```

Both inputs are **new** and **do not exist in the code today** — the design spec's payload contract lists them (`measured-background[]{component, element, level}`, `device-model`) but they were never implemented. Task 1 adds them. If they are absent for an element Dosing needs, **Dosing parks in `awaiting-physics`** and produces nothing. It does not estimate.

> ⚠️ **The 3σ/10σ multipliers are the IUPAC convention, not an SMX measurement.** They live as named constants in exactly one file (`DetectionFloor.cs`) with a comment saying so. **Confirm them with SMX physics at first live use.** If SMX uses different factors, that file is the only place to change.

**Units are load-bearing.** `backgroundLevel` and `deviceLod` must be in the **same unit as the ppm window** (ppm). Adding a value in counts to a value in ppm produces a number that is *not wrong-looking* — it is just wrong, and it silently mis-doses. `DetectionFloor` **refuses to compute** when units disagree. That is a real invariant with a real test.

### 2. Metal loading is cross-project knowledge: the operator enters it once, and every future project reads it.

Order amounts need the **mass fraction of the marker element in the compound** (Y₂O₃ is 78.7% Y). It exists nowhere: `catalog-products.json` has compound *names*, not formulas, so there is nothing to compute from.

The operator enters it once per CAS, and it is persisted **cross-project** in a new `substance-properties` Cosmos container (PK `/cas`) alongside the Marker Library and MSDS Registry. The next project that meets that CAS never asks again. **This is the knowledge layer doing exactly what it exists for.**

Missing loading ⇒ Dosing parks in `awaiting-operator`, naming exactly which CAS it needs. `POST /projects/{id}/dosing/loading` records it and re-triggers.

**Guard, and it is not defensive coding:** `metalLoading ∈ (0, 1]`. A loading of **0** divides by zero → an infinite order amount. A loading **> 1** claims more metal than compound — physically impossible, and it *under*-orders. Both are refused.

### 3. ppm is mass/mass. A batch **volume** cannot produce an order amount, and pretending otherwise mis-doses by up to 20×.

The UX spec says *"ppm × batch volume ÷ metal loading"*. That is sloppy and following it literally is a bug. ppm is mg/kg — **mass over mass**. To get from a *volume* to a mass you need a density, and assuming water (1 L = 1 kg) is wrong for every polymer (~0.9), and catastrophically wrong for gold (19.3).

So the intake field is **`BatchMassKg`**, not batch volume. If the operator only knows a volume, they multiply by their density and enter the mass. `OrderAmount` refuses a non-positive mass. Say this in the field's comment, or someone will "fix" it back.

```
elementMassMg  = recommendedPpm (mg/kg) × batchMassKg (kg)
compoundMassMg = elementMassMg ÷ metalLoading
```

### 4. The agent PROPOSES a determination. Only the OPERATOR's determination gets dosed.

The compliant set is `Determination == "recommended"` — **strictly the operator's field**. A substance no human said yes to never reaches a customer's product.

To keep that strict rule from becoming unusable, the **Regulatory agent now pre-fills a proposal** (`ProposedDetermination` + `ProposedReason`) so the operator confirms rather than authors. **These are two different fields and they must never be conflated.** If the agent's proposal could be read as the operator's determination, the agent would be signing the gate through the back door — the exact thing Law 9 exists to prevent. Task 7 pins that with a test whose failure is a design alarm.

### 5. Cost never estimates a price. Ever.

`price` exists as **free text** (`"$115.00"`, pack `"500 mg"`) on **77** catalog products, and `CatalogCard` doesn't even carry it today. Coverage is sparse.

So: parse what parses, and **fail closed**. No price on file ⇒ `"no price on file — quote required"`. Never interpolate, never average, never convert a currency (a `"Published (CNY)"` figure is **not** comparable to a `$` figure and must never be silently treated as one). Every figure links to its `ref-catalog` listing.

---

## File structure

**Create:**

| File | Responsibility |
|---|---|
| `src/Smx.Domain/DetectionFloor.cs` | Pure: measured background + device LOD → floor. **Unit-safe. The 3σ/10σ constants live here and nowhere else.** |
| `src/Smx.Domain/OrderAmount.cs` | Pure: ppm × batchMassKg ÷ metalLoading → grams of compound |
| `src/Smx.Domain/RatioSignature.cs` | Pure: a code's ppm set → its normalized ratio signature |
| `src/Smx.Domain/CompliantSet.cs` | Pure: verdicts → the substances Dosing may dose. **The false-pass boundary.** |
| `src/Smx.Domain/PriceParse.cs` | Pure: `"$115.00"` + `"500 mg"` → $/g, or a refusal. **Never estimates.** |
| `src/Smx.Domain/Records/DosingDoc.cs` | `DosingDoc`, `PpmWindow`, `MarkerCode`, `CodeMarker`, `Bound` |
| `src/Smx.Domain/Records/CostDoc.cs` | `CostDoc`, `SupplierAudit`, `PriceQuote` |
| `src/Smx.Domain/Records/SubstancePropertyDoc.cs` | Cross-project metal loading, PK `/cas` |
| `src/Smx.Orchestrator/Agents/DosingAgent.cs` | The ppm/code agent + its validation |
| `src/Smx.Orchestrator/Cost/CostAudit.cs` | The deterministic supplier/cost audit |
| `src/Smx.Backend/Api/DosingEndpoints.cs` | `GET …/dosing` · `POST …/dosing/loading` · `POST …/dosing/review` |
| `src/Smx.Backend/Api/CostEndpoints.cs` | `GET …/cost` |
| Tests mirroring each of the above | |

**Modify:** `Records/ConstraintsDoc.cs` · `Records/VerdictDoc.cs` · `Records/RecordIds.cs` · `Records/ProjectDoc.cs` · `Records/KnowledgeIds.cs` · `IRecordStore.cs` · `IKnowledgeStore.cs` · `RevisionEffects.cs` · `IntakeAnswers.cs` · `Tools/ITools.cs` (`CatalogCard`) · `Smx.Infrastructure/CosmosRecordStore.cs` · `CosmosKnowledgeStore.cs` · `Search/CatalogLookup.cs` · `Smx.Orchestrator/Agents/{IntakeAgent,RegulatoryAgent,ToolBox}.cs` · `Dispatch/{StageDispatcher,RecordDocRouter,AgentRuns}.cs` · `Smx.Backend/Program.cs` · both `infra/**/data.bicep` twins · the fakes.

**Infra DOES change this time** (unlike Plan 3c): a new `substance-properties` container. Both twins. See Task 6.

---

## Task 1: The physics inputs — measured background, device LODs, batch mass

**Read design decisions 1 and 3 before writing a line.** These fields are *measured data*, and like the element pools they must be unwritable by chat.

**Files:**
- Modify: `src/Smx.Domain/Records/ConstraintsDoc.cs`, `src/Smx.Domain/IntakeAnswers.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`, `src/Smx.Domain.Tests/IntakeAnswersTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `src/Smx.Domain.Tests/IntakeAnswersTests.cs`:

```csharp
    [Theory]
    [InlineData("measuredBackground.0.level")]
    [InlineData("device.lods.0.lodPpm")]
    [InlineData("device.model")]
    public void Patch_REFUSES_ToTouchTheMeasuredPhysicsInputs(string field)
    {
        // Same law as the element pools, and for the same reason: these are MEASURED values, and the ppm
        // detection floor is computed from them. A floor that reads low ships a marker nobody can detect in
        // the field, and there is no downstream check that catches it. A chat tool that can write them is a
        // mechanism by which a language model can silently alter measured data.
        var (patched, error) = IntakeAnswers.Patch(Payload(), field, "0.001");
        Assert.Null(patched);
        Assert.Contains("measured", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patch_AllowsBatchMassKg_BecauseItIsAnOperatorKnownProductFact()
    {
        // Batch mass is not a measurement — it is a production fact the operator knows and may well supply
        // in conversation. It IS answerable. (Its VALIDITY is enforced by OrderAmount, not here.)
        var (patched, error) = IntakeAnswers.Patch(Payload(), "components.bottle.batchMassKg", "250");
        Assert.Null(error);
        Assert.Equal("250", patched!.Value.GetProperty("components")[0].GetProperty("batchMassKg").GetString());
    }
```

Add to `src/Smx.Domain.Tests/RecordDocsTests.cs`:

```csharp
    [Fact]
    public void ConstraintsDoc_CarriesTheMeasuredPhysicsInputs_TheFloorIsComputedFrom()
    {
        var c = new ConstraintsDoc
        {
            Id = RecordIds.Constraints("proj-1"), ProjectId = "proj-1",
            Components = [new ComponentSpec("bottle", "HDPE", "packaging", ["EU"], "brand", 250.0)],
            MeasuredBackground = [new MeasuredBackground("bottle", "Zr", 4.0, "ppm")],
            Device = new XrfDevice("Olympus Vanta M", [new DeviceLod("Zr", 1.5, "ppm")]),
        };
        var json = JsonSerializer.Serialize(c, Json.Options);
        var back = JsonSerializer.Deserialize<ConstraintsDoc>(json, Json.Options)!;

        Assert.Equal(4.0, Assert.Single(back.MeasuredBackground).LevelPpm);
        Assert.Equal("ppm", Assert.Single(back.MeasuredBackground).Unit);
        Assert.Equal(1.5, Assert.Single(back.Device!.Lods).LodPpm);
        Assert.Equal(250.0, Assert.Single(back.Components).BatchMassKg);
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test src/Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter "IntakeAnswersTests|RecordDocsTests"`
Expected: FAIL — the types and members do not exist.

- [ ] **Step 3: Implement**

In `src/Smx.Domain/Records/ConstraintsDoc.cs`:

```csharp
/// A component's production facts. BatchMassKg is MASS, deliberately — see OrderAmount. ppm is mg/kg, so a
/// batch VOLUME cannot yield an order amount without a density, and assuming water (1 L = 1 kg) mis-doses a
/// polymer by ~10% and gold by 19×. If the operator has a volume, they multiply by density and enter mass.
public sealed record ComponentSpec(
    string Id, string Material, string Application, IReadOnlyList<string> Markets, string Objective,
    double? BatchMassKg = null);

/// The physicist's MEASURED background for one element in one component, in ppm. Together with the device
/// LOD this is what the ppm detection floor is computed from (DetectionFloor). It is measured data: like
/// ElementPool, it is not writable through chat (IntakeAnswers).
///
/// `Unit` is carried, not assumed. DetectionFloor REFUSES to add a background to a LOD whose unit differs —
/// mixing counts with ppm yields a number that looks perfectly reasonable and is simply wrong.
public sealed record MeasuredBackground(string Component, string Element, double LevelPpm, string Unit);

/// The XRF device the marker must be READ BY in deployment, and its per-element limit of detection.
/// The floor targets THIS device (UX spec §8: deployment-device-targeted floor), not an assumed lab unit.
public sealed record DeviceLod(string Element, double LodPpm, string Unit);
public sealed record XrfDevice(string Model, IReadOnlyList<DeviceLod> Lods);
```

and on `ConstraintsDoc`:

```csharp
    public List<MeasuredBackground> MeasuredBackground { get; set; } = [];
    public XrfDevice? Device { get; set; }
```

In `IntakeAnswers.cs`: add `"batchMassKg"` to `ComponentFields`, and refuse `measuredBackground` / `device` **by name** (alongside the existing `elementPools` / `providedCandidates` refusals), with an error naming them as measured data:

```csharp
        if (field.StartsWith("measuredBackground", StringComparison.OrdinalIgnoreCase)
         || field.StartsWith("device", StringComparison.OrdinalIgnoreCase))
            return (null, "the measured background and the device LODs are the physicist's measured data — " +
                          "the ppm detection floor is computed from them, and a floor that reads low ships a " +
                          "marker nobody can detect. They cannot be changed through chat.");
```

- [ ] **Step 4: Run tests, then the full suite.** `dotnet test src/Smx.Backend.sln`.

- [ ] **Step 5: Mutation-test.** Remove the `measuredBackground` refusal → the theory must go red on all three cases. Restore.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain src/Smx.Domain.Tests
git commit -m "feat(domain): measured background + device LODs + batch MASS — the floor's inputs, unwritable by chat"
```

---

## Task 2: `DetectionFloor` — the binding constraint, computed and never guessed

**Files:**
- Create: `src/Smx.Domain/DetectionFloor.cs`
- Test: `src/Smx.Domain.Tests/DetectionFloorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DetectionFloorTests
{
    private static readonly XrfDevice Vanta =
        new("Olympus Vanta M", [new DeviceLod("Zr", 1.5, "ppm"), new DeviceLod("Y", 2.0, "ppm")]);
    private static readonly MeasuredBackground[] Background =
        [new("bottle", "Zr", 4.0, "ppm"), new("bottle", "Y", 0.0, "ppm")];

    [Fact]
    public void Compute_IsBackgroundPlusThreeSigmaForDetection_AndTenSigmaForQuantification()
    {
        // The IUPAC convention. These two numbers decide whether a marker can be READ in the field.
        var (floor, error) = DetectionFloor.Compute(Background, Vanta, "bottle", "Zr");

        Assert.Null(error);
        Assert.Equal(4.0 + 3 * 1.5, floor!.DetectionPpm);        // 8.5
        Assert.Equal(4.0 + 10 * 1.5, floor.QuantificationPpm);   // 19.0
        Assert.Contains("Olympus Vanta M", floor.Basis);         // the basis names the device it targets
        Assert.Contains("4", floor.Basis);                       // ...and the measured background it used
    }

    [Fact]
    public void Compute_WithAZeroBackground_IsPurelyTheDeviceLimit()
    {
        var (floor, error) = DetectionFloor.Compute(Background, Vanta, "bottle", "Y");
        Assert.Null(error);
        Assert.Equal(3 * 2.0, floor!.DetectionPpm);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheUnitsDisagree()
    {
        // THE POINT OF THIS FILE'S GUARD. Adding a background in counts to a LOD in ppm produces a number
        // that looks entirely reasonable and is simply wrong — and a floor that reads low ships a marker
        // nobody can detect. There is no downstream check that catches it. Refuse.
        var counts = new MeasuredBackground[] { new("bottle", "Zr", 4.0, "counts") };
        var (floor, error) = DetectionFloor.Compute(counts, Vanta, "bottle", "Zr");

        Assert.Null(floor);
        Assert.Contains("unit", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("counts", error);
        Assert.Contains("ppm", error);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheBackgroundWasNeverMeasured()
    {
        // Dosing must PARK, not guess. An absent measurement is not a zero background — a genuinely zero
        // background is a MEASUREMENT, and it is recorded as 0.0. Silence is not data.
        var (floor, error) = DetectionFloor.Compute([], Vanta, "bottle", "Zr");
        Assert.Null(floor);
        Assert.Contains("no measured background", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zr", error);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheDeviceHasNoLodForTheElement()
    {
        var (floor, error) = DetectionFloor.Compute(Background, new XrfDevice("Vanta", []), "bottle", "Zr");
        Assert.Null(floor);
        Assert.Contains("no LOD", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_WhenNoDeviceWasCaptured() =>
        Assert.Contains("device", DetectionFloor.Compute(Background, null, "bottle", "Zr").Error!,
            StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(-1.0)]
    public void Compute_REFUSES_ANegativeLod(double lod)
    {
        // A negative LOD would pull the floor BELOW the measured background — the marker would be
        // "detectable" beneath the noise it has to be seen against.
        var (floor, error) = DetectionFloor.Compute(
            Background, new XrfDevice("bad", [new DeviceLod("Zr", lod, "ppm")]), "bottle", "Zr");
        Assert.Null(floor);
        Assert.NotNull(error);
    }
}
```

- [ ] **Step 2: Run to verify it fails.** Expected: FAIL — `DetectionFloor` does not exist.

- [ ] **Step 3: Implement** `src/Smx.Domain/DetectionFloor.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// One element's ppm floor in one component, with the basis it was computed from. `Basis` is not decoration:
/// the UX spec requires every bound to show its basis, and the operator must be able to check the number.
public sealed record Floor(double DetectionPpm, double QuantificationPpm, string Basis);

/// The ppm detection floor — the BINDING CONSTRAINT on the whole product.
///
/// A recommended ppm below the true floor means the marker is physically unreadable by the deployment
/// device: SMX ships a taggant nobody can detect, and there is no downstream check that catches it. So this
/// is computed from data a human MEASURED, and it refuses — loudly — rather than guess. Dosing parks.
///
/// The 3σ / 10σ multipliers are the IUPAC convention (detection vs. quantification), NOT an SMX
/// measurement. They live here and nowhere else. CONFIRM THEM WITH SMX PHYSICS AT FIRST LIVE USE; if SMX
/// works to different factors, this file is the only thing that changes.
public static class DetectionFloor
{
    public const double DetectionSigma = 3.0;
    public const double QuantificationSigma = 10.0;
    public const string Ppm = "ppm";

    public static (Floor? Floor, string? Error) Compute(
        IReadOnlyList<MeasuredBackground> background, XrfDevice? device, string componentId, string element)
    {
        if (device is null)
            return (null, "no XRF device was captured at intake, so the ppm floor cannot be targeted at the " +
                          "device that must read the marker in deployment. Enter the device and its LODs.");

        if (background.FirstOrDefault(b => b.Component == componentId && b.Element == element) is not { } bg)
            return (null, $"no measured background for {element} in '{componentId}'. The floor is computed " +
                          $"from a measurement — an absent one is not a zero (a zero background is itself a " +
                          $"measurement, recorded as 0). The physicist must measure it.");

        if (device.Lods.FirstOrDefault(l => l.Element == element) is not { } lod)
            return (null, $"the device '{device.Model}' has no LOD for {element}, so the floor for it cannot " +
                          $"be computed. Enter the LOD.");

        // Units are carried, not assumed. Adding counts to ppm yields a perfectly reasonable-looking number
        // that is simply wrong, and it mis-doses in the direction nobody checks.
        if (!string.Equals(bg.Unit, Ppm, StringComparison.OrdinalIgnoreCase))
            return (null, $"the measured background for {element} is in '{bg.Unit}', not '{Ppm}'. The floor " +
                          $"is a ppm value and cannot be computed from a background in another unit.");
        if (!string.Equals(lod.Unit, Ppm, StringComparison.OrdinalIgnoreCase))
            return (null, $"the LOD for {element} is in '{lod.Unit}', not '{Ppm}'.");

        if (bg.LevelPpm < 0) return (null, $"the measured background for {element} is negative ({bg.LevelPpm}).");
        if (lod.LodPpm <= 0) return (null, $"the LOD for {element} must be positive; it is {lod.LodPpm}. A " +
                                          $"non-positive LOD would put the floor at or below the background " +
                                          $"the marker has to be seen against.");

        return (new Floor(
            bg.LevelPpm + DetectionSigma * lod.LodPpm,
            bg.LevelPpm + QuantificationSigma * lod.LodPpm,
            $"{device.Model}: LOD {lod.LodPpm} ppm ({element}) over a measured background of {bg.LevelPpm} ppm " +
            $"in '{componentId}'; detection = bg + {DetectionSigma}σ, quantification = bg + {QuantificationSigma}σ (IUPAC)"),
            null);
    }
}
```

- [ ] **Step 4: Run tests, then the full suite.**

- [ ] **Step 5: Mutation-test.** Drop the unit check → the units test goes red. Change `DetectionSigma` to 1 → the first test goes red. Restore both.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/DetectionFloor.cs src/Smx.Domain.Tests/DetectionFloorTests.cs
git commit -m "feat(domain): DetectionFloor — measured, unit-safe, and it refuses rather than guess"
```

---

## Task 3: `OrderAmount` + `RatioSignature`

**Files:**
- Create: `src/Smx.Domain/OrderAmount.cs`, `src/Smx.Domain/RatioSignature.cs`
- Test: `src/Smx.Domain.Tests/OrderAmountTests.cs`, `src/Smx.Domain.Tests/RatioSignatureTests.cs`

- [ ] **Step 1: Write the failing tests**

`OrderAmountTests.cs`:

```csharp
using Smx.Domain;

namespace Smx.Domain.Tests;

public class OrderAmountTests
{
    [Fact]
    public void Compute_ConvertsPpmAndBatchMassIntoGramsOfCOMPOUND_NotGramsOfElement()
    {
        // 10 ppm of Y in a 250 kg batch = 2500 mg of Y. Y2O3 is 78.7% Y, so you must order
        // 2500 / 0.787 = 3176.6 mg of the OXIDE. Ordering 2500 mg of Y2O3 under-doses by 21%.
        var (amount, error) = OrderAmount.Compute(ppm: 10.0, batchMassKg: 250.0, metalLoading: 0.787);

        Assert.Null(error);
        Assert.Equal(2500.0, amount!.ElementMassMg, 3);
        Assert.Equal(2500.0 / 0.787, amount.CompoundMassMg, 3);
        Assert.Equal(amount.CompoundMassMg / 1000.0, amount.CompoundMassG, 6);
    }

    [Fact]
    public void Compute_REFUSES_AZeroLoading_RatherThanDivideByZero()
    {
        // A zero loading is an infinite order. Left unguarded this is a NaN/∞ that flows into a purchase
        // order, and IEEE-754 will not complain.
        var (amount, error) = OrderAmount.Compute(10.0, 250.0, 0.0);
        Assert.Null(amount);
        Assert.Contains("loading", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_ALoadingAboveOne_BecauseItIsPhysicallyImpossible()
    {
        // More metal than compound. It also UNDER-orders, which is the direction nobody checks.
        var (amount, error) = OrderAmount.Compute(10.0, 250.0, 1.4);
        Assert.Null(amount);
        Assert.Contains("1", error!);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Compute_REFUSES_ANonPositiveBatchMass(double kg)
    {
        // Guards the "the operator entered a VOLUME and we treated it as a mass" family, and the
        // "batchMassKg was never supplied so it defaulted to 0" family. Both order nothing, silently.
        var (amount, error) = OrderAmount.Compute(10.0, kg, 0.787);
        Assert.Null(amount);
        Assert.Contains("batch mass", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_ANonPositivePpm() =>
        Assert.NotNull(OrderAmount.Compute(0.0, 250.0, 0.787).Error);
}
```

`RatioSignatureTests.cs`:

```csharp
using Smx.Domain;

namespace Smx.Domain.Tests;

public class RatioSignatureTests
{
    [Fact]
    public void Of_NormalisesToTheLargestMarker_SoTheSignatureIsScaleInvariant()
    {
        // The signature is what a reader IDENTIFIES the code by, so it must survive a uniform scaling of
        // the whole code (the same code at 2× dosing is the SAME code).
        Assert.Equal(RatioSignature.Of([("Y", 20.0), ("Zr", 10.0)]),
                     RatioSignature.Of([("Y", 40.0), ("Zr", 20.0)]));
    }

    [Fact]
    public void Of_RendersLargestFirst_WithTwoDecimals()
    {
        Assert.Equal("Y:Zr = 1.00:0.50", RatioSignature.Of([("Zr", 10.0), ("Y", 20.0)]));
    }

    [Fact]
    public void Of_IsStableUnderInputOrder()
    {
        Assert.Equal(RatioSignature.Of([("Y", 20.0), ("Zr", 10.0), ("Hf", 5.0)]),
                     RatioSignature.Of([("Hf", 5.0), ("Y", 20.0), ("Zr", 10.0)]));
    }

    [Fact]
    public void Of_ThrowsOnANonPositivePpm() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RatioSignature.Of([("Y", 0.0), ("Zr", 10.0)]));
}
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.**

`src/Smx.Domain/OrderAmount.cs`:

```csharp
namespace Smx.Domain;

public sealed record Order(double ElementMassMg, double CompoundMassMg)
{
    public double CompoundMassG => CompoundMassMg / 1000.0;
}

/// How much of the COMPOUND to buy. ppm is mg/kg — mass over mass — so this takes a batch MASS, never a
/// volume: converting a volume without a density silently mis-doses (a polymer by ~10%, gold by 19×).
///
/// It returns the compound mass, not the element mass. Ordering the element mass of an oxide under-doses by
/// whatever the oxide's non-metal fraction is (21% for Y2O3), which lands the whole batch below the floor.
public static class OrderAmount
{
    public static (Order? Amount, string? Error) Compute(double ppm, double? batchMassKg, double metalLoading)
    {
        if (ppm <= 0) return (null, $"the ppm must be positive; it is {ppm}.");
        if (batchMassKg is not > 0)
            return (null, "the batch mass (kg) is missing or not positive. ppm is mg/kg, so an order amount " +
                          "needs a MASS — if you have a volume, multiply by the density and enter the mass.");
        // A zero loading divides by zero and puts an ∞ on a purchase order; IEEE-754 will not object.
        // A loading above 1 claims more metal than compound: impossible, and it UNDER-orders.
        if (metalLoading is <= 0 or > 1)
            return (null, $"the metal loading must be in (0, 1]; it is {metalLoading}. It is the mass " +
                          $"fraction of the marker element in the compound (Y2O3 is 0.787).");

        var elementMassMg = ppm * batchMassKg.Value;   // (mg/kg) × kg = mg
        return (new Order(elementMassMg, elementMassMg / metalLoading), null);
    }
}
```

`src/Smx.Domain/RatioSignature.cs`:

```csharp
using System.Globalization;

namespace Smx.Domain;

/// A code's identity: the ppm RATIO between its markers, normalised to the largest. Scale-invariant on
/// purpose — the same code dosed 2× heavier is the same code, and the reader identifies it by the ratio.
public static class RatioSignature
{
    public static string Of(IReadOnlyList<(string Element, double Ppm)> markers)
    {
        if (markers.Any(m => m.Ppm <= 0))
            throw new ArgumentOutOfRangeException(nameof(markers), "every marker's ppm must be positive");

        var max = markers.Max(m => m.Ppm);
        // Ordered by ppm descending, then by element — so the signature does not depend on input order,
        // which would make the same code render two ways and break any equality check downstream.
        var parts = markers
            .OrderByDescending(m => m.Ppm).ThenBy(m => m.Element, StringComparer.Ordinal)
            .ToList();
        return string.Join(":", parts.Select(m => m.Element)) + " = " +
               string.Join(":", parts.Select(m => (m.Ppm / max).ToString("0.00", CultureInfo.InvariantCulture)));
    }
}
```

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Change `elementMassMg / metalLoading` to `elementMassMg * metalLoading` → the first order test goes red. Drop the `> 1` guard → its test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/OrderAmount.cs src/Smx.Domain/RatioSignature.cs src/Smx.Domain.Tests
git commit -m "feat(domain): OrderAmount (compound mass, batch MASS, guarded loading) + RatioSignature"
```

---

## Task 4: `CompliantSet` — the false-pass boundary

**Read design decision 4.** This function decides which chemicals reach a customer's product.

**Files:**
- Create: `src/Smx.Domain/CompliantSet.cs`
- Modify: `src/Smx.Domain/Records/VerdictDoc.cs` (add a `Determinations` constants class)
- Test: `src/Smx.Domain.Tests/CompliantSetTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class CompliantSetTests
{
    private static VerdictDoc V(string cas, VerdictStatus overall, string? determination = null) => new()
    {
        Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
        Element = "Zr", Form = "oxide",
        Dimensions = [new("ElementGate", overall, [new Citation("reg", "x", "t")], 0.9, "r")],
        Determination = determination,
    };

    [Fact]
    public void Of_IncludesOnlyWhatTheOPERATORRecommended()
    {
        var set = CompliantSet.Of([
            V("cas-in",  VerdictStatus.Pass, Determinations.Recommended),
            V("cas-out", VerdictStatus.Pass, Determinations.Rejected),
        ]);
        Assert.Equal("cas-in", Assert.Single(set).Cas);
    }

    [Fact]
    public void Of_EXCLUDES_ACleanPassTheOperatorNeverDetermined()
    {
        // The strict rule (the user's call): nothing reaches a customer's product without a named human
        // saying yes to it. The Regulatory AGENT pre-fills a PROPOSAL so this is a confirmation, not an
        // authoring burden — but a proposal is not a determination.
        Assert.Empty(CompliantSet.Of([V("cas-1", VerdictStatus.Pass)]));
    }

    [Fact]
    public void Of_IGNORES_TheAgentsProposal_EntirelyAndOnPurpose()
    {
        // THE LAW-9 LINE, AT THE DOSING BOUNDARY. If a proposal could carry a substance into the compliant
        // set, the agent would be signing the regulatory gate through the back door. The two fields are
        // different fields, and only the operator's counts. This test failing is a design alarm.
        var proposed = V("cas-1", VerdictStatus.Pass);
        proposed.ProposedDetermination = Determinations.Recommended;
        proposed.ProposedReason = "the agent is very confident";

        Assert.Empty(CompliantSet.Of([proposed]));
    }

    [Fact]
    public void Of_HonoursAnOperatorOverrideOfAFail_BecauseThatIsWhatAHumanGateIsFor()
    {
        // The R.E. may overrule the agent's Fail — that is the point of a human gate, and the override
        // carries a mandatory reason (Plan 2). The signature is the authority.
        var overridden = V("cas-1", VerdictStatus.Fail, Determinations.Recommended);
        overridden.DeterminationReason = "the listing was superseded in the March amendment";
        Assert.Single(CompliantSet.Of([overridden]));
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement.**

In `src/Smx.Domain/Records/VerdictDoc.cs` — one literal, not three:

```csharp
/// The R.E.'s ruling on a substance × component. One constant, because the endpoint that records it, the
/// compliant-set filter that reads it, and the fakes must not drift apart on the string that decides
/// whether a chemical goes into a customer's product.
public static class Determinations
{
    public const string Recommended = "recommended";
    public const string Rejected = "rejected";
}
```

and on `VerdictDoc`, **next to** the existing operator fields:

```csharp
    // The AGENT's proposal (Plan 4). It exists so the operator CONFIRMS rather than authors — nothing more.
    // It is deliberately a SEPARATE field from Determination: if a proposal could be read as a
    // determination, the agent would be signing the regulatory gate through the back door. CompliantSet
    // ignores these two fields entirely, and a test pins that.
    public string? ProposedDetermination { get; set; }   // null | Determinations.*
    public string? ProposedReason { get; set; }
```

`src/Smx.Domain/CompliantSet.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// Which substances Dosing may dose — i.e. which chemicals may reach a customer's product.
///
/// The rule is strict and it is deliberately strict: ONLY what the OPERATOR recommended. Not what the agent
/// proposed (that is a separate field, and reading it here would let the agent sign the regulatory gate by
/// the back door). Not a clean Pass nobody spoke about (silence is not consent). An operator override of a
/// Fail IS honoured — that is what a human gate is for, and it carries a mandatory reason.
///
/// The Regulatory agent pre-fills ProposedDetermination precisely so this strictness costs the operator a
/// confirmation rather than an authoring burden.
public static class CompliantSet
{
    public static IReadOnlyList<VerdictDoc> Of(IReadOnlyList<VerdictDoc> verdicts) =>
        verdicts.Where(v => v.Determination == Determinations.Recommended).ToList();
}
```

Also replace the bare `"recommended" or "rejected"` literals in `src/Smx.Backend/Api/ProjectEndpoints.cs:64` with the constants.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Make `Of` also accept `ProposedDetermination` → `Of_IGNORES_TheAgentsProposal` **must** go red. Restore. *If it does not go red, stop and report — the guard is theatre.*
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain src/Smx.Backend/Api/ProjectEndpoints.cs src/Smx.Domain.Tests
git commit -m "feat(domain): CompliantSet — only the operator's recommendation gets dosed, never the agent's proposal"
```

---

## Task 5: `SubstancePropertyDoc` — metal loading as cross-project knowledge

**Read design decision 2.**

**Files:**
- Create: `src/Smx.Domain/Records/SubstancePropertyDoc.cs`
- Modify: `src/Smx.Domain/Records/KnowledgeIds.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void SubstanceProperty_RoundTrips_AndIsKeyedByCasSoItIsReusedAcrossProjects()
    {
        var doc = new SubstancePropertyDoc
        {
            Id = KnowledgeIds.SubstanceProperty("1314-36-9"), Cas = "1314-36-9",
            Element = "Y", Form = "oxide", MetalLoading = 0.787,
            Basis = "Y2O3, M(Y)=88.906, M(Y2O3)=225.81 → 2×88.906/225.81",
            EnteredAt = "2026-07-14T10:00:00.0000000+00:00",
        };
        var json = JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"type\":\"substance-property\"", json);
        Assert.Equal("substance-property|1314-36-9", doc.Id);
        Assert.Equal(0.787, JsonSerializer.Deserialize<SubstancePropertyDoc>(json, Json.Options)!.MetalLoading);
    }
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement** `src/Smx.Domain/Records/SubstancePropertyDoc.cs`:

```csharp
namespace Smx.Domain.Records;

/// The metal loading of a compound — the mass fraction of the marker ELEMENT in it (Y2O3 is 0.787 Y).
/// Order amounts are computed from it, and it exists in no catalog we have: the operator enters it once,
/// and it is keyed by CAS in the CROSS-PROJECT knowledge layer, so the next project that meets this
/// compound never asks again. That is the knowledge layer doing what it exists for.
///
/// `Basis` is mandatory and is the operator's own words (a formula, a supplier spec sheet, an assay). It is
/// what makes the number checkable — the same discipline as a Learned Conclusion's provenance.
///
/// NOT on the per-project record bus: this lives in the `substance-properties` container, PK /cas.
public sealed class SubstancePropertyDoc
{
    public required string Id { get; set; }
    public required string Cas { get; set; }              // partition key
    public string Type { get; set; } = KnowledgeTypes.SubstanceProperty;
    public required string Element { get; set; }
    public required string Form { get; set; }
    /// Mass fraction in (0, 1]. Validated at the endpoint AND at OrderAmount: a 0 is an infinite order, and
    /// a value above 1 is physically impossible and silently UNDER-orders.
    public required double MetalLoading { get; set; }
    public required string Basis { get; set; }
    public required string EnteredAt { get; set; }        // "O" format
}
```

In `KnowledgeIds.cs`, add to `KnowledgeTypes`:
```csharp
    public const string SubstanceProperty = "substance-property";
```
and to `KnowledgeIds`:
```csharp
    public static string SubstanceProperty(string cas) => $"substance-property|{cas}";
```
and to `KnowledgeKinds` (Dosing revisions need a conclusion kind — see Task 8):
```csharp
    public const string Dosing = "dosing";
```

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Domain.Tests
git commit -m "feat(domain): SubstancePropertyDoc — metal loading, entered once, reused across every project"
```

---

## Task 6: Persist substance properties (Cosmos + fake + **infra**)

**Files:**
- Modify: `src/Smx.Domain/IKnowledgeStore.cs`, `src/Smx.Infrastructure/CosmosKnowledgeStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryKnowledgeStore.cs`, `infra/modules/data.bicep`, `infra/single-rg/modules/data.bicep`
- Test: `src/Smx.Domain.Tests/InMemoryKnowledgeStoreTests.cs`, `src/Smx.Orchestrator.Tests/CosmosQueryTextTests.cs`

> **Fake↔prod parity is a hard requirement**, and the fake must **deep-copy** like `InMemoryRecordStore` does (round-trip through `Json.Options` on read and write). A fake that hands out live references certifies a read-modify-write that production does not have.
>
> ⚠️ **Infra changes.** Both `data.bicep` twins gain the container. They must stay byte-identical in that block — **fix one, fix the other**.

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Domain.Tests/InMemoryKnowledgeStoreTests.cs`:

```csharp
public class SubstancePropertyStoreTests
{
    private static SubstancePropertyDoc Y2O3 => new()
    {
        Id = KnowledgeIds.SubstanceProperty("1314-36-9"), Cas = "1314-36-9", Element = "Y", Form = "oxide",
        MetalLoading = 0.787, Basis = "2×M(Y)/M(Y2O3)", EnteredAt = "2026-07-14T10:00:00.0000000+00:00",
    };

    [Fact]
    public async Task Get_OnAColdStore_ReturnsNull_NotAnException()
    {
        // Cold-start safety: the very first project has an empty knowledge layer, and Dosing must PARK on
        // that, not crash on it.
        Assert.Null(await new InMemoryKnowledgeStore().GetSubstancePropertyAsync("1314-36-9"));
    }

    [Fact]
    public async Task Upsert_ThenGet_RoundTripsByCas()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertSubstancePropertyAsync(Y2O3);
        Assert.Equal(0.787, (await store.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading);
    }

    [Fact]
    public async Task Upsert_ReplacesByCas_SoACorrectionOverwritesRatherThanDuplicates()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertSubstancePropertyAsync(Y2O3);
        var corrected = Y2O3; corrected.MetalLoading = 0.7874; corrected.Basis = "recomputed from IUPAC 2021 masses";
        await store.UpsertSubstancePropertyAsync(corrected);
        Assert.Equal(0.7874, (await store.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading);
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement.**

`IKnowledgeStore`:
```csharp
    /// Metal loading, keyed by CAS and shared by every project (design §6). Null on a cold store —
    /// Dosing parks and asks the operator, and the answer is kept forever.
    Task<SubstancePropertyDoc?> GetSubstancePropertyAsync(string cas, CancellationToken ct = default);
    Task UpsertSubstancePropertyAsync(SubstancePropertyDoc doc, CancellationToken ct = default);
```

`CosmosKnowledgeStore` — mirror `GetMsdsAsync`/`UpsertMsdsAsync` exactly (they are the closest analogue: a point-read on a `/cas` partition, 404 → null). Take the new `Container` in the constructor the same way the others are taken.

`InMemoryKnowledgeStore` — the twin, **deep-copying** through `Json.Options` on both write and read, matching how `InMemoryRecordStore` does it.

**Infra — both twins.** In the knowledge-container list (next to `marker-library` and `msds-registry`):
```bicep
  { name: 'substance-properties', pk: '/cas' }
```
Then register the container in DI wherever `msds-registry` is registered (`Smx.Backend/Program.cs` and `Smx.Orchestrator/Program.cs` — check both).

- [ ] **Step 4: Verify.**

```bash
dotnet test src/Smx.Backend.sln
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
diff infra/modules/data.bicep infra/single-rg/modules/data.bicep   # expect only the documented divergences
```

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Infrastructure src/Smx.Domain.Tests src/Smx.Backend src/Smx.Orchestrator infra/
git commit -m "feat(knowledge): substance-properties container — metal loading persisted across projects (both infra twins)"
```

---

## Task 7: The Regulatory agent proposes a determination

**Read design decision 4.** The proposal exists so the operator *confirms* rather than *authors*. It must never be mistaken for the operator's signature.

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/RegulatoryAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/RegulatoryAgentTests.cs` (add), `src/Smx.Backend.Tests/` (the gate tests)

The real signature is `RegulatoryAgent.RunAsync(ISmxAgent agent, ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct)` → `Task<AgentRunResult<VerdictDoc>>`. **Read `RegulatoryAgentTests` and reuse its `Constraints()` / `Candidate()` helpers rather than writing new ones.**

- [ ] **Step 1: Write the failing tests**

```csharp
    private const string PassDimension =
        """{"dimension":"ElementGate","status":"Pass","citations":[{"source":"reg","reference":"REACH XVII","retrievedAt":"2026-07-14"}],"confidence":0.9,"rationale":"not listed"}""";

    [Fact]
    public async Task Regulatory_ProposesADetermination_WithAReason()
    {
        // The proposal is what turns "the operator must determine EVERY cell" from an authoring burden into
        // a confirmation. The agent does the reading; the human does the deciding.
        var agent = new ScriptedAgent($$"""
            {"dimensions":[{{PassDimension}}],
             "proposedDetermination":"recommended","proposedReason":"clean on all four dimensions"}
            """);

        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(Determinations.Recommended, result.Output!.ProposedDetermination);
        Assert.Equal("clean on all four dimensions", result.Output.ProposedReason);
        Assert.Null(result.Output.Determination);            // THE OPERATOR HAS NOT SPOKEN.
        Assert.Null(result.Output.DeterminationReason);
    }

    [Fact]
    public async Task Regulatory_CannotWriteTheOperatorsDeterminationField_EvenWhenTheModelTries()
    {
        // A model emitting `"determination":"recommended"` must NOT have it land on the operator's field.
        // That field is a SIGNATURE. If this test fails, the agent can sign the regulatory gate — which is
        // the single thing Law 9 exists to make impossible. It is a design alarm, not a test to adjust.
        var agent = new ScriptedAgent($$"""
            {"dimensions":[{{PassDimension}}],
             "determination":"recommended","determinationReason":"I hereby approve this"}
            """);

        var result = await RegulatoryAgent.RunAsync(agent, Constraints(), Candidate(), null, default);

        // The DTO has no such members, so STJ discards them: the guard is structural, not a check.
        Assert.Null(result.Output?.Determination);
        Assert.Null(result.Output?.DeterminationReason);
    }

    [Fact]
    public void Validate_RejectsAProposalWithNoReason()
    {
        // Plan 2's law, extended to the proposal: every determination — recommend OR reject — carries a
        // reason. A bare "recommended" is precisely the rubber stamp the whole design is against.
        var output = new RegulatoryOutput
        {
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("reg", "x", "t")], 0.9, "r")],
            ProposedDetermination = Determinations.Recommended,
            ProposedReason = "   ",
        };
        Assert.Contains("reason", RegulatoryAgent.Validate(output)!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RejectsAProposalThatIsNeitherRecommendedNorRejected()
    {
        var output = new RegulatoryOutput
        {
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("reg", "x", "t")], 0.9, "r")],
            ProposedDetermination = "probably fine",
            ProposedReason = "looks ok",
        };
        Assert.NotNull(RegulatoryAgent.Validate(output));
    }
```

(`RegulatoryAgent.Validate` may already exist under another name — mirror whatever `RegulatoryAgent` actually calls from `ValidatedAgentRunner`, and make it `internal static string?` if it is not already, so these can test it directly.)

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.** Add `ProposedDetermination` + `ProposedReason` to the agent's **output DTO** (not the operator's fields), append to the agent's `Instructions`:

```
        Finally, PROPOSE a determination for this substance × component:
          "proposedDetermination": "recommended" | "rejected"
          "proposedReason":        why, in one sentence, citing what you relied on.
        Both are MANDATORY, including for a rejection. You are PROPOSING, not deciding: the Regulatory
        Expert reviews your proposal and signs. Never claim to have approved or rejected anything.
```

and in the agent's `Validate`, reject a proposal with a blank reason or a value outside `Determinations.*`.

**Map ONLY the proposal fields onto the `VerdictDoc`.** The operator's `Determination` / `DeterminationReason` are never written by this path — that is the guard, and it is enforced by construction (the DTO simply has no such fields), not by a check.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Map the DTO's proposal onto `verdict.Determination` → `Regulatory_CannotWriteTheOperatorsDeterminationField` and `CompliantSetTests.Of_IGNORES_TheAgentsProposal` must both go red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): Regulatory proposes a determination — the operator still signs it"
```

---

## Task 8: The stages — `dosing` and `cost`, and every enumeration that must agree

**This task is a single commit on purpose.** Adding a stage constant makes four things live at once; splitting them ships a stage that is half-wired.

**Files:**
- Modify: `src/Smx.Domain/Records/RecordIds.cs`, `src/Smx.Domain/Records/ProjectDoc.cs`, `src/Smx.Domain/RevisionEffects.cs`, `src/Smx.Orchestrator/Agents/ToolBox.cs`, `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Test: `src/Smx.Domain.Tests/RevisionEffectsTests.cs`, `src/Smx.Orchestrator.Tests/ChatAgentTests.cs`, `src/Smx.Backend.Tests/ChatEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Smx.Domain.Tests/RevisionEffectsTests.cs
    [Fact]
    public void Dosing_IsRevisable_ButDoesNotVoidTheRegulatoryGate()
    {
        // Dosing is DOWNSTREAM of the gate: it consumes the compliant set, it does not change it. Re-running
        // it cannot invalidate the operator's regulatory signature, so voiding the gate here would force a
        // pointless re-signature — and a gate the operator learns to re-sign reflexively is a rubber stamp.
        Assert.True(RevisionEffects.IsRevisable(Stages.Dosing));
        Assert.False(RevisionEffects.BreaksRegulatoryGate(Stages.Dosing));
        Assert.Equal(KnowledgeKinds.Dosing, RevisionEffects.ConclusionKind(Stages.Dosing));
    }

    [Fact]
    public void Cost_IsNotRevisable_BecauseItIsDeterministic()
    {
        // There is no judgment in Cost to argue with. To change a cost you change its inputs (the code) or
        // the catalog. Revising a table lookup is a category error.
        Assert.False(RevisionEffects.IsRevisable(Stages.Cost));
        Assert.Throws<ArgumentOutOfRangeException>(() => RevisionEffects.BreaksRegulatoryGate(Stages.Cost));
    }
```

```csharp
// Smx.Orchestrator.Tests/ChatAgentTests.cs — extend the existing exact-tool-set theory
    [InlineData(Stages.Dosing, new[] { "apply_revision", "detection_floor", "order_amount",
                                       "search_learned_conclusions", "search_reference" })]
    [InlineData(Stages.Cost,   new string[0])]   // Cost is deterministic: a chat turn on it holds NO tools
```

- [ ] **Step 2: Run to verify they fail** (and note which *existing* tests go red — the `Stages.All` reflection test and the `ProjectDoc.Create` tripwire **should**, and that is the tripwire working).

- [ ] **Step 3: Implement — all of it, together.**

`RecordIds.cs`:
```csharp
    public const string Dosing = "dosing";
    public const string Cost = "cost";
    public static readonly string[] All = [Intake, Discovery, Regulatory, Matrix, Dosing, Cost];
```
and `RecordTypes`:
```csharp
    public const string Dosing = "dosing";
    public const string Cost = "cost";
```
and `RecordIds`:
```csharp
    public static string Dosing(string projectId) => $"{projectId}|dosing";
    public static string Cost(string projectId) => $"{projectId}|cost";
```

`ProjectDoc.Create` — add both stages to the dictionary (the tripwire test forces this).

`RevisionEffects`:
- `IsRevisable` → `stage is Stages.Discovery or Stages.Regulatory or Stages.Dosing`
- `BreaksRegulatoryGate` → still `stage is Stages.Discovery or Stages.Regulatory` (so Dosing answers **false**), keeping the throw for non-revisable stages.
- `ConclusionKind` → `Stages.Dosing => KnowledgeKinds.Dosing`
- Update the XML docs, which currently say "Plan 4's dosing and cost join this list when they arrive."

`ToolBox.ReadToolsFor` → add the two arms. `DosingTools()` is defined **here**, in its first, honest form — the two retrieval tools Dosing definitely has. Task 10 adds the two calculators to it:

```csharp
    /// The Dosing stage's read tools. Task 10 adds the deterministic calculators (detection_floor,
    /// order_amount); these two are the retrieval half, and the §6 knowledge-layer read point.
    public IList<AITool> DosingTools() =>
    [
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Prior ppm and dosing findings from earlier projects, with the reasons they were recorded."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "The reference corpus — formulation-impact basis, application notes, typical loadings."),
    ];
```
```csharp
        Stages.Dosing => DosingTools(),
        Stages.Cost => [],          // Cost is deterministic — a chat turn on it looks things up nowhere
```

`StageDispatcher.StageInputsJsonAsync` → two arms. **Task 9 adds `GetDosingAsync` and Task 17 adds `GetCostAsync`, so this task must land AFTER both** — reorder if you are executing strictly in sequence, or fold these two lines into Task 9 and Task 17 respectively. **Do not stub them to `"{}"`**: that is precisely the "confident conversation about nothing" this task exists to prevent.

```csharp
        Stages.Dosing => JsonSerializer.Serialize(await store.GetDosingAsync(projectId, ct), Json.Options),
        Stages.Cost => JsonSerializer.Serialize(await store.GetCostAsync(projectId, ct), Json.Options),
```

- [ ] **Step 4: Run the full suite.**
- [ ] **Step 5: Mutation-test.** Remove `Stages.Dosing` from `ProjectDoc.Create` → the tripwire goes red. Make `BreaksRegulatoryGate(Dosing)` return `true` → its test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain src/Smx.Orchestrator src/Smx.Domain.Tests src/Smx.Orchestrator.Tests src/Smx.Backend.Tests
git commit -m "feat(domain): the dosing and cost stages — and every enumeration that must agree about them"
```

---

## Task 9: `DosingDoc` + persistence

**Files:**
- Create: `src/Smx.Domain/Records/DosingDoc.cs`
- Modify: `src/Smx.Domain/IRecordStore.cs`, `src/Smx.Infrastructure/CosmosRecordStore.cs`, `src/Smx.Domain.Tests/Fakes/InMemoryRecordStore.cs`, `src/Smx.Orchestrator/Dispatch/RecordDocRouter.cs`
- Test: `src/Smx.Domain.Tests/RecordDocsTests.cs`, `src/Smx.Domain.Tests/InMemoryRecordStoreTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void DosingDoc_RoundTrips_WithItsTypeDiscriminatorOnTheWire()
    {
        var d = new DosingDoc
        {
            Id = RecordIds.Dosing("proj-1"), ProjectId = "proj-1",
            Windows = [new PpmWindow("bottle", "1314-36-9", "Y",
                new Bound(10.0, "Vanta M: LOD 2 ppm over bg 4 ppm; bg + 3σ", "measured", 1.0),
                new Bound(500.0, "no regulatory cap found; formulation-impact estimate", "estimate", 0.4),
                25.0, 34.0)],
            Codes = [new MarkerCode("bottle",
                [new CodeMarker("1314-36-9", "Y", 25.0, 0.787, 6250.0, 7941.55)],
                "Y = 1.00", "two markers give a checkable ratio")],
            GeneratedAt = "2026-07-14T10:00:00.0000000+00:00",
        };
        var json = JsonSerializer.Serialize(d, Json.Options);
        Assert.Contains("\"type\":\"dosing\"", json);

        var back = JsonSerializer.Deserialize<DosingDoc>(json, Json.Options)!;
        Assert.Equal("measured", Assert.Single(back.Windows).Floor.Basis2);
        Assert.Equal(7941.55, Assert.Single(Assert.Single(back.Codes).Markers).CompoundMassMg);
    }
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement** `src/Smx.Domain/Records/DosingDoc.cs`:

```csharp
namespace Smx.Domain.Records;

/// One end of a ppm window, with WHERE IT CAME FROM. The UX spec requires basis + confidence per bound,
/// because the two ends are not equally trustworthy: the floor is MEASURED (confidence 1.0), while an upper
/// bound with no regulatory cap is an ESTIMATE that is known to run low. Rendering them alike would invite
/// the operator to trust a guess as much as a measurement.
///
/// `Kind` is "measured" | "regulatory" | "estimate". A "measured" bound is never produced by the agent.
public sealed record Bound(double Ppm, string Basis, string Kind, double Confidence)
{
    /// Convenience alias used by the tests for the discriminating field.
    public string Basis2 => Kind;
}

/// The dosable range for one substance in one component. The RECOMMENDED value must sit strictly inside
/// (Floor, Upper), with headroom — and a quantification objective needs MORE headroom than mere detection.
public sealed record PpmWindow(
    string ComponentId, string Cas, string Element,
    Bound Floor, Bound Upper, double RecommendedPpm, double QuantificationPpm);

/// One marker inside a code, with the order amount that follows from its ppm.
public sealed record CodeMarker(
    string Cas, string Element, double Ppm, double MetalLoading, double ElementMassMg, double CompoundMassMg);

/// A code: 2–3 markers in ONE component, identified by their ppm RATIO. Per component — there is no
/// product-wide marker.
public sealed record MarkerCode(
    string ComponentId, IReadOnlyList<CodeMarker> Markers, string RatioSignature, string Rationale);

public sealed class DosingDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Dosing;
    public List<PpmWindow> Windows { get; set; } = [];
    public List<MarkerCode> Codes { get; set; } = [];
    /// The soft code-finalization checkpoint (UX §4.5). A REVIEW NOTE, not a gate: it records that the
    /// PL/VP/physics review happened. It does not block, and it must never be made to block — the hard
    /// gates are Regulatory and VP, and adding a third would dilute what a signature means.
    public string? ReviewNote { get; set; }
    public string? ReviewedAt { get; set; }
    public required string GeneratedAt { get; set; }
}
```

Add `GetDosingAsync` / `UpsertDosingAsync` to `IRecordStore` + both stores (a point-read on the project partition; mirror `GetMatrixAsync`). Add the router arm:
```csharp
    RecordTypes.Dosing => element.Deserialize<DosingDoc>(Json.Options),
```

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Infrastructure src/Smx.Orchestrator src/Smx.Domain.Tests
git commit -m "feat(domain): DosingDoc — ppm windows with a basis per bound, codes with a ratio signature"
```

---

## Task 10: The Dosing tools — the calculators, exposed to the agent

**The agent does NOT do arithmetic.** It calls the deterministic calculators and picks a ppm *inside* the window they return. A model that could compute its own floor could compute one that is too low.

**Files:**
- Modify: `src/Smx.Orchestrator/Agents/ToolBox.cs`
- Test: `src/Smx.Orchestrator.Tests/ToolBoxTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task DetectionFloorTool_ReturnsTheComputedFloor_AndItsBasis()
    {
        // Invoked through the REAL AIFunction, never the C# method — a schema that lies has shipped here
        // before (Plan 3a, a tool dead on arrival for a full release).
        var tool = Tools().DosingTools().Cast<AIFunction>().Single(t => t.Name == "detection_floor");

        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["componentId"] = "bottle", ["element"] = "Zr",
        }))?.ToString() ?? "";

        Assert.Contains("8.5", result);              // 4.0 bg + 3 × 1.5 LOD
        Assert.Contains("Vanta", result);            // the basis names the device
    }

    [Fact]
    public async Task DetectionFloorTool_OnAMissingMeasurement_SaysSo_RatherThanReturningANumber()
    {
        var tool = Tools().DosingTools().Cast<AIFunction>().Single(t => t.Name == "detection_floor");
        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["componentId"] = "bottle", ["element"] = "Xx",
        }))?.ToString() ?? "";

        Assert.Contains("no measured background", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"floor\"", result);   // no number, of any kind, in any field
    }

    [Fact]
    public async Task OrderAmountTool_RefusesAnUnknownMetalLoading_RatherThanAssumeOne()
    {
        // The loading is not in any catalog. If the tool guessed 1.0 ("it's pure metal"), an oxide order
        // would be short by ~21% and the whole batch would land below the floor.
        var tool = Tools().DosingTools().Cast<AIFunction>().Single(t => t.Name == "order_amount");
        var result = (await tool.InvokeAsync(new AIFunctionArguments
        {
            ["cas"] = "cas-unknown", ["ppm"] = 25.0, ["componentId"] = "bottle",
        }))?.ToString() ?? "";

        Assert.Contains("metal loading", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("operator", result, StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement.** `ToolBox` gains the constraints + knowledge store it needs (it already takes `IKnowledgeStore`; it will need the project's `ConstraintsDoc`, so pass it per-turn — **`DosingTools()` is built per stage-run, closed over the project's constraints, exactly as `ChatTools` is closed over its project**). Add:

```csharp
    /// The Dosing stage's tools. The two calculators are DETERMINISTIC and the agent may not do their
    /// arithmetic itself: a model that computes its own floor can compute one that is too low, and a low
    /// floor ships a marker nobody can detect. It calls these, and picks a ppm INSIDE what they return.
    public IList<AITool> DosingTools() =>
    [
        AIFunctionFactory.Create(DetectionFloorAsync, "detection_floor",
            "The ppm detection and quantification floors for an element in a component, computed from the " +
            "physicist's MEASURED background and the deployment device's LOD. If the measurement is missing " +
            "this returns an error and NO number — say so; never estimate a floor."),
        AIFunctionFactory.Create(OrderAmountAsync, "order_amount",
            "How many grams of the COMPOUND to order for a given ppm in a component, from the batch mass and " +
            "the substance's metal loading. If the loading is unknown this returns an error naming the CAS — " +
            "the operator must supply it; never assume one."),
        AIFunctionFactory.Create(SearchLearnedConclusionsAsync, "search_learned_conclusions",
            "Prior ppm and dosing findings from earlier projects, with their reasons."),
        AIFunctionFactory.Create(SearchReferenceAsync, "search_reference",
            "The reference corpus — formulation-impact basis, application notes, typical loadings."),
    ];
```

with both calculator methods returning a JSON string wrapping the `(value, error)` from `DetectionFloor.Compute` / `OrderAmount.Compute`, and the `order_amount` path reading the loading from `IKnowledgeStore.GetSubstancePropertyAsync(cas)` and returning the *"the operator must supply it"* error when it is null.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Make `order_amount` default an unknown loading to `1.0` → its test goes red. Restore. *This is the mutation that matters most in this task.*
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(agents): the Dosing tools — the agent picks a ppm, the code computes the floor"
```

---

## Task 11: `DosingAgent` + the invariants that fence it

**Files:**
- Create: `src/Smx.Orchestrator/Agents/DosingAgent.cs`
- Test: `src/Smx.Orchestrator.Tests/DosingAgentTests.cs`

> **`Validate` returns an error STRING; it does not throw.** `ValidatedAgentRunner` feeds that string back to the model, retries twice, and then returns `AgentRunResult.NeedsReview(error)`. So the invariants are tested **directly against the pure `Validate`** — fast, exhaustive, no agent needed — exactly as `DiscoveryAgentTests` tests `DiscoveryAgent.Validate`. Mark it `internal static` like Discovery's.

- [ ] **Step 1: Write the failing tests**

```csharp
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Tests;

public class DosingAgentTests
{
    private static readonly Floor ZrFloor = new(8.5, 19.0, "Vanta M: LOD 1.5 over bg 4.0");

    /// The floors the CODE computed, keyed as the agent's Validate receives them.
    private static Dictionary<(string, string), Floor> Floors => new() { [("bottle", "Zr")] = ZrFloor };

    private static readonly VerdictDoc[] Compliant =
    [
        new() { Id = "v1", ProjectId = "p1", Cas = "cas-ok", ComponentId = "bottle", Element = "Zr",
                Form = "oxide", Determination = Determinations.Recommended },
        new() { Id = "v2", ProjectId = "p1", Cas = "cas-ok2", ComponentId = "bottle", Element = "Y",
                Form = "oxide", Determination = Determinations.Recommended },
    ];

    private static DosingOutput Output(double recommended = 25.0, double upper = 500.0,
        string upperKind = "estimate", string[]? codeCas = null, string codeComponent = "bottle") => new()
    {
        Windows =
        [
            new DosingWindowOutput { ComponentId = "bottle", Cas = "cas-ok", Element = "Zr",
                RecommendedPpm = recommended, QuantificationPpm = 34.0,
                UpperPpm = upper, UpperBasis = "no regulatory cap found", UpperKind = upperKind,
                UpperConfidence = 0.4, Rationale = "margin above the floor for a quantification objective" },
        ],
        Codes =
        [
            new DosingCodeOutput { ComponentId = codeComponent, Cas = codeCas ?? ["cas-ok", "cas-ok2"],
                Rationale = "two markers give a checkable ratio" },
        ],
    };

    [Fact]
    public void Validate_RejectsARecommendedPpmBelowTheDetectionFloor()
    {
        // THE HEADLINE INVARIANT. A ppm under the floor is a marker that cannot be read in the field, and
        // there is no downstream check that catches it. The agent may propose it; the code refuses it.
        var error = DosingAgent.Validate(Output(recommended: 5.0), Floors, Compliant);
        Assert.Contains("below the detection floor", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8.5", error!);          // it names the floor it violated
    }

    [Fact]
    public void Validate_RejectsARecommendedPpmAtOrAboveTheUpperBound() =>
        Assert.Contains("upper", DosingAgent.Validate(Output(recommended: 600.0), Floors, Compliant)!,
            StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Validate_RejectsACodeContainingASubstanceThatIsNotInTheCompliantSet()
    {
        // THE FALSE-PASS GUARD. A code goes to procurement. If a rejected substance can ride into one, the
        // regulatory gate has been bypassed entirely — by the stage that runs right after it.
        var error = DosingAgent.Validate(Output(codeCas: ["cas-ok", "cas-rejected"]), Floors, Compliant);
        Assert.Contains("cas-rejected", error!);
        Assert.Contains("not in the compliant set", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(new[] { "cas-ok" })]                              // 1 marker: no ratio, so not a code at all
    [InlineData(new[] { "cas-ok", "cas-ok2", "cas-ok", "cas-ok2" })] // 4: beyond what the reader resolves
    public void Validate_RejectsACodeWithFewerThanTwoOrMoreThanThreeMarkers(string[] cas) =>
        Assert.NotNull(DosingAgent.Validate(Output(codeCas: cas), Floors, Compliant));

    [Fact]
    public void Validate_RejectsACodeWhoseMarkersAreNotAllInItsOwnComponent()
    {
        // Codes are PER COMPONENT (interaction law 1 — there is no product-wide marker). A code naming the
        // label but built from the bottle's substances is dosed into neither.
        var error = DosingAgent.Validate(Output(codeComponent: "label"), Floors, Compliant);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_RejectsAnUpperBoundTheModelCallsMEASURED()
    {
        // "measured" is a claim only the physicist's data can make. An agent that could label its own
        // estimate as measured would launder a guess into the one field the operator trusts absolutely.
        Assert.Contains("measured", DosingAgent.Validate(Output(upperKind: "measured"), Floors, Compliant)!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AcceptsAWellFormedOutput() =>
        Assert.Null(DosingAgent.Validate(Output(), Floors, Compliant));
}
```

And two tests that the **code**, not the model, owns the arithmetic — these run the real `RunAsync` against a `ScriptedAgent`, mirroring `DiscoveryAgentTests`:

```csharp
    [Fact]
    public async Task Run_ComputesTheRatioSignatureItself_NotFromTheModelsArithmetic()
    {
        // The signature IS the code's identity — what a reader matches a seized sample against. It is
        // arithmetic, so code owns it. A model that mis-multiplies mints a code that identifies as a
        // different code, and the field reader would call a genuine product counterfeit.
        var agent = new ScriptedAgent(JsonSerializer.Serialize(new
        {
            windows = new[] { new { componentId = "bottle", cas = "cas-ok", element = "Zr",
                recommendedPpm = 20.0, quantificationPpm = 34.0, upperPpm = 500.0,
                upperBasis = "no cap", upperKind = "estimate", upperConfidence = 0.4, rationale = "r" },
                new { componentId = "bottle", cas = "cas-ok2", element = "Y",
                recommendedPpm = 10.0, quantificationPpm = 20.0, upperPpm = 500.0,
                upperBasis = "no cap", upperKind = "estimate", upperConfidence = 0.4, rationale = "r" } },
            codes = new[] { new { componentId = "bottle", cas = new[] { "cas-ok", "cas-ok2" },
                rationale = "ratio is checkable" } },
        }, Json.Options));

        var result = await DosingAgent.RunAsync(agent, Constraints(), Compliant, Floors, Loadings(), null, default);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("Zr:Y = 1.00:0.50", Assert.Single(result.Output!.Codes).RatioSignature);
    }

    [Fact]
    public async Task Run_ComputesOrderAmountsItself_FromTheOperatorEnteredLoading()
    {
        var result = await DosingAgent.RunAsync(/* same ScriptedAgent as above */);
        var marker = Assert.Single(result.Output!.Codes).Markers.Single(m => m.Cas == "cas-ok");

        // 20 ppm × 250 kg = 5000 mg of Zr; at a 0.74 loading that is 5000/0.74 mg of the oxide.
        Assert.Equal(5000.0, marker.ElementMassMg, 3);
        Assert.Equal(5000.0 / 0.74, marker.CompoundMassMg, 3);
    }
```

with helpers `Constraints()` (one `bottle` component, `BatchMassKg = 250.0`) and `Loadings()` (`{ ["cas-ok"] = 0.74, ["cas-ok2"] = 0.787 }`).

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement** `src/Smx.Orchestrator/Agents/DosingAgent.cs`.

The output DTO carries **only judgment** — the model never returns a floor, a ratio, or an order amount:

```csharp
public sealed class DosingWindowOutput
{
    public string ComponentId { get; set; } = "";
    public string Cas { get; set; } = "";
    public string Element { get; set; } = "";
    public double RecommendedPpm { get; set; }
    public double QuantificationPpm { get; set; }
    public double UpperPpm { get; set; }
    public string UpperBasis { get; set; } = "";
    public string UpperKind { get; set; } = "";      // "regulatory" | "estimate" — NEVER "measured"
    public double UpperConfidence { get; set; }
    public string Rationale { get; set; } = "";
}
public sealed class DosingCodeOutput
{
    public string ComponentId { get; set; } = "";
    public IReadOnlyList<string> Cas { get; set; } = [];
    public string Rationale { get; set; } = "";
}
public sealed class DosingOutput
{
    public List<DosingWindowOutput> Windows { get; set; } = [];
    public List<DosingCodeOutput> Codes { get; set; } = [];
}
```

`RunAsync` mirrors `DiscoveryAgent.RunAsync` exactly:

```csharp
    public static async Task<AgentRunResult<DosingDoc>> RunAsync(
        ISmxAgent agent, ConstraintsDoc constraints,
        IReadOnlyList<VerdictDoc> compliant,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyDictionary<string, double> loadings,     // cas → metal loading (operator-entered)
        RevisionDoc? revision, CancellationToken ct)
```

It serializes the compliant set + the **already-computed floors** into the prompt, runs `ValidatedAgentRunner.RunAsync<DosingOutput>(agent, task, o => Validate(o, floors, compliant), ct)`, and on success **builds the `DosingDoc` itself**:

- `Floor` bound ← `floors[(componentId, element)]`, `Kind = "measured"`, `Confidence = 1.0` — **never from the model**
- `Upper` bound ← the model's `UpperPpm/UpperBasis/UpperKind/UpperConfidence`
- `RatioSignature` ← `RatioSignature.Of(...)` over the code's markers' recommended ppms
- each `CodeMarker`'s masses ← `OrderAmount.Compute(ppm, component.BatchMassKg, loadings[cas])`, returning `NeedsReview` on any error rather than writing a NaN

`Validate(DosingOutput o, IReadOnlyDictionary<(string,string), Floor> floors, IReadOnlyList<VerdictDoc> compliant)` — `internal static string?`, returning the **first** violation, naming the offender:

- a window whose `(ComponentId, Element)` has no computed floor → *"no floor was computed for …"*
- `RecommendedPpm <= floor.DetectionPpm` → *"… is below the detection floor (8.5 ppm) — a marker dosed under its floor cannot be read"*
- `RecommendedPpm >= UpperPpm` → *"… is at or above the upper bound"*
- `UpperKind` not in `{"regulatory", "estimate"}` → and say **explicitly** that `"measured"` is not a claim the agent may make
- a code with `Cas.Count is < 2 or > 3`
- a code CAS not in `CompliantSet` → *"'{cas}' is not in the compliant set"*
- a code CAS whose verdict's `ComponentId` differs from the code's `ComponentId`
- a code CAS with no window

`Instructions` must say plainly:

```
        Call detection_floor for every (component, element) — NEVER compute a floor yourself. A ppm below
        the floor is a marker nobody can read in the field, and nothing downstream will catch it.
        Your recommended ppm must sit strictly INSIDE (floor, upper), with margin above the floor; a
        quantification objective needs MORE headroom than mere detection.
        The upper bound is a regulatory cap when Regulatory found one ("kind":"regulatory"), otherwise a
        formulation-impact estimate ("kind":"estimate") — say which, and give your confidence. You may NOT
        call an upper bound "measured": only the physicist's data is measured.
        Codes are 2-3 markers, all from ONE component and all from the compliant set you were given.
        Reply with ONLY a JSON object: { "windows": [...], "codes": [...] }
```

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test EACH invariant** (remove the check → its test goes red; restore). All six.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator/Agents/DosingAgent.cs src/Smx.Orchestrator.Tests/DosingAgentTests.cs
git commit -m "feat(agents): DosingAgent — it picks the ppm; the code owns the floor, the ratio and the arithmetic"
```

---

## Task 12: Dispatch — `OnMatrixAsync` runs Dosing, and it PARKS rather than guess

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`, `src/Smx.Orchestrator/Dispatch/AgentRuns.cs` (+ `IAgentRuns`), `src/Smx.Orchestrator.Tests/Fakes/FakeAgentRuns.cs`
- Test: `src/Smx.Orchestrator.Tests/DosingDispatchTests.cs`

**The trigger is the `MatrixDoc`** — today `case MatrixDoc: break; // terminal`. The matrix assembles only when the Regulatory gate is approved *and* `RegulatoryGate.Armable` still holds (`TryAssembleAsync` re-checks), so its existence is precisely the signal "the compliant set is final". Read `TryAssembleAsync` and confirm that for yourself before relying on it.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Matrix_TriggersDosing_OverTheCompliantSetOnly()
    {
        // Seed: cas-ok recommended, cas-no rejected. The dosing agent must be handed ONLY cas-ok.
        await SeedApprovedProjectAsync();
        IReadOnlyList<VerdictDoc>? seen = null;
        _agents.Dosing = (_, compliant, _, _) => { seen = compliant; return Task.FromResult(EmptyDosing()); };

        await Dispatcher().OnRecordChangedAsync(Delivered(Matrix()), default);

        Assert.Equal("cas-ok", Assert.Single(seen!).Cas);
    }

    [Fact]
    public async Task Dosing_ParksInAwaitingPhysics_WhenTheMeasuredBackgroundIsMissing()
    {
        // It does NOT run the agent with a missing measurement and hope. It parks, and the operator can see
        // exactly what physics owes them.
        await SeedApprovedProjectAsync(measuredBackground: []);
        await Dispatcher().OnRecordChangedAsync(Delivered(Matrix()), default);

        var stage = (await _store.GetProjectAsync(P))!.Stages[Stages.Dosing];
        Assert.Equal("awaiting-physics", stage.Status);
        Assert.Contains("Zr", stage.Error);
        Assert.Equal(0, _agents.DosingCalls);      // the agent was never asked
        Assert.Null(await _store.GetDosingAsync(P));
    }

    [Fact]
    public async Task Dosing_ParksInAwaitingOperator_WhenAMetalLoadingIsUnknown()
    {
        // The FIRST project to use a compound pays this cost, once, ever. Every later project reads it.
        await SeedApprovedProjectAsync();                       // knowledge store has no loading for cas-ok
        await Dispatcher().OnRecordChangedAsync(Delivered(Matrix()), default);

        var stage = (await _store.GetProjectAsync(P))!.Stages[Stages.Dosing];
        Assert.Equal("awaiting-operator", stage.Status);
        Assert.Contains("cas-ok", stage.Error);
        Assert.Contains("metal loading", stage.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dosing_IsIdempotent_UnderChangeFeedRedelivery()
    {
        await SeedApprovedProjectAsync(withLoadingFor: "cas-ok");
        var m = Matrix();
        var d = Dispatcher();
        await d.OnRecordChangedAsync(Delivered(m), default);
        await d.OnRecordChangedAsync(Delivered(m), default);   // at-least-once
        Assert.Equal(1, _agents.DosingCalls);
    }

    [Fact]
    public async Task Dosing_WritesTheDoc_AndMarksTheStageDone()
    {
        await SeedApprovedProjectAsync(withLoadingFor: "cas-ok");
        await Dispatcher().OnRecordChangedAsync(Delivered(Matrix()), default);

        Assert.NotNull(await _store.GetDosingAsync(P));
        Assert.Equal("done", (await _store.GetProjectAsync(P))!.Stages[Stages.Dosing].Status);
    }
```

**Use the `Delivered()` helper from `ChatDispatchTests`** — it round-trips a doc through `RecordDocRouter`, which is what a change feed actually is. Handing the dispatcher your own object hides bugs; it already hid one in this codebase.

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.** Replace `case MatrixDoc: break;` with `case MatrixDoc m: await OnMatrixAsync(m, ct); break;` and add:

```csharp
    /// The compliant set is final — the matrix only assembles behind an approved gate that TryAssembleAsync
    /// re-checked is still armable — so Dosing may run.
    ///
    /// It resolves EVERY input first and parks on any gap. It does not run the agent on a partial picture
    /// and let it improvise the holes: the two things missing here are a MEASUREMENT and a MASS FRACTION,
    /// and a model that invents either produces a marker nobody can detect or a batch nobody dosed right.
    private async Task OnMatrixAsync(MatrixDoc m, CancellationToken ct)
    {
        var project = await store.GetProjectAsync(m.ProjectId, ct);
        // At-least-once feed. Also the re-entry point: POST /dosing/loading sets this back to `pending`.
        if (project is null || project.Stages[Stages.Dosing].Status is not "pending") return;

        var constraints = await store.GetConstraintsAsync(m.ProjectId, ct);
        if (constraints is null) return;

        var compliant = CompliantSet.Of(await store.GetVerdictsAsync(m.ProjectId, ct));
        if (compliant.Count == 0)
        {
            await SetStageAsync(m.ProjectId, Stages.Dosing, s =>
            {
                s.Status = "needs-review";
                s.Error = "the compliant set is empty — no substance carries an R.E. determination of " +
                          "'recommended', so there is nothing that may be dosed.";
            }, ct);
            return;
        }

        // 1. Every floor, from the physicist's MEASURED data. Collect ALL the gaps, not the first —
        //    the operator should make one trip to the physicist, not five.
        var floors = new Dictionary<(string, string), Floor>();
        var physicsGaps = new List<string>();
        foreach (var v in compliant)
        {
            var (floor, error) = DetectionFloor.Compute(
                constraints.MeasuredBackground, constraints.Device, v.ComponentId, v.Element);
            if (floor is null) physicsGaps.Add(error!);
            else floors[(v.ComponentId, v.Element)] = floor;
        }
        if (physicsGaps.Count > 0)
        {
            await SetStageAsync(m.ProjectId, Stages.Dosing, s =>
            {
                s.Status = "awaiting-physics";
                s.Error = string.Join(" | ", physicsGaps.Distinct());
            }, ct);
            return;
        }

        // 2. Every metal loading, from the CROSS-PROJECT knowledge layer. The first project to meet a
        //    compound pays this once, ever; every later project reads it.
        var loadings = new Dictionary<string, double>();
        var loadingGaps = new List<string>();
        foreach (var cas in compliant.Select(v => v.Cas).Distinct())
        {
            if (await knowledge.GetSubstancePropertyAsync(cas, ct) is { } p) loadings[cas] = p.MetalLoading;
            else loadingGaps.Add(cas);
        }
        if (loadingGaps.Count > 0)
        {
            await SetStageAsync(m.ProjectId, Stages.Dosing, s =>
            {
                s.Status = "awaiting-operator";
                s.Error = "the metal loading (mass fraction of the marker element in the compound) is not " +
                          "on file for: " + string.Join(", ", loadingGaps) +
                          ". Enter it once via POST /projects/{id}/dosing/loading — it is kept for every " +
                          "future project that uses the same compound.";
            }, ct);
            return;
        }

        await SetStageAsync(m.ProjectId, Stages.Dosing, s => { s.Status = "running"; s.Attempts++; }, ct);
        try
        {
            var result = await agents.RunDosingAsync(constraints, compliant, floors, loadings, null, ct);
            if (!result.Succeeded)
            {
                await SetStageAsync(m.ProjectId, Stages.Dosing,
                    s => { s.Status = "needs-review"; s.Error = result.Error; }, ct);
                return;
            }
            await store.UpsertDosingAsync(result.Output!, ct);
            await SetStageAsync(m.ProjectId, Stages.Dosing,
                s => { s.Status = "done"; s.Error = null; }, ct);
        }
        catch (Exception e)
        {
            await SetStageAsync(m.ProjectId, Stages.Dosing,
                s => { s.Status = "failed"; s.Error = e.Message; }, ct);
        }
    }
```

`StageDispatcher` gains an `IKnowledgeStore knowledge` constructor parameter (it does not have one today — the `LearnedConclusionWriter` owns the knowledge writes). Update every construction site, including the test helpers.

`IAgentRuns` gains, mirroring `RunDiscoveryAsync`'s explicit-`revision` convention:

```csharp
    /// <param name="revision">null for an ordinary run; non-null re-runs Dosing applying the operator's
    /// revise-with-reason. Explicit, not an overload: forgetting it is a compile error, not an agent that
    /// silently ignores the operator.</param>
    Task<AgentRunResult<DosingDoc>> RunDosingAsync(
        ConstraintsDoc constraints, IReadOnlyList<VerdictDoc> compliant,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyDictionary<string, double> loadings,
        RevisionDoc? revision, CancellationToken ct);
```

`FakeAgentRuns` gets a matching `Dosing` func plus a `DosingCalls` counter (`Interlocked.Increment`), exactly as `Chat`/`ChatCalls` are shaped.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Remove the `"pending"` idempotency guard → the redelivery test goes red. Make the floor gap fall through to the agent instead of parking → the `awaiting-physics` test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(dispatch): the Dosing stage — it parks on a missing measurement, it never guesses one"
```

---

## Task 13: The Dosing endpoints — loading entry, the soft checkpoint, and the read

**Files:**
- Create: `src/Smx.Backend/Api/DosingEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/DosingEndpointsTests.cs`

⚠️ **`[FromServices]` on every store param.** Miss it and routing breaks for the whole app.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task PostLoading_RecordsItCrossProject_AndReopensDosing()
    {
        // The write goes to the KNOWLEDGE layer (keyed by CAS, not by project), and re-opening the stage IS
        // the re-trigger — the ProjectDoc upsert is a change-feed event.
        await SeedParkedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "1314-36-9", element = "Y", form = "oxide", metalLoading = 0.787, basis = "2×M(Y)/M(Y2O3)" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(0.787, (await _knowledge.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading);
        Assert.Equal("pending", (await _store.GetProjectAsync(P))!.Stages[Stages.Dosing].Status);
    }

    [Fact]
    public async Task PostLoading_IsReadByADIFFERENTProject_WhichNeverHasToAskAgain()
    {
        // The whole point. The knowledge layer is not a cache — it is the system getting smarter.
        await SeedParkedProjectAsync();
        await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "1314-36-9", element = "Y", form = "oxide", metalLoading = 0.787, basis = "b" });

        Assert.NotNull(await _knowledge.GetSubstancePropertyAsync("1314-36-9"));   // not scoped to P
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.2)]
    [InlineData(1.4)]
    public async Task PostLoading_RefusesAnImpossibleLoading_With422(double loading)
    {
        // 0 → an infinite order amount. >1 → more metal than compound, and it silently UNDER-orders.
        await SeedParkedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "c", element = "Y", form = "oxide", metalLoading = loading, basis = "b" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Null(await _knowledge.GetSubstancePropertyAsync("c"));
    }

    [Fact]
    public async Task PostLoading_WithoutABasis_Is422()
    {
        // An unsourced number in the knowledge layer is worse than none: every future project inherits it
        // and nobody can check it.
        await SeedParkedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/loading",
            new { cas = "c", element = "Y", form = "oxide", metalLoading = 0.7, basis = "  " });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task PostReview_RecordsTheSoftCheckpoint_AndDoesNotBlockAnything()
    {
        // SOFT. It records that the code-finalization review happened. It is not a gate, it unlocks nothing,
        // and it must never be made to block — a third "gate" would dilute what a signature means.
        await SeedDosedProjectAsync();
        var res = await _client.PostAsJsonAsync($"/projects/{P}/dosing/review",
            new { note = "PL + physics reviewed the codes on 14 Jul; happy with the Y:Zr ratio" });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var dosing = await _store.GetDosingAsync(P);
        Assert.Contains("Y:Zr", dosing!.ReviewNote);
        Assert.NotNull(dosing.ReviewedAt);
    }

    [Fact]
    public async Task PostReview_WithABlankNote_Is422()
    {
        await SeedDosedProjectAsync();
        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await _client.PostAsJsonAsync($"/projects/{P}/dosing/review", new { note = "   " })).StatusCode);
    }

    [Fact]
    public async Task GetDosing_IsNotFound_BeforeTheStageHasRun() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{P}/dosing")).StatusCode);
```

- [ ] **Step 2: Run to verify they fail** (404 on every route).

- [ ] **Step 3: Implement** `DosingEndpoints.cs` — `MapDosingEndpoints`, with `[FromServices] IRecordStore` and `[FromServices] IKnowledgeStore` on every handler, and `app.MapDosingEndpoints();` in `Program.cs`.

`POST …/dosing/loading` validates `metalLoading is > 0 and <= 1` and a non-blank `basis`, writes the `SubstancePropertyDoc`, then sets `Stages.Dosing` back to `pending` (the re-trigger) and returns 202.

- [ ] **Step 4: Run tests, then the full suite.** *If every backend test suddenly 500s, you left off a `[FromServices]`.*
- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend src/Smx.Backend.Tests
git commit -m "feat(api): dosing loading entry (cross-project), the soft checkpoint, and GET /dosing"
```

---

## Task 14: Revise-with-reason for Dosing

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (`OnRevisionAsync`), `src/Smx.Backend/Api/RevisionEndpoints.cs`
- Test: `src/Smx.Orchestrator.Tests/DosingRevisionTests.cs`

**Build the `StageDispatcher` exactly as `RevisionDispatchTests` does** (it needs `IRecordStore`, `IAgentRuns`, `IKnowledgeStore`, `ILearnedConclusionWriter`, `regulatoryParallelism`), and deliver docs through its `Delivered()` helper.

- [ ] **Step 1: Write the failing tests**

```csharp
    private static RevisionDoc DosingRevision() => new()
    {
        Id = RecordIds.Revision(P, Stages.Dosing, "aaaa1111"), ProjectId = P, Stage = Stages.Dosing,
        Target = "the Y window in the bottle", Reason = "the line reader struggles below 35 ppm",
        CreatedAt = "2026-07-14T10:00:00.0000000+00:00",
    };

    [Fact]
    public async Task ReviseDosing_ReRunsTheAgentWithTheDirective_AndWritesALearnedConclusion()
    {
        // Law 4 on the stage where it pays most: "the line reader struggles below 35 ppm" is hard-won field
        // knowledge that exists in no corpus, and the knowledge layer is the only thing that keeps it. The
        // operator's reason reaches it VERBATIM — code owns the provenance, never the model.
        await SeedDosedProjectAsync();
        string? directive = null;
        _agents.Dosing = (_, _, _, _, rev) => { directive = rev?.Reason; return Task.FromResult(Dosed()); };

        await Dispatcher().OnRecordChangedAsync(Delivered(DosingRevision()), default);

        Assert.Equal("the line reader struggles below 35 ppm", directive);
        var conclusion = Assert.Single(await _knowledge.QueryLearnedConclusionsAsync(null));
        Assert.Equal(KnowledgeKinds.Dosing, conclusion.Kind);
        Assert.Contains("the line reader struggles below 35 ppm", JsonSerializer.Serialize(conclusion, Json.Options));
        Assert.Equal(RevisionStatus.Applied, (await _store.GetRevisionsAsync(P))[0].Status);
    }

    [Fact]
    public async Task ReviseDosing_DoesNOTVoidTheRegulatoryGate()
    {
        // Dosing is DOWNSTREAM of the gate — it consumes the compliant set, it cannot change it. Voiding
        // the gate here would make the operator re-sign for no reason, and an operator who learns to
        // re-sign reflexively is the rubber stamp the entire design exists to prevent.
        await SeedDosedProjectAsync(withApprovedGate: true);

        await Dispatcher().OnRecordChangedAsync(Delivered(DosingRevision()), default);

        var gate = await _store.GetGateAsync(P, GateTypes.Regulatory);
        Assert.Equal("approved", gate!.Status);
        Assert.NotNull(gate.ApprovedAt);
        Assert.Equal("done", (await _store.GetProjectAsync(P))!.Stages[Stages.Regulatory].Status);
    }

    [Fact]
    public async Task ReviseDosing_StillCannotDoseANonCompliantSubstance()
    {
        // The operator's directive is authoritative over the AGENT — it does not outrank the REGULATORY
        // GATE. "Put the Cd back, it worked better" earns a Learned Conclusion; it does not put Cd in a
        // product. Validate runs again on the re-run: a revision is not a licence to break the floor or
        // the compliant set.
        await SeedDosedProjectAsync();
        _agents.Dosing = (_, _, _, _, _) => Task.FromResult(
            AgentRunResult<DosingDoc>.NeedsReview("'cas-rejected' is not in the compliant set"));

        await Dispatcher().OnRecordChangedAsync(Delivered(DosingRevision()), default);

        var revision = Assert.Single(await _store.GetRevisionsAsync(P));
        Assert.Equal(RevisionStatus.Failed, revision.Status);
        Assert.Contains("not in the compliant set", revision.Error!);
        // And nothing was written: a failed revision must not have applied half its change.
        Assert.Equal(Dosed().Output!.Codes.Count, (await _store.GetDosingAsync(P))!.Codes.Count);
    }
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.** In `OnRevisionAsync`'s switch add `Stages.Dosing => await ReviseDosingAsync(constraints, r, ct)`, and:

```csharp
    private async Task<RevisedStage> ReviseDosingAsync(ConstraintsDoc c, RevisionDoc r, CancellationToken ct)
    {
        // Re-resolve the SAME inputs the first run used — the compliant set, the measured floors, the
        // loadings. A revision re-runs the stage; it does not relax it. Validate fires again, so a directive
        // that would dose below the floor or reach outside the compliant set fails here, loudly, with the
        // reason still recorded as a Learned Conclusion.
        var compliant = CompliantSet.Of(await store.GetVerdictsAsync(c.ProjectId, ct));
        var (floors, loadings, gap) = await ResolveDosingInputsAsync(c, compliant, ct);
        if (gap is not null) throw new InvalidOperationException(gap);   // OnRevisionAsync marks it `failed`

        var result = await agents.RunDosingAsync(c, compliant, floors, loadings, r, ct);
        if (!result.Succeeded) throw new InvalidOperationException(result.Error);

        return new RevisedStage(
            JsonSerializer.Serialize(result.Output, Json.Options),
            token => store.UpsertDosingAsync(result.Output!, token));
    }
```

**Extract the floor + loading resolution out of `OnMatrixAsync` into one private helper that both call sites use.** Two copies of "which floors did we compute" is exactly the seam where the first-run path and the revision path drift — and only one of them would keep its guarantees, with nothing to tell you which:

```csharp
    /// The Dosing stage's inputs, resolved once and used by both the first run and a revision. `Gap` is
    /// non-null when something a HUMAN must supply is missing (a measurement, a mass fraction) — the caller
    /// parks on it rather than letting the agent improvise around the hole.
    private async Task<(Dictionary<(string, string), Floor> Floors,
                        Dictionary<string, double> Loadings,
                        string? Gap)>
        ResolveDosingInputsAsync(ConstraintsDoc c, IReadOnlyList<VerdictDoc> compliant, CancellationToken ct)
```

`OnMatrixAsync` distinguishes the two gap kinds (`awaiting-physics` vs `awaiting-operator`) by which collection came back short, so return them separately if that reads more clearly than one `Gap` string — but **resolve them in one place**.

`RevisionEndpoints` needs no change: it already routes on `RevisionEffects.IsRevisable`, which Task 8 made true for `dosing`. **Confirm that with a test rather than assuming it.**

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Make `ReviseDosingAsync` skip `Validate` → the "cannot dose a non-compliant substance" test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(dispatch): revise Dosing with a reason — the floor still holds, the gate stays signed"
```

---

## Task 15: `PriceParse` — parse what parses, and refuse the rest

**Read design decision 5.**

**Files:**
- Create: `src/Smx.Domain/PriceParse.cs`
- Test: `src/Smx.Domain.Tests/PriceParseTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Smx.Domain;

namespace Smx.Domain.Tests;

public class PriceParseTests
{
    [Theory]
    [InlineData("$115.00", "500 mg", 230.0)]     // $115 / 0.5 g
    [InlineData("$66", "25g", 2.64)]
    [InlineData("$340", "100 g", 3.4)]
    public void Parse_ConvertsAPriceAndAPackIntoDollarsPerGram(string price, string pack, double perGram)
    {
        var (quote, error) = PriceParse.Parse(price, pack);
        Assert.Null(error);
        Assert.Equal(perGram, quote!.UsdPerGram, 2);
        Assert.Equal("USD", quote.Currency);
    }

    [Theory]
    [InlineData("Quote")]
    [InlineData("Catalog (login)")]
    [InlineData("n/a")]
    [InlineData("")]
    public void Parse_REFUSES_AFreeTextPrice_RatherThanInventANumber(string price)
    {
        // Most of the seeded supplier data says exactly this. "Quote" is not a price, and a Cost stage that
        // turned it into one would be fabricating the single number procurement acts on.
        var (quote, error) = PriceParse.Parse(price, "25 g");
        Assert.Null(quote);
        Assert.Contains("no price", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_REFUSES_ANonDollarCurrency_AndNeverConvertsIt()
    {
        // A CNY figure is not comparable to a USD one, and this system has no FX rate, no date for it, and
        // no business inventing either. Refusing is the honest answer; converting is a fabricated number
        // that looks authoritative.
        var (quote, error) = PriceParse.Parse("¥800", "25 g");
        Assert.Null(quote);
        Assert.Contains("currency", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_REFUSES_AnUnparseablePack()
    {
        var (quote, error) = PriceParse.Parse("$115.00", "each");
        Assert.Null(quote);
        Assert.Contains("pack", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_REFUSES_AZeroPack_RatherThanDivideByZero() =>
        Assert.Null(PriceParse.Parse("$115.00", "0 g").Quote);

    [Theory]
    [InlineData("500 mg", 0.5)]
    [InlineData("25g", 25.0)]
    [InlineData("1 kg", 1000.0)]
    [InlineData("100 G", 100.0)]
    public void PackGrams_HandlesTheUnitsTheCatalogActuallyUses(string pack, double grams) =>
        Assert.Equal(grams, PriceParse.PackGrams(pack)!.Value, 6);
}
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement** `src/Smx.Domain/PriceParse.cs` — a `Quote(double UsdPerGram, string Currency)`, a `Parse(string? price, string? pack)` returning `(Quote?, string?)`, and a `PackGrams(string?)` returning `double?`. Regex the leading currency symbol and number; map `mg|g|kg` (case-insensitive) to grams. **Accept `$` / `USD` only.** Anything else — including a bare number with no symbol — is refused, because a number whose currency you are guessing at is not a price.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Make the non-dollar path convert at a hard-coded rate → its test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/PriceParse.cs src/Smx.Domain.Tests/PriceParseTests.cs
git commit -m "feat(domain): PriceParse — $/g from what parses, and an honest refusal for everything else"
```

---

## Task 16: `CatalogCard` carries the price (it does not today)

**Files:**
- Modify: `src/Smx.Domain/Tools/ITools.cs`, `src/Smx.Infrastructure/Search/CatalogLookup.cs`
- Test: `src/Smx.Orchestrator.Tests/CosmosQueryTextTests.cs`, `src/Smx.Domain.Tests/Fakes/FakeCatalogLookup.cs`

The seeded `ref-catalog` documents **have** `price` and `pack`. `CosmosCatalogLookup.Row` and `CatalogCard` both **drop them**. Cost cannot exist until they don't.

- [ ] **Step 1: Write the failing test** — extend `CosmosQueryTextTests` to pin that the catalog query's projection addresses `price` and `pack` in **camelCase**, and add a `CatalogCard` construction test asserting the two new members survive.

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement.** Add `string? Price` and `string? Pack` to `CatalogCard` (at the end, so existing positional constructions still compile — then fix any that should carry them). Add them to `Row` and the projection in `CosmosCatalogLookup`. Update `FakeCatalogLookup` to carry them.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain src/Smx.Infrastructure src/Smx.Orchestrator.Tests src/Smx.Domain.Tests
git commit -m "feat(tools): CatalogCard carries price + pack — Cost cannot audit what the card drops"
```

---

## Task 17: `CostDoc` + the deterministic audit

**Files:**
- Create: `src/Smx.Domain/Records/CostDoc.cs`, `src/Smx.Orchestrator/Cost/CostAudit.cs`
- Modify: `src/Smx.Domain/IRecordStore.cs`, both stores, `RecordDocRouter`
- Test: `src/Smx.Orchestrator.Tests/CostAuditTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Audit_QuotesThePriceFromTheCatalog_AndCitesTheListing()
    {
        var audit = await CostAudit.RunAsync(catalog, [("1314-36-9", "Y")], ct: default);
        var line = Assert.Single(audit.Substances);
        Assert.Equal(2.64, line.BestQuote!.UsdPerGram, 2);
        Assert.StartsWith("ref-catalog/", line.BestQuote.Citation.Reference);   // every figure is checkable
    }

    [Fact]
    public async Task Audit_OnASubstanceWithNoParseablePrice_SaysSo_AndQuotesNothing()
    {
        // THE DISCIPLINE. Sparse price coverage is a fact of the seeded data. "No price on file" is a useful
        // answer; an invented one is a harmful answer, because procurement acts on exactly this number.
        var audit = await CostAudit.RunAsync(catalogWithQuoteOnlyPricing, [("cas-x", "Zr")], ct: default);
        var line = Assert.Single(audit.Substances);

        Assert.Null(line.BestQuote);
        Assert.Contains("quote required", line.PriceNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_FlagsASingleSourceSubstance_AsASupplyRisk()
    {
        // One supplier is a supply risk, and it is the kind procurement must know about BEFORE the VP signs.
        var audit = await CostAudit.RunAsync(oneSupplierCatalog, [("cas-x", "Zr")], ct: default);
        Assert.Contains("single-source", Assert.Single(audit.Substances).Risks);
    }

    [Fact]
    public async Task Audit_DoesNotFlagSingleSource_WhenTwoSuppliersListIt()
    {
        var audit = await CostAudit.RunAsync(twoSupplierCatalog, [("cas-x", "Zr")], ct: default);
        Assert.Empty(Assert.Single(audit.Substances).Risks);
    }

    [Fact]
    public async Task Audit_OnASubstanceMissingFromTheCatalogEntirely_IsNotSilent()
    {
        // A substance that is not off-the-shelf is a finding, not an omission. Dropping the row would let it
        // reach the VP looking cleanly costed.
        var audit = await CostAudit.RunAsync(emptyCatalog, [("cas-nowhere", "Zz")], ct: default);
        var line = Assert.Single(audit.Substances);
        Assert.Empty(line.Suppliers);
        Assert.Contains("not-off-the-shelf", line.Risks);
    }
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement.**

`src/Smx.Domain/Records/CostDoc.cs`:
```csharp
namespace Smx.Domain.Records;

/// A price, and the listing it came from. Every figure in Cost carries its citation, because procurement
/// acts on these numbers and must be able to check them.
public sealed record PriceQuote(double UsdPerGram, string Currency, string Supplier, string Pack, Citation Citation);

/// The audit for one substance. `BestQuote` is null when nothing parseable was on file — and `PriceNote`
/// says so in words. Nothing is ever interpolated, averaged, or currency-converted into existence.
public sealed record SupplierAudit(
    string Cas, string Element,
    IReadOnlyList<string> Suppliers,
    PriceQuote? BestQuote,
    string PriceNote,
    IReadOnlyList<string> Risks);      // "single-source" | "not-off-the-shelf"

public sealed class CostDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Cost;
    public List<SupplierAudit> Substances { get; set; } = [];
    public required string GeneratedAt { get; set; }
}
```

`src/Smx.Orchestrator/Cost/CostAudit.cs` — pure over `ICatalogLookup`: for each `(cas, element)` in the finalized codes, look up the element's catalog cards, keep those whose `Cas` matches, `PriceParse.Parse` each, take the **cheapest parseable** as `BestQuote`, collect distinct suppliers, and flag `single-source` (exactly one supplier) / `not-off-the-shelf` (no cards at all).

`GetCostAsync`/`UpsertCostAsync` on `IRecordStore` + both stores; the `RecordTypes.Cost` router arm (**and route it to a doc type, not `null`** — Plan 5's Decision stage will trigger off it).

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Make `BestQuote` fall back to an average of the unparseable rows → the "no parseable price" test goes red. Drop the `not-off-the-shelf` flag → its test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain src/Smx.Orchestrator src/Smx.Infrastructure src/Smx.Domain.Tests src/Smx.Orchestrator.Tests
git commit -m "feat(cost): the deterministic supplier audit — cited prices, an honest silence, and the risk flags"
```

---

## Task 18: Dispatch — Dosing triggers Cost

**Files:**
- Modify: `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs`
- Test: `src/Smx.Orchestrator.Tests/CostDispatchTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task Dosing_TriggersCost_OverTheSubstancesInTheFINALISEDCodes()
    {
        // Cost audits what will actually be ORDERED — the substances in the codes — not every candidate that
        // survived the gate. Costing an unused substance is noise; missing a used one is a purchase nobody
        // priced.
        await SeedDosedProjectAsync();
        await Dispatcher().OnRecordChangedAsync(Delivered(Dosing()), default);

        var cost = await _store.GetCostAsync(P);
        Assert.Equal(["1314-36-9"], cost!.Substances.Select(s => s.Cas));
        Assert.Equal("done", (await _store.GetProjectAsync(P))!.Stages[Stages.Cost].Status);
    }

    [Fact]
    public async Task Cost_RunsWithNoAgent_AtAll()
    {
        // §3.4: Cost is deterministic. If it ever needs an agent, that is a design change to argue for, not
        // a convenience to slip in.
        await SeedDosedProjectAsync();
        await Dispatcher().OnRecordChangedAsync(Delivered(Dosing()), default);
        Assert.Equal(0, _agents.TotalCalls);
    }

    [Fact]
    public async Task Cost_IsIdempotent_UnderChangeFeedRedelivery()
    {
        await SeedDosedProjectAsync();
        var d = Dispatcher();
        var dosing = Dosing();
        await d.OnRecordChangedAsync(Delivered(dosing), default);
        var first = (await _store.GetCostAsync(P))!.GeneratedAt;

        await d.OnRecordChangedAsync(Delivered(dosing), default);   // at-least-once

        Assert.Equal(first, (await _store.GetCostAsync(P))!.GeneratedAt);   // not re-run, not re-stamped
    }

    [Fact]
    public async Task ASoftReviewNote_DoesNotReRunCost()
    {
        // POST /dosing/review upserts the DOSING doc — and the DosingDoc is what triggers Cost. If Cost
        // guarded on "does a DosingDoc exist" it would re-price the whole project every time the operator
        // recorded a review note. The guard must be the STAGE STATUS, not the doc's existence.
        await SeedCostedProjectAsync();                       // cost already done
        var stamped = (await _store.GetCostAsync(P))!.GeneratedAt;

        await Dispatcher().OnRecordChangedAsync(Delivered(DosingWithReviewNote()), default);

        Assert.Equal(stamped, (await _store.GetCostAsync(P))!.GeneratedAt);
    }
```

**That last test is the one to think hardest about** — the soft checkpoint writes to the *same doc* that triggers Cost, so "a DosingDoc exists" is the wrong guard and only the stage status is the right one.

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement** `case DosingDoc d: await OnDosingAsync(d, ct); break;` — guard on `Stages.Cost.Status is "pending"` (so a review-note upsert cannot re-run it), run `CostAudit` over the distinct `(Cas, Element)` in `d.Codes`, persist, mark `done`.

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Mutation-test.** Guard on the DosingDoc's existence instead of the stage status → the review-note test goes red. Restore.
- [ ] **Step 6: Commit**

```bash
git add src/Smx.Orchestrator src/Smx.Orchestrator.Tests
git commit -m "feat(dispatch): Dosing triggers Cost — deterministic, and a review note does not re-price it"
```

---

## Task 19: `GET /projects/{id}/cost` + the chat/read surfaces

**Files:**
- Create: `src/Smx.Backend/Api/CostEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`, `src/Smx.Orchestrator/Dispatch/StageDispatcher.cs` (`StageInputsJsonAsync`)
- Test: `src/Smx.Backend.Tests/CostEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public async Task GetCost_ReturnsTheAudit_WithEveryFigureCited()
    {
        await SeedCostedProjectAsync();
        var cost = await _client.GetFromJsonAsync<JsonElement>($"/projects/{P}/cost");
        var line = cost.GetProperty("substances")[0];
        Assert.StartsWith("ref-catalog/", line.GetProperty("bestQuote").GetProperty("citation")
            .GetProperty("reference").GetString());
    }

    [Fact]
    public async Task GetCost_IsNotFound_BeforeTheStageHasRun() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/projects/{P}/cost")).StatusCode);

and, in `src/Smx.Orchestrator.Tests/ChatDispatchTests.cs`:

```csharp
    [Theory]
    [InlineData(Stages.Dosing, "1314-36-9")]     // the DosingDoc's marker
    [InlineData(Stages.Cost, "ref-catalog/")]    // the CostDoc's citation
    public async Task ChatOnANewStage_SeesThatStagesOwnRecord_NotAnEmptyObject(string stage, string expected)
    {
        // Plan 3c's chat surface works on every stage in Stages.All — which now includes dosing and cost.
        // If StageInputsJsonAsync has no arm for them it returns "{}", and the operator gets an agent
        // holding a confident conversation about nothing. This is the Plan-4 tripwire, as a test.
        await SeedCostedProjectAsync();
        string? seen = null;
        _agents.Chat = (_, _, inputs, _) => { seen = inputs; return Task.FromResult("ok"); };

        var msg = Msg("aaaa1111", "what did you dose?", stage);
        await _store.UpsertChatMessageAsync(msg);
        await Dispatcher().OnRecordChangedAsync(Delivered(msg), default);

        Assert.Contains(expected, seen);
        Assert.NotEqual("{}", seen);
    }
```

- [ ] **Step 2: Run to verify they fail.**

- [ ] **Step 3: Implement** `CostEndpoints` (`[FromServices]` on the store) and confirm `StageInputsJsonAsync` returns the `DosingDoc` for `Stages.Dosing` and the `CostDoc` for `Stages.Cost` (Task 8 wired the switch — **verify it actually reads the new docs, not `"{}"`**).

- [ ] **Step 4: Run tests, then the full suite.**
- [ ] **Step 5: Commit**

```bash
git add src/Smx.Backend src/Smx.Orchestrator src/Smx.Backend.Tests
git commit -m "feat(api): GET /cost, and the dosing/cost chat surfaces read their own records"
```

---

## Task 20: End-to-end + eval + final verification

**Files:**
- Test: `src/Smx.Backend.Tests/DosingCostEndToEndTests.cs`
- Modify: `tools/Smx.Eval/` (add the dosing invariants)

- [ ] **Step 1: The end-to-end test**

Drive the **whole journey** through the real HTTP surface and the real dispatcher: create a project with measured background + device + batch mass → intake → discovery → regulatory (with proposals) → operator determinations → approve the gate → matrix → **dosing parks on the unknown loading** → `POST /dosing/loading` → dosing produces windows + codes → **cost prices them** → `GET /dosing` and `GET /cost` both return cited data.

Assert the three things that would each be a shipped bug:
```csharp
    Assert.All(dosing.Windows, w => Assert.True(w.RecommendedPpm > w.Floor.Ppm,
        $"{w.Cas} in {w.ComponentId} is dosed BELOW its detection floor — the marker cannot be read"));
    Assert.All(dosing.Codes.SelectMany(c => c.Markers),
        m => Assert.Contains(m.Cas, compliantCas));   // nothing rejected reached a code
    Assert.All(cost.Substances, s => Assert.True(s.BestQuote is null || s.BestQuote.Citation is not null));
```

- [ ] **Step 2: Extend the eval harness** (`tools/Smx.Eval`) with the dosing invariants from design §9: `floor < recommended < upper` for every window, every code has 2–3 markers, every marker is in the compliant set. **Keep the non-zero exit on any false-pass.**

- [ ] **Step 3: Final verification**

```bash
dotnet build src/Smx.Backend.sln          # 0 new warnings
dotnet test  src/Smx.Backend.sln          # expect ~470+
dotnet test  src/Smx.Functions.sln        # 70, unregressed
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

- [ ] **Step 4: Commit**

```bash
git add src/Smx.Backend.Tests tools/Smx.Eval
git commit -m "test(e2e): the whole journey to a priced, dosable, cited answer"
```

---

## Spec coverage — point at the code for each claim

| Spec claim | Where it is true |
|---|---|
| §3.3 "detection floor — deterministic, from device model + measured background" | `DetectionFloor.Compute`; the inputs are Task 1's; Dosing **parks** without them |
| §3.3 "upper = regulatory ceiling, else a formulation-impact estimate **flagged as an estimate**" | `Bound.Kind` ∈ {regulatory, estimate}; the agent may never claim `measured` |
| §3.3 "each bound shows basis + confidence" | `Bound.Basis`, `Bound.Confidence` |
| §3.3 "ppm recommendation + 2–3-marker codes with a ratio signature — agent" | `DosingAgent`; `RatioSignature` is computed by **code** |
| §3.3 "order amounts — deterministic: ppm × batch ÷ metal loading" | `OrderAmount` — batch **MASS**, loading from the cross-project knowledge layer |
| §3.3 "tools: floor + order-amount calculators, search_learned_conclusions, search_reference" | `ToolBox.DosingTools()` |
| §3.3 "⏸ soft checkpoint — code-finalization review" | `POST …/dosing/review` → `DosingDoc.ReviewNote`. **Records; does not block.** |
| §3.4 "supplier audit — preferred supplier, price, form, **off-the-shelf only**, single-source risk, each figure linked to its listing" | `CostAudit` + `PriceQuote.Citation`; `not-off-the-shelf` and `single-source` risks |
| §3.4 "deterministic — no agent" | `Cost_RunsWithNoAgent_AtAll` |
| §6 "Learned Conclusions read at **dosing**" | `search_learned_conclusions` in `DosingTools()` |
| §4 "revise-with-reason re-runs the stage and writes a Learned Conclusion" | `ReviseDosingAsync` → `KnowledgeKinds.Dosing` |
| Law 9 "chat never signs a gate" | unchanged — Dosing adds **no** gate. The soft checkpoint is a note. |

---

## Open questions to settle at first live use

- **The 3σ / 10σ multipliers in `DetectionFloor` are the IUPAC convention, not an SMX measurement.** Confirm with SMX physics. One file changes if they differ.
- **`Bound.Confidence` for an estimated upper bound** is the agent's own number. The UX spec says Tier-B candidates carry lower-confidence windows — Plan 4 does not yet *enforce* a tier→confidence ceiling. Consider it once there is a real estimate to calibrate against.
- **Price coverage is sparse** (77 products). Expect many `"no price on file — quote required"` rows on the first real project. That is the honest output, not a bug — but it may motivate real pricing data before Plan 5's VP gate.

---

## Deviations recorded during execution

*(Fill this in as you go — the as-shipped record is worth more than the plan being right.)*

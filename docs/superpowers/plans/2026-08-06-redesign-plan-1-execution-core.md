# Execution Core: Nothing Parks — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete the five `awaiting-*` stage statuses so the pipeline runs end to end on the best data it has, leaving signatures — not stalled computations — as the only outstanding things.

**Architecture:** `2026-07-27-execution-core-design.md` §8/D10 specified this and it was never implemented. Regulatory and Decision land `done` with proposals; Dosing runs over a new `ProvisionalSet` (which folds `Determination ?? ProposedDetermination`) rather than the empty `CompliantSet`, and stamps its output provisional. `CompliantSet` is untouched and remains the only set the two irreversible acts — the compliance-package export and placing an order — consult. Missing XRF yields a declared default floor, flagged, which blocks the order rather than the pipeline.

**Tech Stack:** .NET 8 / C#, xUnit. `src/Smx.Backend.sln`.

**Read first:** `docs/superpowers/specs/2026-08-06-webapp-redesign-3-phase-design.md` §3, §10, **§10.1**.

**Baseline (verified 2026-08-06):** `dotnet test src/Smx.Backend.sln` → 946 + 341 + 12 = **1299 passing**.

---

## Task 0: Learn the test fixtures before writing any test

Tasks 4, 5, 6, 8 and 9 name seed helpers — `SeedProjectWithProposedVerdictsOnlyAsync`,
`SeedProjectWithNoXrfBackgroundAsync`, `SeedProjectThroughDiscoveryAsync`,
`SeedProjectThroughDosingAsync`, `SeedFreshProjectAsync`, `SeedProjectRunToCompletionUnsignedAsync`,
`CreateProjectThroughInterviewToolAsync` — and they **do not exist yet**. They are not placeholders; they
are the fixture surface this plan needs, and inventing a parallel one would fork the test suite.

- [ ] Read `src/Smx.Backend.Tests/DosingDispatchTests.cs`, `DecisionDispatchTests.cs` and
      `DecisionEndpointsTests.cs` and write down: how a project is seeded, what the store/runner/client
      members are actually called, and which existing helper each name above should be built from.
- [ ] Add the missing helpers to the existing shared fixture (not a new file) in the same style, each one
      a thin composition of what is already there.
- [ ] Rename the helpers used in this plan's test code to whatever the fixture really calls them.

Everything below assumes this is done. A task that cannot find its fixture should stop and extend the
fixture, never stub the assertion.

---

## Files

| File | Responsibility | Action |
|---|---|---|
| `src/Smx.Domain/ProvisionalSet.cs` | The set Dosing computes over. Folds `Determination ?? ProposedDetermination`. | Create |
| `src/Smx.Domain/CompliantSet.cs` | Unchanged. The set the irreversible acts consult. | **Do not touch** |
| `src/Smx.Domain/DefaultDetectionFloor.cs` | The declared default floor from a device's generic LOD, when no measurement exists. | Create |
| `src/Smx.Domain/Records/DosingDoc.cs` | Gains `Provisional` + `ProvisionalReasons`. | Modify |
| `src/Smx.Domain/Records/ProjectDoc.cs` | `StageStatus` loses five constants. | Modify |
| `src/Smx.Backend/Pipeline/PipelineRunner.cs` | Stops writing parks; Dosing runs unconditionally; close latches on procurement. | Modify |
| `src/Smx.Backend/Api/ProjectsListEndpoints.cs` | Dashboard drops blocked-on-whom; reports outstanding signatures. | Modify |
| `src/Smx.Backend/Api/ProjectEndpoints.cs` | `awaiting-confirmation` removal. | Modify |
| `src/Smx.Backend/Api/DecisionEndpoints.cs`, `DosingEndpoints.cs`, `Agents/InterviewTools.cs`, `Program.cs` | Park references removed. | Modify |
| `src/Smx.Domain/VpGate.cs` | Park references in prose/logic removed; `Armable` retained. | Modify |
| `src/Smx.Domain.Tests/ProvisionalSetTests.cs` | New. | Create |
| `src/Smx.Backend.Tests/NoParkStatusesTests.cs` | Guard: no `awaiting-` literal survives. | Create |
| `src/Smx.Backend.Tests/UnattendedRunTests.cs` | E2E: complete-unsigned. | Create |

---

### Task 1: `ProvisionalSet` — the set Dosing computes over

**Files:**
- Create: `src/Smx.Domain/ProvisionalSet.cs`
- Create: `src/Smx.Domain.Tests/ProvisionalSetTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Domain.Tests/ProvisionalSetTests.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class ProvisionalSetTests
{
    private static VerdictDoc V(string cas, string? determination = null, string? proposed = null) => new()
    {
        Id = RecordIds.Verdict("p1", cas, "bottle"), ProjectId = "p1", Cas = cas, ComponentId = "bottle",
        Element = "Zr", Form = "oxide",
        Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("reg", "x", "t")], 0.9, "r")],
        Determination = determination,
        ProposedDetermination = proposed,
    };

    [Fact]
    public void Of_PrefersTheOperatorsDetermination_OverTheProposal()
    {
        // The human overrules the agent in BOTH directions, which is the whole point of a signature.
        var set = ProvisionalSet.Of([V("in", Determinations.Recommended, Determinations.Rejected)]);
        Assert.Equal("in", Assert.Single(set).Cas);

        Assert.Empty(ProvisionalSet.Of([V("out", Determinations.Rejected, Determinations.Recommended)]));
    }

    [Fact]
    public void Of_FallsBackToTheProposal_WhenNobodyHasRuled()
    {
        // This is the ONLY difference from CompliantSet, and it is why Dosing can run unattended.
        Assert.Equal("p", Assert.Single(ProvisionalSet.Of([V("p", null, Determinations.Recommended)])).Cas);
    }

    [Fact]
    public void Of_ExcludesASilentVerdict_WithNoDeterminationAndNoProposal()
    {
        Assert.Empty(ProvisionalSet.Of([V("silent")]));
    }

    [Fact]
    public void Of_FailsClosedOnANonCanonicalString()
    {
        // Same safe asymmetry as CompliantSet: a hand-edited document is not a recommendation.
        Assert.Empty(ProvisionalSet.Of([V("weird", null, "Recommended")]));
        Assert.Empty(ProvisionalSet.Of([V("weird2", "yes please")]));
    }

    [Fact]
    public void IsProvisional_IsTrue_WhenAnyMemberRestsOnAProposal()
    {
        Assert.True(ProvisionalSet.IsProvisional([V("p", null, Determinations.Recommended)]));
        Assert.False(ProvisionalSet.IsProvisional([V("s", Determinations.Recommended, null)]));
    }

    [Fact]
    public void ProvisionalReasons_NameEachSubstanceRestingOnAProposal()
    {
        var reasons = ProvisionalSet.ProvisionalReasons([
            V("111-11-1", null, Determinations.Recommended),
            V("222-22-2", Determinations.Recommended, null),
        ]);
        var only = Assert.Single(reasons);
        Assert.Contains("111-11-1", only);
        Assert.DoesNotContain("222-22-2", only);
    }

    [Fact]
    public void Of_IsASUPERSETOfCompliantSet_Always()
    {
        // The relationship that makes the two names safe: anything the operator signed is in both.
        VerdictDoc[] mixed = [
            V("a", Determinations.Recommended),
            V("b", null, Determinations.Recommended),
            V("c", Determinations.Rejected),
        ];
        var compliant = CompliantSet.Of(mixed).Select(v => v.Cas).ToHashSet();
        var provisional = ProvisionalSet.Of(mixed).Select(v => v.Cas).ToHashSet();
        Assert.ProperSubset(provisional, compliant);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter ProvisionalSetTests`
Expected: FAIL — `The name 'ProvisionalSet' does not exist in the current context`.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/ProvisionalSet.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// Which substances Dosing may COMPUTE over — deliberately NOT the same question as which substances may
/// reach a customer's product. That second question is <see cref="CompliantSet"/>'s, and it is stricter.
///
/// The split exists because the pipeline no longer parks (execution-core design §8/D10). Regulatory lands
/// with its verdicts and only the agent's PROPOSED determinations on them; if Dosing ran over
/// <see cref="CompliantSet"/> at that moment it would run over an empty set and produce nothing, while
/// LOOKING as though it had finished. That is worse than parking.
///
/// So this set folds `Determination ?? ProposedDetermination`, and everything downstream that could act
/// irreversibly — the compliance-package export, placing an order — keeps consulting CompliantSet.
///
/// THE LINE: the machine may compute over its own proposals; it may not act on them. Do not "simplify"
/// these two into one set. Making CompliantSet fall back to a proposal is the agent signing the regulatory
/// gate by the back door, and CompliantSetTests calls that out by name.
///
/// The operator's determination always WINS where it exists, in both directions — a signed `rejected` is
/// not resurrected by a hopeful proposal.
public static class ProvisionalSet
{
    public static IReadOnlyList<VerdictDoc> Of(IReadOnlyList<VerdictDoc> verdicts) =>
        verdicts.Where(v => Effective(v) == Determinations.Recommended).ToList();

    /// True when ANY member of the resulting set rests on a proposal rather than a signature. A DosingDoc
    /// computed while this is true is stamped provisional and cannot release an order.
    public static bool IsProvisional(IReadOnlyList<VerdictDoc> verdicts) =>
        verdicts.Any(RestsOnAProposal);

    /// One human-readable line per substance that is in the set only because the agent proposed it. Named
    /// substances, not a count: "3 substances are provisional" is not something an operator can act on.
    public static IReadOnlyList<string> ProvisionalReasons(IReadOnlyList<VerdictDoc> verdicts) =>
    [
        .. verdicts.Where(RestsOnAProposal).Select(v =>
            $"{v.Element} ({v.Cas}) in '{v.ComponentId}' is included on the agent's proposal alone — " +
            $"no operator determination is on file."),
    ];

    private static bool RestsOnAProposal(VerdictDoc v) =>
        v.Determination is null && v.ProposedDetermination == Determinations.Recommended;

    /// Ordinal, like CompliantSet's comparison: a non-canonical string is not a recommendation.
    private static string? Effective(VerdictDoc v) => v.Determination ?? v.ProposedDetermination;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter ProvisionalSetTests`
Expected: PASS, 7 tests.

- [ ] **Step 5: Verify the Law-9 line is still intact**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter CompliantSetTests`
Expected: PASS, unchanged. `CompliantSet.cs` must show no diff.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Domain/ProvisionalSet.cs src/Smx.Domain.Tests/ProvisionalSetTests.cs
git commit -m "feat(domain): ProvisionalSet - what Dosing computes over when nobody has ruled"
```

---

### Task 2: `DefaultDetectionFloor` — a floor when there is no measurement

**Files:**
- Create: `src/Smx.Domain/DefaultDetectionFloor.cs`
- Create: `src/Smx.Domain.Tests/DefaultDetectionFloorTests.cs`

Per spec §3: missing XRF no longer parks Dosing. It proceeds on a declared default floor from the device's generic detection limit and flags the component. `DetectionFloor.Compute` is **unchanged** — it keeps refusing, and this is the explicit, separately-named fallback the caller reaches for when it does.

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Domain.Tests/DefaultDetectionFloorTests.cs`:

```csharp
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DefaultDetectionFloorTests
{
    [Fact]
    public void Of_UsesTheDeviceGenericLod_WhenOneIsOnFile()
    {
        var device = new XrfDevice { Model = "Elvatech", Lods = [new("Zr", 12.0, "ppm")] };
        var floor = DefaultDetectionFloor.Of(device, "Zr");

        Assert.NotNull(floor);
        Assert.Equal(12.0 * DetectionFloor.DetectionSigma, floor!.DetectionPpm);
        Assert.Equal(12.0 * DetectionFloor.QuantificationSigma, floor.QuantificationPpm);
    }

    [Fact]
    public void Of_SaysInItsBasis_ThatNothingWasMeasured()
    {
        // The basis string travels onto the Bound and into the UI. An operator reading "estimated floor"
        // must be able to tell WHY without opening the record.
        var device = new XrfDevice { Model = "Elvatech", Lods = [new("Zr", 12.0, "ppm")] };
        Assert.Contains("no physicist measurement", DefaultDetectionFloor.Of(device, "Zr")!.Basis,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Of_ReturnsNull_WithNoDevice()
    {
        // No device means no generic limit to fall back TO. This is still a refusal -- but it flags the
        // component rather than parking the pipeline; the caller decides that.
        Assert.Null(DefaultDetectionFloor.Of(null, "Zr"));
    }

    [Fact]
    public void Of_ReturnsNull_WhenTheDeviceHasNoLodForThatElement()
    {
        var device = new XrfDevice { Model = "Elvatech", Lods = [new("Ti", 9.0, "ppm")] };
        Assert.Null(DefaultDetectionFloor.Of(device, "Zr"));
    }

    [Fact]
    public void Of_RefusesANonPpmLod_RatherThanMixUnits()
    {
        // Same refusal DetectionFloor makes: mixing counts with ppm yields a number that looks reasonable
        // and is simply wrong.
        var device = new XrfDevice { Model = "Elvatech", Lods = [new("Zr", 12.0, "counts")] };
        Assert.Null(DefaultDetectionFloor.Of(device, "Zr"));
    }

    [Fact]
    public void Of_MatchesElementOrdinally_BecauseCoIsNotCO()
    {
        var device = new XrfDevice { Model = "Elvatech", Lods = [new("CO", 12.0, "ppm")] };
        Assert.Null(DefaultDetectionFloor.Of(device, "Co"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter DefaultDetectionFloorTests`
Expected: FAIL — `DefaultDetectionFloor` does not exist.

> If `XrfDevice`'s property names differ from `Model` / `Lods` or `DeviceLod`'s from `(Element, Value, Unit)`, read `src/Smx.Domain/Records/ConstraintsDoc.cs` and correct the test to the real shape before implementing. Do not change the production record to fit the test.

- [ ] **Step 3: Write the implementation**

Create `src/Smx.Domain/DefaultDetectionFloor.cs`:

```csharp
using Smx.Domain.Records;

namespace Smx.Domain;

/// The DECLARED DEFAULT floor — what Dosing uses when no physicist measurement is on file.
///
/// <see cref="DetectionFloor"/> refuses without a measurement, and that refusal is correct: a floor that
/// reads low ships a marker nobody can detect. What changed (execution-core §8) is what the caller does
/// with the refusal. It no longer parks the pipeline. It falls back to HERE, and flags the component.
///
/// This is a SEPARATE, SEPARATELY-NAMED function rather than a default inside DetectionFloor, for the same
/// reason ProvisionalSet is separate from CompliantSet: the caller must choose the weaker number
/// deliberately and in the open, and every number produced here is stamped as an estimate that BLOCKS THE
/// ORDER. A silent default inside Compute would make a guess indistinguishable from a measurement at every
/// call site.
///
/// It is a floor over the device's generic limit of detection ALONE — there is no substrate background in
/// it, and a real background can only push the true floor UP. So this number is knowingly optimistic, which
/// is exactly why it may not release procurement.
public static class DefaultDetectionFloor
{
    public static Floor? Of(XrfDevice? device, string element)
    {
        if (device is null) return null;

        // Ordinal, like DetectionFloor: "Co" is cobalt and "CO" is not an element.
        var lod = device.Lods.FirstOrDefault(l => string.Equals(l.Element, element, StringComparison.Ordinal));
        if (lod is null) return null;

        // Unit mismatch refuses rather than converts, exactly as DetectionFloor does.
        if (!string.Equals(lod.Unit, DetectionFloor.Ppm, StringComparison.OrdinalIgnoreCase)) return null;

        return new Floor(
            lod.Value * DetectionFloor.DetectionSigma,
            lod.Value * DetectionFloor.QuantificationSigma,
            $"estimated floor — no physicist measurement on file. Computed from the {device.Model} generic " +
            $"limit of detection for {element} ({lod.Value} ppm) at {DetectionFloor.DetectionSigma}σ, with " +
            $"NO substrate background. A measured background can only raise this floor.");
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter DefaultDetectionFloorTests`
Expected: PASS, 6 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/DefaultDetectionFloor.cs src/Smx.Domain.Tests/DefaultDetectionFloorTests.cs
git commit -m "feat(domain): DefaultDetectionFloor - the flagged fallback when XRF is absent"
```

---

### Task 3: `DosingDoc` carries its own provisionality

**Files:**
- Modify: `src/Smx.Domain/Records/DosingDoc.cs`
- Modify: `src/Smx.Domain.Tests/RecordDocsTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `src/Smx.Domain.Tests/RecordDocsTests.cs`:

```csharp
    [Fact]
    public void DosingDoc_DefaultsToNotProvisional_AndSerializesTheFlagEvenWhenFalse()
    {
        // Serialized ALWAYS, like GateDoc.ApprovedBy: the UI must read "not provisional" off the wire
        // rather than infer it from a missing key, because absence would read as false either way and a
        // build skew would silently downgrade a warning into a clean bill of health.
        var doc = new DosingDoc { Id = "p1|dosing", ProjectId = "p1", GeneratedAt = "2026-08-06T00:00:00Z" };
        Assert.False(doc.Provisional);
        Assert.Empty(doc.ProvisionalReasons);

        var json = System.Text.Json.JsonSerializer.Serialize(doc, Json.Options);
        Assert.Contains("\"provisional\"", json);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter DosingDoc_DefaultsToNotProvisional`
Expected: FAIL — `DosingDoc` does not contain a definition for `Provisional`.

- [ ] **Step 3: Add the fields**

In `src/Smx.Domain/Records/DosingDoc.cs`, inside `class DosingDoc`, after `Codes`:

```csharp
    /// TRUE when any substance in this dosing is present on the AGENT'S PROPOSAL rather than an operator
    /// determination (ProvisionalSet), or when any window rests on a DefaultDetectionFloor rather than a
    /// measurement. It is the order-blocking flag: procurement consults CompliantSet and refuses over a
    /// provisional analysis, so this never becomes a thing an operator can wave through.
    ///
    /// Serialized even when false ([JsonIgnore(Never)]) for the same reason GateDoc.ApprovedBy is: the UI
    /// must read "not provisional" off the wire, never infer it from an absent key.
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public bool Provisional { get; set; }

    /// One named line per reason, never a count — "3 substances are provisional" is not actionable.
    public List<string> ProvisionalReasons { get; set; } = [];
```

> If the `[property:]` attribute target does not compile on a plain property (it is for records), use the
> plain form `[System.Text.Json.Serialization.JsonIgnore(Condition = ...JsonIgnoreCondition.Never)]`.

- [ ] **Step 4: Run to verify pass**

Run: `cd src && dotnet test Smx.Domain.Tests/Smx.Domain.Tests.csproj --filter DosingDoc_DefaultsToNotProvisional`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Records/DosingDoc.cs src/Smx.Domain.Tests/RecordDocsTests.cs
git commit -m "feat(domain): DosingDoc.Provisional - the order-blocking flag"
```

---

### Task 4: Dosing runs unattended

**Files:**
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs` — `RunDosingAsync` (~line 618) and `ResolveDosingInputsAsync` (~line 933)
- Modify: `src/Smx.Backend.Tests/DosingDispatchTests.cs`

Three changes, each deleting a stop:

1. **Delete the unsigned-gate skip.** `RunDosingAsync` currently reads the regulatory gate and `return Skip()` when it is not `approved`. That whole check goes. Dosing runs regardless; the signature governs the export and the order, not the pipeline.
2. **`CompliantSet.Of(verdicts)` → `ProvisionalSet.Of(verdicts)`.** Keep the empty-set `NeedsReview` return: an empty *provisional* set means the agent proposed nothing either, which is a genuine failure to report, not a park.
3. **The two gap returns become flags.** `physicsGaps` → fall back to `DefaultDetectionFloor.Of` per (component, element); `loadingGaps` → drop that substance from the dosed set and record a reason. Neither returns a `StageStatus`.

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Backend.Tests/DosingDispatchTests.cs` (match the file's existing fixture helpers for building a project — do not invent new ones):

```csharp
    [Fact]
    public async Task Dosing_RunsOverProposals_WhenTheGateIsUnsigned_AndStampsProvisional()
    {
        // The core of D10: the operator sees a complete proposed answer in one sitting. Previously this
        // project skipped Dosing entirely behind the unsigned gate.
        var project = await SeedProjectWithProposedVerdictsOnlyAsync();

        await Runner.RunAsync(project.Id, CancellationToken.None);

        var dosing = await Store.GetDosingAsync(project.Id, CancellationToken.None);
        Assert.NotNull(dosing);
        Assert.NotEmpty(dosing!.Codes);
        Assert.True(dosing.Provisional);
        Assert.NotEmpty(dosing.ProvisionalReasons);

        var stage = (await Store.GetProjectAsync(project.Id, CancellationToken.None))!.Stages[Stages.Dosing];
        Assert.Equal(StageStatus.Done, stage.Status);
    }

    [Fact]
    public async Task Dosing_WithNoMeasuredBackground_UsesTheDefaultFloor_AndFlagsIt()
    {
        var project = await SeedProjectWithNoXrfBackgroundAsync();

        await Runner.RunAsync(project.Id, CancellationToken.None);

        var dosing = await Store.GetDosingAsync(project.Id, CancellationToken.None);
        Assert.NotNull(dosing);
        Assert.True(dosing!.Provisional);
        Assert.Contains(dosing.ProvisionalReasons,
            r => r.Contains("no physicist measurement", StringComparison.OrdinalIgnoreCase));

        var stage = (await Store.GetProjectAsync(project.Id, CancellationToken.None))!.Stages[Stages.Dosing];
        Assert.Equal(StageStatus.Done, stage.Status);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter DosingDispatchTests`
Expected: FAIL — the new tests fail (`dosing` is null; Dosing skipped behind the unsigned gate).

- [ ] **Step 3: Make the three changes in `RunDosingAsync`**

Delete these lines entirely:

```csharp
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        if (gate?.Status != "approved") return Skip();
```

Replace `var compliant = CompliantSet.Of(verdicts);` with:

```csharp
        // ProvisionalSet, not CompliantSet — see spec §10.1. The pipeline no longer waits for the R.E., so
        // dosing computes over `Determination ?? ProposedDetermination` and stamps what that cost. Every
        // irreversible act downstream still reads CompliantSet.
        var dosable = ProvisionalSet.Of(verdicts);
        var provisionalReasons = new List<string>(ProvisionalSet.ProvisionalReasons(verdicts));
```

Rename the remaining `compliant` references in this method to `dosable`, and change the empty-set message:

```csharp
        if (dosable.Count == 0)
            return new StageResult(RunOutcome.NeedsReview,
                "nothing may be dosed: no substance carries an operator determination OR an agent proposal " +
                "of 'recommended'.", null, null);
```

Delete the `RegulatoryGate.Armable` early-return block — arming is an export/order precondition now, not a pipeline one.

Replace the two gap returns with flag accumulation:

```csharp
        var (floors, loadings, physicsGaps, loadingGaps) =
            await ResolveDosingInputsAsync(constraints, dosable, ct);

        // Was awaiting-physics. The gap is now a FLAG that blocks the order, not a park (spec §3).
        provisionalReasons.AddRange(physicsGaps.Distinct());
        // Was awaiting-operator. A substance whose metal loading is unknown cannot be dosed at all -- it is
        // dropped from this run and named, rather than stopping the run for the others.
        if (loadingGaps.Count > 0)
        {
            var dropped = loadingGaps.ToHashSet(StringComparer.OrdinalIgnoreCase);
            dosable = [.. dosable.Where(v => !dropped.Contains(v.Cas))];
            provisionalReasons.Add(
                "no metal loading (mass fraction of the marker element in the compound) is on file for: " +
                string.Join(", ", loadingGaps) + ". These substances were left undosed. Enter the loading " +
                "once via POST /projects/{id}/dosing/loading and rerun Dosing.");
        }
        if (dosable.Count == 0)
            return new StageResult(RunOutcome.NeedsReview,
                "every dosable substance was dropped for a missing metal loading: " +
                string.Join(", ", loadingGaps), null, null);
```

After `var dosing = result.Output!;`, stamp the flags:

```csharp
        dosing.ProvisionalReasons = provisionalReasons;
        dosing.Provisional = provisionalReasons.Count > 0;
```

- [ ] **Step 4: Make `ResolveDosingInputsAsync` fall back rather than gap**

Inside its per-element loop, where `DetectionFloor.Compute` returns an error, try the default before recording a gap:

```csharp
            if (computed.Floor is { } measured)
                floors[key] = measured;
            else if (DefaultDetectionFloor.Of(c.Device, v.Element) is { } fallback)
            {
                floors[key] = fallback;
                physicsGaps.Add(fallback.Basis);
            }
            else
                physicsGaps.Add(computed.Error!);
```

A component with neither a measurement nor a device LOD still has no floor: that substance is dropped exactly as a loading gap is, so `RunDosingAsync` must also drop any `dosable` entry with no `floors` key before calling the agent. Add, just before `agents.RunDosingAsync`:

```csharp
        // A substance with no floor at all -- no measurement AND no device limit to fall back on -- cannot
        // be dosed above a floor that does not exist. Dropped and named, never dosed against nothing.
        var floorless = dosable
            .Where(v => !floors.ContainsKey((v.ComponentId, v.Element))).ToList();
        if (floorless.Count > 0)
        {
            dosable = [.. dosable.Except(floorless)];
            provisionalReasons.Add(
                "no ppm floor could be established (no measured background and no device limit of " +
                "detection) for: " + string.Join(", ", floorless.Select(v => $"{v.Element} in '{v.ComponentId}'")) +
                ". These substances were left undosed.");
        }
```

- [ ] **Step 5: Run the full Dosing suite**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter Dosing`
Expected: PASS. Pre-existing tests asserting `awaiting-physics` / `awaiting-operator` will fail — **update them to assert the flag instead of the park**; that is the behaviour change this task exists to make. Do not delete a test without replacing its assertion.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Pipeline/PipelineRunner.cs src/Smx.Backend.Tests/DosingDispatchTests.cs
git commit -m "feat(pipeline): Dosing runs unattended over ProvisionalSet, flagging what it cost"
```

---

### Task 5: Regulatory and Decision land `done`

**Files:**
- Modify: `src/Smx.Backend/Pipeline/PipelineRunner.cs` — `RunRegulatoryAsync` (~528), `RunMatrixAsync` (~600), `RunDecisionAsync` (~754), `OnGateAsync` (~770), `CloseProjectAsync` (~791)
- Modify: `src/Smx.Backend.Tests/PipelineRunnerTests.cs`, `DecisionDispatchTests.cs`, `ProjectCloseDispatchTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `src/Smx.Backend.Tests/PipelineRunnerTests.cs`:

```csharp
    [Fact]
    public async Task Regulatory_LandsDone_WithOnlyProposals_AndTheGateStaysUnsigned()
    {
        var project = await SeedProjectThroughDiscoveryAsync();

        await Runner.RunAsync(project.Id, CancellationToken.None);

        var doc = (await Store.GetProjectAsync(project.Id, CancellationToken.None))!;
        Assert.Equal(StageStatus.Done, doc.Stages[Stages.Regulatory].Status);

        var gate = await Store.GetGateAsync(project.Id, GateTypes.Regulatory, CancellationToken.None);
        Assert.NotEqual("approved", gate?.Status);
    }

    [Fact]
    public async Task Decision_LandsDone_AndProcurementStaysUnreleased()
    {
        var project = await SeedProjectThroughDosingAsync();

        await Runner.RunAsync(project.Id, CancellationToken.None);

        var doc = (await Store.GetProjectAsync(project.Id, CancellationToken.None))!;
        Assert.Equal(StageStatus.Done, doc.Stages[Stages.Decision].Status);

        var decision = await Store.GetDecisionAsync(project.Id, CancellationToken.None);
        Assert.Equal(ProcurementStatus.Unreleased, decision!.Procurement.Status);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter "Regulatory_LandsDone|Decision_LandsDone"`
Expected: FAIL — statuses are `awaiting-RE` / `awaiting-VP`.

- [ ] **Step 3: Stop writing the two park statuses**

In `RunRegulatoryAsync`, change the final return's last argument from `StageStatus.AwaitingRe` to `null`.

In `RunMatrixAsync`, delete the derived-status block entirely:

```csharp
        var gate = await store.GetGateAsync(projectId, GateTypes.Regulatory, ct);
        var stillArmable = RegulatoryGate.Armable(candidates, verdicts).Ok;
        var regStatus = gate?.Status == "approved" && stillArmable ? "done" : StageStatus.AwaitingRe;
        await SetStageAsync(projectId, Stages.Regulatory,
            s => { if (s.Status is not ("failed" or "done")) s.Status = regStatus; }, ct);
```

The stage's status is now what its own run produced. `RegulatoryGate.Armable` keeps its job as an export/order precondition (Task 8) — it simply no longer decides a stage status.

In `RunDecisionAsync`, change the final return's last argument from `StageStatus.AwaitingVp` to `null`.

- [ ] **Step 4: Re-base the close latch on procurement**

`CloseProjectAsync` latches on `Stages[Decision].Status is AwaitingVp`. With the park gone, the latch moves to the state that actually records whether closing has happened:

```csharp
        var decision = await store.GetDecisionAsync(projectId, ct);
        // The latch, re-based off the deleted awaiting-VP park onto the thing that actually records whether
        // this project has been closed. Procurement moves Unreleased -> Released exactly once, so a second
        // signature no-ops here rather than re-running the knowledge writes (re-stamping CreatedAt,
        // re-embedding and re-pushing the conclusion). The writes are idempotent by deterministic id
        // regardless; this is what stops them RUNNING again.
        if (project is null || decision is null) return;
        if (decision.Procurement.Status != ProcurementStatus.Unreleased) return;
```

Delete the old `project.Stages[Stages.Decision].Status is not StageStatus.AwaitingVp` guard.

In `OnGateAsync`, delete the regulatory branch's stage write (`if (s.Status == StageStatus.AwaitingRe) s.Status = "done"`). The gate record is the signature; the stage's status is about whether its agent ran. Keep the VP branch calling `CloseProjectAsync`.

- [ ] **Step 5: Run the affected suites**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter "PipelineRunner|Decision|ProjectClose"`
Expected: PASS after updating pre-existing park assertions to `Done`.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Pipeline/PipelineRunner.cs src/Smx.Backend.Tests/
git commit -m "feat(pipeline): Regulatory and Decision land done; close latches on procurement"
```

---

### Task 6: Delete `awaiting-confirmation`

**Files:**
- Modify: `src/Smx.Domain/Records/ProjectDoc.cs`, `src/Smx.Backend/Agents/InterviewTools.cs`, `src/Smx.Backend/Api/ProjectEndpoints.cs`, `src/Smx.Backend/Pipeline/PipelineRunner.cs`
- Modify: `src/Smx.Backend.Tests/ProjectStartEndpointTests.cs`, `InterviewToolsTests.cs`, `ChatDispatchTests.cs`, `PipelineSupervisorTests.cs`; `src/Smx.Domain.Tests/RecordDocsTests.cs`

Per spec §7, intake runs during creation. A project is created at `pending`, the runner runs intake immediately, and `POST /projects/{id}/start` starts the *analysis*.

- [ ] **Step 1: Write the failing test**

In `src/Smx.Backend.Tests/ProjectStartEndpointTests.cs`:

```csharp
    [Fact]
    public async Task CreatedProject_StartsAtPending_AndIntakeRunsWithoutAPress()
    {
        var id = await CreateProjectThroughInterviewToolAsync();

        var doc = (await Store.GetProjectAsync(id, CancellationToken.None))!;
        Assert.Equal(StageStatus.Pending, doc.Stages[Stages.Intake].Status);

        await Runner.RunAsync(id, CancellationToken.None);

        Assert.NotNull(await Store.GetConstraintsAsync(id, CancellationToken.None));
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter CreatedProject_StartsAtPending`
Expected: FAIL — status is `awaiting-confirmation`.

- [ ] **Step 3: Remove the constant and its writers**

- `ProjectDoc.cs`: delete `AwaitingConfirmation` and its doc comment.
- `InterviewTools.cs`: the created project's intake stage is `StageStatus.Pending`.
- `ProjectEndpoints.cs`: `POST /start` no longer flips `awaiting-confirmation → pending`. It becomes the analysis trigger — it calls `supervisor.TryStart` and returns 202, and is idempotent when the pipeline is already running.
- `PipelineRunner.cs`: delete the `awaiting-confirmation` guard in the intake pass.

- [ ] **Step 4: Run**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter "ProjectStart|InterviewTools|ChatDispatch|PipelineSupervisor"`
Expected: PASS after updating pre-existing assertions.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat: delete awaiting-confirmation; intake runs at creation"
```

---

### Task 7: Delete the five constants and prove it

**Files:**
- Modify: `src/Smx.Domain/Records/ProjectDoc.cs`, `src/Smx.Domain/VpGate.cs`, `src/Smx.Domain/DetectionFloor.cs` (comment only), `src/Smx.Backend/Api/DecisionEndpoints.cs`, `DosingEndpoints.cs`, `ProjectEndpoints.cs`, `Program.cs`, `src/Smx.Infrastructure/SystemTextJsonCosmosSerializer.cs` (comment only)
- Create: `src/Smx.Backend.Tests/NoParkStatusesTests.cs`

- [ ] **Step 1: Write the guard test**

Create `src/Smx.Backend.Tests/NoParkStatusesTests.cs`:

```csharp
using System.Reflection;
using Smx.Domain.Records;

namespace Smx.Backend.Tests;

public class NoParkStatusesTests
{
    [Fact]
    public void StageStatus_DeclaresNoParkConstants()
    {
        // The park family is a compile-time fact, not a review item -- the same discipline that made the
        // frontend's PARKED map a Record over a union. Reintroducing a park means deleting this test, which
        // is a conversation, not an accident.
        var names = typeof(StageStatus)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.DoesNotContain(names, n => n.StartsWith("awaiting-", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            new[] { "cancelled", "done", "failed", "needs-review", "pending", "running" },
            names.Order().ToArray());
    }
}
```

> `cancelled` may not currently be declared on `StageStatus` — spec §3 names it as part of the surviving
> set. If it is absent, add `public const string Cancelled = "cancelled";` rather than weakening the
> assertion.

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter NoParkStatusesTests`
Expected: FAIL — the five constants are still declared.

- [ ] **Step 3: Delete the constants and fix every reference**

Delete `AwaitingRe`, `AwaitingPhysics`, `AwaitingOperator`, `AwaitingVp` from `StageStatus`. Build and fix each compile error:

```bash
cd src && dotnet build Smx.Backend.sln 2>&1 | grep -E "error CS" | sort -u
```

Rules for the fixes:
- A **write** of a park becomes `Done` where the stage's own work finished, or `NeedsReview` where it genuinely failed.
- A **read** comparing against a park becomes a read of the real state: the gate record for a signature, `Procurement.Status` for closure, `DosingDoc.Provisional` for an estimate.
- A **comment** describing a park is rewritten to describe what now happens. Do not leave prose asserting behaviour the code no longer has — this codebase's comments are load-bearing.

- [ ] **Step 4: Run the full suite**

Run: `cd src && dotnet test Smx.Backend.sln`
Expected: PASS, ≥1299 tests plus the new ones.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat: delete the four awaiting-* stage statuses"
```

---

### Task 8: The two irreversible acts still refuse

**Files:**
- Create: `src/Smx.Backend.Tests/UnattendedRunTests.cs`
- Modify: `src/Smx.Backend/Api/ProjectEndpoints.cs` (compliance-package precondition), `DecisionEndpoints.cs` (order precondition)

- [ ] **Step 1: Write the test**

Create `src/Smx.Backend.Tests/UnattendedRunTests.cs`:

```csharp
namespace Smx.Backend.Tests;

/// The shape of the whole change: a project nobody touched runs to the end and STOPS SHORT of both
/// irreversible acts. If this test ever passes its export/order assertions, the product has become a
/// machine that ships taggants with no human in the loop.
public class UnattendedRunTests
{
    [Fact]
    public async Task AnUnattendedProject_ReachesEveryStageDone_WithBothSignaturesOutstanding()
    {
        var id = await SeedFreshProjectAsync();

        await Runner.RunAsync(id, CancellationToken.None);

        var doc = (await Store.GetProjectAsync(id, CancellationToken.None))!;
        foreach (var stage in Stages.Spine)
            Assert.Equal(StageStatus.Done, doc.Stages[stage].Status);

        Assert.NotEqual("approved",
            (await Store.GetGateAsync(id, GateTypes.Regulatory, CancellationToken.None))?.Status);
        Assert.NotEqual("approved",
            (await Store.GetGateAsync(id, GateTypes.Vp, CancellationToken.None))?.Status);
    }

    [Fact]
    public async Task TheCompliancePackageExport_IsRefused_WhileTheRegulatoryGateIsUnsigned()
    {
        var id = await SeedFreshProjectAsync();
        await Runner.RunAsync(id, CancellationToken.None);

        var response = await Client.GetAsync($"/projects/{id}/regulatory/compliance-package");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AnOrder_IsRefused_OverAProvisionalDosing()
    {
        var id = await SeedFreshProjectAsync();
        await Runner.RunAsync(id, CancellationToken.None);

        var dosing = await Store.GetDosingAsync(id, CancellationToken.None);
        Assert.True(dosing!.Provisional, "the fixture must produce a provisional dosing for this to mean anything");

        var response = await Client.PostAsync($"/projects/{id}/orders/1306-38-3", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
```

> Use the existing WebApplicationFactory fixture the other endpoint tests use (see `DecisionEndpointsTests.cs`) rather than inventing `Client` / `Store` / `Runner` — the names above are placeholders for that fixture's members.

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter UnattendedRunTests`
Expected: FAIL on at least the order assertion — nothing yet consults `DosingDoc.Provisional`.

- [ ] **Step 3: Add the provisional precondition to ordering**

In the `POST /projects/{projectId}/orders/{cas}` handler, before the MSDS check:

```csharp
            // A provisional dosing rests on the agent's own proposals, an estimated floor, or both -- see
            // spec §10.1. Ordering is irreversible, so it refuses. This is the flag's whole purpose: it
            // blocks the ORDER, never the pipeline.
            var dosing = await store.GetDosingAsync(projectId, ct);
            if (dosing is { Provisional: true })
                return Results.Conflict(new
                {
                    error = "this dosing is provisional and cannot be ordered against.",
                    reasons = dosing.ProvisionalReasons,
                });
```

- [ ] **Step 4: Verify the export precondition still holds**

`GET /regulatory/compliance-package` should already refuse over an unsigned gate. If it does not, add the same shape of guard reading the regulatory `GateDoc`.

- [ ] **Step 5: Run**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter UnattendedRunTests`
Expected: PASS, 3 tests.

- [ ] **Step 6: Commit**

```bash
git add -A src/
git commit -m "feat(api): a provisional dosing refuses an order"
```

---

### Task 9: The dashboard stops reporting people

**Files:**
- Modify: `src/Smx.Backend/Api/ProjectsListEndpoints.cs`
- Modify: `src/Smx.Backend.Tests/ProjectsListEndpointsTests.cs`

`GET /projects/{id}/dashboard` maps park statuses onto physics / R.E. / VP / client. Those statuses no longer exist, so the mapping is dead code that would report an empty list forever.

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public async Task Dashboard_ReportsOutstandingSignatures_NotPeopleWeAreWaitingOn()
    {
        var id = await SeedProjectRunToCompletionUnsignedAsync();

        var body = await Client.GetFromJsonAsync<JsonElement>($"/projects/{id}/dashboard");

        Assert.False(body.TryGetProperty("blocked", out _), "blocked-on-whom is gone with the parks");

        var outstanding = body.GetProperty("outstandingSignatures").EnumerateArray()
            .Select(e => e.GetProperty("gate").GetString()).ToList();
        Assert.Contains(GateTypes.Regulatory, outstanding);
        Assert.Contains(GateTypes.Vp, outstanding);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter Dashboard_ReportsOutstandingSignatures`
Expected: FAIL — `blocked` still present, `outstandingSignatures` absent.

- [ ] **Step 3: Replace `blocked` with `outstandingSignatures` + `orderBlockers`**

Delete the whole `blocked` loop and its owner `switch`. Replace with:

```csharp
            // Was "blocked on whom". Nothing is blocked on anyone any more (spec §10) -- what is
            // outstanding is a SIGNATURE and, separately, whatever is blocking an order.
            var outstandingSignatures = new List<object>();
            if (regGate?.Status != "approved")
                outstandingSignatures.Add(new { gate = GateTypes.Regulatory, releases = "the compliance-package export" });
            if (vpGate?.Status != "approved")
                outstandingSignatures.Add(new { gate = GateTypes.Vp, releases = "procurement" });

            var dosingDoc = await store.GetDosingAsync(projectId, ct);
            var orderBlockers = dosingDoc is { Provisional: true } ? dosingDoc.ProvisionalReasons : [];
```

Add both to the response object and remove `blocked`. Move the `regGate` / `vpGate` reads above this block if they are currently fetched later.

- [ ] **Step 4: Run**

Run: `cd src && dotnet test Smx.Backend.Tests/Smx.Backend.Tests.csproj --filter ProjectsListEndpoints`
Expected: PASS after updating pre-existing assertions.

- [ ] **Step 5: Commit**

```bash
git add -A src/
git commit -m "feat(api): dashboard reports outstanding signatures, not people"
```

---

### Task 10: Full verification

- [ ] **Step 1: Build both bicep twins** (they are unaffected, but CLAUDE.md requires both compile)

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null
```

- [ ] **Step 2: Full backend suite**

Run: `cd src && dotnet test Smx.Backend.sln`
Expected: PASS. Record the count; it must be ≥ 1299 plus the ~20 tests this plan adds.

- [ ] **Step 3: Confirm no park literal survives production code**

```bash
grep -rn "awaiting-" --include=*.cs src/Smx.Domain src/Smx.Backend src/Smx.Infrastructure
```
Expected: no output.

- [ ] **Step 4: Commit and push**

```bash
git add -A
git commit -m "chore: verify execution core - nothing parks"
git push -u origin worktree-webapp-redesign-3-phase
```

---

## Follow-on plans

| Plan | Content |
|---|---|
| 2 | Cost deleted; Amount + Availability columns; `ClearedCriteria.Cost` → `Availability` |
| 3 | `GET /projects/{id}/table` unified projection; wide XLSX export |
| 4 | Amendments; the rerun dependency table; signature-voiding confirmation; rerun diffs |
| 5 | Frontend shell: top-bar scope, one sidebar, agent right-collapsible |
| 6 | Frontend phase screens: Overview, Discovery, Regulatory, Dosing, Sign-off, Full matrix |

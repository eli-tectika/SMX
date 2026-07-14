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
    public async Task QueryLearnedConclusions_SearchesScopeForm()
    {
        // Form is a first-class scope dimension for Discovery tiering; a browse that omits it hides
        // every form-scoped conclusion from the operator.
        var store = new InMemoryKnowledgeStore();
        var c = Conclusion("zr|bottle", "Prefer this form.");
        await store.UpsertLearnedConclusionAsync(new LearnedConclusionDoc
        {
            Id = c.Id, Kind = c.Kind, Finding = c.Finding, Confidence = c.Confidence,
            Scope = new ConclusionScope("Zr", "neodecanoate", "bottle", null, null, null),
            Provenance = c.Provenance, CreatedAt = c.CreatedAt,
        });
        Assert.Single(await store.QueryLearnedConclusionsAsync("neodecanoate"));
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
    public async Task FindMarkers_AndsSuppliedDimensions_IgnoresNulls_AndOnlyApproved()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMarkerAsync(Marker("m1", "anti-counterfeit"));                       // label/overt
        await store.UpsertMarkerAsync(new MarkerLibraryDoc                                     // same app, different material
        {
            Id = KnowledgeIds.Marker("m2"), Composition = new MarkerComposition(["Y"], 100, "1:0"),
            ValidatedFor = new ValidatedFor("anti-counterfeit", "bottle", "covert"), SourceProject = "p2", CreatedAt = "t",
        });
        await store.UpsertMarkerAsync(new MarkerLibraryDoc                                     // matches on every dimension but retired
        {
            Id = KnowledgeIds.Marker("m3"), Composition = new MarkerComposition(["Zr"], 200, "1:0"),
            ValidatedFor = new ValidatedFor("anti-counterfeit", "label", "overt"), SourceProject = "p3",
            Status = "superseded", CreatedAt = "t",
        });

        // AND over the supplied dimensions: m2's material excludes it, m3 is not approved for reuse.
        var hit = await store.FindMarkersAsync("anti-counterfeit", "label", "overt");
        Assert.Equal([KnowledgeIds.Marker("m1")], hit.Select(m => m.Id));

        // A null dimension is not constrained (m3 still excluded — status is not a dimension).
        Assert.Equal(2, (await store.FindMarkersAsync("anti-counterfeit", null, null)).Count);
        Assert.Equal(2, (await store.FindMarkersAsync(null, null, null)).Count);

        // Case-insensitive contains per dimension.
        Assert.Single(await store.FindMarkersAsync("ANTI-COUNTERFEIT", "LAB", null));

        // A dimension that matches nothing excludes everything, even though the others match.
        Assert.Empty(await store.FindMarkersAsync("anti-counterfeit", "label", "banknote"));
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

    /// The fake used to hand out the STORED instance. That is not what Cosmos does — Cosmos round-trips
    /// through JSON, so a read hands back a fresh graph and a mutation of it persists only if you upsert it.
    /// A fake that aliases certifies read-modify-writes that production silently drops (the whole reason
    /// InMemoryRecordStore deep-copies). Pinned here on MSDS because the bug was general to all four types.
    [Fact]
    public async Task Msds_Get_ReturnsASnapshot_SoMutatingItDoesNotWriteBackToTheStore()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertMsdsAsync(new MsdsRegistryDoc { Id = KnowledgeIds.Msds("c1"), Cas = "c1", Supplier = "Acme", Version = "1", Date = "d" });

        (await store.GetMsdsAsync("c1"))!.ReviewStatus = MsdsReviewStatus.Reviewed;   // no upsert follows

        // In Cosmos this MSDS is still unreviewed — and it gates procurement.
        Assert.Equal(MsdsReviewStatus.Unreviewed, (await store.GetMsdsAsync("c1"))!.ReviewStatus);
    }
}

public class SubstancePropertyStoreTests
{
    private static SubstancePropertyDoc Y2O3() => new()
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
        await store.UpsertSubstancePropertyAsync(Y2O3());

        var got = (await store.GetSubstancePropertyAsync("1314-36-9"))!;
        Assert.Equal(0.787, got.MetalLoading);
        Assert.Equal("2×M(Y)/M(Y2O3)", got.Basis);   // the number is worthless without the basis that backs it
        Assert.Equal("Y", got.Element);

        // Keyed by CAS, and only by CAS: a different compound is a miss, not a lucky hit.
        Assert.Null(await store.GetSubstancePropertyAsync("1314-23-4"));
    }

    [Fact]
    public async Task Upsert_ReplacesByCas_SoACorrectionOverwritesRatherThanDuplicates()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertSubstancePropertyAsync(Y2O3());

        // A SEPARATELY constructed doc for the same CAS — never an alias of the first, so passing this test
        // cannot be an artifact of two names for one object. It is a genuine second write to the same key.
        var corrected = Y2O3();
        corrected.MetalLoading = 0.7874;
        corrected.Basis = "recomputed from IUPAC 2021 masses";
        await store.UpsertSubstancePropertyAsync(corrected);

        // Last write wins on every field — this kills an insert-only (TryAdd) store, which would silently
        // keep serving the WRONG loading forever with the correction sitting right there unused.
        var got = (await store.GetSubstancePropertyAsync("1314-36-9"))!;
        Assert.Equal(0.7874, got.MetalLoading);
        Assert.Equal("recomputed from IUPAC 2021 masses", got.Basis);
    }

    [Fact]
    public async Task Get_ReturnsASnapshot_SoMutatingItDoesNotSilentlyRewriteTheStore()
    {
        var store = new InMemoryKnowledgeStore();
        await store.UpsertSubstancePropertyAsync(Y2O3());

        (await store.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading = 0.5;   // no upsert follows

        // Cosmos hands back a fresh deserialization, so that mutation went nowhere. The fake must agree, or
        // a read-modify-forget-to-write bug goes green here and mis-doses in Azure.
        Assert.Equal(0.787, (await store.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading);
    }

    [Fact]
    public async Task Upsert_SnapshotsTheDoc_SoLaterMutationsOfTheCallersObjectDoNotLeakIn()
    {
        var store = new InMemoryKnowledgeStore();
        var doc = Y2O3();
        await store.UpsertSubstancePropertyAsync(doc);

        doc.MetalLoading = 0.5;   // the caller keeps using its object; no second upsert

        // An upsert SNAPSHOTS. In Cosmos this change never left the process.
        Assert.Equal(0.787, (await store.GetSubstancePropertyAsync("1314-36-9"))!.MetalLoading);
    }
}

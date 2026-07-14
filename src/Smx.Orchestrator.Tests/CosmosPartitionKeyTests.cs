using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Infrastructure;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// <summary>
/// The regression guard for the knowledge layer's <b>partition keys</b> — the sibling of
/// <see cref="CosmosQueryTextTests"/>, and the same failure family.
///
/// <para>
/// A Cosmos container declares a partition-key PATH (<c>/cas</c>). On every write, Cosmos extracts the key
/// from the DOCUMENT at that path and compares it to the <see cref="PartitionKey"/> the SDK call passed.
/// If they disagree it rejects the write — <b>in Azure only</b>. On every point-read it routes to the
/// physical partition that <see cref="PartitionKey"/> names; a wrong one is a permanent 404 that is
/// indistinguishable from "the operator has not entered this yet", so Dosing parks in
/// <c>awaiting-operator</c> forever and the logs say nothing.
/// </para>
///
/// <para>
/// Neither failure can be seen by the rest of the suite: <c>InMemoryKnowledgeStore</c> is a dictionary, and
/// a dictionary does not have partition keys. So these tests drive the <b>real</b>
/// <see cref="CosmosKnowledgeStore"/> against a <see cref="CapturingContainer"/> and assert the one thing
/// the fake cannot: that the key the store PASSES is the key Cosmos will READ out of the serialized document
/// at the path the Bicep actually declares.
/// </para>
///
/// <para>
/// Concretely, each of the four containers gets both halves: <c>upsert_passes_the_partition_key_…</c> pins
/// the write, and <c>point_read_addresses_…</c> pins the read to the coordinates that write created.
/// </para>
/// </summary>
public sealed class CosmosPartitionKeyTests
{
    // ---- The deployed truth ----------------------------------------------------------------------

    /// The partition-key path of each knowledge container, AS DEPLOYED. This mirrors `var knowledgeContainers`
    /// in infra/modules/data.bicep and its infra/single-rg twin — and
    /// <see cref="The_declared_paths_match_both_bicep_twins"/> parses both files and fails if this table and
    /// the infrastructure ever drift apart. That is what stops this from being a test that merely agrees with
    /// itself.
    private static readonly Dictionary<string, string> DeclaredPkPath = new()
    {
        ["learned-conclusions"] = "/kind",
        ["marker-library"] = "/id",
        ["msds-registry"] = "/cas",
        ["substance-properties"] = "/cas",
    };

    [Fact]
    public void The_declared_paths_match_both_bicep_twins()
    {
        foreach (var bicep in new[] { "infra/modules/data.bicep", "infra/single-rg/modules/data.bicep" })
        {
            var path = Path.Combine(RepoRoot(), bicep);
            Assert.True(File.Exists(path), $"{bicep} is missing — the knowledge containers must be deployed by both twins.");

            // `var knowledgeContainers = [ { name: 'x', pk: '/y' } … ]`
            var array = Regex.Match(File.ReadAllText(path), @"var knowledgeContainers\s*=\s*\[(.*?)\]", RegexOptions.Singleline);
            Assert.True(array.Success, $"could not find `var knowledgeContainers` in {bicep}");

            var declared = Regex.Matches(array.Groups[1].Value, @"\{\s*name:\s*'([^']+)'\s*,\s*pk:\s*'([^']+)'\s*\}")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

            Assert.Equal(DeclaredPkPath, declared);
        }
    }

    /// The test file's own compile-time path — independent of the working directory and the bin layout.
    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ---- The harness -----------------------------------------------------------------------------

    /// The real CosmosKnowledgeStore, wired to four containers that record instead of calling Azure. The
    /// serializer is the production one, so the documents captured are byte-for-byte what Cosmos would parse.
    private sealed class Knowledge
    {
        private static readonly CosmosSerializer Serializer = new SystemTextJsonCosmosSerializer(Json.Options);

        public CapturingContainer Conclusions { get; } = new("learned-conclusions", Serializer);
        public CapturingContainer Markers { get; } = new("marker-library", Serializer);
        public CapturingContainer Msds { get; } = new("msds-registry", Serializer);
        public CapturingContainer Substances { get; } = new("substance-properties", Serializer);
        public CosmosKnowledgeStore Store { get; }

        public Knowledge() => Store = new CosmosKnowledgeStore(Conclusions, Markers, Msds, Substances);
    }

    /// Walks a Cosmos partition-key path into the document exactly as Cosmos does: `/cas` means "the value of
    /// the top-level `cas` key of the document AS SERIALIZED". Note this reads the JSON the production
    /// serializer emitted — not the C# object — so a rename, a stray [JsonPropertyName], or a change to
    /// Json.Options' naming policy shows up here as the absent key it would really be.
    private static string ValueCosmosWouldExtract(JsonElement document, string pkPath)
    {
        var element = document;
        foreach (var segment in pkPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out element),
                $"the serialized document has no '{segment}' key, so Cosmos can extract no partition key at " +
                $"'{pkPath}' and would reject the write. Document keys: " +
                string.Join(", ", document.EnumerateObject().Select(p => p.Name)));
        }

        Assert.Equal(JsonValueKind.String, element.ValueKind);
        return element.GetString()!;
    }

    /// THE WRITE HALF. The PartitionKey the store passed must equal the one Cosmos will build from the
    /// document it passed alongside it. A wrong property in the `new PartitionKey(…)` argument fails here.
    private static void AssertUpsertKeyIsWhatCosmosWillExtract(CapturingContainer container)
    {
        var (document, passed) = Assert.Single(container.Upserts);
        var extracted = new PartitionKey(ValueCosmosWouldExtract(document, DeclaredPkPath[container.Id]));

        Assert.Equal(extracted, passed);
    }

    /// THE READ HALF. A point-read must address the exact coordinates — id AND partition key — that the
    /// upsert created, or it is a permanent 404 that reads as "not entered yet".
    private static void AssertReadAddressesTheUpsertedDocument(CapturingContainer container)
    {
        var (document, _) = Assert.Single(container.Upserts);
        var read = Assert.Single(container.Reads);

        Assert.Equal(document.GetProperty("id").GetString(), read.Id);
        Assert.Equal(new PartitionKey(ValueCosmosWouldExtract(document, DeclaredPkPath[container.Id])), read.PartitionKey);
    }

    // ---- The documents ---------------------------------------------------------------------------
    // Every document below is built so that NO TWO of its candidate key properties share a value. That is
    // deliberate: it is what makes a mutation detectable. If a doc's `id` happened to equal its `cas`, then
    // swapping `new PartitionKey(doc.Cas)` for `new PartitionKey(doc.Id)` would still pass and the guard
    // would be theatre.

    private const string Cas = "1314-36-9";
    private const string ScopeKey = "pet|y2o3";

    private static LearnedConclusionDoc Conclusion() => new()
    {
        Id = KnowledgeIds.LearnedConclusion(KnowledgeKinds.Dosing, ScopeKey),   // "dosing|pet|y2o3"
        Kind = KnowledgeKinds.Dosing,                                           // "dosing"  — the PK
        Scope = new ConclusionScope("Y", "oxide", "PET", null, null, null),
        Finding = "600 ppm reads cleanly on PET",
        Confidence = 0.9,
        Provenance = new ConclusionProvenance(["p1"], ["d1"]),
        CreatedAt = "2026-07-14T10:00:00Z",
    };

    private static MarkerLibraryDoc Marker() => new()
    {
        Id = KnowledgeIds.Marker("SMX-001"),                                    // "marker|SMX-001" — the PK
        Composition = new MarkerComposition(["Y"], 600, "1:0"),
        ValidatedFor = new ValidatedFor("bottle", "PET", "authentication"),
        SourceProject = "p1",
        CreatedAt = "2026-07-14T10:00:00Z",
    };

    private static MsdsRegistryDoc Msds() => new()
    {
        Id = KnowledgeIds.Msds(Cas),                                            // "msds|1314-36-9"
        Cas = Cas,                                                              // "1314-36-9" — the PK
        Supplier = "Acme",
        Version = "3",
        Date = "2026-01-01",
    };

    private static SubstancePropertyDoc Substance() => new()
    {
        Id = KnowledgeIds.SubstanceProperty(Cas),                               // "substance-property|1314-36-9"
        Cas = Cas,                                                              // "1314-36-9" — the PK
        Element = "Y",
        Form = "oxide",
        MetalLoading = 0.787,
        Basis = "2xM(Y)/M(Y2O3)",
        EnteredAt = "2026-07-14T10:00:00.0000000+00:00",
    };

    // ---- learned-conclusions, PK /kind -----------------------------------------------------------

    [Fact]
    public async Task LearnedConclusion_upsert_passes_the_partition_key_cosmos_will_extract()
    {
        var k = new Knowledge();
        await k.Store.UpsertLearnedConclusionAsync(Conclusion());
        AssertUpsertKeyIsWhatCosmosWillExtract(k.Conclusions);
    }

    [Fact]
    public async Task LearnedConclusion_point_read_addresses_the_document_the_upsert_wrote()
    {
        var k = new Knowledge();
        var doc = Conclusion();
        await k.Store.UpsertLearnedConclusionAsync(doc);

        Assert.Null(await k.Store.GetLearnedConclusionAsync(doc.Kind, ScopeKey));   // the container is empty: 404 -> null
        AssertReadAddressesTheUpsertedDocument(k.Conclusions);
    }

    // ---- marker-library, PK /id ------------------------------------------------------------------

    [Fact]
    public async Task Marker_upsert_passes_the_partition_key_cosmos_will_extract()
    {
        var k = new Knowledge();
        await k.Store.UpsertMarkerAsync(Marker());
        AssertUpsertKeyIsWhatCosmosWillExtract(k.Markers);
    }

    [Fact]
    public async Task Marker_point_read_addresses_the_document_the_upsert_wrote()
    {
        var k = new Knowledge();
        var doc = Marker();
        await k.Store.UpsertMarkerAsync(doc);

        Assert.Null(await k.Store.GetMarkerAsync(doc.Id));
        AssertReadAddressesTheUpsertedDocument(k.Markers);
    }

    // ---- msds-registry, PK /cas ------------------------------------------------------------------

    [Fact]
    public async Task Msds_upsert_passes_the_partition_key_cosmos_will_extract()
    {
        var k = new Knowledge();
        await k.Store.UpsertMsdsAsync(Msds());
        AssertUpsertKeyIsWhatCosmosWillExtract(k.Msds);
    }

    [Fact]
    public async Task Msds_point_read_addresses_the_document_the_upsert_wrote()
    {
        var k = new Knowledge();
        var doc = Msds();
        await k.Store.UpsertMsdsAsync(doc);

        Assert.Null(await k.Store.GetMsdsAsync(doc.Cas));
        AssertReadAddressesTheUpsertedDocument(k.Msds);
    }

    // ---- substance-properties, PK /cas -----------------------------------------------------------
    // The one that started this: the operator's metal loading. A wrong PK here means the loading is never
    // persisted, Dosing parks in awaiting-operator forever, and nothing anywhere says why.

    [Fact]
    public async Task SubstanceProperty_upsert_passes_the_partition_key_cosmos_will_extract()
    {
        var k = new Knowledge();
        await k.Store.UpsertSubstancePropertyAsync(Substance());
        AssertUpsertKeyIsWhatCosmosWillExtract(k.Substances);
    }

    [Fact]
    public async Task SubstanceProperty_point_read_addresses_the_document_the_upsert_wrote()
    {
        var k = new Knowledge();
        var doc = Substance();
        await k.Store.UpsertSubstancePropertyAsync(doc);

        Assert.Null(await k.Store.GetSubstancePropertyAsync(doc.Cas));
        AssertReadAddressesTheUpsertedDocument(k.Substances);
    }
}

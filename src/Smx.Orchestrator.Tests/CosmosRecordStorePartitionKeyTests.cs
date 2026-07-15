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
/// The partition-key guard for the RECORD store — the sibling of <see cref="CosmosPartitionKeyTests"/>
/// (which covers the knowledge layer), and the same failure family, applied to the one container that was
/// not covered at all.
///
/// <para>
/// The `record` container is partitioned by <c>/projectId</c>. On every write Cosmos extracts the key from
/// the DOCUMENT at that path and compares it to the <see cref="PartitionKey"/> the SDK call passed; disagree
/// and it rejects the write — <b>in Azure only</b>. <see cref="InMemoryRecordStore"/> is a dictionary keyed
/// by id and never looks at a partition key at all, so if <see cref="CosmosRecordStore"/> ever passed the
/// wrong field (e.g. <c>doc.Id</c> — <c>"p1|dosing"</c> — instead of <c>doc.ProjectId</c> — <c>"p1"</c>),
/// EVERY upsert would be rejected in production with a green suite the whole time. This drives the REAL
/// <see cref="CosmosRecordStore"/> against a <see cref="CapturingContainer"/> and asserts the one thing the
/// fake cannot: the key the store passes is the key Cosmos will read out of the serialized document.
/// </para>
///
/// <para>
/// Dosing and Cost are the two representatives (both id != projectId, so a <c>doc.Id</c>-as-PK swap is
/// detectable). They exercise the shared <c>Upsert(doc, doc.ProjectId, …)</c> write helper and the shared
/// <c>ReadAsync(id, projectId, …)</c> read helper that every record doc goes through.
/// </para>
/// </summary>
public sealed class CosmosRecordStorePartitionKeyTests
{
    /// The `record` container's partition-key path, AS DEPLOYED. `The_declared_path_matches_both_bicep_twins`
    /// parses both data.bicep twins and fails if this constant and the infrastructure ever drift apart — the
    /// same thing that keeps the sibling guard from merely agreeing with itself.
    private const string PkPath = "/projectId";

    [Fact]
    public void The_declared_path_matches_both_bicep_twins()
    {
        foreach (var bicep in new[] { "infra/modules/data.bicep", "infra/single-rg/modules/data.bicep" })
        {
            var path = Path.Combine(RepoRoot(), bicep);
            Assert.True(File.Exists(path), $"{bicep} is missing — the record container must be deployed by both twins.");

            // The `record` container is a standalone resource (not the `var knowledgeContainers` array), so
            // grab the first partitionKey path after `name: 'record'`. The closing quote in `'record'` is load
            // bearing: it excludes `name: 'record-leases'` (PK /id), which would otherwise be a false match.
            var m = Regex.Match(File.ReadAllText(path),
                @"name:\s*'record'.*?partitionKey:\s*\{\s*paths:\s*\[\s*'([^']+)'",
                RegexOptions.Singleline);
            Assert.True(m.Success, $"could not find the `record` container's partitionKey in {bicep}");
            Assert.Equal(PkPath, m.Groups[1].Value);
        }
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));

    // ---- The harness -----------------------------------------------------------------------------

    /// The real CosmosRecordStore, wired to one container that records instead of calling Azure. The
    /// serializer is the production one, so the bytes captured are byte-for-byte what Cosmos would parse.
    private sealed class Store
    {
        private static readonly CosmosSerializer Serializer = new SystemTextJsonCosmosSerializer(Json.Options);
        public CapturingContainer Records { get; } = new("record", Serializer);
        public CosmosRecordStore Value { get; }
        public Store() => Value = new CosmosRecordStore(Records);
    }

    /// Walks the `/projectId` path into the serialized document exactly as Cosmos does — reading the JSON the
    /// production serializer emitted, not the C# object, so a rename or a naming-policy change surfaces here as
    /// the absent key it would really be.
    private static string ValueCosmosWouldExtract(JsonElement document)
    {
        var element = document;
        foreach (var segment in PkPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(element.ValueKind == JsonValueKind.Object && element.TryGetProperty(segment, out element),
                $"the serialized document has no '{segment}' key, so Cosmos can extract no partition key at " +
                $"'{PkPath}' and would reject the write. Document keys: " +
                string.Join(", ", document.EnumerateObject().Select(p => p.Name)));
        }
        Assert.Equal(JsonValueKind.String, element.ValueKind);
        return element.GetString()!;
    }

    /// THE WRITE HALF. The PartitionKey the store passed must equal the one Cosmos will build from the document
    /// it passed alongside it. Passing `doc.Id` (id != projectId) instead of `doc.ProjectId` fails here.
    private static void AssertUpsertKeyIsWhatCosmosWillExtract(CapturingContainer container)
    {
        var (document, passed) = Assert.Single(container.Upserts);
        Assert.Equal(new PartitionKey(ValueCosmosWouldExtract(document)), passed);
    }

    /// THE READ HALF. A point-read must address the exact coordinates — id AND partition key — the upsert
    /// created, or it is a permanent 404 that reads as "the operator has not entered this yet".
    private static void AssertReadAddressesTheUpsertedDocument(CapturingContainer container)
    {
        var (document, _) = Assert.Single(container.Upserts);
        var read = Assert.Single(container.Reads);
        Assert.Equal(document.GetProperty("id").GetString(), read.Id);
        Assert.Equal(new PartitionKey(ValueCosmosWouldExtract(document)), read.PartitionKey);
    }

    // ---- The documents ---------------------------------------------------------------------------
    // id != projectId in both, deliberately: it is what makes a `doc.Id`-as-PK mutation detectable. If the id
    // happened to equal the projectId (as it does for ProjectDoc), the swap would still pass and this would be
    // theatre — so Project is not one of the representatives.

    private static DosingDoc Dosing() => new()
    {
        Id = RecordIds.Dosing("p1"),   // "p1|dosing"
        ProjectId = "p1",              // "p1" — the PK
        GeneratedAt = "2026-07-15T00:00:00Z",
    };

    private static CostDoc Cost() => new()
    {
        Id = RecordIds.Cost("p1"),     // "p1|cost"
        ProjectId = "p1",              // "p1" — the PK
        GeneratedAt = "2026-07-15T00:00:00Z",
    };

    // ---- Dosing ----------------------------------------------------------------------------------

    [Fact]
    public async Task Dosing_upsert_passes_the_partition_key_cosmos_will_extract()
    {
        var s = new Store();
        await s.Value.UpsertDosingAsync(Dosing());
        AssertUpsertKeyIsWhatCosmosWillExtract(s.Records);
    }

    [Fact]
    public async Task Dosing_point_read_addresses_the_document_the_upsert_wrote()
    {
        var s = new Store();
        await s.Value.UpsertDosingAsync(Dosing());

        Assert.Null(await s.Value.GetDosingAsync("p1"));   // the container is empty: 404 -> null
        AssertReadAddressesTheUpsertedDocument(s.Records);
    }

    // ---- Cost — the highest-value mutation lives here (UpsertCostAsync passing doc.Id) ------------

    [Fact]
    public async Task Cost_upsert_passes_the_partition_key_cosmos_will_extract()
    {
        var s = new Store();
        await s.Value.UpsertCostAsync(Cost());
        AssertUpsertKeyIsWhatCosmosWillExtract(s.Records);
    }

    [Fact]
    public async Task Cost_point_read_addresses_the_document_the_upsert_wrote()
    {
        var s = new Store();
        await s.Value.UpsertCostAsync(Cost());

        Assert.Null(await s.Value.GetCostAsync("p1"));   // the container is empty: 404 -> null
        AssertReadAddressesTheUpsertedDocument(s.Records);
    }
}

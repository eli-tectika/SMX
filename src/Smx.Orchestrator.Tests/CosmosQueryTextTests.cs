using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Infrastructure;
using Smx.Infrastructure.Search;

namespace Smx.Orchestrator.Tests;

/// <summary>
/// The regression guard for the Cosmos LINQ provider's member naming.
///
/// The rest of the suite runs against <c>InMemoryRecordStore</c> — a dictionary that never generates
/// SQL — so it is *structurally incapable* of catching a wrong-column-name bug: the fakes stay green
/// while every real query in Azure returns an empty list. That is exactly what happened once already
/// (see SystemTextJsonCosmosSerializer's class doc). These tests close that hole the only way a
/// fake-backed suite can: by asserting on the SQL text the SDK actually emits.
///
/// This needs no emulator and no network. <see cref="CosmosClient"/>'s constructor is lazy (it does not
/// connect; only CreateAndInitializeAsync does) and <c>ToQueryDefinition()</c> is pure CPU — it walks the
/// expression tree and renders SQL. Nothing here ever leaves the process.
/// </summary>
public sealed class CosmosQueryTextTests
{
    // The Cosmos emulator's documented endpoint + well-known public key. Never contacted; a CosmosClient
    // just needs *a* syntactically valid endpoint and key to be constructed.
    private const string EmulatorEndpoint = "https://localhost:8081";
    private const string EmulatorKey =
        "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    /// Exactly the CosmosClientOptions both hosts construct (src/Smx.Backend/Program.cs,
    /// src/Smx.Orchestrator/Program.cs). If those diverge from this, these tests stop being meaningful.
    private static CosmosClientOptions ProductionOptions() => new()
    {
        Serializer = new SystemTextJsonCosmosSerializer(Json.Options),
    };

    private static IQueryable<T> Query<T>() =>
        new CosmosClient(EmulatorEndpoint, EmulatorKey, ProductionOptions())
            .GetContainer("smx", "record")
            .GetItemLinqQueryable<T>(
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey("p1") });

    /// The twin of <see cref="Query{T}"/> for the one store method that is NOT partition-scoped.
    ///
    /// The distinction is invisible in the emitted SQL — a partition key is a request option, not query
    /// text — so this exists to keep the test honest about what it pins rather than because the string
    /// differs. What it therefore CANNOT catch: a PartitionKey left on GetProjectsAsync's query, which
    /// would return exactly the one project in that partition, silently, in Azure only. Nothing here can
    /// see that; only reading the store or driving a real emulator can.
    private static IQueryable<T> CrossPartitionQuery<T>() =>
        new CosmosClient(EmulatorEndpoint, EmulatorKey, ProductionOptions())
            .GetContainer("smx", "record")
            .GetItemLinqQueryable<T>();

    /// Asserts the emitted SQL addresses <paramref name="camel"/> (the on-the-wire name) and NOT the
    /// PascalCase C# name. The negative half is the load-bearing one: it is what fails loudly if someone
    /// re-bases SystemTextJsonCosmosSerializer on CosmosSerializer.
    private static void AssertWireName(string sql, string camel)
    {
        var pascal = char.ToUpperInvariant(camel[0]) + camel[1..];
        Assert.Contains($"root[\"{camel}\"]", sql);
        Assert.DoesNotContain($"root[\"{pascal}\"]", sql);
    }

    // ---- CosmosRecordStore.GetVerdictsAsync ------------------------------------------------------

    [Fact]
    public void GetVerdicts_query_uses_wire_property_names()
    {
        var sql = Query<VerdictDoc>()
            .Where(d => d.Type == RecordTypes.Verdict)
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "type");
    }

    // ---- CosmosRecordStore.GetProjectsAsync ------------------------------------------------------

    /// The dashboard's only source of project ids, and the landing page itself is the stake. A PascalCase
    /// `root["Type"]` here would not throw — it would match zero documents, and the operator would be told
    /// the record holds no projects at all while every one of them sat there. The `createdAt` half is the
    /// ORDER BY: get that name wrong and the list comes back in arbitrary order while claiming newest-first.
    ///
    /// No Take: the production query is unbounded, and a cap pinned here that the store does not have would
    /// be a test agreeing with itself.
    [Fact]
    public void GetProjects_query_uses_wire_property_names()
    {
        var sql = CrossPartitionQuery<ProjectDoc>()
            .Where(d => d.Type == RecordTypes.Project)
            .OrderByDescending(d => d.CreatedAt)
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "type");
        AssertWireName(sql, "createdAt");
    }

    /// The loop-closer for the list, in the shape of
    /// <see cref="Query_property_names_match_the_keys_the_serializer_actually_writes"/>: whatever keys the
    /// production serializer writes for a ProjectDoc are the keys its query must address. This is the half
    /// that cannot pass while the writer and the reader disagree.
    [Fact]
    public void GetProjects_query_property_names_match_the_keys_the_serializer_actually_writes()
    {
        var serializer = new SystemTextJsonCosmosSerializer(Json.Options);
        var doc = ProjectDoc.Create("proj-1", "Acme", "Bottle", JsonDocument.Parse("{}").RootElement);
        doc.CreatedAt = "2026-07-16T01:00:00.0000000+00:00";

        using var stream = serializer.ToStream(doc);
        var onDisk = JsonDocument.Parse(stream).RootElement;

        var sql = CrossPartitionQuery<ProjectDoc>()
            .Where(d => d.Type == RecordTypes.Project)
            .OrderByDescending(d => d.CreatedAt)
            .ToQueryDefinition().QueryText;

        foreach (var member in new[] { nameof(ProjectDoc.Type), nameof(ProjectDoc.CreatedAt) })
        {
            var wireName = Json.Options.PropertyNamingPolicy!.ConvertName(member);
            Assert.True(onDisk.TryGetProperty(wireName, out _),
                $"serializer did not write a '{wireName}' key; document keys: " +
                string.Join(", ", onDisk.EnumerateObject().Select(p => p.Name)));
            Assert.Contains($"root[\"{wireName}\"]", sql);
        }
    }

    // ---- CosmosRecordStore.GetRevisionsAsync -----------------------------------------------------

    [Fact]
    public void GetRevisions_query_uses_wire_property_names()
    {
        var sql = Query<RevisionDoc>()
            .Where(d => d.Type == RecordTypes.Revision)
            .OrderBy(d => d.CreatedAt)
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "type");
        AssertWireName(sql, "createdAt");   // the ORDER BY: a PascalCase key here silently unsorts the audit trail
    }

    // ---- CosmosRecordStore.GetChatThreadAsync ----------------------------------------------------

    /// The thread is two queries, one per doc type. Both filter on `stage` as well as `type` — a PascalCase
    /// `root["Stage"]` here would not throw, it would return an empty thread, and the agent would answer the
    /// operator's follow-up with no memory of the conversation it is in the middle of.
    [Fact]
    public void GetChatThread_messages_query_uses_wire_property_names()
    {
        var sql = Query<ChatMessageDoc>()
            .Where(d => d.Type == RecordTypes.ChatMessage && d.Stage == Stages.Discovery)
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "type");
        AssertWireName(sql, "stage");
    }

    [Fact]
    public void GetChatThread_replies_query_uses_wire_property_names()
    {
        var sql = Query<ChatReplyDoc>()
            .Where(d => d.Type == RecordTypes.ChatReply && d.Stage == Stages.Discovery)
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "type");
        AssertWireName(sql, "stage");
    }

    // ---- CosmosCatalogLookup ---------------------------------------------------------------------

    [Fact]
    public void CatalogLookup_query_uses_wire_property_names()
    {
        var sql = Query<CosmosCatalogLookup.Row>()
            .Where(r => r.DocType == "product")
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "docType");
    }

    /// The Cost stage audits every supplier figure against its ref-catalog listing, so LookupAsync must carry
    /// `price` and `pack` off each product doc. The seed writes them camelCase (Reference/Seed/catalog-products.json);
    /// a PascalCase `root["Price"]` here would match ZERO docs in Azure and refuse every price silently. This pins
    /// the projection's actual wire names — the exact silent-in-Azure trap this suite exists for.
    [Fact]
    public void CatalogLookup_projection_addresses_price_and_pack_camelCase()
    {
        var sql = Query<CosmosCatalogLookup.Row>()
            .Where(r => r.DocType == "product")
            .Select(r => new { r.Id, r.Element, r.Molecule, r.Compound, r.Cas, r.Purity, r.Supplier, r.Price, r.Pack })
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "price");
        AssertWireName(sql, "pack");
    }

    // ---- CosmosCompatibilityLookup ---------------------------------------------------------------

    [Fact]
    public void CompatibilityLookup_query_uses_wire_property_names()
    {
        var sql = Query<CosmosCompatibilityLookup.Row>()
            .Where(r => r.Substrate == "PET")
            .Take(1)
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "substrate");
    }

    // ---- The two halves, pinned to each other ----------------------------------------------------

    /// The assertions above compare the SQL against a *hardcoded* camelCase string. This one closes the
    /// loop: it pins the query side to the document side, so the test cannot pass while the writer and
    /// the reader disagree — whatever key the serializer writes is the key the query must address.
    [Fact]
    public void Query_property_names_match_the_keys_the_serializer_actually_writes()
    {
        var serializer = new SystemTextJsonCosmosSerializer(Json.Options);
        var doc = new RevisionDoc
        {
            Id = "p1|revision|discovery|a", ProjectId = "p1", Stage = "discovery",
            Target = "t", Reason = "r", CreatedAt = "2026-07-13T01:00:00Z",
        };

        using var stream = serializer.ToStream(doc);
        var onDisk = JsonDocument.Parse(stream).RootElement;

        var sql = Query<RevisionDoc>()
            .Where(d => d.Type == RecordTypes.Revision)
            .OrderBy(d => d.CreatedAt)
            .ToQueryDefinition().QueryText;

        // Every property the query addresses must be a key that actually exists on the stored document.
        foreach (var member in new[] { nameof(RevisionDoc.Type), nameof(RevisionDoc.CreatedAt) })
        {
            var wireName = Json.Options.PropertyNamingPolicy!.ConvertName(member);
            Assert.True(onDisk.TryGetProperty(wireName, out _),
                $"serializer did not write a '{wireName}' key; document keys: " +
                string.Join(", ", onDisk.EnumerateObject().Select(p => p.Name)));
            Assert.Contains($"root[\"{wireName}\"]", sql);
        }
    }

    // ---- CosmosKnowledgeStore: the /cas partition-key path ---------------------------------------

    /// SubstancePropertyDoc has NO LINQ query — CosmosKnowledgeStore reads it with a point-read and writes it
    /// with an upsert, so there is no SQL text to get wrong and none is invented here. What it has instead is
    /// the fields its readers address by name: `metalLoading` (the number the order amount is computed from)
    /// and `basis` (the provenance that makes that number checkable). A rename, a stray [JsonPropertyName] or
    /// a change to Json.Options' naming policy drops them silently. This asserts the keys the production
    /// serializer actually emits.
    ///
    /// The partition key — the `/cas` half of the contract — is NOT pinned here. Asserting that a serialized
    /// doc's `cas` key holds doc.Cas says nothing about what CosmosKnowledgeStore PASSES to the SDK, which is
    /// the half that actually breaks. See CosmosPartitionKeyTests, which drives the real store and captures it.
    [Theory]
    [InlineData("cas")]
    [InlineData("metalLoading")]
    [InlineData("basis")]
    public void SubstanceProperty_serializes_the_keys_its_container_and_readers_address(string wireName)
    {
        var serializer = new SystemTextJsonCosmosSerializer(Json.Options);
        var doc = new SubstancePropertyDoc
        {
            Id = KnowledgeIds.SubstanceProperty("1314-36-9"), Cas = "1314-36-9", Element = "Y", Form = "oxide",
            MetalLoading = 0.787, Basis = "2xM(Y)/M(Y2O3)", EnteredAt = "2026-07-14T10:00:00.0000000+00:00",
        };

        using var stream = serializer.ToStream(doc);
        var onDisk = JsonDocument.Parse(stream).RootElement;

        Assert.True(onDisk.TryGetProperty(wireName, out _),
            $"serializer did not write a '{wireName}' key; document keys: " +
            string.Join(", ", onDisk.EnumerateObject().Select(p => p.Name)));
    }

    /// Same loop-closing check for the chat thread: whatever keys the serializer writes for a ChatMessageDoc
    /// are the keys its query must address.
    [Fact]
    public void ChatThread_query_property_names_match_the_keys_the_serializer_actually_writes()
    {
        var serializer = new SystemTextJsonCosmosSerializer(Json.Options);
        var doc = new ChatMessageDoc
        {
            Id = RecordIds.ChatMessage("p1", Stages.Discovery, "a"), ProjectId = "p1",
            Stage = Stages.Discovery, Text = "why is Ba tier B?", CreatedAt = "2026-07-13T01:00:00Z",
        };

        using var stream = serializer.ToStream(doc);
        var onDisk = JsonDocument.Parse(stream).RootElement;

        var sql = Query<ChatMessageDoc>()
            .Where(d => d.Type == RecordTypes.ChatMessage && d.Stage == Stages.Discovery)
            .ToQueryDefinition().QueryText;

        foreach (var member in new[] { nameof(ChatMessageDoc.Type), nameof(ChatMessageDoc.Stage) })
        {
            var wireName = Json.Options.PropertyNamingPolicy!.ConvertName(member);
            Assert.True(onDisk.TryGetProperty(wireName, out _),
                $"serializer did not write a '{wireName}' key; document keys: " +
                string.Join(", ", onDisk.EnumerateObject().Select(p => p.Name)));
            Assert.Contains($"root[\"{wireName}\"]", sql);
        }
    }
}

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

    // ---- CosmosCatalogLookup ---------------------------------------------------------------------

    [Fact]
    public void CatalogLookup_query_uses_wire_property_names()
    {
        var sql = Query<CosmosCatalogLookup.Row>()
            .Where(r => r.DocType == "product")
            .ToQueryDefinition().QueryText;

        AssertWireName(sql, "docType");
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
}

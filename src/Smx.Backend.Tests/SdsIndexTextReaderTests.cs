using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Serialization;
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Smx.Domain.Documents;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

/// The SDS half of "what the agent actually read". Both failures this class pins were silent:
/// they produced an empty chunk list, which the viewer renders as "no agent has read this
/// document" — a false provenance claim on the one surface built to prove extraction was faithful.
public class SdsIndexTextReaderTests
{
    private const string RegistryId = "7761-88-8|sigma-aldrich|2024-03-11";
    private static string SheetId => DocumentId.Encode(DocumentId.Sds, RegistryId);

    private static DocumentDetail Detail(string id) => new(
        new DocumentSummary(id, DocumentKinds.Sds, "Silver nitrate", "", true,
            DocumentStates.Available, "application/pdf", "2024-03-11", "2026-07-16T00:00:00Z"),
        [], null, null, null, "sds/7761-88-8/sigma-aldrich/2024-03-11.pdf");

    /// A chunk exactly as `sds-index` holds it: camelCase, because SdsSearchClient's index fields are
    /// camelCase and Smx.Functions configures a camelCase serializer on the write side.
    private static string StoredChunkJson(string chunkKey) => $$"""
        {
          "id": "{{chunkKey}}",
          "cas": "7761-88-8",
          "supplier": "Sigma-Aldrich",
          "productName": "Silver nitrate",
          "revisionDate": "2024-03-11",
          "ghsSection": "2",
          "content": "Section 2 hazards identification"
        }
        """;

    // ── The silent-null guard ───────────────────────────────────────────────────────────────────
    // Azure.Search.Documents' DEFAULT serializer is PascalCase and case-SENSITIVE, so a camelCase
    // index binds nothing into a PascalCase POCO: every field lands null, Content becomes "" and
    // SdsChunkOrdinal.From("") returns int.MaxValue, destroying the ordering too. Nothing throws.
    // This asserts the production client's own serializer, not a local one, so the two cannot drift.
    [Fact]
    public void TheProductionSerializerBindsEveryProjectedField()
    {
        var serializer = SdsIndexTextReader.ClientOptions().Serializer!;
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StoredChunkJson("Nzc2-0")));

        var row = (SdsIndexTextReader.Row)serializer.Deserialize(json, typeof(SdsIndexTextReader.Row), default)!;

        Assert.Equal("Nzc2-0", row.Id);
        Assert.Equal("7761-88-8", row.Cas);
        Assert.Equal("Sigma-Aldrich", row.Supplier);
        Assert.Equal("2024-03-11", row.RevisionDate);
        Assert.Equal("2", row.GhsSection);
        Assert.Equal("Section 2 hazards identification", row.Content);
    }

    // The inverse, so the guard above cannot pass for the wrong reason: the SDK default really does
    // bind nothing. If a future SDK changes its default this fails and the guard becomes redundant
    // rather than silently meaningless.
    [Fact]
    public void TheSdkDefaultSerializerWouldBindNothing()
    {
        var serializer = new JsonObjectSerializer(new JsonSerializerOptions());
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(StoredChunkJson("Nzc2-0")));

        var row = (SdsIndexTextReader.Row)serializer.Deserialize(json, typeof(SdsIndexTextReader.Row), default)!;

        Assert.Null(row.Content);
        Assert.Null(row.Supplier);
    }

    // ── The filter that could never match ───────────────────────────────────────────────────────
    [Fact]
    public async Task AMixedCaseSupplierInTheIndexStillResolvesToItsSheet()
    {
        // The index holds the RAW supplier ("Sigma-Aldrich"); the id holds the normalised one.
        var client = new FakeSearchClient(
            Row("Nzc2-1", "Sigma-Aldrich", "Section 3 composition", "3"),
            Row("Nzc2-0", "Sigma-Aldrich", "Section 1 identification", "1"));

        var chunks = await new SdsIndexTextReader(client).ReadChunksAsync(Detail(SheetId));

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Section 1 identification", chunks[0].Text);     // ordinal 0 first
        Assert.Equal("Section 3 composition", chunks[1].Text);
        Assert.Equal("1", chunks[0].Section);
        Assert.Equal(0, chunks[0].Ordinal);   // recovered from the chunk key, not from the order returned
    }

    /// The server-side filter is on `cas` alone. CAS numbers are digits and hyphens, so
    /// normalisation is the identity on them and an exact `eq` is safe; supplier and revisionDate
    /// are matched client-side because normalisation is NOT the identity on those.
    [Fact]
    public async Task FiltersOnCasOnlyAndNeverOnSupplier()
    {
        var client = new FakeSearchClient(Row("Nzc2-0", "Sigma-Aldrich", "text", "1"));

        await new SdsIndexTextReader(client).ReadChunksAsync(Detail(SheetId));

        Assert.Equal("cas eq '7761-88-8'", client.LastFilter);
        Assert.DoesNotContain("supplier", client.LastFilter!, StringComparison.OrdinalIgnoreCase);
    }

    /// Same CAS, different sheet. The client-side match is what keeps another supplier's sheet — or
    /// an older revision of this one — out of this document's text, and it must not be loose.
    [Fact]
    public async Task RowsForAnotherSupplierOrRevisionAreExcluded()
    {
        var client = new FakeSearchClient(
            Row("Nzc2-0", "Sigma-Aldrich", "mine", "1"),
            Row("b3Ro-0", "Alfa Aesar", "another supplier", "1"),
            Row("cmV2-0", "Sigma-Aldrich", "older revision", "1", revisionDate: "2019-01-01"));

        var chunks = await new SdsIndexTextReader(client).ReadChunksAsync(Detail(SheetId));

        Assert.Equal("mine", Assert.Single(chunks).Text);
    }

    [Fact]
    public async Task ANonSdsDocumentIsNotItsToAnswerFor()
    {
        var client = new FakeSearchClient(Row("Nzc2-0", "Sigma-Aldrich", "text", "1"));
        var regId = DocumentId.Encode(DocumentId.Reg, "echa/svhc");

        Assert.Empty(await new SdsIndexTextReader(client).ReadChunksAsync(Detail(regId)));
        Assert.Null(client.LastFilter);   // and it did not query at all
    }

    private static SdsIndexTextReader.Row Row(
        string id, string supplier, string content, string ghsSection, string revisionDate = "2024-03-11")
        => new() { Id = id, Cas = "7761-88-8", Supplier = supplier, RevisionDate = revisionDate,
                   Content = content, GhsSection = ghsSection };

    /// SearchClient's protected parameterless constructor is the SDK's own mocking seam; SearchAsync
    /// is virtual for the same reason. No mocking package needed, and none is referenced here.
    private sealed class FakeSearchClient(params SdsIndexTextReader.Row[] rows) : SearchClient
    {
        public string? LastFilter { get; private set; }

        public override Task<Response<SearchResults<T>>> SearchAsync<T>(
            string? searchText, SearchOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastFilter = options?.Filter;
            var results = rows.Select(r => SearchModelFactory.SearchResult((T)(object)r, 1.0, null));
            var page = SearchModelFactory.SearchResults(results, rows.Length, null, null, Mock.Response);
            return Task.FromResult(Response.FromValue(page, Mock.Response));
        }
    }

    private static class Mock
    {
        public static Response Response { get; } = new StubResponse();

        private sealed class StubResponse : Response
        {
            public override int Status => 200;
            public override string ReasonPhrase => "OK";
            public override Stream? ContentStream { get; set; }
            public override string ClientRequestId { get; set; } = "";
            public override void Dispose() { }
            protected override bool ContainsHeader(string name) => false;
            protected override IEnumerable<HttpHeader> EnumerateHeaders() => [];
            protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
            { value = null; return false; }
            protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
            { values = null; return false; }
        }
    }
}

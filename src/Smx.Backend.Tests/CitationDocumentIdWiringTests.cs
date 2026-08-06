using System.Text.Json;
using Smx.Backend.Agents;
using Smx.Backend.Tests.Fakes;
using Smx.Domain;
using Smx.Domain.Documents;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Infrastructure.Search;

namespace Smx.Backend.Tests;

/// The PLUMBING behind a citation chip that links: does the document id survive from the search result to
/// the thing the model is shown, and is what we mint the same identifier `GET /documents/{id}` resolves?
///
/// Nothing here tests the model. Whether an agent actually copies the field is a prompt question; whether
/// it CAN is this file's, and before this the answer was no — the id was known only inside the search tool
/// and thrown away one line later.
public class CitationDocumentIdWiringTests
{
    private static ToolBox Box(FakeSearch search) => new(
        new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
        new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch(),
        _ => new FakeWebSearch(), useHostedWebSearch: false, new FakeSdsAcquisition());

    [Fact]
    public async Task SearchRegulatory_ShowsTheAgentTheDocumentIdOfTheChunk()
    {
        var documentId = DocumentId.Encode(DocumentId.Reg, "eur-lex/reach-annex-xvii");
        var search = new FakeSearch
        {
            Results = [new RetrievedChunk("regulatory", "regulatory-corpus/reach_3", "entry 27 …", 4.2, documentId)],
        };

        var json = await Box(search).SearchRegulatoryAsync("cadmium", default);

        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("results")[0];
        Assert.Equal(documentId, result.GetProperty("documentId").GetString());
    }

    /// The reference corpus and Learned Conclusions have no document behind them at all. The model must see
    /// NO documentId rather than an empty string it might copy into a citation — an id-shaped value that
    /// resolves to nothing is worse than the inert chip that absence produces.
    [Fact]
    public async Task AChunkWithNoDocument_ShowsNoDocumentIdField()
    {
        var search = new FakeSearch
        {
            Results = [new RetrievedChunk("reference", "smx-reference/solubility_7", "…", 3.1)],
        };

        var json = await Box(search).SearchReferenceAsync("solubility", default);

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("results")[0].TryGetProperty("documentId", out _));
    }

    /// The far end of the wire: what the model emits deserializes onto Citation.DocumentId. Together with
    /// the tool-side assertion above, this is the whole path — retrieval mints, agent copies, record keeps.
    [Fact]
    public void ACitationTheModelWroteCarriesTheIdOntoTheRecord()
    {
        var documentId = DocumentId.Encode(DocumentId.Sds, "1313-97-9|sigma|2024-03-11");
        var emitted = $$"""
            {"source":"sds","reference":"sds-index/abc-3","retrievedAt":"2026-08-06T00:00:00Z",
             "documentId":"{{documentId}}"}
            """;

        Assert.Equal(documentId, JsonSerializer.Deserialize<Citation>(emitted, Json.Options)!.DocumentId);
    }

    /// THE IDENTIFIER CONTRACT. `sds-index` stores the RAW ingest metadata; the registry id — which IS the
    /// `sds` document-id payload — stores DedupKey's normalised form. Interpolating the three index fields
    /// directly would mint "sds_…U2lnbWEtQWxkcmljaA…" for a sheet filed under "sigma-aldrich": well-formed,
    /// and a 404. SdsRegistryKey is what makes the two sides agree, so assert against a payload built the
    /// way SdsDocumentProvider builds it.
    [Fact]
    public void SdsChunk_MintsTheIdTheRegistryRowIsFiledUnder()
    {
        var id = SdsSearchTool.DocumentIdFor("1313-97-9", "Sigma-Aldrich", "2024-03-11");

        Assert.Equal(DocumentId.Encode(DocumentId.Sds, "1313-97-9|sigma-aldrich|2024-03-11"), id);
        Assert.True(DocumentId.TryDecode(id, out var kind, out var payload));
        Assert.Equal(DocumentId.Sds, kind);
        Assert.Equal("1313-97-9", DocumentId.PartitionKeyOf(kind, payload));   // the point read the viewer does
    }

    /// A sheet ingested with a blank field (an operator upload validates only Cas and PdfBase64) produces a
    /// payload with an empty segment. Encode would mint an id for it; TryDecode would then reject it on the
    /// first click. No id at all is the honest answer.
    [Theory]
    [InlineData("1313-97-9", "sigma", "")]
    [InlineData("1313-97-9", "", "2024-03-11")]
    [InlineData(null, "sigma", "2024-03-11")]
    public void SdsChunkWithAnIncompleteTriple_MintsNoId(string? cas, string? supplier, string? revisionDate)
        => Assert.Null(SdsSearchTool.DocumentIdFor(cas, supplier, revisionDate));
}

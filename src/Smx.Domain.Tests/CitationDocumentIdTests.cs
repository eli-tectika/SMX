using System.Text.Json;
using Smx.Domain.Documents;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

/// The wire contract of Citation.DocumentId.
///
/// The whole feature rests on the chip being able to tell "this citation has no document" from "this
/// response never carried the field". Json.Options ignores nulls by default, so WITHOUT the
/// [JsonIgnore(Never)] on the property those two cases serialize identically — and every citation written
/// before 2026-08-06 is the first case. A chip that cannot tell them apart either guesses (opens the wrong
/// regulation) or never links (the feature does not exist). Hence a test on the KEY, not on the value.
public class CitationDocumentIdTests
{
    [Fact]
    public void UncitedDocument_SerializesTheKeyAsExplicitNull()
    {
        var json = JsonSerializer.Serialize(new Citation("regulatory", "reach-annex-xvii entry 27", "t"), Json.Options);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("documentId", out var value),
            "the documentId key must be PRESENT even when null — its absence is how an old record is told apart");
        Assert.Equal(JsonValueKind.Null, value.ValueKind);
    }

    [Fact]
    public void DocumentId_RoundTrips()
    {
        var id = DocumentId.Encode(DocumentId.Reg, "eur-lex/reach-annex-xvii");
        var json = JsonSerializer.Serialize(new Citation("regulatory", "entry 27", "t", null, id), Json.Options);

        Assert.Contains(id, json, StringComparison.Ordinal);
        Assert.Equal(id, JsonSerializer.Deserialize<Citation>(json, Json.Options)!.DocumentId);
    }

    /// The many thousands of citations already in Cosmos have no such key. They must keep deserializing,
    /// and must land on null — not on some derived-from-Reference guess.
    [Fact]
    public void CitationWrittenBeforeTheFieldExisted_DeserializesWithNoDocumentId()
    {
        var legacy = """{"source":"regulatory","reference":"reach-annex-xvii/e27","retrievedAt":"t"}""";

        var citation = JsonSerializer.Deserialize<Citation>(legacy, Json.Options)!;

        Assert.Null(citation.DocumentId);
        Assert.Equal("reach-annex-xvii/e27", citation.Reference);
    }
}

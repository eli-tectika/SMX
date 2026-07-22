using Smx.Domain.Documents;

namespace Smx.Backend.Tests;

public class DocumentIdTests
{
    // The four kinds and the payload each carries. Decoding must recover the payload EXACTLY —
    // the first segment is a Cosmos partition key, so a lossy round-trip is a cross-partition scan
    // at best and a wrong-document read at worst.
    [Theory]
    [InlineData("sds", "7440-22-4|sigma-aldrich|2024-03-11")]
    [InlineData("reg", "echa-svhc/candidate-list")]
    [InlineData("seed", "eu/clp-annex-vi")]
    [InlineData("sdsgap", "Nd_oxide")]
    public void RoundTrips(string kind, string payload)
    {
        var id = DocumentId.Encode(kind, payload);
        Assert.True(DocumentId.TryDecode(id, out var decodedKind, out var decodedPayload));
        Assert.Equal(kind, decodedKind);
        Assert.Equal(payload, decodedPayload);
    }

    // base64url only: '+' and '/' would be re-encoded by a URL layer and '=' padding is legal but
    // must survive. This is the same constraint DedupKey.ForChunk documents for AI Search keys.
    [Fact]
    public void EncodedFormIsUrlSafe()
    {
        var id = DocumentId.Encode("sds", "7440-22-4|sigma-aldrich|2024-03-11");
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
        Assert.StartsWith("sds_", id);
    }

    // Spec §3 invariant 2. Every one of these must fail BEFORE any store is touched.
    [Theory]
    [InlineData("")]
    [InlineData("sds")]                       // no separator
    [InlineData("sds_")]                      // empty payload
    [InlineData("nope_YWJj")]                 // unknown kind
    [InlineData("sds_!!!!")]                  // not base64
    [InlineData("sds_" + "Li4vLi4vc2VjcmV0")] // decodes to "../../secret"
    public void RejectsMalformed(string id)
    {
        Assert.False(DocumentId.TryDecode(id, out _, out _));
    }

    // Traversal and control characters are refused even when they arrive base64-clean, because the
    // payload's first segment becomes a partition key and the rest becomes part of a blob path.
    [Theory]
    [InlineData("reg", "../etc/passwd")]
    [InlineData("reg", "echa/../../bronze")]
    [InlineData("reg", "echa/doc id")]
    [InlineData("reg", "echa/doc\nid")]
    public void RejectsDangerousPayloads(string kind, string payload)
    {
        var id = kind + "_" + DocumentId.EncodePayloadForTest(payload);
        Assert.False(DocumentId.TryDecode(id, out _, out _));
    }

    /// A SPACE is not dangerous, and refusing one would be a real bug.
    ///
    /// DedupKey.Norm lowercases and COLLAPSES whitespace — it does not strip it — so a registry id
    /// legitimately contains single spaces whenever the supplier name does. "Alfa Aesar", "Sigma
    /// Aldrich" and "Fisher Scientific" all yield ids with a space in them, and a validator that
    /// rejected those would make every multi-word supplier's safety sheet unopenable.
    [Theory]
    [InlineData("7440-22-4|alfa aesar|2024-01-01")]
    [InlineData("1313-97-9|fisher scientific|2025-06-30")]
    public void AcceptsSpacesInsidePayloadSegments(string payload)
    {
        var id = DocumentId.Encode(DocumentId.Sds, payload);
        Assert.True(DocumentId.TryDecode(id, out _, out var decoded));
        Assert.Equal(payload, decoded);
    }

    // Segment counts are fixed per kind: a 'reg' payload is exactly sourceId/docId. Extra segments
    // would let a caller append path components onto the constructed blob path.
    [Theory]
    [InlineData("reg", "onlyonesegment")]
    [InlineData("reg", "a/b/c")]
    [InlineData("seed", "a/b/c")]
    [InlineData("sds", "cas|supplier")]        // needs three
    [InlineData("sds", "a|b|c|d")]
    public void RejectsWrongSegmentCount(string kind, string payload)
    {
        var id = kind + "_" + DocumentId.EncodePayloadForTest(payload);
        Assert.False(DocumentId.TryDecode(id, out _, out _));
    }

    // The partition key is the first segment. Getting this wrong is silent: the read just returns null.
    [Theory]
    [InlineData("sds_", "7440-22-4|sigma|2024-01-01", "7440-22-4")]
    [InlineData("reg_", "echa-svhc/candidate-list", "echa-svhc")]
    [InlineData("seed_", "eu/clp-annex-vi", "eu")]
    [InlineData("sdsgap_", "Nd_oxide", "Nd")]
    public void PartitionKeyIsTheFirstSegment(string prefix, string payload, string expected)
    {
        var id = prefix + DocumentId.EncodePayloadForTest(payload);
        Assert.True(DocumentId.TryDecode(id, out var kind, out var decoded));
        Assert.Equal(expected, DocumentId.PartitionKeyOf(kind, decoded));
    }
}

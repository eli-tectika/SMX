namespace Smx.Domain.Documents;

/// Recovers a chunk's position from its AI Search key.
///
/// `sds-index` has no ordinal field, so ordering must be reconstructed from the key that
/// DedupKey.ForChunk builds: base64url(registryId) + "-" + ordinal. base64url uses '-' as a
/// character, so the split is on the LAST '-'.
///
/// This is a known fragility (spec §11): if DedupKey.ForChunk ever changes shape, SDS chunk order
/// breaks silently. The durable fix is an ordinal field on the index, which needs a re-ingest.
public static class SdsChunkOrdinal
{
    public static int From(string? chunkKey)
    {
        if (string.IsNullOrEmpty(chunkKey)) return int.MaxValue;
        var dash = chunkKey.LastIndexOf('-');
        if (dash < 0 || dash == chunkKey.Length - 1) return int.MaxValue;
        // Unparseable sorts LAST, never 0 — one bad key must not silently reorder a whole sheet.
        return int.TryParse(chunkKey[(dash + 1)..], out var ordinal) ? ordinal : int.MaxValue;
    }
}

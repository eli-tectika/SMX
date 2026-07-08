using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// Turns parsed chunks into Silver documents: each carries a full structured citation (regulation, authority,
// entry, source url, official date) so every downstream verdict traces to a cited source. Chunk id is
// deterministic ("{docId}#{i}") → idempotent upserts to Cosmos and to the Gold index.
public static class SilverBuilder
{
    public static IReadOnlyList<SilverChunk> Build(
        RegSource source, RegDoc doc, string docSha256, string runId, string syncDate,
        IReadOnlyList<ParsedChunk> parsed)
    {
        var chunks = new List<SilverChunk>(parsed.Count);
        for (var i = 0; i < parsed.Count; i++)
        {
            var p = parsed[i];
            var citation = new Citation(
                source.Regulation, source.Authority, p.EntryId, p.ArticleOrAnnex, doc.Url, p.OfficialDate);
            chunks.Add(new SilverChunk(
                $"{doc.DocId}#{i}", source.SourceId, doc.DocId, i, p.Text, citation,
                docSha256, runId, syncDate, "staged"));
        }
        return chunks;
    }

    // Seed-import overload: the citation is already resolved (from a metadata sidecar / body header) and the
    // status is passed explicitly — the one-time seed writes chunks as authoritative `live` with no review gate.
    public static IReadOnlyList<SilverChunk> Build(
        string sourceId, string docId, string docSha256, string runId, string syncDate,
        Citation citation, IReadOnlyList<string> texts, string status)
    {
        var chunks = new List<SilverChunk>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
            chunks.Add(new SilverChunk(
                $"{docId}#{i}", sourceId, docId, i, texts[i], citation,
                docSha256, runId, syncDate, status));
        return chunks;
    }
}

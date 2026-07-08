using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// A parsed, citable unit of regulatory text. For structured sources (OEHHA/ECHA datasets) this is one entry
// per row; for text sources (EUR-Lex) one per article/annex. `OfficialDate` is the authoritative date the
// parser derives from the document body (§15: every chunk carries source + official_date + sync_date).
public sealed record ParsedChunk(string Text, string? EntryId, string? ArticleOrAnnex, string OfficialDate);

// One parser per source format. Selected by name from RegSource.Parser via RegParserRegistry.
public interface IRegParser
{
    string Name { get; }
    IReadOnlyList<ParsedChunk> Parse(byte[] raw, RegSource source, RegDoc doc);
}

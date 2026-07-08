using System.Globalization;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// A header-driven CSV parser configured per source via RegSource.ParserConfig, so ECHA-style datasets can be
// onboarded with config rather than new code. Config keys (all header-name substrings, case-insensitive):
//   nameColumn  (required) — the human-readable entry name, used to build the chunk text
//   entryColumn (required) — the stable identifier used as the citation entry id (e.g. CAS/EC number)
//   dateColumn  (optional) — the official/listing date; normalised to ISO when parseable
//   extraColumns(optional) — comma-separated additional header substrings appended to the text for context
// Missing/unrecognised required columns yield 0 chunks — deliberately a parse anomaly that trips the circuit
// breaker rather than silently emitting malformed citations.
public sealed class GenericCsvParser : IRegParser
{
    public string Name => "GenericCsvParser";

    public IReadOnlyList<ParsedChunk> Parse(byte[] raw, RegSource source, RegDoc doc)
    {
        var cfg = source.ParserConfig;
        if (cfg is null) return Array.Empty<ParsedChunk>();
        if (!cfg.TryGetValue("nameColumn", out var nameHint) || !cfg.TryGetValue("entryColumn", out var entryHint))
            return Array.Empty<ParsedChunk>();

        var rows = CsvReader.Parse(raw);
        if (rows.Count < 2) return Array.Empty<ParsedChunk>();

        var header = rows[0];
        int nameIdx = IndexOf(header, nameHint);
        int entryIdx = IndexOf(header, entryHint);
        int dateIdx = cfg.TryGetValue("dateColumn", out var dh) ? IndexOf(header, dh) : -1;
        // Optional explicit date format disambiguates US vs EU slash dates for this source.
        var dateFormats = cfg.TryGetValue("dateFormat", out var df) && !string.IsNullOrWhiteSpace(df)
            ? new[] { df } : DefaultDateFormats;
        var extraIdx = (cfg.TryGetValue("extraColumns", out var extras) ? extras.Split(',') : Array.Empty<string>())
            .Select(e => IndexOf(header, e.Trim())).Where(i => i >= 0).ToArray();
        if (nameIdx < 0 || entryIdx < 0) return Array.Empty<ParsedChunk>();

        var chunks = new List<ParsedChunk>(rows.Count - 1);
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var name = At(row, nameIdx);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var entry = At(row, entryIdx);

            var extra = string.Join("; ", extraIdx.Select(i => At(row, i)).Where(v => v.Length > 0));
            var text = $"{name}" + (string.IsNullOrEmpty(entry) ? "" : $" ({entry})") +
                       $" — {source.Regulation} ({source.Authority})" + (extra.Length > 0 ? $": {extra}" : ".");

            chunks.Add(new ParsedChunk(text, string.IsNullOrWhiteSpace(entry) ? name : entry, null,
                dateIdx >= 0 ? NormalizeDate(At(row, dateIdx), dateFormats) : ""));
        }
        return chunks;
    }

    private static int IndexOf(string[] header, string contains)
    {
        for (var i = 0; i < header.Length; i++)
            if (header[i].Trim().ToLowerInvariant().Contains(contains.ToLowerInvariant())) return i;
        return -1;
    }

    private static string At(string[] row, int idx) => idx >= 0 && idx < row.Length ? row[idx].Trim() : "";

    // Broad set tried in order when a source gives no explicit dateFormat. Unambiguous inputs (day > 12) parse
    // correctly regardless; for ambiguous slash dates a source should set "dateFormat" to remove all doubt.
    private static readonly string[] DefaultDateFormats =
        { "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd/MM/yyyy", "d.M.yyyy", "dd.MM.yyyy" };
    private static string NormalizeDate(string s, string[] formats)
        => DateTime.TryParseExact(s.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : s;
}

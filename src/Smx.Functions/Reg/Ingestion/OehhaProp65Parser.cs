using System.Globalization;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// Parses the official OEHHA Proposition 65 chemical list (CSV). One chunk per listed chemical; the entry id is
// the CAS number (falling back to the chemical name when CAS is absent, e.g. chemical groups), and the official
// date is the entry's "Date Listed". Column order is resolved by header name, not position, so a reordering of
// the official file does not silently corrupt the mapping.
public sealed class OehhaProp65Parser : IRegParser
{
    public string Name => "OehhaProp65Parser";

    public IReadOnlyList<ParsedChunk> Parse(byte[] raw, RegSource source, RegDoc doc)
    {
        var rows = CsvReader.Parse(raw);
        if (rows.Count < 2) return Array.Empty<ParsedChunk>();

        var header = rows[0];
        int chem = IndexOf(header, "chemical");
        int cas = IndexOf(header, "cas");
        int date = IndexOf(header, "date listed");
        int tox = IndexOf(header, "type of toxicity");
        if (chem < 0) return Array.Empty<ParsedChunk>(); // header not recognised → 0 chunks (a parse anomaly)

        var chunks = new List<ParsedChunk>(rows.Count - 1);
        for (var r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            var chemical = At(row, chem);
            if (string.IsNullOrWhiteSpace(chemical)) continue;

            var casNo = At(row, cas);
            var listed = At(row, date);
            var toxicity = At(row, tox);
            var entryId = string.IsNullOrWhiteSpace(casNo) ? chemical : casNo;

            var text = $"{chemical}" +
                       (string.IsNullOrWhiteSpace(casNo) ? "" : $" (CAS {casNo})") +
                       $" is listed under California Proposition 65" +
                       (string.IsNullOrWhiteSpace(toxicity) ? "" : $" for {toxicity}") +
                       (string.IsNullOrWhiteSpace(listed) ? "." : $", listed {listed}.");

            chunks.Add(new ParsedChunk(text, entryId, toxicity is { Length: > 0 } ? toxicity : null,
                NormalizeDate(listed)));
        }
        return chunks;
    }

    private static int IndexOf(string[] header, string contains)
    {
        for (var i = 0; i < header.Length; i++)
            if (header[i].Trim().ToLowerInvariant().Contains(contains)) return i;
        return -1;
    }

    private static string At(string[] row, int idx) => idx >= 0 && idx < row.Length ? row[idx].Trim() : "";

    // Prop 65 (a US source) publishes dates as US "M/d/yyyy". Parse with invariant/explicit formats so a
    // non-US host locale can't silently swap day/month. Normalise to ISO; keep the source string if unparseable.
    private static readonly string[] DateFormats = { "M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd" };
    private static string NormalizeDate(string s)
        => DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : s;
}

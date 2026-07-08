using System.Globalization;
using System.Text.Json;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// Parses the eCFR versioner API "versions" document — the documented, machine-readable endpoint
//   GET https://www.ecfr.gov/api/versioner/v1/versions/title-{N}.json
// which returns { "content_versions": [ { date, amendment_date, issue_date, identifier, name, part,
//   substantive, removed, subpart, title, type }, ... ] }. Each section identifier appears once per
// historical amendment, so we collapse to the current state (the entry with the latest amendment_date)
// and emit one citable chunk per section, carrying its authoritative "last amended" date. This gives
// precise change detection (the endpoint's amendment dates) without guessing at the full-text CFR XML.
//
// Optional ParserConfig["parts"] is a comma-separated part allowlist (e.g. "170,175,176,177,178"); when
// present, only sections in those parts are emitted so a whole-title feed can be narrowed to the parts
// that matter for marker screening. A missing/empty "content_versions" array yields 0 chunks (a parse
// anomaly that trips the circuit breaker) rather than a guessed mapping.
public sealed class EcfrParser : IRegParser
{
    public string Name => "EcfrParser";

    public IReadOnlyList<ParsedChunk> Parse(byte[] raw, RegSource source, RegDoc doc)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(raw); }
        catch (JsonException) { return Array.Empty<ParsedChunk>(); } // malformed body → parse anomaly

        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("content_versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<ParsedChunk>();

            var partsFilter = ParsePartsFilter(source.ParserConfig);

            // Collapse historical versions to the current state of each section: keep, per identifier, the
            // entry with the latest amendment_date. A section whose latest state is "removed" is dropped.
            var latest = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var v in versions.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                var identifier = Str(v, "identifier");
                var amendment = Str(v, "amendment_date");
                if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(amendment)) continue;

                var part = Str(v, "part");
                if (partsFilter is not null && !partsFilter.Contains(part)) continue;

                var e = new Entry(identifier, amendment, Str(v, "name"), part, Str(v, "title"),
                    Str(v, "type"), Bool(v, "removed"));
                if (!latest.TryGetValue(identifier, out var cur) ||
                    string.CompareOrdinal(e.Amendment, cur.Amendment) > 0)
                    latest[identifier] = e;
            }

            // Deterministic order (stable chunk ids {docId}#{i}): by part (numeric when parseable), then id.
            var ordered = latest.Values
                .Where(e => !e.Removed)
                .OrderBy(e => int.TryParse(e.Part, out var p) ? p : int.MaxValue)
                .ThenBy(e => e.Part, StringComparer.Ordinal)
                .ThenBy(e => e.Identifier, StringComparer.Ordinal)
                .ToList();

            var chunks = new List<ParsedChunk>(ordered.Count);
            foreach (var e in ordered)
            {
                var name = Collapse(e.Name);
                var cfrRef = $"{e.Title} CFR {e.Identifier}";
                var kind = string.IsNullOrWhiteSpace(e.Type) ? "" : $" ({e.Type})";
                var text = $"{cfrRef}{kind}" +
                           (string.IsNullOrWhiteSpace(name) ? "" : $" — {name}") +
                           $" [{source.Authority}, {source.Regulation}], last amended {NormalizeDate(e.Amendment)}.";
                chunks.Add(new ParsedChunk(text, cfrRef,
                    string.IsNullOrWhiteSpace(e.Part) ? null : $"Part {e.Part}", NormalizeDate(e.Amendment)));
            }
            return chunks;
        }
    }

    private sealed record Entry(string Identifier, string Amendment, string Name, string Part,
        string Title, string Type, bool Removed);

    private static IReadOnlySet<string>? ParsePartsFilter(IReadOnlyDictionary<string, string>? cfg)
    {
        if (cfg is null || !cfg.TryGetValue("parts", out var parts) || string.IsNullOrWhiteSpace(parts))
            return null;
        var set = parts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        return set.Count == 0 ? null : set;
    }

    private static string Str(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // eCFR names carry irregular internal whitespace (e.g. "§ 170.3   General."); collapse runs to one space.
    private static string Collapse(string s)
        => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // amendment_date is already ISO (yyyy-MM-dd); validate and pass through, keeping the raw value if it is
    // ever in another shape rather than silently emitting a fabricated date.
    private static string NormalizeDate(string s)
        => DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : s.Trim();
}

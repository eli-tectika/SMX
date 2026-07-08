using System.Text;
using System.Text.RegularExpressions;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Seeding;

// Provenance parsed from a companion `_metadata.txt` sidecar (present for the EU files). The format is
// mostly `Key: Value`, but a few keys (notably `Official Title`) carry their value on the following line(s).
public sealed record SeedMetadata(
    string? OfficialTitle, string? Celex, string? Eli, string? SourceUrl,
    string? DocumentDate, string? PublicationDate, string? Scraped);

// Provenance salvaged from a body `.txt` header when no metadata sidecar exists (`SOURCE URL:` + a date line).
public sealed record BodyHeader(string? SourceUrl, string? Published);

// Reads seed-corpus provenance from either a `_metadata.txt` sidecar or a body `.txt` header, and folds the
// best available facts into a Citation so every seeded chunk traces to a cited source + date (like SyncPipeline).
public static class MetadataReader
{
    // `Key: value` — the key is a run of letters/digits/space and a few separators, up to the first colon.
    private static readonly Regex KeyLine = new(@"^(?<k>[A-Za-z][A-Za-z0-9 /_.\-]*?):\s?(?<v>.*)$", RegexOptions.Compiled);
    private static readonly Regex UrlRx = new(@"https?://[^\s()]+", RegexOptions.Compiled);

    public static SeedMetadata ParseMetadata(string text)
    {
        var lines = Normalize(text).Split('\n');
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var m = KeyLine.Match(lines[i]);
            if (!m.Success) continue;
            var key = m.Groups["k"].Value.Trim();
            var val = m.Groups["v"].Value.Trim();
            if (val.Length == 0)
            {
                // Value continues on the following non-blank line(s), up to the next key line or a blank line.
                var sb = new StringBuilder();
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var l = lines[j].Trim();
                    if (l.Length == 0 || KeyLine.IsMatch(lines[j])) break;
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(l);
                }
                val = sb.ToString();
            }
            if (val.Length > 0 && !map.ContainsKey(key)) map[key] = val;
        }

        return new SeedMetadata(
            OfficialTitle: First(map, "Official Title"),
            Celex: First(map, "CELEX Number", "Document Number"),
            Eli: First(map, "ELI URI", "ELI"),
            SourceUrl: ExtractUrl(First(map, "Source URL", "Source")),
            DocumentDate: First(map, "Document Date"),
            PublicationDate: First(map, "Publication Date"),
            Scraped: First(map, "Scraped"));
    }

    public static BodyHeader ParseBody(string bodyText)
    {
        var lines = Normalize(bodyText).Split('\n');
        string? url = null, published = null;
        // The header sits at the very top of the body; scan a bounded window so we never treat prose as metadata.
        var limit = Math.Min(lines.Length, 40);
        for (var i = 0; i < limit; i++)
        {
            var t = lines[i].Trim();
            if (t.Length == 0) continue;
            if (url is null && t.StartsWith("SOURCE URL:", StringComparison.OrdinalIgnoreCase))
                url = ExtractUrl(t["SOURCE URL:".Length..]);
            else if (published is null && TryDateLine(t, out var d))
                published = d;
        }
        return new BodyHeader(url, published);
    }

    // Fold the best available provenance into a Citation. Regulation prefers the metadata Official Title, then the
    // slugged file name; authority is derived from the region folder; the date prefers the document/publication
    // date, then the body's Published line. CELEX (when present) rides along as the citation EntryId.
    public static Citation ToCitation(string region, string fileNameNoExt, SeedMetadata? meta, BodyHeader body)
    {
        var regulation = !string.IsNullOrWhiteSpace(meta?.OfficialTitle) ? meta!.OfficialTitle!.Trim() : DeriveName(fileNameNoExt);
        var authority = DeriveAuthority(region);
        var sourceUrl = Coalesce(meta?.SourceUrl, body.SourceUrl) ?? "";
        var officialDate = Coalesce(meta?.DocumentDate, meta?.PublicationDate, body.Published) ?? "";
        var entryId = string.IsNullOrWhiteSpace(meta?.Celex) ? null : meta!.Celex!.Trim();
        return new Citation(regulation, authority, entryId, null, sourceUrl, officialDate);
    }

    // "Basel_Convention_Hazardous_Waste" → "Basel Convention Hazardous Waste".
    public static string DeriveName(string fileNameNoExt) => fileNameNoExt.Replace('_', ' ').Trim();

    // "02_European_Union" → "European Union"; "01_Global" → "Global".
    public static string DeriveAuthority(string region)
        => Regex.Replace(region, @"^\d+[_-]", "").Replace('_', ' ').Trim();

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string? First(IReadOnlyDictionary<string, string> map, params string[] keys)
    {
        foreach (var k in keys)
            if (map.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }

    private static string? Coalesce(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string? ExtractUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = UrlRx.Match(raw);
        if (m.Success) return m.Value.TrimEnd('.', ',', ';');
        var t = raw.Trim();
        return t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? t : null;
    }

    private static bool TryDateLine(string line, out string? date)
    {
        date = null;
        foreach (var prefix in new[] { "Published:", "Publication Date:", "Document Date:", "Date:" })
        {
            if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var v = line[prefix.Length..].Trim();
                if (v.Length == 0) return false;
                date = v;
                return true;
            }
        }
        return false;
    }
}

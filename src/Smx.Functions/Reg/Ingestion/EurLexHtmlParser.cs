using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// Parses CONVEX-converted OJ XHTML from the EU Publications Office (Cellar) — the machine-readable
// rendering of a EUR-Lex act. One chunk per substance row of a target annex.
//
// It locates the annex named by ParserConfig["annex"] (e.g. "I" / "XVII" / "VI"), then walks the
// OJ substance tables (<table class="oj-table">) inside it. For each data row it joins the row's cell
// texts with " | " and emits a chunk ONLY when the row carries a chemical identifier — a CAS number
// (\d{2,7}-\d{2}-\d, e.g. 309-00-2) or, failing that, an EC number (\d{3}-\d{3}-\d, e.g. 206-215-8);
// the identifier becomes the citation EntryId. Rows without an identifier (headers, name-only rows,
// exemption prose) are skipped. Cell text is taken only from <p class="oj-tbl-txt"> paragraphs, so the
// nested exemption sub-tables (oj-normal) and footnote anchors never leak in as false identifiers.
//
// CORRECTNESS: the only machine-reachable Cellar endpoint serves BASE acts, which omit later amendments
// (e.g. RoHS Annex II is missing the four phthalates added by (EU) 2015/863). A missing annex, or an
// annex with no identifier-bearing rows, yields 0 chunks — a parse anomaly, never a guessed mapping.
// OfficialDate is not derivable from the base-act body here, so it is taken from ParserConfig["officialDate"]
// when supplied, else left empty rather than fabricated.
public sealed class EurLexHtmlParser : IRegParser
{
    public string Name => "EurLexHtmlParser";

    // CAS: 2–7 / 2 / 1 digit groups (e.g. 309-00-2, 40088-47-9). EC: 3 / 3 / 1 (e.g. 231-100-4).
    private static readonly Regex CasRx = new(@"\b\d{2,7}-\d{2}-\d\b", RegexOptions.Compiled);
    private static readonly Regex EcRx = new(@"\b\d{3}-\d{3}-\d\b", RegexOptions.Compiled);
    // OJ substance tables carry class="oj-table"; nested exemption sub-tables are plain <table> (no class).
    private static readonly Regex OjTableOpenRx =
        new("<table\\b[^>]*class=\"oj-table\"[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Top-level annex rows/cells carry class="oj-table"; nested sub-table rows/cells do not.
    private static readonly Regex RowOpenRx =
        new("<tr\\b[^>]*class=\"oj-table\"[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CellOpenRx =
        new("<td\\b[^>]*class=\"oj-table\"[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Body cell paragraphs (not oj-tbl-hdr headers, not nested oj-normal prose).
    private static readonly Regex TblTxtRx =
        new("<p\\b[^>]*class=\"oj-tbl-txt\"[^>]*>(.*?)</p>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TagRx = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.Singleline);

    public IReadOnlyList<ParsedChunk> Parse(byte[] raw, RegSource source, RegDoc doc)
    {
        var cfg = source.ParserConfig;
        if (cfg is null || !cfg.TryGetValue("annex", out var annexRaw) || string.IsNullOrWhiteSpace(annexRaw))
            return Array.Empty<ParsedChunk>(); // no target annex configured → nothing to parse
        var annex = annexRaw.Trim();
        var officialDate = cfg.TryGetValue("officialDate", out var od) && od is not null ? od.Trim() : "";

        var html = Encoding.UTF8.GetString(raw);
        var section = ExtractAnnexSection(html, annex);
        if (section is null) return Array.Empty<ParsedChunk>(); // annex heading not found → parse anomaly

        var chunks = new List<ParsedChunk>();
        foreach (var table in ExtractBalanced(section, OjTableOpenRx, "table"))
        {
            foreach (var row in ExtractRows(table))
            {
                var cells = ExtractCellTexts(row);
                if (cells.Count == 0) continue;
                var text = string.Join(" | ", cells);
                var cas = CasRx.Match(text);
                var entryId = cas.Success ? cas.Value : EcRx.Match(text) is { Success: true } ec ? ec.Value : null;
                if (entryId is null) continue; // rows with no chemical identifier are skipped
                chunks.Add(new ParsedChunk(text, entryId, $"Annex {annex}", officialDate));
            }
        }
        return chunks; // 0 chunks when the annex has no identifier-bearing rows (a parse anomaly)
    }

    // Bounds the target annex: from its heading (uppercase "ANNEX <n>" text node) to the next annex
    // heading (or end of document). Case-sensitive on ANNEX so title-case running-text references
    // ("Annex I") can't be mistaken for a section boundary; the trailing '<' pins an exact roman numeral
    // so "ANNEX I" never matches "ANNEX II"/"ANNEX III".
    private static string? ExtractAnnexSection(string html, string annex)
    {
        var start = new Regex($">\\s*ANNEX\\s+{Regex.Escape(annex)}\\s*<").Match(html);
        if (!start.Success) return null;
        var from = start.Index + start.Length;
        var next = new Regex(">\\s*ANNEX\\s+[IVXLC]+\\s*<").Match(html, from);
        var end = next.Success ? next.Index : html.Length;
        return html.Substring(from, end - from);
    }

    // Top-level annex rows only (nested sub-table rows lack class="oj-table"). A row runs from one marker
    // to the next (or table end), so its nested exemption sub-tables travel with it and are filtered later.
    private static IEnumerable<string> ExtractRows(string table)
    {
        var rows = RowOpenRx.Matches(table);
        for (var i = 0; i < rows.Count; i++)
        {
            var s = rows[i].Index + rows[i].Length;
            var e = i + 1 < rows.Count ? rows[i + 1].Index : table.Length;
            yield return table.Substring(s, e - s);
        }
    }

    // Each top-level cell's text = its oj-tbl-txt paragraphs joined by a space; empty cells are dropped so
    // the " | " join stays clean (e.g. "Aldrin | 309-00-2 | 206-215-8").
    private static List<string> ExtractCellTexts(string row)
    {
        var cells = new List<string>();
        var i = 0;
        while (true)
        {
            var open = CellOpenRx.Match(row, i);
            if (!open.Success) break;
            var contentStart = open.Index + open.Length;
            var end = MatchingClose(row, contentStart, "td");
            if (end < 0) break;
            var text = CellText(row.Substring(contentStart, end - contentStart));
            if (text.Length > 0) cells.Add(text);
            i = end + "</td>".Length;
        }
        return cells;
    }

    private static string CellText(string cellHtml)
    {
        var parts = new List<string>();
        foreach (Match p in TblTxtRx.Matches(cellHtml))
        {
            var t = Clean(p.Groups[1].Value);
            if (t.Length > 0) parts.Add(t);
        }
        return string.Join(" ", parts);
    }

    // Strip inner tags, decode entities, collapse all whitespace (incl. NBSP) to single spaces.
    private static string Clean(string html)
    {
        var text = WebUtility.HtmlDecode(TagRx.Replace(html, " "));
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // Emits the inner content of each top-level element matched by `openRx`, honouring nesting of same-named
    // tags (OJ substance tables contain nested plain <table> exemption blocks).
    private static IEnumerable<string> ExtractBalanced(string s, Regex openRx, string tag)
    {
        var i = 0;
        while (true)
        {
            var open = openRx.Match(s, i);
            if (!open.Success) yield break;
            var contentStart = open.Index + open.Length;
            var end = MatchingClose(s, contentStart, tag);
            if (end < 0) yield break;
            yield return s.Substring(contentStart, end - contentStart);
            i = end + tag.Length + 3; // past "</tag>"
        }
    }

    // Index of the </tag> that closes the element whose content starts at `from`, counting nested <tag ...>.
    private static int MatchingClose(string s, int from, string tag)
    {
        var openTok = "<" + tag;
        var closeTok = "</" + tag;
        var depth = 1;
        var i = from;
        while (i < s.Length)
        {
            var open = s.IndexOf(openTok, i, StringComparison.OrdinalIgnoreCase);
            var close = s.IndexOf(closeTok, i, StringComparison.OrdinalIgnoreCase);
            if (close < 0) return -1;
            if (open >= 0 && open < close) { depth++; i = open + openTok.Length; }
            else { if (--depth == 0) return close; i = close + closeTok.Length; }
        }
        return -1;
    }
}

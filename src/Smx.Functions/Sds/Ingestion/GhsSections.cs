using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Ingestion;

// The single definition of "what counts as a GHS section header", shared by the validator
// (counting) and the chunker (splitting) so the two can never drift apart.
//
// Two dialects observed in the wild (2026-07-16 allowlist shakedown):
//   "SECTION 4: First-aid measures"  — explicit word form; always trusted.
//   "4. First-aid measures"         — Thermo Fisher / Alfa Aesar / Materion style.
// The dot form is accepted only when its number ascends past the last accepted header, so a
// numbered list INSIDE a section ("1. Rinse with water") cannot split it. The trailing \p{L}
// requirement keeps decimals ("1.2 mg/kg") from matching. Note the residual false-positive
// surface (a 10+-item ascending numbered list in a CAS-bearing non-SDS doc) is accepted: the
// domain allowlist is the trust boundary; this gate is a shape check, not a forgery detector.
public static class GhsSections
{
    private static readonly Regex Header = new(
        @"(?im)^\s*(?:SECTION\s+(?<w>\d{1,2})\b|(?<d>\d{1,2})\s*\.\s*\p{L})",
        RegexOptions.Compiled);

    public static IReadOnlyList<(int Number, int Index)> FindHeaders(string text)
    {
        var accepted = new List<(int Number, int Index)>();
        var last = 0;
        foreach (Match m in Header.Matches(text))
        {
            var isWord = m.Groups["w"].Success;
            var n = int.Parse((isWord ? m.Groups["w"] : m.Groups["d"]).Value);
            if (n is < 1 or > 16) continue;
            if (!isWord && n <= last) continue;   // dot form must ascend
            accepted.Add((n, m.Index));
            last = n;
        }
        return accepted;
    }
}

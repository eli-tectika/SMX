using System.Text.RegularExpressions;

namespace Smx.CustomerDataLoad;

/// Pulls CAS numbers out of the free-text cells the polymers workbook uses, e.g.
/// "SrCO3: 1633-05-2 / GeO2: 1310-53-8" or a bare "584-09-8".
public static partial class Cas
{
    // Optional "<molecule>:" label, then the number itself.
    [GeneratedRegex(@"(?:([A-Za-z0-9()·\.\s\-]+?)\s*:\s*)?(\d{2,7}-\d{2}-\d)")]
    private static partial Regex Pattern();

    public static IReadOnlyList<(string Cas, string Molecule)> Extract(string field)
    {
        if (string.IsNullOrWhiteSpace(field)) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<(string, string)>();
        foreach (Match m in Pattern().Matches(field))
        {
            var cas = m.Groups[2].Value;
            if (seen.Add(cas)) found.Add((cas, m.Groups[1].Value.Trim()));
        }
        return found;
    }
}

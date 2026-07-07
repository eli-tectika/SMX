using System.Text.RegularExpressions;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class SdsValidator
{
    private readonly int _minGhsSections;
    public SdsValidator(int minGhsSections = 10) => _minGhsSections = minGhsSections;

    public ValidationResult Validate(string text, string requestedCas, string sourceDomain,
        IReadOnlySet<string> allowlistDomains)
    {
        var host = sourceDomain.ToLowerInvariant();
        if (!allowlistDomains.Any(d => host == d || host.EndsWith("." + d)))
            return new ValidationResult(false, $"source domain '{sourceDomain}' not on allowlist");

        var sections = CountGhsSections(text);
        if (sections < _minGhsSections)
            return new ValidationResult(false, $"only {sections} GHS sections found (min {_minGhsSections})");

        var cas = requestedCas.Trim();
        if (!Regex.IsMatch(text, $@"\b{Regex.Escape(cas)}\b"))
            return new ValidationResult(false, $"requested CAS {cas} not present in document");

        return new ValidationResult(true, null);
    }

    private static int CountGhsSections(string text)
    {
        var found = new HashSet<int>();
        foreach (Match m in Regex.Matches(text, @"(?im)^\s*SECTION\s+(\d{1,2})\b"))
            if (int.TryParse(m.Groups[1].Value, out var n) && n is >= 1 and <= 16) found.Add(n);
        return found.Count;
    }
}

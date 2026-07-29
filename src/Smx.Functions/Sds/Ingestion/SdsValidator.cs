using System.Text.RegularExpressions;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Ingestion;

public sealed class SdsValidator
{
    private readonly int _minGhsSections;
    public SdsValidator(int minGhsSections = 10) => _minGhsSections = minGhsSections;

    /// Judges a document by its CONTENT alone. The source domain used to be checked here as well; it is
    /// not any more, and that is the load-bearing change of the 2026-07-29 design.
    ///
    /// The domain check never established that a document was the RIGHT document — these two checks did.
    /// It established only that someone had curated the host, which capped coverage at the size of a
    /// hand-maintained dictionary (13 of 53 substances on 2026-07-29). Provenance is still recorded on
    /// the registry pointer; it is simply no longer a gate.
    public ValidationResult Validate(string text, string requestedCas)
    {
        var sections = CountGhsSections(text);
        if (sections < _minGhsSections)
            return new ValidationResult(false, $"only {sections} GHS sections found (min {_minGhsSections})");

        var cas = requestedCas.Trim();
        if (!Regex.IsMatch(text, $@"\b{Regex.Escape(cas)}\b"))
            return new ValidationResult(false, $"requested CAS {cas} not present in document");

        return new ValidationResult(true, null);
    }

    private static int CountGhsSections(string text)
        => GhsSections.FindHeaders(text).Select(h => h.Number).Distinct().Count();
}

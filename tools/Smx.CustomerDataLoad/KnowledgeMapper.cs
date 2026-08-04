using System.Text.RegularExpressions;
using Smx.Domain.Records;

namespace Smx.CustomerDataLoad;

/// What was read from a workbook, and what was deliberately NOT loaded.
public sealed record MapResult(
    IReadOnlyList<LearnedConclusionDoc> Conclusions, IReadOnlyList<string> Held);

/// Workbook rows → LearnedConclusionDoc.
///
/// Two things are deliberately NOT produced here:
///
///   • MarkerLibraryDoc. `MarkerComposition.Ppm` is a single number and 14 of the 25 historical rows
///     record a RANGE ("30, 40, 50, 60 ppm", "dilute 1%-4%"). Picking one of those and storing it as
///     the validated concentration would invent a fact the source does not contain, so the polymers
///     rows land as conclusions — whose Finding is free text and holds the range intact — and the
///     Marker Library stays empty until an operator adjudicates each row.
///
///   • MsdsRegistryDoc. It requires Supplier, Version and Date; the workbook carries none of the
///     three. The one thing it does carry — which project put a CAS in play — is preserved as a
///     substance-scoped conclusion instead of being padded out with placeholder registry fields.
public static partial class KnowledgeMapper
{
    /// A physicist's measurement against a real sample. High, but not 1.0: it is one instrument on one
    /// batch, and the sheets themselves say so ("also appears in BMC's Ligura, does not appear in the
    /// Ligura purchased in Israel").
    private const double Measured = 0.9;

    /// A decision another project already took. Recorded faithfully, but the regulation, the supplier
    /// and the formulation may all have moved since, and none of it has been re-verified here.
    private const double HistoricalDecision = 0.7;

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotSlug();

    private static string Slug(string s) =>
        NotSlug().Replace(s.ToLowerInvariant(), "-").Trim('-');

    private static string Verdict(string status) => status switch
    {
        "V" => "is usable — no interfering background signal was seen",
        "L" => "is CONDITIONAL — usable only subject to the signal note",
        "X" => "is PRESENT in the background and is not available as a marker line",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "not a V/L/X verdict"),
    };

    public static MapResult FromBackground(IReadOnlyList<BackgroundRow> rows, string createdAt)
    {
        var docs = new List<LearnedConclusionDoc>();
        var held = new List<string>();

        foreach (var row in rows)
        {
            // The same rule XrfSheet enforces on an uploaded sheet: a conditional verdict with no
            // stated reason is not knowledge, it is an unexplained maybe. Loading it would put a
            // verdict in front of a future project with nothing behind it, so it is held back and
            // reported instead — it can be loaded once the physicist supplies the note.
            if (row.Status == "L" && row.Note.Trim().Length == 0)
            {
                held.Add($"{row.Material} / {row.Element} ({row.Line} line): conditional with no signal note");
                continue;
            }

            var finding = $"On {row.Material}, {row.Element} ({row.Line} line) {Verdict(row.Status)}.";
            if (row.Note.Trim().Length > 0) finding += $" Signal note: {row.Note.Trim()}";

            docs.Add(new LearnedConclusionDoc
            {
                Id = KnowledgeIds.LearnedConclusion(
                    KnowledgeKinds.XrfBackground,
                    $"{Slug(row.Material)}|{row.Element}|{row.Line}"),
                Kind = KnowledgeKinds.XrfBackground,
                Scope = new ConclusionScope(
                    Element: row.Element, Form: null, Material: row.Material,
                    Application: null, Market: null, Substance: null),
                Finding = finding,
                Confidence = Measured,
                Provenance = new ConclusionProvenance(
                    SourceProjects: ["customer XRF background workbook (Markers Assessment_BG_)"],
                    Decisions: []),
                CreatedAt = createdAt,
            });
        }
        return new MapResult(docs, held);
    }

    public static MapResult FromPolymers(IReadOnlyList<PolymerRow> rows, string createdAt)
    {
        var docs = new List<LearnedConclusionDoc>();
        var held = new List<string>();

        foreach (var row in rows)
        {
            var markers = string.Join(", ", row.Markers);
            var scope = new ConclusionScope(
                Element: markers.Length > 0 ? markers : null, Form: null,
                Material: row.Material.Length > 0 ? row.Material : null,
                Application: row.Application.Length > 0 ? row.Application : null,
                Market: null, Substance: null);

            if (row.SelectionReason.Length > 0 && markers.Length > 0)
            {
                var finding =
                    $"Project '{row.Project}': markers {markers} were selected for {row.Material} " +
                    $"({(row.Application.Length > 0 ? row.Application : "application unspecified")}) " +
                    $"on the basis of: {row.SelectionReason}.";
                if (row.MasterbatchPpm.Length > 0) finding += $" Masterbatch loading: {row.MasterbatchPpm}.";
                if (row.FinalPpm.Length > 0) finding += $" Concentration in the final application: {row.FinalPpm}.";
                if (row.Molecules.Count > 0) finding += $" Molecules used: {string.Join(", ", row.Molecules)}.";

                docs.Add(Conclusion(KnowledgeKinds.Material, Slug(row.Project) + "|selection",
                    scope, finding, row.Project, createdAt));
            }

            // An explicit reversal is the most valuable thing in this workbook — it is the only place a
            // reason a marker FAILED is written down.
            foreach (var text in new[] { row.NextItems, row.Notes })
            {
                if (text.Length == 0 || !Reversal().IsMatch(text)) continue;
                docs.Add(Conclusion(KnowledgeKinds.Decision, Slug(row.Project) + "|" + Slug(text)[..Math.Min(40, Slug(text).Length)],
                    scope, $"Project '{row.Project}' ({row.Material}): {text}", row.Project, createdAt));
            }

            // Which project put a CAS in play — the MsdsRegistry field we can fill honestly.
            foreach (var (cas, molecule) in row.CasEntries)
            {
                if (!Smx.Domain.CasNumber.IsValid(cas))
                {
                    held.Add($"{row.Project}: CAS '{cas}'" +
                             $"{(molecule.Length > 0 ? $" ({molecule})" : "")} fails its check digit");
                    continue;
                }
                docs.Add(Conclusion(KnowledgeKinds.Material, $"cas-{Slug(cas)}|{Slug(row.Project)}",
                    scope with { Substance = cas },
                    $"CAS {cas}{(molecule.Length > 0 ? $" ({molecule})" : "")} was used as a marker " +
                    $"substance by project '{row.Project}' in {row.Material}.",
                    row.Project, createdAt));
            }

            if (row.Markers.Count == 0 && row.SelectionReason.Length == 0)
                held.Add($"{row.Project}: no markers and no selection reason recorded — nothing to load");
        }
        return new MapResult(docs, held);
    }

    [GeneratedRegex(@"\bomit\b|\bnot compliant\b|\bcannot be used\b", RegexOptions.IgnoreCase)]
    private static partial Regex Reversal();

    private static LearnedConclusionDoc Conclusion(
        string kind, string scopeKey, ConclusionScope scope, string finding, string project, string createdAt) =>
        new()
        {
            Id = KnowledgeIds.LearnedConclusion(kind, scopeKey),
            Kind = kind,
            Scope = scope,
            Finding = finding,
            Confidence = HistoricalDecision,
            Provenance = new ConclusionProvenance(SourceProjects: [project], Decisions: []),
            CreatedAt = createdAt,
        };
}

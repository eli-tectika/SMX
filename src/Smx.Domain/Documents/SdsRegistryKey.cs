using System.Text.RegularExpressions;

namespace Smx.Domain.Documents;

/// The `sds-registry` id's normalisation, mirrored on the read side.
///
/// Two representations of the same sheet exist and they are NOT the same string. The registry id —
/// which is also the `sds` document-id payload — is DedupKey.ForRegistry(cas, supplier,
/// revisionDate), whose segments are normalised. But IngestionPipeline pushes the RAW metadata into
/// `sds-index` (IngestionPipeline.cs:36-42), so the index holds "Sigma-Aldrich" where the id holds
/// "sigma-aldrich". Azure AI Search `eq` on a filterable Edm.String is exact and case-sensitive, so
/// a filter built from the id can never match an index row — and every supplier in
/// Sds/Config/suppliers.allowlist.json is mixed-case. Normalising the index's own values through
/// this class is what makes the two sides agree by construction rather than by coincidence.
///
/// MIRRORS Smx.Functions/Sds/Domain/DedupKey.Norm and ForRegistry and MUST NOT DRIFT from them. It
/// cannot call them: DedupKey lives in the Functions app, which is a different solution. The guard
/// is SdsRegistryKeyTests, which compiles the real DedupKey.cs into the test assembly and asserts
/// the two agree — so an edit over there fails a test over here.
public static class SdsRegistryKey
{
    /// DedupKey.Norm: trim, lowercase invariantly, collapse each whitespace run to one space.
    /// Null-tolerant where DedupKey is not, because these values arrive from a search projection
    /// where every field binds nullable; a null must fail to match, never throw on the text endpoint.
    public static string Norm(string? value) =>
        value is null ? "" : WhitespaceRuns.Replace(value.Trim().ToLowerInvariant(), " ");

    /// DedupKey.ForRegistry: the `sds-registry` document id, and the `sds` document-id payload.
    public static string ForRegistry(string? cas, string? supplier, string? revisionDate) =>
        $"{Norm(cas)}|{Norm(supplier)}|{Norm(revisionDate)}";

    private static readonly Regex WhitespaceRuns = new(@"\s+", RegexOptions.Compiled);
}

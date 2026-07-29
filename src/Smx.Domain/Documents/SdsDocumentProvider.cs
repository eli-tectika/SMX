namespace Smx.Domain.Documents;

/// The safety-data-sheet half of the catalog: `sds-registry` (sheets that exist) unioned with the
/// rows of `sds-master-list` that are not already covered by a sheet — substances the system knows
/// it needs a sheet for and does not have.
///
/// "Covered" is judged by actual linkage to a registry row (masterListId, falling back to CAS — see
/// ListAsync), not by trusting the master row's own `Status` text. A `fetched` status with no
/// matching sheet is real drift (a deleted registry row, a broken link) and must still surface as a
/// gap; the point of D9 is that the catalog reports what it can actually open, not what a status
/// field claims.
///
/// Emitting the gaps is deliberate (design D9). A missing MSDS is exactly what blocks an order, so a
/// library that listed only files would let absence read as coverage.
public sealed class SdsDocumentProvider(ISdsDocumentSource source)
{
    public const string SheetContentType = "application/pdf";

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        var sheets = await source.ListSheetsAsync(ct);
        var master = await source.ListMasterAsync(ct);

        var rows = sheets.Select(ToSummary).ToList();

        // Suppress the gap for anything already covered by a sheet. The link is masterListId where
        // the ingest recorded one, and CAS otherwise — older registry rows predate masterListId.
        var coveredMasterIds = sheets.Where(s => s.MasterListId is { Length: > 0 })
                                     .Select(s => s.MasterListId!)
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Deliberately includes superseded sheets: a substance whose only sheet is superseded is
        // covered, not a gap — the sheet still opens, and State names it superseded. Narrowing this
        // to non-superseded sheets only would look like a tightening but would re-emit a gap for a
        // substance that already has something openable, just outdated.
        var coveredCas = sheets.Select(s => s.Cas).ToHashSet(StringComparer.OrdinalIgnoreCase);

        rows.AddRange(master
            .Where(m => !coveredMasterIds.Contains(m.Id) && !coveredCas.Contains(m.Cas))
            .Select(m => ToGapSummary(m, Explain(m))));

        return rows;
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out var payload)) return null;

        if (kind == DocumentId.Sds)
        {
            var sheet = await source.GetSheetAsync(payload, DocumentId.PartitionKeyOf(kind, payload), ct);
            return sheet is null ? null : new DocumentDetail(
                ToSummary(sheet),
                Provenance(sheet),
                UnavailableReason: null,
                UnavailableDetail: null,
                SupersededById: sheet.SupersededBy is { Length: > 0 }
                    ? DocumentId.Encode(DocumentId.Sds, sheet.SupersededBy) : null,
                BlobPath: sheet.BlobPath);
        }

        if (kind == DocumentId.SdsGap)
        {
            var m = await source.GetMasterAsync(payload, DocumentId.PartitionKeyOf(kind, payload), ct);
            if (m is null) return null;

            var explanation = Explain(m);
            return new DocumentDetail(
                ToGapSummary(m, explanation),
                [
                    new("CAS", m.Cas),
                    new("Element", m.Element),
                    new("Form", m.Form),
                    new("Status", m.Status),
                    new("Fetch attempts", m.AttemptCount.ToString()),
                    new("Last attempt", m.LastAttemptUtc ?? "not recorded"),
                    // "not scheduled" rather than "never": the sweep picks the row up regardless, and
                    // the operator can force it now. An absent stamp is a gap in the schedule, not a
                    // statement that the system has given up — it no longer can.
                    new("Next attempt", m.NextAttemptUtc ?? "not scheduled"),
                ],
                UnavailableReason: UnavailableReasons.NeverFetched,
                UnavailableDetail: explanation,
                SupersededById: null,
                BlobPath: null);
        }

        return null;
    }

    // DocumentId.Encode validates only the KIND, never the payload — a registry id with an empty
    // segment (Sds/Triggers/OperatorUpload.cs validates only Cas and PdfBase64, so a blank
    // RevisionDate reaches here as "cas|supplier|") still encodes into something that looks like a
    // normal id and then fails TryDecode the moment anyone opens it. CanEncode catches that HERE, at
    // list time (BLOCKING 2), so the row is visibly broken instead of silently un-openable — a bare
    // 404 naming no cause is the inverse of spec §8. GetAsync never reaches the "unresolvable" branch
    // itself: a malformed id already fails DocumentId.TryDecode before dispatch, so this row can only
    // ever be seen through ListAsync, which is exactly where it needs to be visible.
    private static DocumentSummary ToSummary(SdsSheetRow s)
    {
        var id = DocumentId.Encode(DocumentId.Sds, s.Id);
        if (!DocumentId.CanEncode(DocumentId.Sds, s.Id))
            return new DocumentSummary(
                Id: id,
                Kind: DocumentKinds.Sds,
                Title: string.IsNullOrWhiteSpace(s.ProductName) ? s.Cas : s.ProductName,
                Subtitle: $"CAS {s.Cas} · {UnavailableReasons.UnresolvableId}: malformed registry id \"{s.Id}\"",
                Available: false,
                State: DocumentStates.Missing,
                ContentType: null,
                OfficialDate: s.RevisionDate,
                IngestedUtc: s.IngestedUtc,
                Cas: s.Cas);

        return new DocumentSummary(
            Id: id,
            Kind: DocumentKinds.Sds,
            Title: string.IsNullOrWhiteSpace(s.ProductName) ? s.Cas : s.ProductName,
            Subtitle: $"CAS {s.Cas} · {s.Supplier} · rev {s.RevisionDate} · {RegionLanguage(s)}",
            Available: true,
            State: s.SupersededBy is { Length: > 0 } ? DocumentStates.Superseded : DocumentStates.Available,
            ContentType: SheetContentType,
            OfficialDate: s.RevisionDate,
            IngestedUtc: s.IngestedUtc,
            Cas: s.Cas);
    }

    // Same exposure on the gap side: SdsMasterRow.Id is `{element}_{form}` (DedupKey.ForMasterList),
    // and a blank Form produces the same empty-segment shape.
    private static DocumentSummary ToGapSummary(SdsMasterRow m, string explanation)
    {
        var id = DocumentId.Encode(DocumentId.SdsGap, m.Id);
        if (!DocumentId.CanEncode(DocumentId.SdsGap, m.Id))
            return new DocumentSummary(
                Id: id,
                Kind: DocumentKinds.Sds,
                Title: $"{m.Element} {m.Form} — no safety sheet",
                Subtitle: $"CAS {m.Cas} · {UnavailableReasons.UnresolvableId}: malformed master-list id \"{m.Id}\"",
                Available: false,
                State: DocumentStates.Missing,
                ContentType: null,
                OfficialDate: null,
                IngestedUtc: null,
                Cas: m.Cas);

        return new DocumentSummary(
            Id: id,
            Kind: DocumentKinds.Sds,          // facet: a missing sheet is still a sheet
            Title: $"{m.Element} {m.Form} — no safety sheet",
            Subtitle: $"CAS {m.Cas} · {explanation}",
            Available: false,
            State: DocumentStates.Missing,
            ContentType: null,
            OfficialDate: null,
            IngestedUtc: null,
            Cas: m.Cas);
    }

    private static IReadOnlyList<ProvenanceField> Provenance(SdsSheetRow s) =>
    [
        new("Source URL", s.SourceUrl, ProvenanceKinds.Url),
        new("Supplier", s.Supplier),
        new("Product name", string.IsNullOrWhiteSpace(s.ProductName) ? "not recorded" : s.ProductName),
        new("CAS", s.Cas),
        new("Revision date", s.RevisionDate),
        new("Region / language", RegionLanguage(s)),
        new("Ingested", s.IngestedUtc),
        new("Indexed", s.Indexed ? "yes" : "no"),
        new("Superseded by", s.SupersededBy is { Length: > 0 } ? s.SupersededBy : "—"),
    ];

    /// RegistryPointer.Region and .Language are both nullable at the writer, so "EU / en" has three
    /// other real shapes. Interpolating them directly rendered a bare " / " for a sheet that carried
    /// neither — which reads as a value rather than as an absence, and spec §3 invariant 6 is that
    /// what was not recorded says so. Each half is named independently: one known and one missing is
    /// the common case, and flattening it would discard a fact the registry does hold.
    private static string RegionLanguage(SdsSheetRow s)
    {
        var region = string.IsNullOrWhiteSpace(s.Region) ? "not recorded" : s.Region;
        var language = string.IsNullOrWhiteSpace(s.Language) ? "not recorded" : s.Language;
        return region == "not recorded" && language == "not recorded" ? "not recorded" : $"{region} / {language}";
    }

    /// What a gap row says about itself.
    ///
    /// The `failed` line names the NEXT ATTEMPT, which is the fact the operator actually needs. Before
    /// the 2026-07-29 redesign a row that burned its retry cap went to `awaiting_operator` and read
    /// "awaiting operator upload — no automated source" — a sentence that was true when written and
    /// that the redesign falsifies twice over: there is an automated source now, and backoff means the
    /// system will keep chasing it forever rather than giving up. Nothing is terminal, so nothing here
    /// may read as terminal.
    ///
    /// The `awaiting_operator` branch is KEPT on purpose even though the status is deleted. A
    /// migration rewrites those rows, but a read can land before it runs, and the fallback arm renders
    /// a raw storage token at the operator. Its wording is the new truth, not the old one.
    private static string Explain(SdsMasterRow m) => m.Status switch
    {
        "failed" => $"{m.AttemptCount} fetch attempt(s) failed · last {m.LastAttemptUtc ?? "not recorded"}"
                    + NextAttempt(m),
        // Legacy: pre-migration rows only.
        "awaiting_operator" => $"{m.AttemptCount} fetch attempt(s) failed · scheduled for retry"
                               + NextAttempt(m),
        "pending" => "queued for fetch",
        _ => m.Status,
    };

    /// Date only, not the full instant: the retry is a daily sweep, so an hour and a minute would be
    /// precision the schedule does not have.
    private static string NextAttempt(SdsMasterRow m) =>
        m.NextAttemptUtc is { Length: >= 10 } n ? $" · next attempt {n[..10]}" : "";
}

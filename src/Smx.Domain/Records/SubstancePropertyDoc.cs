namespace Smx.Domain.Records;

/// The metal loading of a compound — the mass fraction of the marker ELEMENT in it (Y2O3 is 0.787 Y).
/// Order amounts are computed from it, and it exists in no catalog we have: the operator enters it once,
/// and it is keyed by CAS in the CROSS-PROJECT knowledge layer, so the next project that meets this
/// compound never asks again. That is the knowledge layer doing what it exists for.
///
/// `Basis` is mandatory and is the operator's own words (a formula, a supplier spec sheet, an assay). It is
/// what makes the number checkable — the same discipline as a Learned Conclusion's provenance.
///
/// NOT on the per-project record bus: this lives in the `substance-properties` container, PK /cas.
public sealed class SubstancePropertyDoc
{
    public required string Id { get; set; }
    public required string Cas { get; set; }              // partition key
    public string Type { get; set; } = KnowledgeTypes.SubstanceProperty;
    public required string Element { get; set; }
    public required string Form { get; set; }

    /// Mass fraction in (0, 1]. NOT validated here — see <see cref="OrderAmount.Compute"/>, which refuses
    /// `is &lt;= 0 or &gt; 1` and any non-finite value. That guard sits on the only path where a bad loading
    /// can do harm (a wrong number on a purchase order), so it is the one that must hold; the write endpoint
    /// rejects bad operator input early for a good error message, not as the safety net. A third copy of the
    /// rule here would be a third place for it to drift.
    public required double MetalLoading { get; set; }

    public required string Basis { get; set; }
    public required string EnteredAt { get; set; }        // "O" format
}

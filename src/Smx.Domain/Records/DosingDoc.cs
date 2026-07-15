namespace Smx.Domain.Records;

/// The three legal values of <see cref="Bound.Kind"/>. A constants class rather than three bare literals,
/// for the same reason <see cref="Determinations"/> is one: the strings are spelled in the agent's prompt,
/// in DosingAgent's validation, and in the UI, and a typo in any of them ("estimated" for "estimate") is not
/// a compile error — it is a bound that renders as an unknown kind and quietly loses the one signal that
/// tells the operator how far to trust it.
public static class BoundKinds
{
    /// The physicist's data. NEVER produced by the agent — see <see cref="Bound"/>.
    public const string Measured = "measured";
    public const string Regulatory = "regulatory";
    public const string Estimate = "estimate";

    /// Every kind there is. DosingAgent validates an agent-authored bound against this (and additionally
    /// rejects <see cref="Measured"/>), so an unknown kind is caught at the boundary rather than persisted.
    public static readonly string[] All = [Measured, Regulatory, Estimate];
}

/// One end of a ppm window, with WHERE IT CAME FROM. The UX spec requires basis + confidence per bound,
/// because the two ends are not equally trustworthy: the floor is MEASURED (confidence 1.0), while an upper
/// bound with no regulatory cap is an ESTIMATE that is known to run low. Rendering them alike would invite
/// the operator to trust a guess as much as a measurement.
///
/// <see cref="Kind"/> is one of <see cref="BoundKinds"/>. A "measured" bound is NEVER produced by the agent —
/// only the physicist's data is measured, and an agent that could label its own estimate "measured" would
/// launder a guess into the one field the operator trusts absolutely.
public sealed record Bound(double Ppm, string Basis, string Kind, double Confidence);

/// The dosable range for one substance in one component. The RECOMMENDED value must sit strictly inside
/// (Floor, Upper) — a ppm at or below the floor is a marker nobody can read in the field, and there is no
/// downstream check that catches it.
public sealed record PpmWindow(
    string ComponentId, string Cas, string Element,
    Bound Floor, Bound Upper, double RecommendedPpm, double QuantificationPpm);

/// One marker inside a code, with the order amount that follows from its ppm.
public sealed record CodeMarker(
    string Cas, string Element, double Ppm, double MetalLoading, double ElementMassMg, double CompoundMassMg);

/// A code: 2–3 markers in ONE component, identified by their ppm RATIO. Per component — there is no
/// product-wide marker (interaction law 1).
public sealed record MarkerCode(string ComponentId, IReadOnlyList<CodeMarker> Markers, string Rationale)
{
    /// DERIVED from <see cref="Markers"/> on every read — deliberately NOT a stored field.
    ///
    /// The signature is the code's IDENTITY: it is what a field reader matches a suspect sample against, and
    /// <see cref="Smx.Domain.RatioSignature"/> notes that nothing downstream re-derives it. Stored as a plain
    /// string it would be a second, independently-writable copy of a truth that already lives in the markers'
    /// ppms — and the two can part company. The operator revises a code's ppm through the agent (Law 8: no
    /// direct edits), the agent rewrites `markers` and forgets to rewrite `ratioSignature`, and the record now
    /// carries a code whose stated identity is not its actual ratio. Nothing would catch it: no schema
    /// violation, no failing test, no downstream recomputation. The field reader would call a genuine product
    /// counterfeit.
    ///
    /// Deriving it makes that disagreement unrepresentable. It also takes the field out of the AGENT's hands
    /// entirely — the model authors ppms, never the identity those ppms imply — which is the same principle
    /// that stops it from stamping <see cref="BoundKinds.Measured"/> on its own estimate.
    ///
    /// On the wire in BOTH directions, as it must be: System.Text.Json serializes a get-only property (so
    /// Cosmos, the API and the UI all still see `ratioSignature`), and IGNORES it when deserializing (so a
    /// stale or tampered value in a persisted document cannot be read back in — it is recomputed from the
    /// markers that are the actual truth).
    ///
    /// Fully qualified because the property name shadows the class name inside this scope.
    public string RatioSignature =>
        Smx.Domain.RatioSignature.Of([.. Markers.Select(m => (m.Element, m.Ppm))]);
}

public sealed class DosingDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Dosing;
    public List<PpmWindow> Windows { get; set; } = [];
    public List<MarkerCode> Codes { get; set; } = [];

    /// The SOFT code-finalization checkpoint (UX §4.5). A REVIEW NOTE, not a gate: it records that the
    /// PL/VP/physics review happened. It does not block, and it must never be made to block — the hard gates
    /// are Regulatory and VP, and adding a third would dilute what a signature means.
    public string? ReviewNote { get; set; }
    public string? ReviewedAt { get; set; }
    public required string GeneratedAt { get; set; }
}

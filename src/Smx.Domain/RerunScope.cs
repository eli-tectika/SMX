using Smx.Domain.Records;

namespace Smx.Domain;

/// WHAT A CHANGED REQUIREMENT INVALIDATES — the blast radius of each amendable field.
///
/// The rule is DATA DEPENDENCY, NOT PIPELINE POSITION. A batch mass is a multiplier on order amounts and
/// touches nothing else; a target market is an input to the per-component application check and changes no
/// chemistry. Re-running everything downstream of a change would burn minutes of Foundry time on stages
/// whose inputs did not move — and, worse, would void signatures for nothing, since a rerun that reaches
/// Regulatory voids the R.E.'s approval whether or not the analysis changed.
///
/// THIS MAP AND <see cref="IntakeAnswers"/>'s ALLOW-LIST ARE TWO VIEWS OF ONE LIST. A field the operator can
/// change but whose consequences nobody declared would silently amend the record and re-run NOTHING: the
/// project would go on displaying an analysis computed from a requirement that no longer holds, with no
/// error anywhere. <c>RerunScopeTests</c> reflects over both and fails the build if they drift — the same
/// discipline that made the parked-status family a compile error rather than a review problem.
///
/// Adding a writable field therefore means deciding, in the open, what it invalidates.
public static class RerunScope
{
    /// Everything from the pool onward. The polymer and the objective decide which chemistry is a candidate
    /// at all, so there is no earlier stage to preserve.
    private static readonly string[] Everything =
        [Stages.Pool, Stages.Discovery, Stages.Regulatory, Stages.Matrix, Stages.Dosing, Stages.Decision];

    /// The regulatory lane. Matrix rides along because it is ASSEMBLED from the verdicts — leaving it would
    /// keep a matrix that looks current and describes verdicts that no longer exist. Decision rides along
    /// because its rows were folded over the old compliant set.
    private static readonly string[] RegulatoryLane =
        [Stages.Regulatory, Stages.Matrix, Stages.Decision];

    /// The cheapest rerun there is, and the reason this map earns its keep: a batch mass scales the ORDER
    /// AMOUNT and nothing else. Nothing about the chemistry, the verdicts or the ppm window moves.
    private static readonly string[] DosingLane = [Stages.Dosing, Stages.Decision];

    /// Keyed on the FIELD NAME, not the full path: the allow-list addresses component fields as
    /// `components.{id}.{field}`, and the blast radius of `markets` does not depend on which component's
    /// markets changed.
    public static readonly IReadOnlyDictionary<string, string[]> Map =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // The polymer decides which chemistry is compatible at all.
            ["material"] = Everything,
            // What Discovery is optimising for.
            ["objective"] = Everything,
            // Target markets are a direct input to the per-component application check. The candidate
            // chemistry is untouched — which is exactly why this is not `Everything`.
            ["markets"] = RegulatoryLane,
            // The same check, other axis.
            ["application"] = RegulatoryLane,
            // A client ban behaves as a regulatory constraint.
            ["clientRestrictedList"] = RegulatoryLane,
            // A pure multiplier on order amounts.
            ["batchMassKg"] = DosingLane,
        };

    /// The stages an amendment to <paramref name="field"/> invalidates, in pipeline order.
    ///
    /// THROWS on an undeclared field rather than returning empty. Empty would read as "this change
    /// invalidates nothing", which is the single most dangerous answer this function can give: the record
    /// would carry the new requirement while every stage went on displaying an analysis computed from the
    /// old one. Fail closed, loudly, the same way <see cref="RevisionEffects"/> does.
    public static IReadOnlyList<string> For(string field)
    {
        var leaf = Leaf(field);
        return Map.TryGetValue(leaf, out var stages)
            ? stages
            : throw new ArgumentOutOfRangeException(nameof(field),
                $"no rerun scope is declared for '{field}'. Every amendable field must declare what it " +
                $"invalidates — see RerunScope.Map. Declaring none would let the requirement change while " +
                $"the analysis went on describing the old one.");
    }

    /// `components.bottle.markets` → `markets`; `clientRestrictedList` → itself.
    private static string Leaf(string field)
    {
        var parts = (field ?? "").Split('.');
        return parts[^1];
    }

    /// Whether any stage in the blast radius carries a signature that resetting it would void.
    ///
    /// The one place an amendment stops to ask (spec §9.4). Everything unsigned just re-runs — nothing waits
    /// — but silently un-signing a human's approval is not something software should do quietly, and
    /// PipelineRunner already voids `ApprovedAt`/`ApprovedBy` as a pair when a revision breaks a gate.
    ///
    /// Regulatory in the radius threatens the R.E.'s signature; Decision threatens the VP's.
    public static IReadOnlyList<string> SignaturesAtRisk(
        IReadOnlyList<string> scope, bool regulatorySigned, bool vpSigned)
    {
        var at = new List<string>();
        if (regulatorySigned && scope.Contains(Stages.Regulatory)) at.Add(GateTypes.Regulatory);
        if (vpSigned && scope.Contains(Stages.Decision)) at.Add(GateTypes.Vp);
        return at;
    }
}

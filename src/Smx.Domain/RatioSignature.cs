using System.Globalization;

namespace Smx.Domain;

/// A code's identity: the ppm RATIO between its markers, normalised to the largest. Scale-invariant on
/// purpose — the same code dosed 2x heavier is the same code, and the field reader identifies it by the ratio,
/// not by absolute ppm.
///
/// This THROWS where the rest of the domain returns a (value, error) tuple, and the asymmetry is deliberate.
/// A tuple says "a human may legitimately hand me this"; a throw says "reaching this state is a BUG". Every
/// ppm arriving here is code-owned: DosingAgent.Validate has already pinned each recommended ppm strictly
/// inside (floor, upper), and a detection floor is always positive (background >= 0 plus 3 x a positive LOD).
/// So a non-positive or non-finite ppm here means an invariant upstream has broken, and the loudest failure
/// is the one you want — StageDispatcher wraps every stage run in a catch and parks the stage `failed` with
/// this message, so a throw costs a visible parked stage, not a crashed dispatch loop. The alternative is
/// worse than a parked stage: a rendered-anyway signature mints a code whose IDENTITY is wrong, and a wrong
/// signature makes a field reader call a genuine product counterfeit. Nothing downstream re-derives it.
public static class RatioSignature
{
    public static string Of(IReadOnlyList<(string Element, double Ppm)> markers)
    {
        // A code's identity is the ratio BETWEEN its markers, so fewer than two has no ratio to take: one
        // marker would render the vacuously scale-invariant "Y = 1.00", which identifies nothing while
        // looking exactly like a signature, and an empty list would die inside Max() with an opaque
        // InvalidOperationException. The UPPER bound (a code is at most 3 markers) is a different rule — it
        // is about what a field reader can resolve, not about whether a ratio exists — and it belongs with
        // DosingAgent's code validation, where a violation feeds the model an error and retries rather than
        // failing the stage.
        if (markers.Count < 2)
            throw new ArgumentException(
                $"a code is 2-3 markers and its signature is the ratio BETWEEN them; " +
                $"one cannot be formed from {markers.Count}.", nameof(markers));

        foreach (var m in markers)
            // Finite AND positive, in that order. NaN is the one that matters: it fails `<= 0` as surely as
            // it fails every other comparison, and Enumerable.Max SKIPS NaN rather than propagating it — so
            // without this guard a NaN ppm renders straight into the signature ("Y:Zr = 1.00:NaN") and is
            // persisted as a code's identity. ("NaN" reaches a double through STJ: JsonSerializerDefaults.Web
            // sets NumberHandling.AllowReadingFromString, which reads the named literal out of a JSON string.)
            if (!double.IsFinite(m.Ppm) || m.Ppm <= 0)
                throw new ArgumentOutOfRangeException(nameof(markers),
                    $"every marker's ppm must be positive and finite; {m.Element} is {Num(m.Ppm)}");

        var max = markers.Max(m => m.Ppm);
        // Ordered by ppm descending, then by element — so the signature does not depend on input order, which
        // would make the same code render two ways and break every equality check downstream. The element
        // tie-break is load-bearing rather than cosmetic: two markers at the SAME ppm (a 1:1 code, the
        // simplest code there is) are exactly the case the ppm sort cannot order.
        var parts = markers
            .OrderByDescending(m => m.Ppm).ThenBy(m => m.Element, StringComparer.Ordinal)
            .ToList();

        // Two decimals, and InvariantCulture always: a signature is compared as a STRING, so under a
        // comma-decimal culture the same code would render "1,00:0,50" and match nothing.
        //
        // The two decimals ROUND, and that is an accepted risk, not an oversight: true ratios of 0.504 and
        // 0.4996 both render 0.50, so two distinct codes can collide on one signature. Adding decimals is the
        // wrong fix — a field XRF cannot resolve two ratios 1% apart either, so codes that collide here are
        // codes the READER could not tell apart in deployment. The collision is therefore real, and a future
        // code-uniqueness check should treat it as a conflict to REJECT, never resolve it by refining the
        // rendering (which would mint two "distinct" codes that read identically in the field).
        return string.Join(":", parts.Select(m => m.Element)) + " = " +
               string.Join(":", parts.Select(m => (m.Ppm / max).ToString("0.00", CultureInfo.InvariantCulture)));
    }

    /// InvariantCulture, always — the message is read by the operator (it lands in the stage's Error) and
    /// quoted back to a model. Matches DetectionFloor.Num.
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}

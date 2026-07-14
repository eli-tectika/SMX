using System.Globalization;
using Smx.Domain.Records;

namespace Smx.Domain;

/// One element's ppm floor in one component, with the basis it was computed from. `Basis` is not decoration:
/// the UX spec requires every bound to show its basis, and the operator must be able to re-derive the number.
public sealed record Floor(double DetectionPpm, double QuantificationPpm, string Basis);

/// The ppm detection floor — the BINDING CONSTRAINT on the whole product.
///
/// A recommended ppm below the true floor means the marker is physically unreadable by the deployment
/// device: SMX ships a taggant nobody can detect, and there is no downstream check that catches it. Nobody
/// finds out until deployment. So this is computed from data a human MEASURED, and it refuses — loudly —
/// rather than guess. Dosing parks (awaiting-physics) and produces nothing.
///
/// Every guard below therefore fails in the SAME direction: refuse. None of them substitutes a default, and
/// in particular none treats an absent measurement as a zero — a genuinely zero background is a measurement,
/// and it is recorded as 0.0. Silence is not data.
///
/// The 3σ / 10σ multipliers are the IUPAC convention (detection vs. quantification), NOT an SMX
/// measurement. They live here and nowhere else. CONFIRM THEM WITH SMX PHYSICS AT FIRST LIVE USE; if SMX
/// works to different factors, this file is the only thing that changes.
public static class DetectionFloor
{
    public const double DetectionSigma = 3.0;
    public const double QuantificationSigma = 10.0;
    public const string Ppm = "ppm";

    /// <param name="componentId">Matched ORDINALLY, like <paramref name="element"/> below. The API already
    /// enforces that a background names a declared component (CreateProjectRequest.Validate, an ordinal
    /// HashSet), so a case-variant id cannot reach here from intake; and if one ever did, the result is a
    /// refusal rather than a floor computed against the wrong row.</param>
    /// <param name="element">Matched ORDINALLY because element symbols are case-significant chemistry: "Co"
    /// is cobalt and "CO" is not an element. A case-insensitive match could pair a request for one with a
    /// measurement of the other — and the wrong element's background is a wrong floor.</param>
    public static (Floor? Floor, string? Error) Compute(
        IReadOnlyList<MeasuredBackground> background, XrfDevice? device, string componentId, string element)
    {
        if (device is null)
            return (null, "no XRF device was captured at intake, so the ppm floor cannot be targeted at the " +
                          "device that must read the marker in deployment. Enter the device and its LODs.");

        // Duplicates REFUSE rather than take the first. A physicist who re-measures leaves two rows for the
        // same (component, element), and `FirstOrDefault` would silently pick whichever happened to be first
        // — which may be the stale, LOWER one. Low is the direction that ships an unreadable marker, so the
        // system does not get to choose the measurement it likes: it names the conflict and parks.
        var matches = background.Where(b => b.Component == componentId && b.Element == element).ToList();
        if (matches.Count == 0)
            return (null, $"no measured background for {element} in '{componentId}'. The floor is computed " +
                          $"from a measurement — an absent one is not a zero (a zero background is itself a " +
                          $"measurement, recorded as 0). The physicist must measure it.");
        if (matches.Count > 1)
            return (null, $"more than one measured background for {element} in '{componentId}' " +
                          $"({string.Join(", ", matches.Select(m => Num(m.Level) + " " + m.Unit))}). The " +
                          $"floor cannot be computed from two measurements — silently taking one risks taking " +
                          $"the stale, lower value. Keep only the current measurement.");
        var bg = matches[0];

        var lods = device.Lods.Where(l => l.Element == element).ToList();
        if (lods.Count == 0)
            return (null, $"the device '{device.Model}' has no LOD for {element}, so the floor for it cannot " +
                          $"be computed. Enter the LOD.");
        if (lods.Count > 1)
            return (null, $"the device '{device.Model}' lists more than one LOD for {element} " +
                          $"({string.Join(", ", lods.Select(l => Num(l.Lod) + " " + l.Unit))}). Keep one.");
        var lod = lods[0];

        // Units are carried, not assumed. Adding counts to ppm yields a perfectly reasonable-looking number
        // that is simply wrong, and it mis-doses in the direction nobody checks.
        if (!string.Equals(bg.Unit, Ppm, StringComparison.OrdinalIgnoreCase))
            return (null, $"the measured background for {element} is in '{bg.Unit}', not '{Ppm}'. The floor " +
                          $"is a ppm value and cannot be computed from a background in another unit.");
        if (!string.Equals(lod.Unit, Ppm, StringComparison.OrdinalIgnoreCase))
            return (null, $"the LOD for {element} on '{device.Model}' is in '{lod.Unit}', not '{Ppm}'. The " +
                          $"floor is a ppm value and cannot be computed from a LOD in another unit.");

        // Finiteness before ordering, because NaN fails `< 0` AND fails `<= 0`: it would sail through every
        // check below and produce a NaN floor — and every comparison against a NaN floor is false, so a
        // downstream "is the ppm above the floor?" answers yes. That is the false pass, in the one number
        // nothing downstream re-checks. ("NaN" reaches a double through STJ: JsonSerializerDefaults.Web sets
        // NumberHandling.AllowReadingFromString, which reads the named literal out of a JSON string.)
        if (!double.IsFinite(bg.Level))
            return (null, $"the measured background for {element} is not a finite number ({bg.Level}).");
        if (!double.IsFinite(lod.Lod))
            return (null, $"the LOD for {element} is not a finite number ({lod.Lod}).");

        if (bg.Level < 0) return (null, $"the measured background for {element} is negative ({Num(bg.Level)}).");
        if (lod.Lod <= 0) return (null, $"the LOD for {element} must be positive; it is {Num(lod.Lod)}. A " +
                                        $"non-positive LOD would put the floor at or below the background " +
                                        $"the marker has to be seen against.");

        return (new Floor(
            bg.Level + DetectionSigma * lod.Lod,
            bg.Level + QuantificationSigma * lod.Lod,
            $"{device.Model}: LOD {Num(lod.Lod)} ppm ({element}) over a measured background of " +
            $"{Num(bg.Level)} ppm in '{componentId}'; detection = bg + {Num(DetectionSigma)}σ, " +
            $"quantification = bg + {Num(QuantificationSigma)}σ (IUPAC)"),
            null);
    }

    /// InvariantCulture, always. The basis is read by the operator and quoted to a model, and under a
    /// comma-decimal culture "1.5" renders as "1,5" — a separator read the other way is exactly the 1000×
    /// mis-dose this codebase already refuses on intake (IntakeAnswers.ParseNumber). A number in the basis
    /// must mean one thing everywhere.
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}

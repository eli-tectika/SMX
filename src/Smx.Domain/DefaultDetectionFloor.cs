using Smx.Domain.Records;

namespace Smx.Domain;

/// The DECLARED DEFAULT floor — what Dosing uses when no physicist measurement is on file.
///
/// <see cref="DetectionFloor"/> refuses without a measurement, and that refusal is correct: a floor that
/// reads low ships a marker nobody can detect in the field. What changed (execution-core design §8) is what
/// the CALLER does with the refusal. It no longer parks the pipeline. It falls back to here, and flags the
/// component — and the flag blocks the ORDER, not the run.
///
/// This is a SEPARATE, SEPARATELY-NAMED function rather than a default hidden inside
/// <see cref="DetectionFloor.Compute"/>, for the same reason <see cref="ProvisionalSet"/> is separate from
/// <see cref="CompliantSet"/>: the caller must choose the weaker number deliberately and in the open. A
/// silent default inside Compute would make a guess indistinguishable from a measurement at every call
/// site, which is precisely the confusion the Bound.Kind machinery exists to prevent.
///
/// It is a floor over the device's generic limit of detection ALONE. There is no substrate background in
/// it, and a real background can only push the true floor UP — so this number is knowingly OPTIMISTIC.
/// That is exactly why it may not release procurement.
public static class DefaultDetectionFloor
{
    public static Floor? Of(XrfDevice? device, string element)
    {
        if (device is null) return null;

        // Ordinal, like DetectionFloor: element symbols are case-significant chemistry. "Co" is cobalt and
        // "CO" is not an element, and the wrong element's limit is a wrong floor.
        var lod = device.Lods.FirstOrDefault(l => string.Equals(l.Element, element, StringComparison.Ordinal));
        if (lod is null) return null;

        // A unit mismatch REFUSES rather than converts, exactly as DetectionFloor does. Mixing counts with
        // ppm yields a number that looks perfectly reasonable and is simply wrong.
        if (!string.Equals(lod.Unit, DetectionFloor.Ppm, StringComparison.OrdinalIgnoreCase)) return null;

        return new Floor(
            lod.Lod * DetectionFloor.DetectionSigma,
            lod.Lod * DetectionFloor.QuantificationSigma,
            $"estimated floor — no physicist measurement on file. Computed from the {device.Model} generic " +
            $"limit of detection for {element} ({lod.Lod} ppm) at {DetectionFloor.DetectionSigma}σ, with NO " +
            $"substrate background. A measured background can only raise this floor.");
    }
}

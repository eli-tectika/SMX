using System.Globalization;

namespace Smx.Domain;

/// What procurement buys for one marker in one component: the mass of the ELEMENT that has to end up in the
/// batch, and the mass of the COMPOUND you must order to put it there. They are not the same number, and the
/// difference is the whole point — see <see cref="OrderAmount"/>.
public sealed record Order(double ElementMassMg, double CompoundMassMg)
{
    public double CompoundMassG => CompoundMassMg / 1000.0;
}

/// How much of the COMPOUND to buy. ppm is mg/kg — mass over mass — so this takes a batch MASS, never a
/// volume: converting a volume without a density silently mis-doses (a polymer by ~10%, gold by 19x). The UX
/// spec says "ppm x batch volume"; following it literally is a bug. If the operator has a volume, they
/// multiply by the density and enter the mass.
///
/// It returns the compound mass, not the element mass. Ordering the element mass of an oxide under-doses by
/// whatever the oxide's non-metal fraction is (21% for Y2O3), which lands the whole batch below the detection
/// floor — the marker becomes unreadable in the field and nothing downstream catches it.
///
/// Every guard fails in the same direction: refuse, and name the offender. None substitutes a default — in
/// particular none treats an unknown metal loading as 1.0 ("it's the pure metal"), which is precisely the
/// assumption that under-orders an oxide.
public static class OrderAmount
{
    /// <param name="ppm">The recommended ppm (mg/kg) for this marker in this component.</param>
    /// <param name="batchMassKg">Nullable because intake's <c>ComponentSpec.BatchMassKg</c> is: the operator
    /// may simply never have been asked. Absent is not zero and it is not "assume 1 kg" — it is a refusal.</param>
    /// <param name="metalLoading">The mass fraction of the marker element in the compound (Y2O3 is 0.787),
    /// operator-entered with a basis. Guarded to (0, 1].</param>
    public static (Order? Amount, string? Error) Compute(double ppm, double? batchMassKg, double metalLoading)
    {
        // Finiteness FIRST, before any ordering comparison, because NaN fails ALL of them: NaN <= 0 is false
        // and NaN > 1 is false, so a NaN sails through every range guard below and yields a NaN order amount
        // — "order NaN grams" on a purchase order, with no exception raised anywhere. Infinity is the same
        // story one step further on. Non-finite doubles are genuinely reachable: Json.Options is built on
        // JsonSerializerDefaults.Web, whose NumberHandling.AllowReadingFromString parses the literal "NaN"
        // out of a JSON string — so a model reply or a persisted doc can carry one. (Same reasoning, same
        // ordering, as DetectionFloor.)
        if (!double.IsFinite(ppm))
            return (null, $"the ppm is not a finite number ({Num(ppm)}).");
        if (batchMassKg is { } kg && !double.IsFinite(kg))
            return (null, $"the batch mass (kg) is not a finite number ({Num(kg)}).");
        if (!double.IsFinite(metalLoading))
            return (null, $"the metal loading is not a finite number ({Num(metalLoading)}).");

        if (ppm <= 0) return (null, $"the ppm must be positive; it is {Num(ppm)}.");
        if (batchMassKg is not > 0)
            return (null, "the batch mass (kg) is missing or not positive. ppm is mg/kg, so an order amount " +
                          "needs a MASS — if you have a volume, multiply by the density and enter the mass.");
        // A zero loading divides by zero and puts an infinity on a purchase order; IEEE-754 will not object.
        // A loading above 1 claims more metal than compound: impossible, and it UNDER-orders, which is the
        // direction nobody checks.
        if (metalLoading is <= 0 or > 1)
            return (null, $"the metal loading must be in (0, 1]; it is {Num(metalLoading)}. It is the mass " +
                          $"fraction of the marker element in the compound (Y2O3 is 0.787).");

        var elementMassMg = ppm * batchMassKg.Value;              // (mg/kg) x kg = mg
        return (new Order(elementMassMg, elementMassMg / metalLoading), null);
    }

    /// InvariantCulture, always — these errors are read by the operator and quoted back to a model, and under
    /// a comma-decimal culture "1.4" renders as "1,4": a separator read the other way is the 1000x mis-dose
    /// this codebase already refuses on intake. Matches DetectionFloor.Num.
    private static string Num(double value) => value.ToString(CultureInfo.InvariantCulture);
}

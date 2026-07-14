using Smx.Domain;

namespace Smx.Domain.Tests;

public class OrderAmountTests
{
    [Fact]
    public void Compute_ConvertsPpmAndBatchMassIntoGramsOfCOMPOUND_NotGramsOfElement()
    {
        // 10 ppm of Y in a 250 kg batch = 2500 mg of Y. Y2O3 is 78.7% Y, so you must order
        // 2500 / 0.787 = 3176.6 mg of the OXIDE. Ordering 2500 mg of Y2O3 under-doses by 21%.
        var (amount, error) = OrderAmount.Compute(ppm: 10.0, batchMassKg: 250.0, metalLoading: 0.787);

        Assert.Null(error);
        Assert.Equal(2500.0, amount!.ElementMassMg, 3);
        Assert.Equal(2500.0 / 0.787, amount.CompoundMassMg, 3);
        // The compound mass is strictly MORE than the element mass — the direction the whole function exists
        // to get right. A `*` where the `/` belongs still passes the two equalities' tolerances for a loading
        // near 1, and it is exactly the mutation that under-orders.
        Assert.True(amount.CompoundMassMg > amount.ElementMassMg);
        Assert.Equal(amount.CompoundMassMg / 1000.0, amount.CompoundMassG, 6);
    }

    [Fact]
    public void Compute_REFUSES_AZeroLoading_RatherThanDivideByZero()
    {
        // A zero loading is an infinite order. Left unguarded this is a NaN/infinity that flows into a
        // purchase order, and IEEE-754 will not complain.
        var (amount, error) = OrderAmount.Compute(10.0, 250.0, 0.0);
        Assert.Null(amount);
        Assert.Contains("loading", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_ALoadingAboveOne_BecauseItIsPhysicallyImpossible()
    {
        // More metal than compound. It also UNDER-orders, which is the direction nobody checks.
        var (amount, error) = OrderAmount.Compute(10.0, 250.0, 1.4);
        Assert.Null(amount);
        // The offending VALUE and the allowed RANGE, both. (The plan asked for `Contains("1", error)` here,
        // which passes for the wrong reason: "1" occurs in "(0, 1]", in "1.4", and in most English sentences.)
        Assert.Contains("1.4", error!);
        Assert.Contains("(0, 1]", error);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Compute_REFUSES_ANonPositiveBatchMass(double kg)
    {
        // Guards the "the operator entered a VOLUME and we treated it as a mass" family, and the
        // "batchMassKg was never supplied so it defaulted to 0" family. Both order nothing, silently.
        var (amount, error) = OrderAmount.Compute(10.0, kg, 0.787);
        Assert.Null(amount);
        Assert.Contains("batch mass", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_AMissingBatchMass_AndSaysAVolumeIsNotAMass()
    {
        // Intake's BatchMassKg is nullable: the operator may never have been asked. Absent is not zero, and
        // it is certainly not "assume 1 kg" — so the refusal has to say what to enter, and why a volume is
        // not it (converting one without a density mis-doses a polymer by ~10% and gold by 19x).
        var (amount, error) = OrderAmount.Compute(10.0, null, 0.787);
        Assert.Null(amount);
        Assert.Contains("batch mass", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("density", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_ANonPositivePpm() =>
        Assert.NotNull(OrderAmount.Compute(0.0, 250.0, 0.787).Error);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_REFUSES_ANonFinitePpm(double ppm)
    {
        // NaN fails `<= 0` — it fails EVERY ordering comparison — so it sails through a positivity guard and
        // yields a NaN order amount. (`JsonSerializerDefaults.Web` reads the literal "NaN" out of a JSON
        // string, so a non-finite double is reachable from a model reply and from persisted data alike.)
        var (amount, error) = OrderAmount.Compute(ppm, 250.0, 0.787);
        Assert.Null(amount);
        Assert.Contains("ppm", error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_REFUSES_ANonFiniteBatchMass(double kg)
    {
        var (amount, error) = OrderAmount.Compute(10.0, kg, 0.787);
        Assert.Null(amount);
        Assert.Contains("batch mass", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_ANaNLoading_WHICH_PASSES_BOTH_RANGE_GUARDS()
    {
        // The sneakiest one in the file. NaN <= 0 is false AND NaN > 1 is false, so a NaN loading satisfies
        // "in (0, 1]" as far as C# is concerned, and `x / NaN` is NaN: an order for NaN grams, on a purchase
        // order, with no exception anywhere.
        var (amount, error) = OrderAmount.Compute(10.0, 250.0, double.NaN);
        Assert.Null(amount);
        Assert.Contains("loading", error, StringComparison.OrdinalIgnoreCase);
    }
}

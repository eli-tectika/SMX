using Smx.Domain;

namespace Smx.Domain.Tests;

public class RatioSignatureTests
{
    [Fact]
    public void Of_NormalisesToTheLargestMarker_SoTheSignatureIsScaleInvariant()
    {
        // The signature is what a reader IDENTIFIES the code by, so it must survive a uniform scaling of
        // the whole code (the same code at 2x dosing is the SAME code).
        Assert.Equal(RatioSignature.Of([("Y", 20.0), ("Zr", 10.0)]),
                     RatioSignature.Of([("Y", 40.0), ("Zr", 20.0)]));
    }

    [Fact]
    public void Of_RendersLargestFirst_WithTwoDecimals()
    {
        Assert.Equal("Y:Zr = 1.00:0.50", RatioSignature.Of([("Zr", 10.0), ("Y", 20.0)]));
    }

    [Fact]
    public void Of_IsStableUnderInputOrder()
    {
        Assert.Equal(RatioSignature.Of([("Y", 20.0), ("Zr", 10.0), ("Hf", 5.0)]),
                     RatioSignature.Of([("Hf", 5.0), ("Y", 20.0), ("Zr", 10.0)]));
    }

    [Fact]
    public void Of_IsStableUnderInputOrder_WhenTwoMarkersShareAPpm()
    {
        // The case the ppm sort CANNOT order, and therefore the only case that pins the element tie-break.
        // (With distinct ppms, LINQ's stable OrderByDescending already fixes the order, so the test above
        // stays green even with no tie-break at all — it does not test one.) Two markers at the same ppm are
        // ordinary: a 1:1 code is the simplest code there is, and it must not render two ways.
        Assert.Equal("Y:Zr = 1.00:1.00", RatioSignature.Of([("Zr", 10.0), ("Y", 10.0)]));
        Assert.Equal(RatioSignature.Of([("Y", 10.0), ("Zr", 10.0)]),
                     RatioSignature.Of([("Zr", 10.0), ("Y", 10.0)]));
    }

    [Fact]
    public void Of_ThrowsOnANonPositivePpm() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RatioSignature.Of([("Y", 0.0), ("Zr", 10.0)]));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Of_ThrowsOnANonFinitePpm(double ppm)
    {
        // NaN fails `<= 0`, so a positivity guard alone lets it through — and `Enumerable.Max` SKIPS NaN
        // rather than propagating it, so the signature would render as "Y:Zr = 1.00:NaN": a code whose
        // identity is garbage, persisted, with no exception raised. A wrong signature makes a field reader
        // call a genuine product counterfeit, and nothing downstream re-derives it.
        Assert.Throws<ArgumentOutOfRangeException>(() => RatioSignature.Of([("Y", 20.0), ("Zr", ppm)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Of_ThrowsWhenThereIsNoRatioToTake(int markerCount)
    {
        // A code's identity is the ratio BETWEEN markers: one marker has no ratio (it would render the
        // vacuously scale-invariant "Y = 1.00", which identifies nothing yet looks like a signature), and an
        // empty list would otherwise die inside Max() with an opaque InvalidOperationException.
        (string, double)[] markers = markerCount == 0 ? [] : [("Y", 20.0)];
        Assert.Throws<ArgumentException>(() => RatioSignature.Of(markers));
    }
}

using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DefaultDetectionFloorTests
{
    private static XrfDevice Device(params DeviceLod[] lods) => new("Elvatech", lods);

    [Fact]
    public void Of_UsesTheDeviceGenericLod_WhenOneIsOnFile()
    {
        var floor = DefaultDetectionFloor.Of(Device(new DeviceLod("Zr", 12.0, "ppm")), "Zr");

        Assert.NotNull(floor);
        Assert.Equal(12.0 * DetectionFloor.DetectionSigma, floor!.DetectionPpm);
        Assert.Equal(12.0 * DetectionFloor.QuantificationSigma, floor.QuantificationPpm);
    }

    [Fact]
    public void Of_SaysInItsBasis_ThatNothingWasMeasured()
    {
        // The basis string travels onto the Bound and into the UI. An operator reading "estimated floor"
        // must be able to tell WHY without opening the record.
        var floor = DefaultDetectionFloor.Of(Device(new DeviceLod("Zr", 12.0, "ppm")), "Zr");

        Assert.Contains("no physicist measurement", floor!.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Of_SaysInItsBasis_ThatTheFloorIsOptimistic()
    {
        // Without this the number reads as a floor like any other. It is knowingly LOW -- there is no
        // substrate background in it -- and the sentence saying so is the only thing carrying that.
        var floor = DefaultDetectionFloor.Of(Device(new DeviceLod("Zr", 12.0, "ppm")), "Zr");

        Assert.Contains("can only raise this floor", floor!.Basis, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Of_ReturnsNull_WithNoDevice()
    {
        // No device means no generic limit to fall back TO. Still a refusal -- but the caller turns it into
        // a flag rather than a park.
        Assert.Null(DefaultDetectionFloor.Of(null, "Zr"));
    }

    [Fact]
    public void Of_ReturnsNull_WhenTheDeviceHasNoLodForThatElement()
    {
        Assert.Null(DefaultDetectionFloor.Of(Device(new DeviceLod("Ti", 9.0, "ppm")), "Zr"));
    }

    [Fact]
    public void Of_RefusesANonPpmLod_RatherThanMixUnits()
    {
        // Same refusal DetectionFloor makes: mixing counts with ppm yields a number that looks reasonable
        // and is simply wrong.
        Assert.Null(DefaultDetectionFloor.Of(Device(new DeviceLod("Zr", 12.0, "counts")), "Zr"));
    }

    [Fact]
    public void Of_MatchesElementOrdinally_BecauseCoIsNotCO()
    {
        Assert.Null(DefaultDetectionFloor.Of(Device(new DeviceLod("CO", 12.0, "ppm")), "Co"));
    }

    [Fact]
    public void Of_IsNeverHigherThanAMeasuredFloorWouldBe_ForTheSameLod()
    {
        // The property that makes this safe to flag rather than refuse: a measured background ADDS to the
        // signal the floor must clear, so the default can only ever sit at or below the real floor. It is
        // an optimistic bound, never an inflated one -- which is why it must not release an order.
        var lod = new DeviceLod("Zr", 12.0, "ppm");
        var fallback = DefaultDetectionFloor.Of(Device(lod), "Zr")!;

        var (measured, error) = DetectionFloor.Compute(
            [new MeasuredBackground("bottle", "Zr", 40.0, "ppm")], Device(lod), "bottle", "Zr");

        Assert.Null(error);
        Assert.True(fallback.DetectionPpm <= measured!.DetectionPpm);
    }
}

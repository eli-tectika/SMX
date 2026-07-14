using System.Globalization;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DetectionFloorTests
{
    private static readonly XrfDevice Vanta =
        new("Olympus Vanta M", [new DeviceLod("Zr", 1.5, "ppm"), new DeviceLod("Y", 2.0, "ppm")]);
    private static readonly MeasuredBackground[] Background =
        [new("bottle", "Zr", 4.0, "ppm"), new("bottle", "Y", 0.0, "ppm")];

    [Fact]
    public void Compute_IsBackgroundPlusThreeSigmaForDetection_AndTenSigmaForQuantification()
    {
        // The IUPAC convention. These two numbers decide whether a marker can be READ in the field.
        var (floor, error) = DetectionFloor.Compute(Background, Vanta, "bottle", "Zr");

        Assert.Null(error);
        Assert.Equal(4.0 + 3 * 1.5, floor!.DetectionPpm);        // 8.5
        Assert.Equal(4.0 + 10 * 1.5, floor.QuantificationPpm);   // 19.0
    }

    [Fact]
    public void Compute_BasisCarriesBothOperandsAndTheDeviceItTargets()
    {
        // The basis is not decoration: the UX spec requires every bound to show its basis, and the operator
        // must be able to RE-DERIVE the number from it. So it has to name the device the floor targets and
        // carry BOTH operands — a basis missing the background or the LOD cannot be checked against anything.
        var (floor, _) = DetectionFloor.Compute(Background, Vanta, "bottle", "Zr");

        Assert.Contains("Olympus Vanta M", floor!.Basis);  // the device it targets
        Assert.Contains("1.5", floor.Basis);               // the LOD it used
        Assert.Contains("4", floor.Basis);                 // the measured background it used
        Assert.Contains("Zr", floor.Basis);
        Assert.Contains("bottle", floor.Basis);
    }

    [Fact]
    public void Compute_BasisFormatsItsNumbersInvariantly()
    {
        // A basis rendered under a comma-decimal culture reads "LOD 1,5 ppm" — and this codebase already
        // refuses "1,000" on intake precisely because a separator read the other way mis-doses by 1000x. The
        // basis is read by a human and quoted to a model; its numbers must mean one thing everywhere.
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            var (floor, _) = DetectionFloor.Compute(Background, Vanta, "bottle", "Zr");
            Assert.Contains("1.5", floor!.Basis);
            Assert.DoesNotContain("1,5", floor.Basis);
        }
        finally { Thread.CurrentThread.CurrentCulture = culture; }
    }

    [Fact]
    public void Compute_WithAZeroBackground_IsPurelyTheDeviceLimit()
    {
        var (floor, error) = DetectionFloor.Compute(Background, Vanta, "bottle", "Y");
        Assert.Null(error);
        Assert.Equal(3 * 2.0, floor!.DetectionPpm);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheUnitsDisagree()
    {
        // THE POINT OF THIS FILE'S GUARD. Adding a background in counts to a LOD in ppm produces a number
        // that looks entirely reasonable and is simply wrong — and a floor that reads low ships a marker
        // nobody can detect. There is no downstream check that catches it. Refuse.
        var counts = new MeasuredBackground[] { new("bottle", "Zr", 4.0, "counts") };
        var (floor, error) = DetectionFloor.Compute(counts, Vanta, "bottle", "Zr");

        Assert.Null(floor);
        Assert.Contains("unit", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("counts", error);
        Assert.Contains("ppm", error);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheDeviceLodIsNotInPpm()
    {
        // The same hazard from the other operand: the device's LOD sheet may quote counts-per-second.
        var device = new XrfDevice("Vanta", [new DeviceLod("Zr", 1.5, "cps")]);
        var (floor, error) = DetectionFloor.Compute(Background, device, "bottle", "Zr");

        Assert.Null(floor);
        Assert.Contains("cps", error);
        Assert.Contains("ppm", error);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheBackgroundWasNeverMeasured()
    {
        // Dosing must PARK, not guess. An absent measurement is not a zero background — a genuinely zero
        // background is a MEASUREMENT, and it is recorded as 0.0. Silence is not data.
        var (floor, error) = DetectionFloor.Compute([], Vanta, "bottle", "Zr");
        Assert.Null(floor);
        Assert.Contains("no measured background", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zr", error);
    }

    [Theory]
    [InlineData("ZR", "bottle")]  // not an element symbol; "Co" is cobalt and "CO" is not an element at all
    [InlineData("Zr", "Bottle")]  // not the component the physicist measured
    public void Compute_MatchesTheElementAndComponentExactly(string element, string component)
    {
        // Case-significant, ordinal, both sides. A case-insensitive match would silently pair a request for
        // "CO" with a measurement of cobalt. And note the failure direction: a mismatch REFUSES (dosing
        // parks) rather than computing against the wrong row, so exactness is the safe default here.
        var (floor, error) = DetectionFloor.Compute(Background, Vanta, component, element);
        Assert.Null(floor);
        Assert.Contains("no measured background", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_WhenTheDeviceHasNoLodForTheElement()
    {
        var (floor, error) = DetectionFloor.Compute(Background, new XrfDevice("Vanta", []), "bottle", "Zr");
        Assert.Null(floor);
        Assert.Contains("no LOD", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compute_REFUSES_WhenNoDeviceWasCaptured() =>
        Assert.Contains("device", DetectionFloor.Compute(Background, null, "bottle", "Zr").Error!,
            StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(-1.0)]
    [InlineData(0.0)]
    public void Compute_REFUSES_ANonPositiveLod(double lod)
    {
        // A negative LOD would pull the floor BELOW the measured background — the marker would be
        // "detectable" beneath the noise it has to be seen against. A zero LOD claims a device with no
        // detection limit at all, which pins the floor to the bare background.
        var (floor, error) = DetectionFloor.Compute(
            Background, new XrfDevice("bad", [new DeviceLod("Zr", lod, "ppm")]), "bottle", "Zr");
        Assert.Null(floor);
        Assert.NotNull(error);
    }

    [Fact]
    public void Compute_REFUSES_ANegativeBackground()
    {
        var negative = new MeasuredBackground[] { new("bottle", "Zr", -4.0, "ppm") };
        var (floor, error) = DetectionFloor.Compute(negative, Vanta, "bottle", "Zr");
        Assert.Null(floor);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_REFUSES_ANonFiniteBackground(double level)
    {
        // NaN fails `< 0` AND fails `<= 0`, so it sails through every ordering guard and yields a NaN floor
        // — and EVERY comparison against a NaN floor is false, so a downstream `if (ppm < floor) reject` does
        // not reject: the false pass, silently, in the one number nothing downstream re-checks.
        //
        // Reachable: JsonSerializerDefaults.Web sets NumberHandling.AllowReadingFromString, and STJ reads the
        // named literal "NaN" from a JSON *string* without AllowNamedFloatingPointLiterals. (1e400 likewise
        // overflows to Infinity.) The POST path happens to fail closed today when it re-serializes the
        // payload, but this function is the single place the floor is computed and it must not depend on that.
        var (floor, error) = DetectionFloor.Compute(
            [new("bottle", "Zr", level, "ppm")], Vanta, "bottle", "Zr");
        Assert.Null(floor);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Compute_REFUSES_ANonFiniteLod(double lod)
    {
        var (floor, error) = DetectionFloor.Compute(
            Background, new XrfDevice("bad", [new DeviceLod("Zr", lod, "ppm")]), "bottle", "Zr");
        Assert.Null(floor);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(9.0)]  // a re-measurement nobody removed the old row for: "first wins" would take 4.0...
    [InlineData(4.0)]  // ...and an exact duplicate is still two rows where the contract allows one.
    public void Compute_REFUSES_TwoBackgroundsForTheSameElementInTheSameComponent(double second)
    {
        // FirstOrDefault would silently take whichever row is first. If the stale row is the LOWER one the
        // floor reads low, and low is the direction that ships an unreadable marker. The system does not get
        // to pick which measurement it likes; it says which rows conflict and parks.
        var duplicated = new MeasuredBackground[]
            { new("bottle", "Zr", 4.0, "ppm"), new("bottle", "Zr", second, "ppm") };
        var (floor, error) = DetectionFloor.Compute(duplicated, Vanta, "bottle", "Zr");

        Assert.Null(floor);
        Assert.Contains("more than one", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Zr", error);
    }

    [Fact]
    public void Compute_REFUSES_TwoLodsForTheSameElement()
    {
        // Identical hazard on the device side: two LOD rows for Zr and "first wins" picks one of them.
        var device = new XrfDevice("Vanta", [new DeviceLod("Zr", 1.5, "ppm"), new DeviceLod("Zr", 0.5, "ppm")]);
        var (floor, error) = DetectionFloor.Compute(Background, device, "bottle", "Zr");

        Assert.Null(floor);
        Assert.Contains("more than one", error, StringComparison.OrdinalIgnoreCase);
    }
}

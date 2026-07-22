using Smx.Domain.Xrf;

namespace Smx.Domain.Tests;

public class XrfConfirmationTests
{
    private static XrfProposal P(
        string component = "bottle", string element = "Ba", string line = "Ka",
        string status = "V", string? note = null, double? level = 12.5,
        string? levelUnit = "ppm", string? device = "Niton XL5", double? lod = 3.0,
        string? lodUnit = "ppm", int row = 2) =>
        new(row, component, element, line, status, note, level, levelUnit, device, lod, lodUnit, []);

    private static readonly string[] Components = ["bottle", "lid"];

    [Fact]
    public void Build_TurnsRowsIntoPoolsBackgroundsAndOneDevice()
    {
        var (built, error) = XrfConfirmation.Build(
            [P(element: "Ba"), P(element: "Sr", status: "L", note: "shoulder on Ba Ka", lod: 2.5)],
            Components);

        Assert.Null(error);
        Assert.Equal(2, built!.ElementPools.Count);
        Assert.Equal(2, built.MeasuredBackgrounds.Count);
        Assert.Equal("Niton XL5", built.Device!.Model);
        Assert.Equal(2, built.Device.Lods.Count);
        Assert.Equal("ppm", built.MeasuredBackgrounds[0].Unit);
    }

    [Fact]
    public void Build_DoesNotPutAnXRowInThePool_ButDoesRecordItsBackground()
    {
        // X = present in the background, so it is not a candidate element and must not appear in the
        // pool Discovery screens against. It IS a measurement, so it is recorded: an element measured
        // and rejected must not be indistinguishable from one nobody looked at.
        var (built, error) = XrfConfirmation.Build([P(element: "Fe", status: "X", level: 940.0)], Components);

        Assert.Null(error);
        Assert.Empty(built!.ElementPools);
        Assert.Equal("Fe", Assert.Single(built.MeasuredBackgrounds).Element);
    }

    [Fact]
    public void Build_RefusesAConditionalRowWithNoSignalNote()
    {
        // The gate that makes the L status mean something. Same rule CreateProjectRequest.Validate
        // enforces — this is the door that replaced it.
        var (_, error) = XrfConfirmation.Build([P(status: "L", note: null)], Components);
        Assert.Contains("signal", error!);
    }

    [Fact]
    public void Build_RefusesARowNamingAComponentTheProjectDoesNotHave()
    {
        // A background measured on a component this product does not have is a measurement of nothing.
        // It would sit in the record looking exactly like data while the real component silently has
        // none — and the floor would then be computed without it.
        var (_, error) = XrfConfirmation.Build([P(component: "sleeve")], Components);
        Assert.Contains("sleeve", error!);
    }

    [Fact]
    public void Build_RefusesTwoMeasurementsOfTheSameElementOnTheSameComponent()
    {
        // Exactly what DetectionFloor refuses, refused earlier. Taking "the first" risks taking the
        // stale, LOWER one — and low is the direction that ships a marker nobody can read.
        var (_, error) = XrfConfirmation.Build([P(level: 12.5), P(level: 4.0, row: 3)], Components);
        Assert.Contains("Ba", error!);
        Assert.Contains("bottle", error!);
    }

    [Fact]
    public void Build_RefusesTwoDifferentLodsForOneElement()
    {
        var (_, error) = XrfConfirmation.Build(
            [P(component: "bottle", lod: 3.0), P(component: "lid", lod: 5.0, row: 3)], Components);
        Assert.Contains("Ba", error!);
    }

    [Fact]
    public void Build_AcceptsTheSameLodRepeatedAcrossComponents()
    {
        // The natural shape of the file: one LOD per element, restated on every row it applies to.
        var (built, error) = XrfConfirmation.Build(
            [P(component: "bottle", lod: 3.0), P(component: "lid", lod: 3.0, row: 3)], Components);
        Assert.Null(error);
        Assert.Single(built!.Device!.Lods);
    }

    [Fact]
    public void Build_RefusesTwoDeviceModels()
    {
        // A project targets ONE deployment device. Two models is a file describing two projects.
        var (_, error) = XrfConfirmation.Build(
            [P(device: "Niton XL5"), P(element: "Sr", device: "Vanta M", row: 3)], Components);
        Assert.Contains("Niton XL5", error!);
        Assert.Contains("Vanta M", error!);
    }

    [Fact]
    public void Build_RefusesANonPositiveLod()
    {
        // DetectionFloor's words: a non-positive LOD would put the floor at or below the background
        // the marker has to be seen against.
        var (_, error) = XrfConfirmation.Build([P(lod: 0)], Components);
        Assert.Contains("positive", error!);
    }

    [Fact]
    public void Build_RefusesAUnitThatIsNotPpm()
    {
        var (_, error) = XrfConfirmation.Build([P(levelUnit: "counts")], Components);
        Assert.Contains("counts", error!);
    }

    [Fact]
    public void Build_RefusesARowThatStillCarriesItsOwnProblems()
    {
        // The screen disables confirm while any row has a problem, but the screen is a convenience.
        // A row that failed to parse must not be confirmable by a caller that ignores the screen.
        var bad = P() with { Problems = ["status 'maybe' is not one of V / L / X."] };
        var (_, error) = XrfConfirmation.Build([bad], Components);
        Assert.Contains("row 2", error!);
    }

    [Fact]
    public void Build_RefusesAnEmptyConfirmation()
    {
        // Confirming nothing would write an empty pool set, which reads downstream exactly like "the
        // physicist measured nothing usable" rather than "nobody pressed anything".
        var (_, error) = XrfConfirmation.Build([], Components);
        Assert.NotNull(error);
    }

    [Fact]
    public void Build_AllowsRowsWithNoDeviceAtAll_SoPoolsCanLandBeforeTheDeviceIsKnown()
    {
        // The pools unblock Discovery; the device only matters at Dosing. Making the device mandatory
        // here would hold the whole pipeline for a number the next stage does not need.
        var (built, error) = XrfConfirmation.Build(
            [P(device: null, lod: null, lodUnit: null)], Components);
        Assert.Null(error);
        Assert.Null(built!.Device);
        Assert.Single(built.ElementPools);
    }
}

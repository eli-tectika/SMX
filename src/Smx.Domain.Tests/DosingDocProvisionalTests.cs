using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class DosingDocProvisionalTests
{
    private static DosingDoc Doc() =>
        new() { Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "2026-08-06T00:00:00Z" };

    [Fact]
    public void DefaultsToNotProvisional_WithNoReasons()
    {
        var doc = Doc();
        Assert.False(doc.Provisional);
        Assert.Empty(doc.ProvisionalReasons);
    }

    [Fact]
    public void SerializesTheFlag_EvenWhenFalse()
    {
        // The UI must read "not provisional" off the wire, never infer it from a missing key -- a build
        // skew would otherwise turn an absent alarm into a clean bill of health.
        Assert.Contains("\"provisional\"", JsonSerializer.Serialize(Doc(), Json.Options));
    }

    [Fact]
    public void RoundTripsProvisionalityThroughTheWire()
    {
        var doc = Doc();
        doc.Provisional = true;
        doc.ProvisionalReasons = ["Ce (1306-38-3) in 'bottle' is included on the agent's proposal alone."];

        var back = JsonSerializer.Deserialize<DosingDoc>(
            JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;

        Assert.True(back.Provisional);
        Assert.Equal(doc.ProvisionalReasons, back.ProvisionalReasons);
    }
}

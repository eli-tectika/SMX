using System.Text.Json;
using Smx.Domain.Records;
using Smx.Orchestrator.Dispatch;

namespace Smx.Orchestrator.Tests;

public class RecordDocRouterTests
{
    [Theory]
    [InlineData("project", typeof(ProjectDoc))]
    [InlineData("constraints", typeof(ConstraintsDoc))]
    [InlineData("verdict", typeof(VerdictDoc))]
    [InlineData("matrix", typeof(MatrixDoc))]
    public void Route_DeserializesByTypeDiscriminator(string type, Type expected)
    {
        var json = type switch
        {
            "project" => """{"id":"p1","projectId":"p1","type":"project","client":"A","product":"P","payload":{},"stages":{}}""",
            "constraints" => """{"id":"p1|constraints","projectId":"p1","type":"constraints"}""",
            "verdict" => """{"id":"p1|verdict|c|b","projectId":"p1","type":"verdict","cas":"c","componentId":"b","element":"E","form":"f"}""",
            _ => """{"id":"p1|matrix","projectId":"p1","type":"matrix"}""",
        };
        var doc = RecordDocRouter.Route(JsonDocument.Parse(json).RootElement);
        Assert.IsType(expected, doc);
    }

    [Fact]
    public void Route_UnknownType_ReturnsNull()
    {
        Assert.Null(RecordDocRouter.Route(JsonDocument.Parse("""{"id":"x","type":"lease-ish"}""").RootElement));
    }

    [Fact]
    public void Route_DeserializesGateDoc_ByDiscriminator()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new GateDoc
        {
            Id = RecordIds.Gate("p1", GateTypes.Regulatory), ProjectId = "p1",
            GateType = GateTypes.Regulatory, Status = "approved",
        }, Smx.Domain.Json.Options);
        var routed = RecordDocRouter.Route(json);
        var gate = Assert.IsType<GateDoc>(routed);
        Assert.Equal("approved", gate.Status);
    }

    /// Drop the Dosing arm and the change feed silently stops dispatching the Dosing stage — no error, the
    /// null just gets skipped by ChangeFeedWorker. This is what shows up instead.
    [Fact]
    public void Route_DeserializesDosingDoc_ByDiscriminator()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new DosingDoc
        {
            Id = RecordIds.Dosing("p1"), ProjectId = "p1", GeneratedAt = "2026-07-15T00:00:00Z",
        }, Smx.Domain.Json.Options);
        Assert.IsType<DosingDoc>(RecordDocRouter.Route(json));
    }

    /// The latent bug this task fixes: a CostDoc on the change feed routed to null, so the Cost stage would
    /// never dispatch and Plan 5's Decision stage (which triggers off CostDoc) would never fire. Remove the
    /// Cost arm and this goes red.
    [Fact]
    public void Route_DeserializesCostDoc_ByDiscriminator()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToElement(new CostDoc
        {
            Id = RecordIds.Cost("p1"), ProjectId = "p1", GeneratedAt = "2026-07-15T00:00:00Z",
        }, Smx.Domain.Json.Options);
        Assert.IsType<CostDoc>(RecordDocRouter.Route(json));
    }
}

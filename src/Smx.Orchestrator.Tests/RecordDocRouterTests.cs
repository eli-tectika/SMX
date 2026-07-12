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
}

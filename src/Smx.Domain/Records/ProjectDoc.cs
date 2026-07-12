using System.Text.Json;

namespace Smx.Domain.Records;

public sealed class StageState
{
    public string Status { get; set; } = "pending"; // pending|running|failed|needs-review|done
    public int Attempts { get; set; }
    public string? Error { get; set; }
}

public sealed class ProjectDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Project;
    public required string Client { get; set; }
    public required string Product { get; set; }
    public JsonElement Payload { get; set; } // the POST /projects body, verbatim
    public Dictionary<string, StageState> Stages { get; set; } = new();
    public string CreatedAt { get; set; } = "";

    public static ProjectDoc Create(string projectId, string client, string product, JsonElement payload) => new()
    {
        Id = projectId, ProjectId = projectId, Client = client, Product = product,
        Payload = payload.Clone(),
        Stages = new()
        {
            [Records.Stages.Intake] = new StageState(),
            [Records.Stages.Discovery] = new StageState(),
            [Records.Stages.Regulatory] = new StageState(),
            [Records.Stages.Matrix] = new StageState(),
        },
    };
}

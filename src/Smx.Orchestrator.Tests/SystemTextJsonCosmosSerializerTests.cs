using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Infrastructure;

namespace Smx.Orchestrator.Tests;

public class SystemTextJsonCosmosSerializerTests
{
    private readonly SystemTextJsonCosmosSerializer _serializer = new(Json.Options);

    [Fact]
    public void RoundTrips_ProjectDoc_PreservingJsonElementPayload()
    {
        // The Newtonsoft default serializer mangles JsonElement to {"valueKind":...}; STJ must not.
        var payload = JsonSerializer.SerializeToElement(new { components = new[] { new { id = "bottle" } }, substances = new[] { new { cas = "301-08-6" } } }, Json.Options);
        var doc = ProjectDoc.Create("proj-x", "Acme", "Bottle", payload);

        using var stream = _serializer.ToStream(doc);
        var back = _serializer.FromStream<ProjectDoc>(stream);

        Assert.Equal("proj-x", back.ProjectId);
        Assert.Equal(JsonValueKind.Object, back.Payload.ValueKind);
        Assert.Equal("bottle", back.Payload.GetProperty("components")[0].GetProperty("id").GetString());
        Assert.Equal("301-08-6", back.Payload.GetProperty("substances")[0].GetProperty("cas").GetString());
    }

    [Fact]
    public void FromStream_DeserializesRawChangeFeedElement()
    {
        // The change-feed processor reads each change as JsonElement; STJ handles this natively.
        var json = """{"id":"p1","projectId":"p1","type":"project","client":"A","product":"P"}"""u8.ToArray();
        var el = _serializer.FromStream<JsonElement>(new MemoryStream(json));
        Assert.Equal("project", el.GetProperty("type").GetString());
    }

    [Fact]
    public void WritesCamelCaseIdForCosmos()
    {
        var doc = ProjectDoc.Create("proj-y", "Acme", "Bottle", JsonSerializer.SerializeToElement(new { }, Json.Options));
        using var stream = _serializer.ToStream(doc);
        var text = new StreamReader(stream).ReadToEnd();
        Assert.Contains("\"id\":\"proj-y\"", text);         // Cosmos requires a lowercase "id"
        Assert.Contains("\"projectId\":\"proj-y\"", text);  // partition key
    }
}

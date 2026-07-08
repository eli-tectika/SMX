using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Orchestrator.Dispatch;

public static class RecordDocRouter
{
    public static object? Route(JsonElement element) =>
        element.TryGetProperty("type", out var t) ? t.GetString() switch
        {
            RecordTypes.Project => element.Deserialize<ProjectDoc>(Json.Options),
            RecordTypes.Constraints => element.Deserialize<ConstraintsDoc>(Json.Options),
            RecordTypes.Verdict => element.Deserialize<VerdictDoc>(Json.Options),
            RecordTypes.Matrix => element.Deserialize<MatrixDoc>(Json.Options),
            _ => null,
        } : null;
}

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
            RecordTypes.Pool => element.Deserialize<PoolDoc>(Json.Options),
            RecordTypes.Candidates => element.Deserialize<CandidatesDoc>(Json.Options),
            RecordTypes.Verdict => element.Deserialize<VerdictDoc>(Json.Options),
            RecordTypes.Matrix => element.Deserialize<MatrixDoc>(Json.Options),
            RecordTypes.Dosing => element.Deserialize<DosingDoc>(Json.Options),
            RecordTypes.Cost => element.Deserialize<CostDoc>(Json.Options),
            RecordTypes.Decision => element.Deserialize<DecisionDoc>(Json.Options),
            RecordTypes.Gate => element.Deserialize<GateDoc>(Json.Options),
            RecordTypes.Revision => element.Deserialize<RevisionDoc>(Json.Options),
            RecordTypes.ChatMessage => element.Deserialize<ChatMessageDoc>(Json.Options),
            // Terminal: a reply is an OUTPUT, not a trigger. Routing it to a doc type would have the
            // dispatcher re-enter on its own output — an agent in an infinite conversation with itself,
            // billed per turn. (ChangeFeedWorker skips a null.) The arm is spelled out rather than left to
            // `_` so the decision is visible to whoever adds the next chat doc type.
            RecordTypes.ChatReply => null,
            _ => null,
        } : null;
}

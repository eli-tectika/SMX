using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Backend.Pipeline;

/// Deserializes a record document by its `type` discriminator. It was written for the change feed, and the
/// change feed is gone — but it SURVIVES the one-service merge, for two reasons: StageDispatcher (which it
/// feeds) survives until the pipeline runner replaces it, and it is what a dozen dispatch tests round-trip
/// through to get the object a delivery actually produces rather than the one the test happened to build.
/// That round-trip has caught a real bug in this codebase. It goes when StageDispatcher goes.
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
            // billed per turn. (The dispatch loop skips a null.) The arm is spelled out rather than left to
            // `_` so the decision is visible to whoever adds the next chat doc type.
            RecordTypes.ChatReply => null,
            // Explicit, not merely "falls through the default". The brief is per-project and belongs on
            // the audit trail, so it lives in `record` — but it is not a stage output and must never
            // dispatch. An explicit arm is a statement of intent that survives someone later making the
            // default arm throw on unknown types.
            RecordTypes.IntakeBrief => null,
            _ => null,
        } : null;
}

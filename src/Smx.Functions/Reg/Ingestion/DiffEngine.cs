using Smx.Functions.Reg.Config;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Ingestion;

// Computes the corpus diff for a run and decides whether it is anomalous — the circuit breaker. A run promotes
// automatically unless an anomaly trips, in which case it is held for R.E. sign-off (§15 safety net for the
// higher-blast-radius regulatory corpus). Thresholds come from RegOptions; in dev they are set very high so a
// run never holds (same code, different config).
public static class DiffEngine
{
    public static CorpusDiff Compute(string runId, IReadOnlyList<DocOutcome> outcomes, RegOptions opts)
    {
        var added = outcomes.Count(o => o.Result == DocResult.Added);
        var changed = outcomes.Count(o => o.Result == DocResult.Changed);
        var unchanged = outcomes.Count(o => o.Result == DocResult.Unchanged);
        var errors = outcomes.Count(o => o.Result == DocResult.Error);
        var changedDocIds = outcomes
            .Where(o => o.Result is DocResult.Added or DocResult.Changed)
            .Select(o => o.DocId).ToList();
        var changedChunks = outcomes
            .Where(o => o.Result is DocResult.Added or DocResult.Changed)
            .Sum(o => o.ChunkCount);

        var reasons = new List<string>();
        if (errors > 0)
            reasons.Add($"{errors} document(s) failed to fetch or parse");
        // A changed doc that yielded 0 chunks is a parse/format anomaly (the official format likely changed).
        var emptyParses = outcomes.Count(o => o.Result is DocResult.Added or DocResult.Changed && o.ChunkCount == 0);
        if (emptyParses > 0)
            reasons.Add($"{emptyParses} changed document(s) parsed to 0 chunks (possible format change)");
        if (changedChunks >= opts.AnomalyDiffAbs)
            reasons.Add($"changed chunk count {changedChunks} ≥ absolute threshold {opts.AnomalyDiffAbs}");

        var anomaly = new AnomalyAssessment(reasons.Count > 0, reasons);
        return new CorpusDiff(runId, added, changed, unchanged, errors, changedDocIds, anomaly);
    }
}

using Smx.Domain.Records;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;

namespace Smx.Orchestrator.Tests.Fakes;

/// Bypasses LLM entirely: dispatcher tests exercise orchestration, not reasoning.
public sealed class FakeAgentRuns : IAgentRuns
{
    public Func<ProjectDoc, Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>>> Intake { get; set; } =
        p => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>.Ok(new ConstraintsDoc
        {
            Id = RecordIds.Constraints(p.ProjectId), ProjectId = p.ProjectId,
            Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
            ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    /// Takes the ProjectDoc the real IAgentRuns takes — a fake that dropped it would let the dispatcher stop
    /// passing the project (and with it the sensitive terms) without a single test noticing.
    public Func<ProjectDoc, ConstraintsDoc, RevisionDoc?, Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>>> Discovery { get; set; } =
        (_, c, _) => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        }));

    public Func<ConstraintsDoc, CandidateSubstance, RevisionDoc?, Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>>> Regulatory { get; set; } =
        (c, cand, _) => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, cand.Cas, cand.ComponentId), ProjectId = c.ProjectId,
            Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        }));

    public Func<RevisionDoc, ConstraintsDoc, string, Task<AgentRunResult<ConclusionOutput>>> Conclusion { get; set; } =
        (r, _, _) => Task.FromResult(AgentRunResult<ConclusionOutput>.Ok(new ConclusionOutput
        {
            Scope = new(null, null, null, null, null, null),
            Finding = $"Distilled: {r.Reason}",
            Confidence = 0.6,
        }));

    public int IntakeCalls; public int DiscoveryCalls; public int RegulatoryCalls; public int ConclusionCalls;

    Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>> IAgentRuns.RunIntakeAsync(ProjectDoc p, CancellationToken ct)
    { Interlocked.Increment(ref IntakeCalls); return Intake(p); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>> IAgentRuns.RunDiscoveryAsync(
        ProjectDoc project, ConstraintsDoc c, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref DiscoveryCalls); return Discovery(project, c, revision); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>> IAgentRuns.RunRegulatoryAsync(ConstraintsDoc c, CandidateSubstance cand, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref RegulatoryCalls); return Regulatory(c, cand, revision); }
    Task<AgentRunResult<ConclusionOutput>> IAgentRuns.RunConclusionAsync(RevisionDoc revision, ConstraintsDoc c, string stageOutputJson, CancellationToken ct)
    { Interlocked.Increment(ref ConclusionCalls); return Conclusion(revision, c, stageOutputJson); }
}

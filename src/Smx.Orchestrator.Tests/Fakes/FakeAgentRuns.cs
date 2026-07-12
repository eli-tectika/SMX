using Smx.Domain.Records;
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

    public Func<ConstraintsDoc, Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>>> Discovery { get; set; } =
        c => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>.Ok(new CandidatesDoc
        {
            Id = RecordIds.Candidates(c.ProjectId), ProjectId = c.ProjectId,
            Substances = [new("bottle", "Zr", "neodecanoate", "cas-zr", null, null, true, "A", "ok",
                [new Citation("catalog", "ref-catalog/x", "t")])],
        }));

    public Func<ConstraintsDoc, CandidateSubstance, Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>>> Regulatory { get; set; } =
        (c, cand) => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, cand.Cas, cand.ComponentId), ProjectId = c.ProjectId,
            Cas = cand.Cas, ComponentId = cand.ComponentId, Element = cand.Element, Form = cand.Form,
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        }));

    public int IntakeCalls; public int DiscoveryCalls; public int RegulatoryCalls;

    Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>> IAgentRuns.RunIntakeAsync(ProjectDoc p, CancellationToken ct)
    { Interlocked.Increment(ref IntakeCalls); return Intake(p); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>> IAgentRuns.RunDiscoveryAsync(ConstraintsDoc c, CancellationToken ct)
    { Interlocked.Increment(ref DiscoveryCalls); return Discovery(c); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>> IAgentRuns.RunRegulatoryAsync(ConstraintsDoc c, CandidateSubstance cand, CancellationToken ct)
    { Interlocked.Increment(ref RegulatoryCalls); return Regulatory(c, cand); }
}

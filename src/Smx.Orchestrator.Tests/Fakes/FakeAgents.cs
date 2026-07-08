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
            Substances = [new("Zr", "neodecanoate", "cas-zr")],
            DerivedScope = [new("reach-annex-xvii", "*", "r", new Citation("regulatory", "x", "t"))],
        }));

    public Func<ConstraintsDoc, SubstanceSpec, string, Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>>> Screen { get; set; } =
        (c, s, comp) => Task.FromResult(Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>.Ok(new VerdictDoc
        {
            Id = RecordIds.Verdict(c.ProjectId, s.Cas, comp), ProjectId = c.ProjectId,
            Cas = s.Cas, ComponentId = comp, Element = s.Element, Form = s.Form,
            Dimensions = [new("ElementGate", VerdictStatus.Pass, [new Citation("regulatory", "x", "t")], 0.9, "ok")],
        }));

    public int IntakeCalls; public int ScreenCalls;

    Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>> IAgentRuns.RunIntakeAsync(ProjectDoc p, CancellationToken ct)
    { Interlocked.Increment(ref IntakeCalls); return Intake(p); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>> IAgentRuns.RunScreeningAsync(ConstraintsDoc c, SubstanceSpec s, string comp, CancellationToken ct)
    { Interlocked.Increment(ref ScreenCalls); return Screen(c, s, comp); }
}

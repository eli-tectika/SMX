using Smx.Domain;
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

    /// The default pool run proposes one suggestion for the default Intake component. Mirrors the real
    /// RunPoolAsync signature (incl. the ProjectDoc that carries sensitive terms) so a dispatcher that stops
    /// passing the project cannot slip past unnoticed.
    public Func<ProjectDoc, ConstraintsDoc, RevisionDoc?, Task<AgentRunResult<PoolDoc>>> Pool { get; set; } =
        (_, c, _) => Task.FromResult(AgentRunResult<PoolDoc>.Ok(new PoolDoc
        {
            Id = RecordIds.Pool(c.ProjectId), ProjectId = c.ProjectId,
            Suggestions = [new("bottle", "Zr", "compound", "an oxide suits a solid polymer", [])],
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

    /// The default run succeeds with an empty DosingDoc: a dispatch test exercises orchestration (does the
    /// approved gate trigger dosing? over the compliant set only?), not the ppm arithmetic — a test that
    /// cares about the windows scripts this. The signature mirrors the real RunDosingAsync exactly, so a
    /// dispatcher that stops passing the floors or the loadings cannot slip past this fake unnoticed.
    public Func<ConstraintsDoc, IReadOnlyList<VerdictDoc>,
                IReadOnlyDictionary<(string, string), Floor>, IReadOnlyDictionary<string, double>,
                RevisionDoc?, Task<AgentRunResult<DosingDoc>>> Dosing { get; set; } =
        (c, _, _, _, _) => Task.FromResult(AgentRunResult<DosingDoc>.Ok(new DosingDoc
        {
            Id = RecordIds.Dosing(c.ProjectId), ProjectId = c.ProjectId,
            GeneratedAt = "2026-07-15T00:00:00Z",
        }));

    /// The default run mirrors the assembled matrix and proposes the FIRST finalized code for each component
    /// (never a confirmation — ConfirmedCode is the VP's field, and this fake writing it would be the exact
    /// conflation the real agent is fenced against): a dispatch test exercises orchestration (does the
    /// CostDoc landing trigger Decision? does the stage park awaiting-VP?), not the pick reasoning — a test
    /// that cares about the pick scripts this. The signature mirrors the real RunDecisionAsync exactly, so a
    /// dispatcher that stops passing the assembly or the dosing codes cannot slip past this fake unnoticed.
    public Func<IReadOnlyList<ComponentDecision>, DosingDoc, RevisionDoc?,
                Task<AgentRunResult<DecisionDoc>>> Decision { get; set; } =
        (assembled, dosing, _) => Task.FromResult(AgentRunResult<DecisionDoc>.Ok(new DecisionDoc
        {
            Id = RecordIds.Decision(dosing.ProjectId), ProjectId = dosing.ProjectId,
            Components = [.. assembled.Select(c =>
                dosing.Codes.FirstOrDefault(k => k.ComponentId == c.ComponentId) is { } code
                    ? c with { ProposedCode = new ProposedCode(
                        code.RatioSignature, [.. code.Markers.Select(m => m.Cas)], "fake pick") }
                    : c)],
            GeneratedAt = "2026-07-16T00:00:00Z",
        }));

    public Func<RevisionDoc, ConstraintsDoc, string, Task<AgentRunResult<ConclusionOutput>>> Conclusion { get; set; } =
        (r, _, _) => Task.FromResult(AgentRunResult<ConclusionOutput>.Ok(new ConclusionOutput
        {
            Scope = new(null, null, null, null, null, null),
            Finding = $"Distilled: {r.Reason}",
            Confidence = 0.6,
        }));

    /// Echoes the operator's message, so a dispatch test can prove the RIGHT message reached the agent
    /// rather than merely that a reply came back. The ChatTools instance is handed through untouched: a
    /// test that wants the mutating half exercised scripts this to call it. The stage is not a separate
    /// argument — it is `chatTools.Stage`, the one the mutating tools are actually bound to.
    public Func<ChatTools, string, string, string, Task<string>> Chat { get; set; } =
        (_, _, _, message) => Task.FromResult($"Echo: {message}");

    public int IntakeCalls; public int PoolCalls; public int DiscoveryCalls; public int RegulatoryCalls; public int ConclusionCalls;
    public int ChatCalls; public int DosingCalls; public int DecisionCalls;

    /// Every agent invocation across all arms. Cost is DETERMINISTIC (§3.4) — no agent — so a Cost dispatch
    /// test asserts this is unchanged: if Cost ever needs a model, that is a design change to argue for in the
    /// open, not one that slips in behind a green suite.
    public int TotalCalls => IntakeCalls + PoolCalls + DiscoveryCalls + RegulatoryCalls + ConclusionCalls + ChatCalls + DosingCalls
        + DecisionCalls;

    Task<Smx.Orchestrator.Agents.AgentRunResult<ConstraintsDoc>> IAgentRuns.RunIntakeAsync(ProjectDoc p, CancellationToken ct)
    { Interlocked.Increment(ref IntakeCalls); return Intake(p); }
    Task<AgentRunResult<PoolDoc>> IAgentRuns.RunPoolAsync(ProjectDoc project, ConstraintsDoc c, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref PoolCalls); return Pool(project, c, revision); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<CandidatesDoc>> IAgentRuns.RunDiscoveryAsync(
        ProjectDoc project, ConstraintsDoc c, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref DiscoveryCalls); return Discovery(project, c, revision); }
    Task<Smx.Orchestrator.Agents.AgentRunResult<VerdictDoc>> IAgentRuns.RunRegulatoryAsync(ConstraintsDoc c, CandidateSubstance cand, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref RegulatoryCalls); return Regulatory(c, cand, revision); }
    Task<AgentRunResult<DosingDoc>> IAgentRuns.RunDosingAsync(
        ConstraintsDoc c, IReadOnlyList<VerdictDoc> compliant,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyDictionary<string, double> loadings, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref DosingCalls); return Dosing(c, compliant, floors, loadings, revision); }
    Task<AgentRunResult<DecisionDoc>> IAgentRuns.RunDecisionAsync(
        IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing, RevisionDoc? revision, CancellationToken ct)
    { Interlocked.Increment(ref DecisionCalls); return Decision(assembled, dosing, revision); }
    Task<AgentRunResult<ConclusionOutput>> IAgentRuns.RunConclusionAsync(RevisionDoc revision, ConstraintsDoc c, string stageOutputJson, CancellationToken ct)
    { Interlocked.Increment(ref ConclusionCalls); return Conclusion(revision, c, stageOutputJson); }
    Task<string> IAgentRuns.RunChatAsync(ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct)
    { Interlocked.Increment(ref ChatCalls); return Chat(chatTools, thread, stageInputsJson, message); }
}

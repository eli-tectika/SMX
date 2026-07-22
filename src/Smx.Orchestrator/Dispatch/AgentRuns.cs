using Microsoft.Extensions.AI;
using Smx.Domain;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);

    /// The need-driven pool proposal (PoolAgent), run BEFORE Discovery from the need alone.
    /// <param name="project">carries the sensitive terms the pool's web-search tool must refuse to send —
    /// same contract as RunDiscoveryAsync.</param>
    /// <param name="revision">null for an ordinary run; non-null re-proposes applying the operator's
    /// revise-with-reason. Explicit, not an overload, for the usual reason.</param>
    Task<AgentRunResult<PoolDoc>> RunPoolAsync(
        ProjectDoc project, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct);

    /// <param name="project">carries Client / Product / ProjectId — the terms the web-search tool must
    /// refuse to send. Required, not optional: a Discovery run without them is a run that cannot protect the
    /// project, and that must be a compile error rather than a silent leak.</param>
    /// <param name="revision">null for an ordinary run; non-null re-runs the stage applying the operator's
    /// revise-with-reason. Explicit rather than an overload: forgetting it is a compile error, not an agent
    /// that silently ignores the operator.</param>
    Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(
        ProjectDoc project, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct);

    /// <param name="revision">null for an ordinary run; non-null re-screens the cell applying the revision.</param>
    Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct);

    /// <param name="compliant">the operator-recommended verdicts (CompliantSet.Of) — the ONLY substances
    /// Dosing may dose.</param>
    /// <param name="floors">(component, element) → the MEASURED detection floor, computed by CODE from the
    /// physicist's background. The agent sees it and doses above it; it never computes it.</param>
    /// <param name="loadings">cas → metal loading (operator-entered). Feeds the order amount only.</param>
    /// <param name="revision">null for an ordinary run; non-null re-runs Dosing applying the operator's
    /// revise-with-reason. Explicit, not an overload: forgetting it is a compile error, not an agent that
    /// silently ignores the operator.</param>
    Task<AgentRunResult<DosingDoc>> RunDosingAsync(
        ConstraintsDoc constraints, IReadOnlyList<VerdictDoc> compliant,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyDictionary<string, double> loadings,
        RevisionDoc? revision, CancellationToken ct);

    /// <param name="assembled">the DETERMINISTIC decision matrix (DecisionAssembler.Assemble) — the agent
    /// only PICKS a code over these rows; it authors none of them.</param>
    /// <param name="dosing">the finalized codes the pick chooses among, and the ProjectId the doc is keyed
    /// by.</param>
    /// <param name="revision">null for an ordinary run; non-null re-picks applying the operator's
    /// revise-with-reason. Explicit rather than an overload: forgetting it is a compile error, not an agent
    /// that silently ignores the operator.</param>
    Task<AgentRunResult<DecisionDoc>> RunDecisionAsync(
        IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing, RevisionDoc? revision, CancellationToken ct);

    Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct);

    /// One chat turn. Returns the agent's reply text; the tool-call trail is collected by the ChatTools
    /// instance the caller passes in (it is bound to this project + stage, so the model cannot name another).
    ///
    /// The stage is NOT a parameter: it is read from `chatTools.Stage`, which is the stage the mutating
    /// tools are bound to. A separate parameter could disagree with it — the turn would then retrieve with
    /// one stage's tools and write the other stage's revision — so the disagreement is made unrepresentable
    /// rather than merely tested for.
    Task<string> RunChatAsync(ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct);
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

    public Task<AgentRunResult<PoolDoc>> RunPoolAsync(
        ProjectDoc project, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct) =>
        PoolAgent.RunAsync(
            new MafAgent(chatClient, PoolAgent.AgentName, PoolAgent.Instructions,
                toolBox.PoolTools(TermsFor(project))),
            constraints, revision, ct);

    public Task<AgentRunResult<CandidatesDoc>> RunDiscoveryAsync(
        ProjectDoc project, ConstraintsDoc constraints, RevisionDoc? revision, CancellationToken ct) =>
        DiscoveryAgent.RunAsync(
            new MafAgent(chatClient, DiscoveryAgent.AgentName, DiscoveryAgent.Instructions,
                toolBox.DiscoveryTools(TermsFor(project))),
            constraints, revision, ct);

    /// The project's own identifiers. These are exactly the strings that must never reach an external search:
    /// each one, in an outbound query, tells the provider which client is evaluating which chemistry.
    private static SensitiveTerms TermsFor(ProjectDoc p) =>
        new(new[] { p.Client, p.Product, p.ProjectId }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList());

    public Task<AgentRunResult<VerdictDoc>> RunRegulatoryAsync(ConstraintsDoc constraints, CandidateSubstance candidate, RevisionDoc? revision, CancellationToken ct) =>
        RegulatoryAgent.RunAsync(
            new MafAgent(chatClient, RegulatoryAgent.AgentName, RegulatoryAgent.Instructions, toolBox.RegulatoryTools()),
            constraints, candidate, revision, ct);

    /// The Dosing tools are closed over THIS project's constraints — the same discipline as DiscoveryTools over
    /// SensitiveTerms — so the deterministic floor targets this project's measured background and device and no
    /// other's. Mirrors RunDiscoveryAsync: build the MafAgent with the stage's tools, hand the agent to the
    /// static RunAsync that owns the reasoning.
    public Task<AgentRunResult<DosingDoc>> RunDosingAsync(
        ConstraintsDoc constraints, IReadOnlyList<VerdictDoc> compliant,
        IReadOnlyDictionary<(string ComponentId, string Element), Floor> floors,
        IReadOnlyDictionary<string, double> loadings,
        RevisionDoc? revision, CancellationToken ct) =>
        DosingAgent.RunAsync(
            new MafAgent(chatClient, DosingAgent.AgentName, DosingAgent.Instructions, toolBox.DosingTools(constraints)),
            constraints, compliant, floors, loadings, revision, ct);

    /// Decision reads what Dosing reads (learned conclusions + reference) — the matrix it picks over is
    /// already assembled deterministically, so there is deliberately no tool that could let the model "look
    /// up" a different answer than the record it is proposing over.
    public Task<AgentRunResult<DecisionDoc>> RunDecisionAsync(
        IReadOnlyList<ComponentDecision> assembled, DosingDoc dosing, RevisionDoc? revision, CancellationToken ct) =>
        DecisionAgent.RunAsync(
            new MafAgent(chatClient, DecisionAgent.AgentName, DecisionAgent.Instructions, toolBox.DecisionReadTools()),
            assembled, dosing, revision, ct);

    /// No tools: the distiller reasons only over what it is handed (the revision + the stage output it
    /// produced). Giving it search tools would let it "support" the conclusion with evidence the revision
    /// never rested on.
    public Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(
        RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct) =>
        ConclusionAgent.RunAsync(
            new MafAgent(chatClient, ConclusionAgent.AgentName, ConclusionAgent.Instructions, []),
            revision, constraints, stageOutputJson, ct);

    public Task<string> RunChatAsync(ChatTools chatTools, string thread, string stageInputsJson,
        string message, CancellationToken ct) =>
        ChatAgent.RunAsync(
            new MafAgent(chatClient, ChatAgent.AgentName, ChatAgent.Instructions, ChatTurnTools(toolBox, chatTools)),
            thread, stageInputsJson, message, ct);

    /// Everything a chat turn can DO: the stage's READ tools (so it answers for its stage from that stage's
    /// own sources) plus THIS turn's MUTATING tools (bound to this project + stage + chat message, so the
    /// model cannot name another project's analysis).
    ///
    /// BOTH halves take the stage from `chatTools.Stage` — one source of truth. A separate `stage` parameter
    /// would let the halves disagree: Regulatory's retrieval tools paired with a Discovery-bound
    /// `apply_revision`, producing a RevisionDoc that looks perfectly legitimate on the bus and was screened
    /// against the wrong stage's sources. Nothing downstream could detect it, so the caller is not given the
    /// chance to make the mistake.
    ///
    /// What is deliberately NOT in this list: anything that could sign a gate, approve a stage, or record an
    /// R.E. determination. No such tool exists in ToolBox or ChatTools, so chat cannot approve anything — the
    /// capability is absent, not merely forbidden by the Instructions (Law 9: gates are operator-signed
    /// records, never voice-committed). An agent acts only through its tools; this list is the whole of it.
    ///
    /// A named, public function rather than an inline collection expression precisely because it is the whole
    /// of it: FakeAgentRuns replaces the entire run, so nothing else in the suite can observe this list, and a
    /// tool silently added to — or dropped from — it would otherwise be invisible until production.
    public static IList<AITool> ChatTurnTools(ToolBox toolBox, ChatTools chatTools) =>
        [.. toolBox.ReadToolsFor(chatTools.Stage), .. chatTools.Tools()];
}

using Microsoft.Extensions.AI;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;

namespace Smx.Orchestrator.Dispatch;

public interface IAgentRuns
{
    Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct);

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

    Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct);
}

public sealed class AgentRuns(IChatClient chatClient, ToolBox toolBox) : IAgentRuns
{
    public Task<AgentRunResult<ConstraintsDoc>> RunIntakeAsync(ProjectDoc project, CancellationToken ct) =>
        IntakeAgent.RunAsync(
            new MafAgent(chatClient, IntakeAgent.AgentName, IntakeAgent.Instructions, toolBox.IntakeTools()),
            project, ct);

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

    /// No tools: the distiller reasons only over what it is handed (the revision + the stage output it
    /// produced). Giving it search tools would let it "support" the conclusion with evidence the revision
    /// never rested on.
    public Task<AgentRunResult<ConclusionOutput>> RunConclusionAsync(
        RevisionDoc revision, ConstraintsDoc constraints, string stageOutputJson, CancellationToken ct) =>
        ConclusionAgent.RunAsync(
            new MafAgent(chatClient, ConclusionAgent.AgentName, ConclusionAgent.Instructions, []),
            revision, constraints, stageOutputJson, ct);
}

using System.Text.Json;
using Smx.Domain.Records;
using Smx.Domain.Tools;
using Smx.Orchestrator.Agents;
using Smx.Orchestrator.Dispatch;
using Smx.Orchestrator.Tests.Fakes;

namespace Smx.Orchestrator.Tests;

/// What AgentRuns hands the Discovery agent BEFORE the model is reached. The tool set is built eagerly, from
/// the ProjectDoc, and the SensitiveTerms it closes over are the only thing standing between the client's
/// identity and an external search provider — so they are asserted here, not assumed.
public class AgentRunsTests
{
    private static ConstraintsDoc Constraints(string projectId = "p1") => new()
    {
        Id = RecordIds.Constraints(projectId), ProjectId = projectId,
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand")],
        ElementPools = [new("bottle", "Zr", "Kα", "V", null)],
    };

    private static ProjectDoc Project(string projectId, string client, string product) =>
        ProjectDoc.Create(projectId, client, product, JsonDocument.Parse("{}").RootElement);

    /// Runs Discovery over a ToolBox whose web-search factory records the terms it was handed.
    private static async Task<SensitiveTerms?> TermsHandedToTheWebToolAsync(ProjectDoc project)
    {
        SensitiveTerms? captured = null;
        var search = new FakeSearch();
        var toolBox = new ToolBox(
            new FakeCatalogLookup(), new FakeCompatibilityLookup(), search, search, search,
            new Smx.Domain.Tests.Fakes.InMemoryKnowledgeStore(), new FakeLearnedConclusionsSearch(),
            terms => { captured = terms; return new FakeWebSearch(); });

        IAgentRuns runs = new AgentRuns(new FakeChatClient(), toolBox);
        await runs.RunDiscoveryAsync(project, Constraints(project.ProjectId), null, default);
        return captured;
    }

    // THE leak test. The crown-jewel IP is "which client is evaluating which chemistry", and the orchestrator
    // is the ONLY component that knows the client and product names — the Search Proxy is deliberately
    // project-blind. If AgentRuns builds the Discovery tool set without them (as it did while SensitiveTerms
    // was a placeholder), the guard cannot block a leak it was never told about and every one of these names
    // is free to ride out in a query.
    [Fact]
    public async Task RunDiscovery_HandsTheWebSearchTool_TheProjectsClientProductAndProjectId()
    {
        var terms = await TermsHandedToTheWebToolAsync(Project("p1", "Acme Bottling", "SparkleCola"));

        Assert.NotNull(terms);
        Assert.Contains("Acme Bottling", terms!.Terms);
        Assert.Contains("SparkleCola", terms.Terms);
        Assert.Contains("p1", terms.Terms);
    }

    // A blank term would match nothing useful, but an all-whitespace one fed to the guard's regex would be a
    // pattern that matches every query — a guard that blocks everything is as broken as one that blocks
    // nothing, because Discovery then never searches at all.
    [Fact]
    public async Task RunDiscovery_DropsBlankProjectTerms()
    {
        var terms = await TermsHandedToTheWebToolAsync(Project("p1", "Acme", "   "));

        Assert.NotNull(terms);
        Assert.Equal(["Acme", "p1"], terms!.Terms);
    }
}

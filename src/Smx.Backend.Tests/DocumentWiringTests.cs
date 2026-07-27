using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain.Documents;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

/// The production registrations in Program.cs, which every other document test bypasses by injecting
/// fakes. Nothing here talks to Azure — resolving the singletons is enough, because the branch being
/// checked is the choice of implementation, and a wrong choice is a misconfiguration that would
/// otherwise only show up on a deployed environment.
public class DocumentWiringTests
{
    /// COSMOS_ACCOUNT_ENDPOINT is what gates the whole production block; a syntactically valid
    /// endpoint is all the CosmosClient constructor needs, since it connects lazily.
    ///
    /// SEARCH_ENDPOINT and FOUNDRY_ENDPOINT are here because that block now also builds the agents (the
    /// orchestrator was folded into this app) and refuses to start without them — see
    /// AMissingSearchEndpointFailsByName, which is the test for that refusal. They are irrelevant to the
    /// document wiring these tests are about; they are what a configured deployment always has.
    private static WebApplicationFactory<Program> HostWith(params (string Key, string Value)[] settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("COSMOS_ACCOUNT_ENDPOINT", "https://cosmos.example.invalid:443/");
            b.UseSetting("SEARCH_ENDPOINT", "https://search.example.invalid");
            b.UseSetting("FOUNDRY_ENDPOINT", "https://foundry.example.invalid");
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });

    [Fact]
    public void ALocalPathWinsOverAnAccountName()
    {
        using var host = HostWith(
            ("BRONZE_LOCAL_PATH", Path.GetTempPath()),
            ("BRONZE_ACCOUNT_NAME", "stsomething"));

        var store = host.Services.GetRequiredService<IDocumentContentStore>();

        // Both set is the ordinary local-dev shape: the operator's .dev-local/backend.env carries the
        // account name from the estate and the developer adds a path to work offline. The path wins,
        // or that override does nothing.
        Assert.IsType<LocalBronzeDocumentStore>(store);
    }

    [Fact]
    public void AnAccountNameAloneGivesTheAdlsStore()
    {
        using var host = HostWith(("BRONZE_ACCOUNT_NAME", "stsomething"));
        Assert.IsType<BronzeDocumentStore>(host.Services.GetRequiredService<IDocumentContentStore>());
    }

    // Neither set would otherwise resolve "https://.dfs.core.windows.net" and fail as DNS on the
    // operator's first click. It has to name the variable instead.
    [Fact]
    public void NeitherSetFailsByName()
    {
        using var host = HostWith();

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<IDocumentContentStore>());

        Assert.Contains("BRONZE_ACCOUNT_NAME", ex.Message);
        Assert.Contains("BRONZE_LOCAL_PATH", ex.Message);
    }

    [Fact]
    public void TheCatalogAndTextReaderResolveFromTheProductionRegistrations()
    {
        using var host = HostWith(("BRONZE_LOCAL_PATH", Path.GetTempPath()));

        Assert.IsType<DocumentCatalog>(host.Services.GetRequiredService<IDocumentCatalog>());
        Assert.IsType<CompositeDocumentTextReader>(host.Services.GetRequiredService<IDocumentTextReader>());
        Assert.IsType<CosmosSdsDocumentSource>(host.Services.GetRequiredService<ISdsDocumentSource>());
        Assert.IsType<CosmosRegDocumentSource>(host.Services.GetRequiredService<IRegDocumentSource>());
    }

    // SEARCH_ENDPOINT defaults to "" in BackendOptions, and `new Uri("")` throws a UriFormatException
    // that names nothing. The document viewer reads SDS chunk text from the sds-index and the agents
    // search all three indexes, so a configured deployment without it is broken either way — since the
    // orchestrator was folded in, that is caught at host construction rather than at the operator's
    // first click. Either way it must NAME the setting.
    [Fact]
    public void AMissingSearchEndpointFailsByName()
    {
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("COSMOS_ACCOUNT_ENDPOINT", "https://cosmos.example.invalid:443/");
            b.UseSetting("FOUNDRY_ENDPOINT", "https://foundry.example.invalid");
            b.UseSetting("BRONZE_LOCAL_PATH", Path.GetTempPath());
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<IDocumentTextReader>());

        Assert.Contains("SEARCH_ENDPOINT", ex.Message);
    }

    // The same for the other half of the agent host's config. FOUNDRY_ENDPOINT feeds an AzureOpenAIClient
    // that IS constructed eagerly, so without the guard the host dies on `new Uri("")` — an opaque
    // UriFormatException from a client nobody mentioned.
    [Fact]
    public void AMissingFoundryEndpointFailsByName()
    {
        using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("COSMOS_ACCOUNT_ENDPOINT", "https://cosmos.example.invalid:443/");
            b.UseSetting("SEARCH_ENDPOINT", "https://search.example.invalid");
            b.UseSetting("BRONZE_LOCAL_PATH", Path.GetTempPath());
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<IDocumentTextReader>());

        Assert.Contains("FOUNDRY_ENDPOINT", ex.Message);
    }

    // Spec §8: "Bronze unconfigured — 503 with a plain message, surfaced by the viewer rather than
    // swallowed." The message above is only worth writing if it reaches the operator, and a factory
    // that throws during [FromServices] resolution surfaces as a bare 500 naming nothing.
    //
    // The list endpoint is the one that proves it without touching Cosmos: DocumentCatalog's own
    // factory resolves IDocumentContentStore for RegDocumentProvider, so resolution fails before any
    // query is issued.
    [Fact]
    public async Task BronzeUnconfiguredIs503WithTheReason()
    {
        using var host = HostWith();
        using var client = host.CreateClient();

        var res = await client.GetAsync("/documents");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("BRONZE_ACCOUNT_NAME", await res.Content.ReadAsStringAsync());
    }

    // The other half of the same fault: with no COSMOS_ACCOUNT_ENDPOINT the whole production block is
    // skipped, so IDocumentCatalog is not registered at all and the operator got
    // "No service for type ... IDocumentCatalog", which names nothing they can act on.
    [Fact]
    public async Task ADeploymentWithoutTheDocumentServicesIs503NotACrash()
    {
        using var host = new WebApplicationFactory<Program>();
        using var client = host.CreateClient();

        var res = await client.GetAsync("/documents");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains("not configured", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }
}

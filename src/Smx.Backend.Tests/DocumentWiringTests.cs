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
    private static WebApplicationFactory<Program> HostWith(params (string Key, string Value)[] settings)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("COSMOS_ACCOUNT_ENDPOINT", "https://cosmos.example.invalid:443/");
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
        using var host = HostWith(
            ("BRONZE_LOCAL_PATH", Path.GetTempPath()),
            ("SEARCH_ENDPOINT", "https://search.example.invalid"));

        Assert.IsType<DocumentCatalog>(host.Services.GetRequiredService<IDocumentCatalog>());
        Assert.IsType<CompositeDocumentTextReader>(host.Services.GetRequiredService<IDocumentTextReader>());
        Assert.IsType<CosmosSdsDocumentSource>(host.Services.GetRequiredService<ISdsDocumentSource>());
        Assert.IsType<CosmosRegDocumentSource>(host.Services.GetRequiredService<IRegDocumentSource>());
    }

    // SEARCH_ENDPOINT defaults to "" in BackendOptions, and `new Uri("")` throws a UriFormatException
    // that names nothing.
    [Fact]
    public void AMissingSearchEndpointFailsByName()
    {
        using var host = HostWith(("BRONZE_LOCAL_PATH", Path.GetTempPath()));

        var ex = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredService<IDocumentTextReader>());

        Assert.Contains("SEARCH_ENDPOINT", ex.Message);
    }
}

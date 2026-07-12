using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

/// IKnowledgeStore over three cross-project Cosmos containers. PKs: learned-conclusions /kind,
/// marker-library /id, msds-registry /cas. Query* does a Cosmos CONTAINS (case-insensitive) browse.
public sealed class CosmosKnowledgeStore(Container conclusions, Container markers, Container msds) : IKnowledgeStore
{
    public Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default) =>
        ReadAsync<LearnedConclusionDoc>(conclusions, KnowledgeIds.LearnedConclusion(kind, scopeKey), kind, ct);
    public async Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default)
    {
        // Cosmos NoSQL has no `??`; CONTAINS on an absent/undefined path yields undefined, which the OR
        // correctly treats as non-matching — so the missing-scope-field case needs no coalesce.
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? "SELECT * FROM c WHERE c.type = @t"
            : "SELECT * FROM c WHERE c.type = @t AND (CONTAINS(c.finding, @s, true) OR CONTAINS(c.scope.element, @s, true) OR CONTAINS(c.scope.material, @s, true) OR CONTAINS(c.scope.application, @s, true) OR CONTAINS(c.scope.market, @s, true) OR CONTAINS(c.scope.substance, @s, true))")
            .WithParameter("@t", KnowledgeTypes.LearnedConclusion).WithParameter("@s", search ?? "");
        return await RunAsync<LearnedConclusionDoc>(conclusions, q, ct);
    }
    public Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default) =>
        conclusions.UpsertItemAsync(doc, new PartitionKey(doc.Kind), cancellationToken: ct);

    public Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default) =>
        ReadAsync<MarkerLibraryDoc>(markers, id, id, ct);
    public async Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default)
    {
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? "SELECT * FROM c WHERE c.type = @t"
            : "SELECT * FROM c WHERE c.type = @t AND (CONTAINS(c.validatedFor.application, @s, true) OR CONTAINS(c.validatedFor.material, @s, true) OR CONTAINS(c.validatedFor.objective, @s, true))")
            .WithParameter("@t", KnowledgeTypes.MarkerLibrary).WithParameter("@s", search ?? "");
        return await RunAsync<MarkerLibraryDoc>(markers, q, ct);
    }
    public Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default) =>
        markers.UpsertItemAsync(doc, new PartitionKey(doc.Id), cancellationToken: ct);

    public Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default) =>
        ReadAsync<MsdsRegistryDoc>(msds, KnowledgeIds.Msds(cas), cas, ct);
    public async Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default)
    {
        var q = new QueryDefinition(string.IsNullOrWhiteSpace(search)
            ? "SELECT * FROM c WHERE c.type = @t"
            : "SELECT * FROM c WHERE c.type = @t AND (CONTAINS(c.cas, @s, true) OR CONTAINS(c.supplier, @s, true))")
            .WithParameter("@t", KnowledgeTypes.MsdsRegistry).WithParameter("@s", search ?? "");
        return await RunAsync<MsdsRegistryDoc>(msds, q, ct);
    }
    public Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default) =>
        msds.UpsertItemAsync(doc, new PartitionKey(doc.Cas), cancellationToken: ct);

    private static async Task<T?> ReadAsync<T>(Container c, string id, string pk, CancellationToken ct) where T : class
    {
        try { return (await c.ReadItemAsync<T>(id, new PartitionKey(pk), cancellationToken: ct)).Resource; }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    private static async Task<IReadOnlyList<T>> RunAsync<T>(Container c, QueryDefinition q, CancellationToken ct)
    {
        var results = new List<T>();
        using var it = c.GetItemQueryIterator<T>(q);
        while (it.HasMoreResults) results.AddRange(await it.ReadNextAsync(ct));
        return results;
    }
}

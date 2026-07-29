using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

public sealed class InMemorySupplierStore : ISupplierStore
{
    public readonly ConcurrentDictionary<string, AllowlistEntry> Items = new();
    public int Lists;

    public Task<AllowlistEntry?> GetAsync(string domain, CancellationToken ct)
        => Task.FromResult(Items.TryGetValue(domain.ToLowerInvariant(), out var e) ? e : null);

    public Task UpsertAsync(AllowlistEntry entry, CancellationToken ct)
    { Items[entry.Id] = entry; return Task.CompletedTask; }

    public Task<IReadOnlyList<AllowlistEntry>> ListAllAsync(CancellationToken ct)
    { Lists++; return Task.FromResult<IReadOnlyList<AllowlistEntry>>(Items.Values.ToList()); }
}

/// A store that cannot be reached at all — a local host with no Cosmos, or Cosmos having a bad minute.
public sealed class UnreachableSupplierStore : ISupplierStore
{
    public Task<AllowlistEntry?> GetAsync(string domain, CancellationToken ct)
        => throw new HttpRequestException("no route to Cosmos");
    public Task UpsertAsync(AllowlistEntry entry, CancellationToken ct)
        => throw new HttpRequestException("no route to Cosmos");
    public Task<IReadOnlyList<AllowlistEntry>> ListAllAsync(CancellationToken ct)
        => throw new HttpRequestException("no route to Cosmos");
}

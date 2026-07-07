using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

public sealed class InMemoryRegistryStore : IRegistryStore
{
    public readonly ConcurrentDictionary<string, RegistryPointer> Items = new();
    public Task<RegistryPointer?> GetByCasAsync(string cas, CancellationToken ct)
        => Task.FromResult(Items.Values.FirstOrDefault(p => p.Cas == cas && p.SupersededBy is null));
    public Task<RegistryPointer?> GetByProductNameAsync(string name, CancellationToken ct)
        => Task.FromResult(Items.Values.FirstOrDefault(p => p.ProductName == name && p.SupersededBy is null));
    public Task UpsertAsync(RegistryPointer p, CancellationToken ct) { Items[p.Id] = p; return Task.CompletedTask; }
}

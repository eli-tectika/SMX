using System.Collections.Concurrent;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

public sealed class InMemoryMasterListStore : IMasterListStore
{
    public readonly ConcurrentDictionary<string, MasterListEntry> Items = new();
    public Task<MasterListEntry?> GetAsync(string id, string element, CancellationToken ct)
        => Task.FromResult(Items.TryGetValue(id, out var e) ? e : null);
    public Task UpsertAsync(MasterListEntry entry, CancellationToken ct)
    { Items[entry.Id] = entry; return Task.CompletedTask; }
    public Task<IReadOnlyList<MasterListEntry>> ListAllAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MasterListEntry>>(Items.Values.ToList());
}

using System.Collections.Concurrent;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Domain;

public sealed class InMemoryRegStateStore : IRegStateStore
{
    public readonly ConcurrentDictionary<string, RegDocState> Items = new();
    private static string Key(string docId, string sourceId) => $"{sourceId}|{docId}";

    public Task<RegDocState?> GetAsync(string docId, string sourceId, CancellationToken ct)
        => Task.FromResult(Items.TryGetValue(Key(docId, sourceId), out var s) ? s : null);

    public Task UpsertAsync(RegDocState state, CancellationToken ct)
    { Items[Key(state.Id, state.SourceId)] = state; return Task.CompletedTask; }
}

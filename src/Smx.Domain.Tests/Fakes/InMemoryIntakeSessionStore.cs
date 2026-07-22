using System.Collections.Concurrent;
using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// The test twin of CosmosIntakeSessionStore.
///
/// Every doc is DEEP-COPIED through <see cref="Json.Options"/> on the way in and on the way out, the
/// same discipline InMemoryRecordStore uses: Cosmos round-trips through JSON, so a dictionary of live
/// references would let a test mutate the object it handed in and retroactively change what the store
/// "already had" — hiding aliasing bugs a real Cosmos round-trip would have exposed.
public sealed class InMemoryIntakeSessionStore : IIntakeSessionStore
{
    private readonly ConcurrentDictionary<string, IntakeSessionDoc> _docs = new();

    private static T Copy<T>(T doc) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;

    public Task<IntakeSessionDoc?> GetAsync(string sessionId, CancellationToken ct = default) =>
        Task.FromResult(_docs.TryGetValue(sessionId, out var doc) ? Copy(doc) : null);

    public Task UpsertAsync(IntakeSessionDoc doc, CancellationToken ct = default)
    {
        _docs[doc.SessionId] = Copy(doc);
        return Task.CompletedTask;
    }
}

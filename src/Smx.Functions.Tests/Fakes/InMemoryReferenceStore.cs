// src/Smx.Functions.Tests/Fakes/InMemoryReferenceStore.cs
using Smx.Functions.Reference.Data;
using Smx.Functions.Reference.Domain;

public sealed class InMemoryReferenceStore : IReferenceStore
{
    // container -> (id -> doc)
    public readonly Dictionary<string, Dictionary<string, object>> Containers = new();

    public Task UpsertAsync(string container, object doc, string partitionValue, CancellationToken ct)
    {
        var id = ((IHasId)doc).Id;
        if (!Containers.TryGetValue(container, out var m)) { m = new(); Containers[container] = m; }
        m[id] = doc;
        return Task.CompletedTask;
    }

    public int Count(string container) => Containers.TryGetValue(container, out var m) ? m.Count : 0;
}

using System.Collections.Concurrent;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// In-memory IKnowledgeStore for tests + WebApplicationFactory DI swaps. Mirrors InMemoryRecordStore.
public sealed class InMemoryKnowledgeStore : Smx.Domain.IKnowledgeStore
{
    private readonly ConcurrentDictionary<string, LearnedConclusionDoc> _conclusions = new();
    private readonly ConcurrentDictionary<string, MarkerLibraryDoc> _markers = new();
    private readonly ConcurrentDictionary<string, MsdsRegistryDoc> _msds = new();

    private static bool Match(string? search, params string?[] fields) =>
        string.IsNullOrWhiteSpace(search) ||
        fields.Any(f => f is not null && f.Contains(search, StringComparison.OrdinalIgnoreCase));

    public Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default) =>
        Task.FromResult(_conclusions.TryGetValue(KnowledgeIds.LearnedConclusion(kind, scopeKey), out var d) ? d : null);
    public Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LearnedConclusionDoc>>(_conclusions.Values
            .Where(c => Match(search, c.Finding, c.Scope.Element, c.Scope.Material, c.Scope.Application, c.Scope.Market, c.Scope.Substance)).ToList());
    public Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default) { _conclusions[doc.Id] = doc; return Task.CompletedTask; }

    public Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_markers.TryGetValue(id, out var d) ? d : null);
    public Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarkerLibraryDoc>>(_markers.Values
            .Where(m => Match(search, m.ValidatedFor.Application, m.ValidatedFor.Material, m.ValidatedFor.Objective)).ToList());
    public Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default) { _markers[doc.Id] = doc; return Task.CompletedTask; }

    public Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default) =>
        Task.FromResult(_msds.TryGetValue(KnowledgeIds.Msds(cas), out var d) ? d : null);
    public Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MsdsRegistryDoc>>(_msds.Values.Where(m => Match(search, m.Cas, m.Supplier)).ToList());
    public Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default) { _msds[doc.Id] = doc; return Task.CompletedTask; }
}

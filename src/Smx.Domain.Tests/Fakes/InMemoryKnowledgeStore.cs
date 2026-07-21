using System.Collections.Concurrent;
using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Domain.Tests.Fakes;

/// <summary>
/// In-memory IKnowledgeStore for tests + WebApplicationFactory DI swaps. Mirrors InMemoryRecordStore.
///
/// Every doc is DEEP-COPIED through <see cref="Json.Options"/> on the way in and on the way out, for exactly
/// the reasons spelled out on InMemoryRecordStore: Cosmos round-trips through JSON, so an upsert SNAPSHOTS
/// the doc and a read hands back a FRESH graph. A dictionary of live references does neither — and then code
/// that mutates a doc and forgets to upsert it still appears to persist the change here (it is the same
/// object) while in Cosmos the change is simply lost, with a green suite either way.
///
/// This store did NOT copy until now, so that hazard was live for all four knowledge types. The one that
/// would have hurt most: a Dosing agent that reads a SubstancePropertyDoc, corrects the metal loading in
/// memory and never writes it back would look correct here and mis-dose in Azure forever.
/// </summary>
public sealed class InMemoryKnowledgeStore : Smx.Domain.IKnowledgeStore
{
    private readonly ConcurrentDictionary<string, LearnedConclusionDoc> _conclusions = new();
    private readonly ConcurrentDictionary<string, MarkerLibraryDoc> _markers = new();
    private readonly ConcurrentDictionary<string, MsdsRegistryDoc> _msds = new();
    private readonly ConcurrentDictionary<string, SubstancePropertyDoc> _substances = new();

    /// Opt-in failure injection: while set, every UpsertMarkerAsync throws it — in production this is a
    /// remote Cosmos write and CAN die mid-call. A test sets this to prove a caller SURVIVES the store
    /// dying (stamps a visible failure instead of letting the exception escape to the checkpoint-and-lose
    /// change feed), then clears it to prove the re-run converges. Null (the default) never throws.
    public Exception? ThrowOnUpsertMarker { get; set; }

    private static T Copy<T>(T doc) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(doc, Json.Options), Json.Options)!;

    private static bool Match(string? search, params string?[] fields) =>
        string.IsNullOrWhiteSpace(search) ||
        fields.Any(f => f is not null && f.Contains(search, StringComparison.OrdinalIgnoreCase));

    /// One supplied dimension vs. one doc field — the fake's twin of Cosmos `CONTAINS(path, @s, true)`:
    /// a blank dimension is unconstrained; CONTAINS against an absent field is non-matching.
    private static bool Dimension(string? value, string? field) =>
        string.IsNullOrWhiteSpace(value) ||
        (field is not null && field.Contains(value, StringComparison.OrdinalIgnoreCase));

    public Task<LearnedConclusionDoc?> GetLearnedConclusionAsync(string kind, string scopeKey, CancellationToken ct = default) =>
        Task.FromResult(_conclusions.TryGetValue(KnowledgeIds.LearnedConclusion(kind, scopeKey), out var d) ? Copy(d) : null);
    public Task<IReadOnlyList<LearnedConclusionDoc>> QueryLearnedConclusionsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LearnedConclusionDoc>>(_conclusions.Values
            .Where(c => Match(search, c.Finding, c.Scope.Element, c.Scope.Form, c.Scope.Material, c.Scope.Application, c.Scope.Market, c.Scope.Substance))
            .Select(Copy).ToList());
    public Task UpsertLearnedConclusionAsync(LearnedConclusionDoc doc, CancellationToken ct = default) { _conclusions[doc.Id] = Copy(doc); return Task.CompletedTask; }

    public Task<MarkerLibraryDoc?> GetMarkerAsync(string id, CancellationToken ct = default) =>
        Task.FromResult(_markers.TryGetValue(id, out var d) ? Copy(d) : null);
    public Task<IReadOnlyList<MarkerLibraryDoc>> QueryMarkersAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarkerLibraryDoc>>(_markers.Values
            .Where(m => Match(search, m.ValidatedFor.Application, m.ValidatedFor.Material, m.ValidatedFor.Objective))
            .Select(Copy).ToList());
    public Task<IReadOnlyList<MarkerLibraryDoc>> FindMarkersAsync(string? application, string? material, string? objective, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MarkerLibraryDoc>>(_markers.Values
            .Where(m => m.Status == MarkerStatus.Approved
                     && Dimension(application, m.ValidatedFor.Application)
                     && Dimension(material, m.ValidatedFor.Material)
                     && Dimension(objective, m.ValidatedFor.Objective))
            .Select(Copy).ToList());
    public Task UpsertMarkerAsync(MarkerLibraryDoc doc, CancellationToken ct = default)
    {
        if (ThrowOnUpsertMarker is { } e) throw e;
        _markers[doc.Id] = Copy(doc); return Task.CompletedTask;
    }

    public Task<MsdsRegistryDoc?> GetMsdsAsync(string cas, CancellationToken ct = default) =>
        Task.FromResult(_msds.TryGetValue(KnowledgeIds.Msds(cas), out var d) ? Copy(d) : null);
    public Task<IReadOnlyList<MsdsRegistryDoc>> QueryMsdsAsync(string? search, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MsdsRegistryDoc>>(_msds.Values.Where(m => Match(search, m.Cas, m.Supplier)).Select(Copy).ToList());
    public Task UpsertMsdsAsync(MsdsRegistryDoc doc, CancellationToken ct = default) { _msds[doc.Id] = Copy(doc); return Task.CompletedTask; }

    /// The twin of the Cosmos point-read on the /cas partition: a miss is null, never a throw. Dosing parks
    /// on that null and names the CAS it needs.
    public Task<SubstancePropertyDoc?> GetSubstancePropertyAsync(string cas, CancellationToken ct = default) =>
        Task.FromResult(_substances.TryGetValue(KnowledgeIds.SubstanceProperty(cas), out var d) ? Copy(d) : null);
    public Task UpsertSubstancePropertyAsync(SubstancePropertyDoc doc, CancellationToken ct = default) { _substances[doc.Id] = Copy(doc); return Task.CompletedTask; }
}

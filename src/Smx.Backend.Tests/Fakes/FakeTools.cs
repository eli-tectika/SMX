using Smx.Domain.Tools;

namespace Smx.Backend.Tests.Fakes;

public sealed class FakeCatalogLookup : ICatalogLookup
{
    public Dictionary<string, List<CatalogCard>> Cards { get; } = new();
    public List<string> Calls { get; } = [];
    public Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default)
    {
        Calls.Add(element);
        return Task.FromResult<IReadOnlyList<CatalogCard>>(Cards.TryGetValue(element, out var c) ? c : []);
    }

    /// Register the cards a lookup for <paramref name="element"/> returns. Cost-stage tests pass cards that
    /// carry a price/pack (via CatalogCard's optional trailing params); every other test omits them and the
    /// card's Price/Pack stay null. Returns `this` so a test can chain a couple of elements in one setup line.
    public FakeCatalogLookup Returns(string element, params CatalogCard[] cards)
    {
        Cards[element] = [.. cards];
        return this;
    }
}

public sealed class FakeCompatibilityLookup : ICompatibilityLookup
{
    public Dictionary<(string, string), CompatibilityCard> Cards { get; } = new();
    public List<(string Element, string Substrate)> Calls { get; } = [];
    public Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default)
    {
        Calls.Add((element, substrate));
        return Task.FromResult(Cards.TryGetValue((element, substrate), out var c) ? c : (CompatibilityCard?)null);
    }
}

public sealed class FakeSearch : IRegulatorySearch, ISdsSearch, IReferenceSearch
{
    public List<string> Queries { get; } = [];
    public List<RetrievedChunk> Results { get; set; } = [];
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Results.Take(top).ToList());
    }
}

public sealed class FakeLearnedConclusionsSearch : ILearnedConclusionsSearch
{
    public List<string> Queries { get; } = [];
    public List<RetrievedChunk> Results { get; } = [];
    public Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default)
    {
        Queries.Add(query);
        return Task.FromResult<IReadOnlyList<RetrievedChunk>>(Results.Take(top).ToList());
    }
}

/// Records what was asked of the SDS service and answers whatever the test scripted. Defaults to
/// `already-had`, which is the zero-egress answer — a test that cares about a fetch or a failure says so.
public sealed class FakeSdsAcquisition : ISdsAcquisition
{
    public List<(string Cas, string? Element, string? Form)> Ensured { get; } = [];
    public List<(string Element, string Form, string Cas)> Appended { get; } = [];
    public SdsEnsureResult Result { get; set; } = new(SdsEnsureStatus.AlreadyHad, RegistryId: "sds-1", Supplier: "Acme");

    /// A ledger append that fails. The real client swallows its own transport failures, but the runner may
    /// not RELY on that: a bookkeeping call must not be able to fail a stage no matter how it misbehaves.
    public bool ThrowOnAppend { get; set; }

    public Task<SdsEnsureResult> EnsureAsync(string cas, string? element, string? form, CancellationToken ct)
    {
        Ensured.Add((cas, element, form));
        return Task.FromResult(Result);
    }

    public Task AppendAsync(string element, string form, string cas, CancellationToken ct)
    {
        if (ThrowOnAppend) throw new InvalidOperationException("the SDS service is down");
        Appended.Add((element, form, cas));
        return Task.CompletedTask;
    }

    /// No agent and no pipeline stage uploads a sheet — the operator does, through the registry screen,
    /// and KnowledgeEndpointsTests fakes that separately. Recorded here rather than thrown so that a
    /// caller appearing in this direction shows up as an unexpected entry rather than a crash.
    public List<SdsUpload> Uploaded { get; } = [];

    public Task<SdsUploadResult> UploadAsync(SdsUpload upload, CancellationToken ct)
    {
        Uploaded.Add(upload);
        return Task.FromResult(new SdsUploadResult(true, RegistryId: "sds-1"));
    }
}

using Smx.Domain.Documents;

namespace Smx.Domain.Tests.Fakes;

/// In-memory ISdsDocumentSource: `Sheets` is sds-registry, `Master` is sds-master-list.
public sealed class InMemorySdsDocumentSource : ISdsDocumentSource
{
    public List<SdsSheetRow> Sheets { get; } = [];
    public List<SdsMasterRow> Master { get; } = [];

    public Task<IReadOnlyList<SdsSheetRow>> ListSheetsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SdsSheetRow>>(Sheets.ToList());

    public Task<SdsSheetRow?> GetSheetAsync(string registryId, string cas, CancellationToken ct = default) =>
        Task.FromResult(Sheets.FirstOrDefault(s =>
            s.Id == registryId && string.Equals(s.Cas, cas, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<SdsMasterRow>> ListMasterAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SdsMasterRow>>(Master.ToList());

    public Task<SdsMasterRow?> GetMasterAsync(string masterId, string element, CancellationToken ct = default) =>
        Task.FromResult(Master.FirstOrDefault(m =>
            m.Id == masterId && string.Equals(m.Element, element, StringComparison.OrdinalIgnoreCase)));
}

/// In-memory IRegDocumentSource: `Sources` is reg-registry, `Docs` is reg-state.
public sealed class InMemoryRegDocumentSource : IRegDocumentSource
{
    public List<RegSourceRow> Sources { get; } = [];
    public List<RegDocRow> Docs { get; } = [];

    public Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegSourceRow>>(Sources.ToList());

    public Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegDocRow>>(Docs.ToList());

    public Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default) =>
        Task.FromResult(Docs.FirstOrDefault(d => d.DocId == docId && d.SourceId == sourceId));
}

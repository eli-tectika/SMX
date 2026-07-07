using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public interface IMasterListStore
{
    Task<MasterListEntry?> GetAsync(string id, string element, CancellationToken ct);
    Task UpsertAsync(MasterListEntry entry, CancellationToken ct);
    Task<IReadOnlyList<MasterListEntry>> ListAllAsync(CancellationToken ct);
}

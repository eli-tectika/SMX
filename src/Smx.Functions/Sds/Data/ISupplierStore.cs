using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

/// Suppliers as runtime data. The bundled `suppliers.allowlist.json` seeds this container once and then
/// stops being the source of truth: an operator adds a supplier by writing a row, not by opening a PR.
public interface ISupplierStore
{
    Task<AllowlistEntry?> GetAsync(string domain, CancellationToken ct);
    Task UpsertAsync(AllowlistEntry entry, CancellationToken ct);
    Task<IReadOnlyList<AllowlistEntry>> ListAllAsync(CancellationToken ct);
}

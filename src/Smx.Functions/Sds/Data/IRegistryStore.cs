using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public interface IRegistryStore
{
    Task<RegistryPointer?> GetByCasAsync(string cas, CancellationToken ct);
    Task<RegistryPointer?> GetByProductNameAsync(string productName, CancellationToken ct);
    Task UpsertAsync(RegistryPointer pointer, CancellationToken ct);
}

using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Data;

public sealed class RegistryRepo
{
    private readonly IRegistryStore _store;
    public RegistryRepo(IRegistryStore store) => _store = store;

    public Task<RegistryPointer?> GetForSubstanceAsync(string? cas, string? productName, CancellationToken ct)
        => !string.IsNullOrWhiteSpace(cas) ? _store.GetByCasAsync(cas!, ct)
         : !string.IsNullOrWhiteSpace(productName) ? _store.GetByProductNameAsync(productName!, ct)
         : Task.FromResult<RegistryPointer?>(null);

    public Task UpsertAsync(RegistryPointer pointer, CancellationToken ct) => _store.UpsertAsync(pointer, ct);
}

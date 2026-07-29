using Microsoft.Extensions.Logging;
using Smx.Functions.Common;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Sourcing;

namespace Smx.Functions.Sds.Config;

/// Turns the `sds-suppliers` container into an `AllowlistProvider`, seeding it on first run from the
/// bundled JSON.
public static class SupplierStore
{
    public static Task<AllowlistProvider> LoadAsync(
        ISupplierStore store, string bundledJson, CancellationToken ct)
        => LoadAsync(store, bundledJson, null, ct);

    public static async Task<AllowlistProvider> LoadAsync(
        ISupplierStore store, string bundledJson, ILogger? log, CancellationToken ct)
    {
        try
        {
            var stored = await store.ListAllAsync(ct);

            // A non-empty container wins outright — the bundled entries are NOT merged back underneath.
            // Seeding is a first-run bootstrap, not a floor: if it were a floor, a supplier could never
            // be removed without a redeploy, which is the exact thing this change exists to end.
            if (stored.Count > 0) return new AllowlistProvider(stored);

            var seed = AllowlistProvider.FromJson(bundledJson).Ordered;
            foreach (var entry in seed) await store.UpsertAsync(entry, ct);
            log?.LogInformation("Seeded {Count} suppliers from the bundled allowlist", seed.Count);
            return new AllowlistProvider(seed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Local dev has no Cosmos, and Cosmos can have a bad minute. Neither is a reason for the
            // sweep to run with no suppliers at all — the bundled file is still on disk.
            log?.LogWarning(ex, "Supplier store unreachable; falling back to the bundled allowlist");
            return AllowlistProvider.FromJson(bundledJson);
        }
    }
}

/// The `AllowlistProvider` singleton, loaded lazily.
///
/// Program.cs builds the container synchronously and `LoadAsync` is async, so the provider cannot be
/// constructed during DI: blocking on `.Result` inside a factory deadlocks or starves the worker's
/// thread pool under load, and it would make a Cosmos round-trip a precondition of *starting*. Instead
/// the load happens on first use, behind a gate so eight concurrent sweep entries seed the container
/// once rather than eight times.
public sealed class SupplierCatalog
{
    private readonly ISupplierStore _store;
    private readonly string _bundledPath;
    private readonly ILogger? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AllowlistProvider? _cached;

    public SupplierCatalog(ISupplierStore store, string bundledPath, ILogger<SupplierCatalog>? log)
    { _store = store; _bundledPath = bundledPath; _log = log; }

    private SupplierCatalog(AllowlistProvider fixedProvider)
    { _store = null!; _bundledPath = ""; _cached = fixedProvider; }

    /// A catalog over an already-known provider — for tests and for callers that were handed one.
    public static SupplierCatalog Fixed(AllowlistProvider provider) => new(provider);

    public async ValueTask<AllowlistProvider> GetAsync(CancellationToken ct)
    {
        if (_cached is not null) return _cached;
        await _gate.WaitAsync(ct);
        try
        {
            // ContentRoot.Resolve: relative paths must anchor to the content root, never the CWD —
            // on Flex Consumption the CWD is a standby dir and the raw read crashed the first live sweep.
            return _cached ??= await SupplierStore.LoadAsync(
                _store, File.ReadAllText(ContentRoot.Resolve(_bundledPath)), _log, ct);
        }
        finally { _gate.Release(); }
    }

    /// Drop the cache so the next read sees a supplier added since startup. Adding one has to take
    /// effect without a restart, or moving the list to Cosmos bought nothing over the file it replaced.
    public void Invalidate() => _cached = null;
}

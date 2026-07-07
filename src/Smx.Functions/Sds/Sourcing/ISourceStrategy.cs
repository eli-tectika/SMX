using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

// Supplied by the sweep, backed by the single IEgressClient. Strategies never construct their own egress.
public delegate Task<EgressResult?> EgressFetch(Uri url, CancellationToken ct);

public interface ISourceStrategy
{
    string Name { get; }
    Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct);
}

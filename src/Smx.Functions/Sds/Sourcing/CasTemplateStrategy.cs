using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class CasTemplateStrategy : ISourceStrategy
{
    public string Name => "casTemplate";

    public Task<IReadOnlyList<SourceCandidate>> ResolveAsync(
        AllowlistEntry entry, SubstanceKey key, EgressFetch fetch, CancellationToken ct)
    {
        var url = new Uri(entry.SdsUrlTemplate.Replace("{cas}", key.Cas));
        IReadOnlyList<SourceCandidate> result = new[] { new SourceCandidate(entry.Supplier, entry.Domain, url) };
        return Task.FromResult(result);
    }
}

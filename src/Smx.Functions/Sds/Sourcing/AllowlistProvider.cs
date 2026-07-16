using System.Text.Json;
using Smx.Functions.Common;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class AllowlistProvider
{
    private readonly IReadOnlyList<AllowlistEntry> _entries;

    public AllowlistProvider(IReadOnlyList<AllowlistEntry> entries)
        => _entries = entries.OrderBy(e => e.Priority).ToList();

    // ContentRoot.Resolve: relative paths must anchor to the content root, never the CWD —
    // on Flex Consumption the CWD is a standby dir and the raw read crashed the first live sweep.
    public static AllowlistProvider FromFile(string path) => FromJson(File.ReadAllText(ContentRoot.Resolve(path)));

    public static AllowlistProvider FromJson(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var entries = JsonSerializer.Deserialize<List<AllowlistEntry>>(json, opts)
                      ?? throw new InvalidOperationException("Allowlist parsed to null.");
        if (entries.Count == 0) throw new InvalidOperationException("Allowlist is empty.");
        return new AllowlistProvider(entries);
    }

    public IReadOnlyList<AllowlistEntry> Ordered => _entries;

    public IReadOnlySet<string> Domains
        => _entries.Select(e => e.Domain.ToLowerInvariant()).ToHashSet();
}

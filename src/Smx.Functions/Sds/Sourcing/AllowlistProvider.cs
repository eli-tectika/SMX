using System.Text.Json;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Sourcing;

public sealed class AllowlistProvider
{
    private readonly IReadOnlyList<AllowlistEntry> _entries;

    public AllowlistProvider(IReadOnlyList<AllowlistEntry> entries)
        => _entries = entries.OrderBy(e => e.Priority).ToList();

    public static AllowlistProvider FromFile(string path) => FromJson(File.ReadAllText(path));

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

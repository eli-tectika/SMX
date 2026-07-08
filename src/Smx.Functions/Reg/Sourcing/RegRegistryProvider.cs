using System.Text.Json;
using Smx.Functions.Reg.Domain;

namespace Smx.Functions.Reg.Sourcing;

// The curated registry of official regulatory sources — the correctness guardrail from §15: fetching is
// confined to these vetted authorities, never open web search. Git-versioned like the SDS supplier allowlist
// (Sds/Sourcing/AllowlistProvider). `Domains` feeds the egress allowlist so the NAT path can only reach them.
public sealed class RegRegistryProvider
{
    private readonly IReadOnlyList<RegSource> _sources;

    public RegRegistryProvider(IReadOnlyList<RegSource> sources)
        => _sources = sources.Select(s => s with { Id = s.SourceId }).ToList();

    public static RegRegistryProvider FromFile(string path) => FromJson(File.ReadAllText(path));

    public static RegRegistryProvider FromJson(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var sources = JsonSerializer.Deserialize<List<RegSource>>(json, opts)
                      ?? throw new InvalidOperationException("Regulators registry parsed to null.");
        if (sources.Count == 0) throw new InvalidOperationException("Regulators registry is empty.");
        return new RegRegistryProvider(sources);
    }

    public IReadOnlyList<RegSource> All => _sources;
    public IReadOnlyList<RegSource> Enabled => _sources.Where(s => s.Enabled).ToList();

    public IReadOnlySet<string> Domains
        => _sources.Select(s => s.Domain.ToLowerInvariant()).ToHashSet();
}

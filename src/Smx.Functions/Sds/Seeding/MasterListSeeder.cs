using System.Text.Json;
using System.Text.RegularExpressions;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Seeding;

public sealed record SdsSeedEntry(string Element, string Form, string Cas);

public sealed record SdsSeedReport(int Derived, int Added, int AlreadyPresent, IReadOnlyList<string> Skipped);

// Derives the initial SDS master-list manifest from the curated reference catalog
// (Reference/Seed/catalog-products.json) and appends it idempotently. This is seeding of the
// manifest ONLY — no fetching happens here; the scheduled sweep does the gathering.
public sealed class MasterListSeeder
{
    private static readonly Regex CasPattern = new(@"^\d{2,7}-\d{2}-\d$", RegexOptions.Compiled);

    private sealed record CatalogRecord(string? Element, string? Compound, string? Cas);

    private readonly MasterListRepo _repo;
    public MasterListSeeder(MasterListRepo repo) => _repo = repo;

    // One master-list row per (element, form): first catalog row wins; duplicates and rows without a
    // usable CAS are reported, not silently dropped — the operator reviews the skip list.
    public static (IReadOnlyList<SdsSeedEntry> Entries, IReadOnlyList<string> Skipped) Derive(string catalogJson)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var records = JsonSerializer.Deserialize<List<CatalogRecord>>(catalogJson, opts)
                      ?? throw new InvalidOperationException("Catalog parsed to null.");

        var entries = new List<SdsSeedEntry>();
        var skipped = new List<string>();
        var seen = new Dictionary<string, SdsSeedEntry>();

        foreach (var r in records)
        {
            var element = r.Element?.Trim() ?? "";
            var form = r.Compound?.Trim() ?? "";
            var cas = r.Cas?.Trim() ?? "";

            if (element.Length == 0 || form.Length == 0)
            { skipped.Add($"missing element/form (cas '{cas}')"); continue; }
            if (!CasPattern.IsMatch(cas))
            { skipped.Add($"{element}/{form}: invalid CAS '{cas}'"); continue; }

            var id = DedupKey.ForMasterList(element, form);
            if (seen.TryGetValue(id, out var kept))
            {
                skipped.Add(kept.Cas == cas
                    ? $"{element}/{form}: duplicate row (cas {cas})"
                    : $"{element}/{form}: duplicate pair, kept cas {kept.Cas}, skipped {cas}");
                continue;
            }

            var entry = new SdsSeedEntry(element, form, cas);
            seen[id] = entry;
            entries.Add(entry);
        }
        return (entries, skipped);
    }

    public async Task<SdsSeedReport> SeedAsync(string catalogJson, string nowUtc, CancellationToken ct)
    {
        var (entries, skipped) = Derive(catalogJson);
        int added = 0, present = 0;
        foreach (var e in entries)
        {
            if (await _repo.AppendAsync(e.Element, e.Form, e.Cas, null, "operator", nowUtc, ct)) added++;
            else present++;
        }
        return new SdsSeedReport(entries.Count, added, present, skipped);
    }
}

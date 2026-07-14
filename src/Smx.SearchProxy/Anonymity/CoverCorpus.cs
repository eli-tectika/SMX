using System.Text.Json;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Anonymity;

/// The decoy pool, keyed by intent. Git-versioned and PR-reviewed, exactly like the SDS supplier allowlist
/// and the regulator registry — this is security-critical data, and a bad edit silently weakens the
/// anonymization rather than breaking anything visible.
///
/// It ships as a file rather than a Cosmos lookup because the proxy's identity has NO corpus RBAC and must
/// keep it (spec §2 D2). The constraint is load-bearing, not incidental.
public sealed class CoverCorpus
{
    /// Below this, a family is too thin to hide a query in: with ~20 decoys per family and 3 drawn per
    /// batch, an observer needs many rounds to distinguish signal from cover. It is a floor, not a target.
    public const int MinimumPerFamily = 20;

    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _families;

    private CoverCorpus(IReadOnlyDictionary<string, IReadOnlyList<string>> families) => _families = families;

    public IReadOnlyList<string> For(string intent) => _families[intent];

    public static CoverCorpus FromFile(string path) => FromJson(File.ReadAllText(path));

    public static CoverCorpus FromJson(string json)
    {
        var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json)
                  ?? throw new InvalidOperationException("cover corpus is empty or unparseable");

        // Two passes, and the order matters. A wholly missing family is the more serious fault — it means an
        // intent shipped with no cover at all — so it must be reported even when some other family also
        // happens to be thin. A single pass would let a thin-family error mask a missing one.
        foreach (var intent in SearchIntents.All)
        {
            if (!raw.ContainsKey(intent))
                throw new InvalidOperationException(
                    $"cover corpus has no decoy family for intent '{intent}' — a real query for it would egress naked");
        }

        var families = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var intent in SearchIntents.All)
        {
            var qs = raw[intent];
            if (qs.Count < MinimumPerFamily)
                throw new InvalidOperationException(
                    $"cover corpus family '{intent}' has {qs.Count} decoys; at least {MinimumPerFamily} are required to hide a query");
            families[intent] = qs;
        }
        return new CoverCorpus(families);
    }
}

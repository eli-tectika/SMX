using System.Text;

namespace Smx.Domain.Documents;

/// The only thing the document API accepts. `{kind}_{base64url(payload)}`.
///
/// Two jobs, both load-bearing. It keeps blob paths out of the API surface — a `?path=` parameter
/// against the bronze container is an arbitrary-read primitive over the entire regulatory corpus
/// (design D2). And it makes every lookup a point read, because the payload's first segment is the
/// Cosmos partition key of the row that owns the document.
///
/// base64url rather than the raw payload because the natural ids contain '|' and spaces — the same
/// constraint, and the same fix, that DedupKey.ForChunk records for AI Search keys.
public static class DocumentId
{
    public const string Sds = "sds";
    public const string Reg = "reg";
    public const string Seed = "seed";
    public const string SdsGap = "sdsgap";

    // kind -> (separator, exact segment count, spaces allowed). Fixed counts matter: an extra
    // segment on a `reg` payload becomes an extra component of the constructed blob path.
    //
    // Spaces are allowed only for `sds`: its segments are DedupKey.ForRegistry values, and
    // DedupKey.Norm lowercases and COLLAPSES whitespace rather than stripping it, so a supplier
    // name like "Alfa Aesar" legitimately puts a single space in the payload. The other three
    // kinds' segments are slugs (sourceId, docId, region, element, form-slug) that are never
    // supposed to contain one — a space there is a malformed or hostile id, not a real one.
    private static readonly Dictionary<string, (char Sep, int Segments, bool SpacesAllowed)> Shapes = new()
    {
        [Sds] = ('|', 3, true),       // cas | supplier | revisionDate
        [Reg] = ('/', 2, false),      // sourceId / docId
        [Seed] = ('/', 2, false),     // region / docId
        [SdsGap] = ('_', 2, false),   // element _ form-slug  (DedupKey.ForMasterList)
    };

    public static string Encode(string kind, string payload)
    {
        if (!Shapes.ContainsKey(kind)) throw new ArgumentException($"unknown document kind '{kind}'", nameof(kind));
        return kind + "_" + EncodePayloadForTest(payload);
    }

    /// base64url of a raw payload. Public only so tests can build deliberately-invalid ids;
    /// production callers use Encode, which validates the kind.
    public static string EncodePayloadForTest(string payload) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? id, out string kind, out string payload)
    {
        kind = ""; payload = "";
        if (string.IsNullOrEmpty(id)) return false;

        var split = id.IndexOf('_');
        if (split <= 0 || split == id.Length - 1) return false;

        var k = id[..split];
        if (!Shapes.TryGetValue(k, out var shape)) return false;

        string decoded;
        try
        {
            var b64 = id[(split + 1)..].Replace('-', '+').Replace('_', '/');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(b64)));
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }

        if (decoded.Length == 0) return false;
        // Traversal and control characters, refused even when base64-clean: the payload becomes both
        // a partition key and part of a blob path.
        if (decoded.Contains("..", StringComparison.Ordinal)) return false;
        if (decoded.Contains('\\')) return false;
        if (decoded.Any(char.IsControl)) return false;
        // A space is legitimate ONLY inside a pipe-separated (sds) segment — see Shapes above.
        if (!shape.SpacesAllowed && decoded.Contains(' ')) return false;

        var segments = decoded.Split(shape.Sep);
        if (segments.Length != shape.Segments) return false;
        if (segments.Any(string.IsNullOrWhiteSpace)) return false;
        // A '/' inside a non-'/'-separated payload would still reach the path builder.
        if (shape.Sep != '/' && decoded.Contains('/')) return false;

        kind = k; payload = decoded;
        return true;
    }

    /// The Cosmos partition key for a decoded payload: always its first segment.
    /// sds -> cas, reg/seed -> sourceId/region, sdsgap -> element.
    public static string PartitionKeyOf(string kind, string payload) =>
        payload.Split(Shapes[kind].Sep)[0];

    /// The payload's segments, in order. Callers know their own kind's shape.
    public static string[] SegmentsOf(string kind, string payload) =>
        payload.Split(Shapes[kind].Sep);

    private static string Pad(string b64) => (b64.Length % 4) switch
    {
        2 => b64 + "==",
        3 => b64 + "=",
        0 => b64,
        _ => b64 + "===",   // length%4==1 is invalid base64; let FromBase64String reject it
    };
}

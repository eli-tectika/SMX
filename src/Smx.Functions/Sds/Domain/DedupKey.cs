using System.Text;
using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Domain;

public static class DedupKey
{
    public static string ForMasterList(string element, string form)
        => $"{element.Trim()}_{Slug(form)}";

    public static string ForRegistry(string cas, string supplier, string revisionDate)
        => $"{Norm(cas)}|{Norm(supplier)}|{Norm(revisionDate)}";

    // Azure AI Search document keys allow ONLY letters, digits, '_', '-', '=' — the readable
    // registry id ('|', spaces) got rejected live (2026-07-16). base64url is exactly that
    // alphabet, deterministic, and collision-free (unlike slugging, where 'a b' and 'a-b' merge).
    public static string ForChunk(string registryId, int ordinal)
        => $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(registryId)).Replace('+', '-').Replace('/', '_')}-{ordinal}";

    private static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string Slug(string s)
        => Regex.Replace(Norm(s), @"[^a-z0-9]+", "-").Trim('-');
}

using System.Text.RegularExpressions;

namespace Smx.Functions.Sds.Domain;

public static class DedupKey
{
    public static string ForMasterList(string element, string form)
        => $"{element.Trim()}_{Slug(form)}";

    public static string ForRegistry(string cas, string supplier, string revisionDate)
        => $"{Norm(cas)}|{Norm(supplier)}|{Norm(revisionDate)}";

    private static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string Slug(string s)
        => Regex.Replace(Norm(s), @"[^a-z0-9]+", "-").Trim('-');
}

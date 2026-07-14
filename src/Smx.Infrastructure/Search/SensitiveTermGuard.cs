using System.Text.RegularExpressions;
using Smx.Domain.Tools;

namespace Smx.Infrastructure.Search;

/// Layer 1 of the anonymization (spec §6.1), and the only layer that can possibly do this job.
///
/// The Search Proxy is project-blind: it cannot know that "Acme Bottling" is a client name, and giving it
/// the client roster would put that list in git on the internet-facing component. The orchestrator already
/// holds the names (ProjectDoc.Client, ProjectDoc.Product, ProjectId) — so the identity check belongs here,
/// and the structural check belongs there.
public static class SensitiveTermGuard
{
    public static bool IsClean(string query, SensitiveTerms terms, out string? offendingTerm)
    {
        foreach (var term in terms.Terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            // Whole-token match: a client called "Ion" must not blacklist "ionic".
            var pattern = $@"(?<![\w-]){Regex.Escape(term.Trim())}(?![\w-])";
            if (Regex.IsMatch(query, pattern, RegexOptions.IgnoreCase))
            {
                offendingTerm = term;
                return false;
            }
        }
        offendingTerm = null;
        return true;
    }
}

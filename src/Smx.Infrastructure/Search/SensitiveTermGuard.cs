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
    // The shortest word-token worth matching on its own. Fragments like "Co" are too common to be
    // identifying; the full phrase is always matched regardless of length.
    private const int MinTokenLength = 4;

    // Ubiquitous corporate words. Matched only as part of a full phrase, never on their own — otherwise a
    // client "Acme Bottling Company" would blacklist the word "company" and nuke legitimate chemistry queries.
    private static readonly HashSet<string> CorporateStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "company", "incorporated", "inc", "ltd", "llc", "limited", "corp", "corporation",
        "gmbh", "group", "holdings", "industries", "international", "the", "and", "of",
    };

    public static bool IsClean(string query, SensitiveTerms terms, out string? offendingTerm)
    {
        foreach (var term in terms.Terms)
        {
            if (string.IsNullOrWhiteSpace(term)) continue;
            var trimmed = term.Trim();

            // 1. The full phrase, always — even a single-word or stopword term the operator deliberately entered.
            if (Matches(query, trimmed))
            {
                offendingTerm = term;
                return false;
            }

            // 2. Each distinctive word of a multi-word term, individually: a query need only carry "Acme" to
            //    identify the project "Acme Bottling Company". Short fragments and ubiquitous corporate words
            //    are skipped so ordinary chemistry queries are not caught.
            foreach (var token in trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length < MinTokenLength || CorporateStopwords.Contains(token)) continue;
                if (Matches(query, token))
                {
                    offendingTerm = token;
                    return false;
                }
            }
        }
        offendingTerm = null;
        return true;
    }

    // Whole-token match: a client called "Ion" must not blacklist "ionic".
    private static bool Matches(string query, string value) =>
        Regex.IsMatch(query, $@"(?<![\w-]){Regex.Escape(value)}(?![\w-])", RegexOptions.IgnoreCase);
}

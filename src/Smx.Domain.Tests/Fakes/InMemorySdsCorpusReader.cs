using Smx.Domain;

namespace Smx.Domain.Tests.Fakes;

/// In-memory ISdsCorpusReader for endpoint tests: `Sheets` is the corpus.
public sealed class InMemorySdsCorpusReader : ISdsCorpusReader
{
    public List<SdsCorpusSheet> Sheets { get; } = [];

    public Task<IReadOnlyList<SdsCorpusSheet>> QuerySheetsAsync(string? search, CancellationToken ct = default)
    {
        IReadOnlyList<SdsCorpusSheet> result = string.IsNullOrWhiteSpace(search)
            ? Sheets.ToList()
            : Sheets.Where(s =>
                s.Cas.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                s.Supplier.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(result);
    }

    public Task<SdsCorpusSheet?> GetLatestForCasAsync(string cas, CancellationToken ct = default) =>
        Task.FromResult(Sheets
            .Where(s => string.Equals(s.Cas, cas, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.RevisionDate, StringComparer.Ordinal)
            .ThenByDescending(s => s.IngestedUtc, StringComparer.Ordinal)
            .FirstOrDefault());
}

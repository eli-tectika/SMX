using System.Collections.Concurrent;
using Smx.Functions.Reg.Data;
using Smx.Functions.Reg.Domain;
using Smx.Functions.Reg.Ingestion;

// In-memory fakes for the Reg Cosmos stores + Gold search client, mirroring the SDS test fakes.

public sealed class InMemoryRegSilverStore : IRegSilverStore
{
    public readonly ConcurrentDictionary<string, SilverChunk> Items = new();

    public Task UpsertStagedAsync(IReadOnlyList<SilverChunk> chunks, CancellationToken ct)
    { foreach (var c in chunks) Items[c.Id] = c; return Task.CompletedTask; }

    public Task<IReadOnlyList<SilverChunk>> GetStagedAsync(string runId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SilverChunk>>(
            Items.Values.Where(c => c.SyncRunId == runId && c.Status == "staged").ToList());

    public Task PromoteStagedToLiveAsync(string runId, IReadOnlyList<string> changedDocIds, CancellationToken ct)
    {
        foreach (var docId in changedDocIds)
            foreach (var c in Items.Values.Where(c => c.DocId == docId && c.Status == "live").ToList())
                Items[c.Id] = c with { Status = "superseded" };
        foreach (var c in Items.Values.Where(c => c.SyncRunId == runId && c.Status == "staged").ToList())
            Items[c.Id] = c with { Status = "live" };
        return Task.CompletedTask;
    }

    public Task DiscardStagedAsync(string runId, CancellationToken ct)
    {
        foreach (var c in Items.Values.Where(c => c.SyncRunId == runId && c.Status == "staged").ToList())
            Items[c.Id] = c with { Status = "superseded" };
        return Task.CompletedTask;
    }
}

public sealed class InMemoryRegReviewStore : IRegReviewStore
{
    public readonly ConcurrentDictionary<string, ReviewRecord> Items = new();
    public Task UpsertAsync(ReviewRecord record, CancellationToken ct) { Items[record.SyncRunId] = record; return Task.CompletedTask; }
    public Task<ReviewRecord?> GetAsync(string runId, CancellationToken ct)
        => Task.FromResult(Items.TryGetValue(runId, out var r) ? r : null);
    public Task<IReadOnlyList<ReviewRecord>> GetByStatusAsync(string status, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ReviewRecord>>(Items.Values.Where(r => r.Status == status).ToList());
}

public sealed class InMemoryRegRunsStore : IRegRunsStore
{
    public readonly ConcurrentDictionary<string, SyncRun> Items = new();
    public Task UpsertAsync(SyncRun run, CancellationToken ct) { Items[run.SyncRunId] = run; return Task.CompletedTask; }
}

public sealed class FakeRegSearchClient : IRegSearchClient
{
    public int EnsureCalls;
    public readonly List<GoldChunk> Pushed = new();
    public Task EnsureIndexAsync(CancellationToken ct) { EnsureCalls++; return Task.CompletedTask; }
    public Task PushAsync(IReadOnlyList<GoldChunk> chunks, CancellationToken ct) { Pushed.AddRange(chunks); return Task.CompletedTask; }
}

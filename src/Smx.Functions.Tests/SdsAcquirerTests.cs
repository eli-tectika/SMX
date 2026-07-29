using Microsoft.Extensions.Logging.Abstractions;
using Smx.Functions.Sds.Acquisition;
using Smx.Functions.Sds.Config;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;
using Smx.Functions.Sds.Ingestion;
using Smx.Functions.Sds.Sourcing;
using Xunit;

public class SdsAcquirerTests
{
    private sealed class TextExtractor : IPdfTextExtractor
    { public string Extract(byte[] pdf) => System.Text.Encoding.UTF8.GetString(pdf); }

    /// Counts calls, so a test can assert that egress did NOT happen — the cache-hit contract is about
    /// the absence of a fetch, and asserting only the return value would pass even if it fetched anyway.
    private sealed class CountingEgressClient : IEgressClient
    {
        private readonly Func<Uri, EgressResult?> _respond;
        public int Calls;
        public CountingEgressClient(Func<Uri, EgressResult?>? respond = null) => _respond = respond ?? (_ => null);
        public Task<EgressResult?> FetchAsync(Uri url, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return Task.FromResult(_respond(url)); }
    }

    private const string AllowJson = """
      [ { "supplier":"Only","domain":"only.example","priority":10,"strategy":"casTemplate",
          "sdsUrlTemplate":"https://only.example/{cas}.pdf" } ]
    """;

    private static (SdsAcquirer Acquirer, InMemoryMasterListStore Master, InMemoryRegistryStore Reg)
        Rig(CountingEgressClient egress, SdsOptions? opts = null)
    {
        var master = new InMemoryMasterListStore();
        var reg = new InMemoryRegistryStore();
        var o = opts ?? new SdsOptions();
        var pipe = new IngestionPipeline(new FakeBronzeStore(), new SdsValidator(10), new TextExtractor(),
            new GhsChunker(), new FakeEmbedder(), new FakeSearchClient(), new RegistryRepo(reg), o);
        var resolver = new SourceResolver(AllowlistProvider.FromJson(AllowJson),
            new ISourceStrategy[] { new CasTemplateStrategy() });
        return (new SdsAcquirer(new MasterListRepo(master), new RegistryRepo(reg), resolver, egress, pipe,
            o, NullLogger<SdsAcquirer>.Instance), master, reg);
    }

    private static EgressResult ValidSheet(Uri url)
        => new(File.ReadAllBytes("Resources/sample_sds.txt"), "application/pdf", url);

    private static EgressResult NotAnSds(Uri url)
        => new("This is an invoice, not a safety data sheet."u8.ToArray(), "application/pdf", url);

    private static SubstanceKey Sodium => new("Na", "hydroxide", "1310-73-2");

    [Fact]
    public async Task A_missing_sheet_is_fetched_and_indexed()
    {
        var (acquirer, master, reg) = Rig(new CountingEgressClient(ValidSheet));

        var result = await acquirer.EnsureAsync(Sodium, force: false, "2026-07-29T00:00:00Z", default);

        Assert.Equal(EnsureStatus.Fetched, result.Status);
        Assert.NotNull(result.RegistryId);
        Assert.Single(reg.Items);
        Assert.Equal(SdsStatus.Fetched, master.Items.Values.Single().Status);
    }

    // The cache hit must be FREE. This is what makes the tool safe for an agent to call whenever it is
    // unsure whether the corpus already has a sheet.
    [Fact]
    public async Task An_existing_sheet_returns_with_no_egress_at_all()
    {
        var egress = new CountingEgressClient(ValidSheet);
        var (acquirer, _, _) = Rig(egress);
        await acquirer.EnsureAsync(Sodium, false, "2026-07-29T00:00:00Z", default);
        var afterFirst = egress.Calls;

        var result = await acquirer.EnsureAsync(Sodium, false, "2026-07-30T00:00:00Z", default);

        Assert.Equal(EnsureStatus.AlreadyHad, result.Status);
        Assert.Equal(afterFirst, egress.Calls);      // not one more request
    }

    // The sweep relies on this: a `fetched` row coming up for its 90-day revision recheck would otherwise
    // short-circuit on the very sheet the recheck exists to replace.
    [Fact]
    public async Task Force_refetches_even_when_a_sheet_exists()
    {
        var egress = new CountingEgressClient(ValidSheet);
        var (acquirer, _, _) = Rig(egress);
        await acquirer.EnsureAsync(Sodium, false, "2026-07-29T00:00:00Z", default);
        var afterFirst = egress.Calls;

        var result = await acquirer.EnsureAsync(Sodium, force: true, "2026-10-29T00:00:00Z", default);

        Assert.Equal(EnsureStatus.Fetched, result.Status);
        Assert.True(egress.Calls > afterFirst);
    }

    [Fact]
    public async Task An_unknown_substance_is_appended_to_the_ledger()
    {
        var (acquirer, master, _) = Rig(new CountingEgressClient(ValidSheet));

        await acquirer.EnsureAsync(new("Xx", "novel form", "99-99-9"), false, "2026-07-29T00:00:00Z", default);

        var entry = Assert.Single(master.Items.Values);
        Assert.Equal("99-99-9", entry.Cas);
        Assert.Equal("ensure", entry.AddedBy);
    }

    // "Unavailable" alone would leave an agent unable to tell a bot-walled supplier from a substance with
    // no published sheet at all. The attempts are part of the contract.
    [Fact]
    public async Task An_unavailable_sheet_reports_every_candidate_and_why_it_failed()
    {
        var (acquirer, _, _) = Rig(new CountingEgressClient(NotAnSds));

        var result = await acquirer.EnsureAsync(new("Zr", "TMHD complex", "18865-74-2"), false,
            "2026-07-29T00:00:00Z", default);

        Assert.Equal(EnsureStatus.Unavailable, result.Status);
        var attempt = Assert.Single(result.Attempted);
        Assert.Contains("rejected", attempt.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public async Task A_candidate_that_never_responds_is_reported_as_such()
    {
        var (acquirer, _, _) = Rig(new CountingEgressClient(_ => null));

        var result = await acquirer.EnsureAsync(Sodium, false, "2026-07-29T00:00:00Z", default);

        Assert.Equal(EnsureStatus.Unavailable, result.Status);
        Assert.Equal("no response", Assert.Single(result.Attempted).Outcome);
    }

    // Failing must leave the substance retryable. This is the whole redesign in one assertion.
    [Fact]
    public async Task A_failed_ensure_leaves_the_entry_retryable_not_parked()
    {
        var (acquirer, master, _) = Rig(new CountingEgressClient(NotAnSds));

        await acquirer.EnsureAsync(Sodium, false, "2026-07-29T00:00:00Z", default);

        var entry = Assert.Single(master.Items.Values);
        Assert.Equal(SdsStatus.Failed, entry.Status);
        Assert.NotNull(entry.NextAttemptUtc);
        Assert.Equal(1, entry.AttemptCount);
    }

    // An agent may have only a CAS. Refusing over bookkeeping would be the wrong trade — answer the
    // question, and simply don't record a ledger row that cannot be keyed.
    [Fact]
    public async Task A_cas_only_request_still_fetches()
    {
        var (acquirer, master, reg) = Rig(new CountingEgressClient(ValidSheet));

        var result = await acquirer.EnsureAsync(new("", "", "1310-73-2"), false, "2026-07-29T00:00:00Z", default);

        Assert.Equal(EnsureStatus.Fetched, result.Status);
        Assert.Single(reg.Items);
        Assert.Empty(master.Items);          // nothing to key on, so nothing recorded
    }

    // ...but if the ledger already knows the CAS, the row is found and updated rather than orphaned.
    [Fact]
    public async Task A_cas_only_request_updates_a_ledger_row_that_already_exists()
    {
        var (acquirer, master, _) = Rig(new CountingEgressClient(ValidSheet));
        await new MasterListRepo(master).AppendAsync("Na", "hydroxide", "1310-73-2", null, "seed",
            "2026-01-01T00:00:00Z", default);

        await acquirer.EnsureAsync(new("", "", "1310-73-2"), false, "2026-07-29T00:00:00Z", default);

        Assert.Equal(SdsStatus.Fetched, Assert.Single(master.Items.Values).Status);
    }

    // A supplier that hangs must not hold an agent's stage open. The budget expiring is a real answer,
    // and it must still record the failure so the entry backs off.
    [Fact]
    public async Task Exceeding_the_budget_is_an_answer_not_a_hang()
    {
        var slow = new CountingEgressClient(_ => { Thread.Sleep(400); return null; });
        var (acquirer, master, _) = Rig(slow, new SdsOptions { EnsureBudgetSeconds = 0 });

        var result = await acquirer.EnsureAsync(Sodium, false, "2026-07-29T00:00:00Z", default);

        Assert.Equal(EnsureStatus.Unavailable, result.Status);
        Assert.Equal(SdsStatus.Failed, Assert.Single(master.Items.Values).Status);
    }

    // Caller cancellation is NOT the budget expiring, and must propagate rather than be reported as
    // "we tried and could not get it" — a claim about the world that was never tested.
    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var (acquirer, _, _) = Rig(new CountingEgressClient(ValidSheet));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => acquirer.EnsureAsync(Sodium, false, "2026-07-29T00:00:00Z", cts.Token));
    }
}

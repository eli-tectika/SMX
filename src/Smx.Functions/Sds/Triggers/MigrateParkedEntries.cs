using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Smx.Functions.Sds.Data;
using Smx.Functions.Sds.Domain;

namespace Smx.Functions.Sds.Triggers;

/// Moves every entry parked in the deleted `awaiting_operator` status to `failed` with an immediate next
/// attempt. On 2026-07-29 that is 40 of 53 substances in dev — every one of them at the old retry cap,
/// in a status IsDue never returned, with no operation anywhere that could bring them back.
///
/// Idempotent and re-runnable: it selects on the dead status alone, so a second run moves nothing and a
/// row legitimately waiting out a backoff is never disturbed.
///
/// AttemptCount is preserved deliberately. It is the record of what was already tried, and zeroing it
/// would both erase the evidence that these suppliers are hard and restart the backoff at one day.
public static class ParkedEntryMigration
{
    /// The literal, kept here rather than on SdsStatus: it names a value the system no longer produces,
    /// and it exists only so this migration and the document library's legacy explanation can recognise
    /// what they are looking at.
    public const string DeletedStatus = "awaiting_operator";

    public static async Task<int> RunAsync(IMasterListStore store, string nowUtc, CancellationToken ct)
    {
        var parked = (await store.ListAllAsync(ct)).Where(e => e.Status == DeletedStatus).ToList();
        foreach (var e in parked)
            await store.UpsertAsync(e with { Status = SdsStatus.Failed, NextAttemptUtc = nowUtc }, ct);
        return parked.Count;
    }
}

public sealed class MigrateParkedEntries
{
    private readonly IMasterListStore _store;
    private readonly ILogger<MigrateParkedEntries> _log;

    public MigrateParkedEntries(IMasterListStore store, ILogger<MigrateParkedEntries> log)
    { _store = store; _log = log; }

    [Function("MigrateParkedEntries")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sds/migrate-parked")] HttpRequestData req)
    {
        var moved = await ParkedEntryMigration.RunAsync(
            _store, DateTimeOffset.UtcNow.ToString("O"), req.FunctionContext.CancellationToken);
        _log.LogInformation("Unparked {Count} SDS master-list entries", moved);

        var resp = req.CreateResponse(HttpStatusCode.OK);
        await resp.WriteAsJsonAsync(new { moved });
        return resp;
    }
}

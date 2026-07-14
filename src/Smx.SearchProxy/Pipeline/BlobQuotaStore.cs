using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace Smx.SearchProxy.Pipeline;

/// One small blob per month, updated with an optimistic-concurrency (ETag) compare-and-swap so two warm
/// instances cannot both read 4,999 and both spend.
public sealed class BlobQuotaStore(BlobContainerClient container, ILogger<BlobQuotaStore> log) : IQuotaStore
{
    private sealed record Counter(int Count);

    public async Task<int> ReadAsync(string month, CancellationToken ct)
    {
        var (count, _) = await LoadAsync(month, ct);
        return count;
    }

    public async Task<int> AddAsync(string month, int delta, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (count, etag) = await LoadAsync(month, ct);
            var next = count + delta;
            var json = BinaryData.FromString(JsonSerializer.Serialize(new Counter(next)));
            var conditions = etag is null
                ? new BlobRequestConditions { IfNoneMatch = ETag.All }   // create-if-absent
                : new BlobRequestConditions { IfMatch = etag };          // update-if-unchanged
            try
            {
                await container.GetBlobClient(Name(month))
                    .UploadAsync(json, new BlobUploadOptions { Conditions = conditions }, ct);
                return next;
            }
            catch (RequestFailedException ex) when (ex.Status is 409 or 412)
            {
                // Another instance won the race; re-read and try again.
            }
        }
        // Failing OPEN here would silently uncap spend. Fail closed: SearchHttp maps this to a 429
        // (quota_unavailable), which is the safe direction for both the bill and the egress volume.
        log.LogError("Quota CAS failed for {Month} after 5 attempts", month);
        throw new QuotaUnavailableException($"quota store contention for {month}");
    }

    private static string Name(string month) => $"quota/{month}.json";

    private async Task<(int Count, ETag? ETag)> LoadAsync(string month, CancellationToken ct)
    {
        try
        {
            var resp = await container.GetBlobClient(Name(month)).DownloadContentAsync(ct);
            var counter = JsonSerializer.Deserialize<Counter>(resp.Value.Content.ToString());
            return (counter?.Count ?? 0, resp.Value.Details.ETag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (0, null);
        }
    }
}

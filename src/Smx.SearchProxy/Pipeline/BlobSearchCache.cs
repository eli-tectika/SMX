using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;
using Smx.SearchProxy.Config;
using Smx.SearchProxy.Contracts;

namespace Smx.SearchProxy.Pipeline;

/// Lives in the proxy's OWN storage account, on which its identity already holds Blob Data Owner
/// (infra/modules/functions.bicep:160-171). No new RBAC — and in particular no Cosmos, no Bronze, no AI
/// Search. The blast radius of this app must stay exactly where it is.
public sealed class BlobSearchCache(BlobContainerClient container, ProxyOptions opts, ILogger<BlobSearchCache> log) : ISearchCache
{
    private sealed record Entry(string FetchedAt, IReadOnlyList<SearchHit> Hits);

    public async Task<IReadOnlyList<SearchHit>?> GetAsync(string key, string nowUtc, CancellationToken ct)
    {
        try
        {
            var resp = await container.GetBlobClient($"{key}.json").DownloadContentAsync(ct);
            var entry = JsonSerializer.Deserialize<Entry>(resp.Value.Content.ToString());
            if (entry is null) return null;

            var fetchedAt = DateTimeOffset.Parse(entry.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var now = DateTimeOffset.Parse(nowUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            return now - fetchedAt < TimeSpan.FromHours(opts.CacheTtlHours) ? entry.Hits : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null; // an ordinary miss
        }
        catch (Exception ex)
        {
            // A broken cache must never fail a search — it degrades to an egress, which is correct behaviour.
            log.LogWarning(ex, "Search cache read failed for {Key}; treating as a miss", key);
            return null;
        }
    }

    public async Task SetAsync(string key, IReadOnlyList<SearchHit> hits, string nowUtc, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Entry(nowUtc, hits));
            await container.GetBlobClient($"{key}.json").UploadAsync(BinaryData.FromString(json), overwrite: true, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Search cache write failed for {Key}", key);
        }
    }
}

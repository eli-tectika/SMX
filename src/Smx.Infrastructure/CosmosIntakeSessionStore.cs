using System.Net;
using Microsoft.Azure.Cosmos;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Infrastructure;

/// The `intake-sessions` container. Id and partition key are BOTH the sessionId — a session is a
/// single document and there is nothing to fan out over.
public sealed class CosmosIntakeSessionStore(Container container) : IIntakeSessionStore
{
    public async Task<IntakeSessionDoc?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            return (await container.ReadItemAsync<IntakeSessionDoc>(
                sessionId, new PartitionKey(sessionId), cancellationToken: ct)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // A missing session is a 404 and a null, not an exception. It is also what an EXPIRED
            // session looks like once the TTL fires, which is a normal outcome, not a fault.
            return null;
        }
    }

    public Task UpsertAsync(IntakeSessionDoc doc, CancellationToken ct = default) =>
        container.UpsertItemAsync(doc, new PartitionKey(doc.SessionId), cancellationToken: ct);
}

using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Azure.Cosmos.Scripts;

namespace Smx.Orchestrator.Tests.Fakes;

/// <summary>
/// A <see cref="Container"/> that records the exact arguments the production code hands to the Cosmos SDK.
///
/// It exists to test the ONE thing a dictionary-backed fake cannot: the <see cref="PartitionKey"/>. Cosmos
/// extracts the partition key from the DOCUMENT at the container's declared path (e.g. <c>/cas</c>) and
/// compares it to the <see cref="PartitionKey"/> the SDK call passed. Disagree and the write is rejected —
/// but only in Azure. <c>InMemoryKnowledgeStore</c> is a dictionary; it never looks at a PartitionKey at all,
/// so a wrong one is invisible to the whole rest of the suite. See <c>CosmosPartitionKeyTests</c>.
///
/// Everything the store does not call throws <see cref="NotSupportedException"/>: if production code starts
/// using a new SDK method against these containers, the test must be taught what to capture rather than
/// silently ignoring the call.
/// </summary>
public sealed class CapturingContainer(string id, CosmosSerializer serializer) : Container
{
    public override string Id { get; } = id;

    /// The document AS IT GOES ON THE WIRE, plus the PartitionKey handed alongside it, for every
    /// UpsertItemAsync — in call order.
    public List<(JsonElement Document, PartitionKey? PartitionKey)> Upserts { get; } = [];

    /// The id + PartitionKey of every ReadItemAsync, in call order.
    public List<(string Id, PartitionKey PartitionKey)> Reads { get; } = [];

    public override Task<ItemResponse<T>> UpsertItemAsync<T>(
        T item, PartitionKey? partitionKey = null, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        // Exactly what the real SDK does with a configured CosmosClientOptions.Serializer: render the item to
        // the bytes that go on the wire — at the same generic T the call site used. Cosmos then extracts the
        // partition key from THOSE bytes, at the container's declared path, and compares it to `partitionKey`.
        // Capturing both halves here is what lets the test compare them.
        using var stream = serializer.ToStream(item);
        // .Clone() so the element outlives the JsonDocument (a disposed payload has bitten this repo before).
        using var parsed = JsonDocument.Parse(stream);
        Upserts.Add((parsed.RootElement.Clone(), partitionKey));

        // CosmosKnowledgeStore's upserts return the Task without ever touching .Resource, so there is nothing
        // to hand back. If a caller ever does read it, the NullReferenceException is a loud, correct signal
        // that this double needs a real ItemResponse — it will not pass silently.
        return Task.FromResult<ItemResponse<T>>(null!);
    }

    public override Task<ItemResponse<T>> ReadItemAsync<T>(
        string id, PartitionKey partitionKey, ItemRequestOptions? requestOptions = null,
        CancellationToken cancellationToken = default)
    {
        Reads.Add((id, partitionKey));
        // An empty container: 404. This is the real path CosmosKnowledgeStore.ReadAsync catches and turns
        // into null. The test asserts on the coordinates captured above, not on the (absent) document.
        throw new CosmosException("not found", HttpStatusCode.NotFound, 0, "activity", 0);
    }

    private static NotSupportedException No([System.Runtime.CompilerServices.CallerMemberName] string m = "") =>
        new($"CapturingContainer does not implement {m}. Production code now calls it — teach the double what to capture.");

    public override Database Database => throw No();
    public override Conflicts Conflicts => throw No();
    public override Scripts Scripts => throw No();

    public override Task<ItemResponse<T>> CreateItemAsync<T>(T i, PartitionKey? p = null, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> CreateItemStreamAsync(Stream s, PartitionKey p, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ItemResponse<T>> ReplaceItemAsync<T>(T i, string id, PartitionKey? p = null, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> ReplaceItemStreamAsync(Stream s, string id, PartitionKey p, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey p, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> DeleteItemStreamAsync(string id, PartitionKey p, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey p, IReadOnlyList<PatchOperation> ops, PatchItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> PatchItemStreamAsync(string id, PartitionKey p, IReadOnlyList<PatchOperation> ops, PatchItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> ReadItemStreamAsync(string id, PartitionKey p, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> UpsertItemStreamAsync(Stream s, PartitionKey p, ItemRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items, ReadManyRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> ReadManyItemsStreamAsync(IReadOnlyList<(string id, PartitionKey partitionKey)> items, ReadManyRequestOptions? o = null, CancellationToken c = default) => throw No();

    public override FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition q, string? token = null, QueryRequestOptions? o = null) => throw No();
    public override FeedIterator<T> GetItemQueryIterator<T>(string? q = null, string? token = null, QueryRequestOptions? o = null) => throw No();
    public override FeedIterator<T> GetItemQueryIterator<T>(FeedRange f, QueryDefinition q, string? token = null, QueryRequestOptions? o = null) => throw No();
    public override FeedIterator GetItemQueryStreamIterator(QueryDefinition q, string? token = null, QueryRequestOptions? o = null) => throw No();
    public override FeedIterator GetItemQueryStreamIterator(string? q = null, string? token = null, QueryRequestOptions? o = null) => throw No();
    public override FeedIterator GetItemQueryStreamIterator(FeedRange f, QueryDefinition q, string? token = null, QueryRequestOptions? o = null) => throw No();
    public override IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? token = null, QueryRequestOptions? o = null, CosmosLinqSerializerOptions? so = null) => throw No();

    public override Task<ContainerResponse> ReadContainerAsync(ContainerRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> ReadContainerStreamAsync(ContainerRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ContainerResponse> ReplaceContainerAsync(ContainerProperties p, ContainerRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> ReplaceContainerStreamAsync(ContainerProperties p, ContainerRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ContainerResponse> DeleteContainerAsync(ContainerRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ResponseMessage> DeleteContainerStreamAsync(ContainerRequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<int?> ReadThroughputAsync(CancellationToken c = default) => throw No();
    public override Task<ThroughputResponse> ReadThroughputAsync(RequestOptions o, CancellationToken c = default) => throw No();
    public override Task<ThroughputResponse> ReplaceThroughputAsync(int t, RequestOptions? o = null, CancellationToken c = default) => throw No();
    public override Task<ThroughputResponse> ReplaceThroughputAsync(ThroughputProperties t, RequestOptions? o = null, CancellationToken c = default) => throw No();

    public override TransactionalBatch CreateTransactionalBatch(PartitionKey p) => throw No();
    public override Task<IReadOnlyList<FeedRange>> GetFeedRangesAsync(CancellationToken c = default) => throw No();
    public override ChangeFeedEstimator GetChangeFeedEstimator(string n, Container l) => throw No();
    public override ChangeFeedProcessorBuilder GetChangeFeedEstimatorBuilder(string n, ChangesEstimationHandler h, TimeSpan? i = null) => throw No();
    public override FeedIterator<T> GetChangeFeedIterator<T>(ChangeFeedStartFrom f, ChangeFeedMode m, ChangeFeedRequestOptions? o = null) => throw No();
    public override FeedIterator GetChangeFeedStreamIterator(ChangeFeedStartFrom f, ChangeFeedMode m, ChangeFeedRequestOptions? o = null) => throw No();
    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder(string n, ChangeFeedStreamHandler h) => throw No();
    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder<T>(string n, ChangeFeedHandler<T> h) => throw No();
    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilder<T>(string n, ChangesHandler<T> h) => throw No();
    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilderWithManualCheckpoint(string n, ChangeFeedStreamHandlerWithManualCheckpoint h) => throw No();
    public override ChangeFeedProcessorBuilder GetChangeFeedProcessorBuilderWithManualCheckpoint<T>(string n, ChangeFeedHandlerWithManualCheckpoint<T> h) => throw No();
}

using Smx.Domain.Records;

namespace Smx.Domain;

/// The pre-project interview scratchpad's store. A SEPARATE port from IRecordStore, over a separate
/// Cosmos container, because a session is not a record: it has no projectId, it is not on the dispatch
/// bus, and it expires. Folding it into IRecordStore would put a document with no partition key value
/// into the container the change feed reads.
public interface IIntakeSessionStore
{
    Task<IntakeSessionDoc?> GetAsync(string sessionId, CancellationToken ct = default);
    Task UpsertAsync(IntakeSessionDoc doc, CancellationToken ct = default);
}

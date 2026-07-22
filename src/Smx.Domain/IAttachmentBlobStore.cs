namespace Smx.Domain;

/// Interview attachment bytes and their extracted text, in the existing ADLS `bronze` filesystem.
///
/// Both apps use this, asymmetrically: the BACKEND writes (upload + extraction) and the ORCHESTRATOR
/// only ever calls GetTextAsync (the read_attachment tool). Blobs are written once and never
/// re-parented — IntakeBriefDoc references the session-scoped path even after the project exists,
/// because copying them on creation would add a partial-failure mode (project created, half the blobs
/// moved) for no benefit: the session DOCUMENT is disposable, the bytes never were (design §5.3).
public interface IAttachmentBlobStore
{
    Task PutAsync(string path, Stream content, string contentType, CancellationToken ct = default);
    Task PutTextAsync(string path, string text, CancellationToken ct = default);
    Task<string?> GetTextAsync(string path, CancellationToken ct = default);
}

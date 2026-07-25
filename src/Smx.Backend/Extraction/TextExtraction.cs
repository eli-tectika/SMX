using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

/// Picks the extractor for a file and guarantees an ExtractionResult comes back — never an exception.
///
/// That guarantee is the point. The bytes are already in blob storage by the time this runs; letting a
/// malformed file throw would fail the request and lose the attachment the operator just added, while
/// leaving an orphaned blob behind. A recorded `failed` keeps the file visible and turns it into a
/// question the agent asks (design §5.2).
public sealed class TextExtraction(IReadOnlyList<ITextExtractor> extractors)
{
    public async Task<ExtractionResult> ExtractAsync(
        string filename, string contentType, Stream input, CancellationToken ct)
    {
        var extension = AttachmentPaths.Extension(filename);

        var extractor = extractors.FirstOrDefault(e => e.CanHandle(contentType ?? "", extension));
        if (extractor is null)
            return ExtractionResult.Unsupported(
                extension.Length > 0 ? $"{extension} files" : $"files like '{filename}'");

        try
        {
            return await extractor.ExtractAsync(input, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return ExtractionResult.Failed($"could not read this file ({e.Message})");
        }
    }
}

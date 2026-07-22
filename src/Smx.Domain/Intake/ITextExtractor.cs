using Smx.Domain.Records;

namespace Smx.Domain.Intake;

/// <param name="Text">The extracted text. Empty unless Status is `extracted`.</param>
/// <param name="Status">One of <see cref="AttachmentStatus"/>.</param>
/// <param name="Error">Why it could not be read. Shown to the OPERATOR and put in the AGENT's context,
/// so it must read as a sentence a person can act on, not a stack trace.</param>
public sealed record ExtractionResult(string Text, string Status, string? Error = null)
{
    public static ExtractionResult Extracted(string text) =>
        new(text, AttachmentStatus.Extracted);

    public static ExtractionResult Unsupported(string what) =>
        new("", AttachmentStatus.Unsupported, $"there is no extractor for {what}");

    public static ExtractionResult Failed(string why) =>
        new("", AttachmentStatus.Failed, why);
}

/// Turns one uploaded file into text, in CODE, before any agent sees it.
///
/// Deliberately model-agnostic: relying on a model's native document or vision input would couple a
/// data-ingestion decision to the choice of model, and the model is not fixed (design §5.1). OCR,
/// image and scanned-PDF extractors arrive later behind this same interface — no schema change, no
/// agent change.
public interface ITextExtractor
{
    /// `extension` is lowercased and includes the dot (".pdf"). `contentType` is whatever the browser
    /// claimed and is ADVISORY ONLY — browsers send `application/octet-stream` for perfectly ordinary
    /// files, so an implementation that requires a content-type match will refuse real uploads.
    bool CanHandle(string contentType, string extension);

    Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct);
}

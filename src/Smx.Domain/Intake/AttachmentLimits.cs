namespace Smx.Domain.Intake;

/// The numbers, in one place, with the reason each one exists. They are enforced SERVER-SIDE: a browser
/// check is a courtesy, not a limit.
public static class AttachmentLimits
{
    /// 25 MB. Big enough for a scanned 100-page questionnaire, small enough that extraction stays
    /// synchronous inside one HTTP request without a queue.
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// 20 files per session. A cap on the whole interview, not per upload — without it a session
    /// document grows unbounded and eventually exceeds Cosmos's 2 MB item limit on its metadata alone.
    public const int MaxFilesPerSession = 20;

    /// Extracted text is truncated here before it is stored. A .docx or .xlsx is a ZIP archive and can
    /// expand enormously from a small upload; without a ceiling a 25 MB workbook can produce hundreds of
    /// megabytes of text and take the backend's memory with it.
    public const int MaxExtractedChars = 400_000;

    /// One `read_attachment` page. Sized so a long document enters the prompt deliberately, a page at a
    /// time, rather than all at once — the agent asks for more if it needs more.
    public const int PageChars = 6_000;
}

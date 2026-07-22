using System.Text;
using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

/// Everything that is already text. `.csv`/`.tsv` are here rather than in a delimiter-aware extractor
/// on purpose: parsing them into cells and re-serialising would add nothing the agent cannot read, and
/// would risk mangling quoted fields that contain commas or newlines.
public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".json", ".xml", ".csv", ".tsv" };

    public bool CanHandle(string contentType, string extension) => Extensions.Contains(extension);

    public async Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct)
    {
        // A CSV saved by Excel carries a UTF-8 BOM, which left in place becomes an invisible prefix on
        // the first header and quietly breaks every comparison against it.
        //
        // What strips it is passing `Encoding.UTF8` — StreamReader always skips a leading BOM that
        // matches the encoding it was GIVEN. Do not "optimise" that to `new UTF8Encoding(false)`: the
        // BOM would then survive, and PlainText_StripsAUtf8Bom is the test that would catch it.
        // `detectEncodingFromByteOrderMarks` does something narrower — it lets the reader SWITCH to
        // UTF-16/UTF-32 when the file opens with one of those BOMs instead. It is true because an
        // operator's export may well be UTF-16, but it is NOT what makes the UTF-8 case work, and no
        // test here pins it (verified by flipping it to false: the BOM test still passes).
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = await reader.ReadToEndAsync(ct);
        return ExtractionResult.Extracted(Truncate(text));
    }

    /// Shared by every extractor in this folder. Truncation is ANNOUNCED, never silent: a cut-off
    /// document that looks complete is a document the agent will confidently reason about the end of.
    internal static string Truncate(string text) =>
        text.Length <= AttachmentLimits.MaxExtractedChars
            ? text
            : text[..AttachmentLimits.MaxExtractedChars] +
              $"\n\n[... truncated at {AttachmentLimits.MaxExtractedChars} characters — this file is longer " +
              "than shown. Ask the operator about anything you need from the rest of it.]";
}

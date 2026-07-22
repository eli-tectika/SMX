using System.Text;
using ClosedXML.Excel;
using Smx.Domain.Intake;

namespace Smx.Backend.Extraction;

/// ClosedXML is already a dependency here (the compatibility-matrix export) and in
/// tools/Smx.ReferenceData.Transform, so the workbook reader is one this codebase already trusts.
public sealed class XlsxExtractor : ITextExtractor
{
    public bool CanHandle(string contentType, string extension) =>
        string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase);

    public async Task<ExtractionResult> ExtractAsync(Stream input, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream();
            await input.CopyToAsync(ms, ct);
            ms.Position = 0;

            using var wb = new XLWorkbook(ms);
            var sb = new StringBuilder();
            foreach (var sheet in wb.Worksheets)
            {
                ct.ThrowIfCancellationRequested();
                // The sheet name is data: a workbook with a tab per component is a component breakdown,
                // and a bare cell value is ambiguous without knowing which sheet it came from.
                sb.AppendLine($"# sheet: {sheet.Name}");

                // RangeUsed() rather than the whole sheet: an empty .xlsx addresses ~1M rows, and
                // iterating them produces a gigabyte of tabs before the truncation ceiling is reached.
                if (sheet.RangeUsed() is not { } used) { sb.AppendLine("(empty)"); continue; }

                foreach (var row in used.Rows())
                {
                    // Tab-separated: it round-trips into the prompt as a readable grid, and it is what
                    // the operator would have pasted anyway.
                    sb.AppendLine(string.Join('\t', row.Cells().Select(c => c.GetFormattedString())));
                    if (sb.Length > AttachmentLimits.MaxExtractedChars) break;
                }
            }

            return ExtractionResult.Extracted(PlainTextExtractor.Truncate(sb.ToString()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return ExtractionResult.Failed($"could not read this workbook ({e.Message})");
        }
    }
}

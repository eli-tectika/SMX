namespace Smx.Domain.Xrf;

/// One row of the physicist's result — parsed from a file, or typed by hand into the manual grid.
///
/// ONE type for both on purpose. A hand-typed number and a parsed number reach the record through the
/// same validator, so there is no easier door: the manual grid is a fallback for unparseable files,
/// not a way around the checks.
///
/// `Problems` is per-row and never fatal on its own — the screen shows every row it read and marks the
/// bad ones, because an operator who can see which three rows failed can fix three cells. One that is
/// told only "the file is invalid" re-exports the whole thing and guesses.
public sealed record XrfProposal(
    int RowNumber,
    string Component,
    string Element,
    string Line,
    string Status,
    string? SignalNote,
    double? BackgroundLevel,
    string? BackgroundUnit,
    string? DeviceModel,
    double? DeviceLod,
    string? DeviceLodUnit,
    IReadOnlyList<string> Problems)
{
    /// V and L are pool statuses — the usable and the conditional. X is a measurement of an element
    /// that is present in the background, which is recorded but is NOT a pool entry.
    public const string Usable = "V";
    public const string Conditional = "L";
    public const string Present = "X";

    public static readonly IReadOnlyList<string> Statuses = [Usable, Conditional, Present];
}

public sealed record XrfParseResult(
    IReadOnlyList<XrfProposal> Proposals,
    /// Problems with the FILE rather than with a row: a missing column, an empty sheet. A sheet
    /// problem means no proposal can be trusted, so the screen must not offer to confirm any of them.
    IReadOnlyList<string> SheetProblems);

using System.Globalization;
using Smx.Domain.Records;

namespace Smx.Domain.Xrf;

/// What a confirmation puts into the record.
public sealed record XrfBuild(
    IReadOnlyList<ElementPool> ElementPools,
    IReadOnlyList<MeasuredBackground> MeasuredBackgrounds,
    XrfDevice? Device);

/// The single door from proposals into `ConstraintsDoc`.
///
/// Every refusal here is a refusal `DetectionFloor` would otherwise make LATER — at dosing, days
/// after the physicist's file was closed and the operator has stopped thinking about it. Duplicate
/// measurements, non-ppm units, a non-positive LOD: all of them are cheap to fix now and expensive to
/// diagnose then. So they are made here, in the operator's words, against the row they came from.
///
/// Returns (build, null) or (null, reason). NEVER throws: the caller is an HTTP handler and the reason
/// is what the operator reads.
public static class XrfConfirmation
{
    public static (XrfBuild? Build, string? Error) Build(
        IReadOnlyList<XrfProposal> proposals, IReadOnlyList<string> componentIds)
    {
        if (proposals.Count == 0)
            return (null, "there is nothing to confirm. Confirming an empty result would record that " +
                          "the physicist found no usable element, which is a very different claim from " +
                          "nobody having entered anything.");

        // A row the parser could not read must not enter by an unchecked caller. The screen blocks it
        // too, but the screen is a convenience and this is the contract.
        if (proposals.FirstOrDefault(p => p.Problems.Count > 0) is { } broken)
            return (null, $"row {broken.RowNumber} still has an unresolved problem: " +
                          $"{broken.Problems[0]} Fix it in the grid, or remove the row.");

        var declared = componentIds.ToHashSet(StringComparer.Ordinal);
        foreach (var p in proposals)
        {
            if (!declared.Contains(p.Component))
                return (null, $"row {p.RowNumber} measures component '{p.Component}', which this " +
                              $"project does not have (it has {string.Join(", ", componentIds)}). A " +
                              "measurement of a component that does not exist would sit in the record " +
                              "looking like data while the real component silently has none.");

            if (!XrfProposal.Statuses.Contains(p.Status))
                return (null, $"row {p.RowNumber} has status '{p.Status}', which is not one of " +
                              $"{string.Join(" / ", XrfProposal.Statuses)}.");

            // Anti-rubber-stamping (design §4). The same rule CreateProjectRequest.Validate enforced;
            // this is the door that replaced it, so the rule moves with it.
            if (p.Status == XrfProposal.Conditional && string.IsNullOrWhiteSpace(p.SignalNote))
                return (null, $"row {p.RowNumber} ({p.Element} on '{p.Component}') is conditional (L) " +
                              "but carries no signal-character note. A conditional verdict with no " +
                              "stated reason cannot be reviewed, only nodded at.");

            if (p.BackgroundUnit is { } bu && !string.Equals(bu, XrfTemplate.Ppm, StringComparison.OrdinalIgnoreCase))
                return (null, $"row {p.RowNumber} records a background in '{bu}', not ppm.");
            if (p.DeviceLodUnit is { } lu && !string.Equals(lu, XrfTemplate.Ppm, StringComparison.OrdinalIgnoreCase))
                return (null, $"row {p.RowNumber} records a LOD in '{lu}', not ppm.");

            if (p.BackgroundLevel is { } lvl && (!double.IsFinite(lvl) || lvl < 0))
                return (null, $"row {p.RowNumber} has a background of {Num(lvl)}, which is not a " +
                              "non-negative finite number.");
            if (p.DeviceLod is { } lod && (!double.IsFinite(lod) || lod <= 0))
                return (null, $"row {p.RowNumber} has a LOD of {Num(lod)}. It must be positive: a " +
                              "non-positive LOD would put the detection floor at or below the " +
                              "background the marker has to be seen against.");
        }

        // Duplicates refuse rather than resolve. DetectionFloor's reasoning, applied one step earlier:
        // silently taking one of two measurements risks taking the stale, LOWER one, and low is the
        // direction that ships an unreadable marker.
        var dupe = proposals
            .Where(p => p.BackgroundLevel is not null)
            .GroupBy(p => (p.Component, p.Element))
            .FirstOrDefault(g => g.Count() > 1);
        if (dupe is not null)
            return (null, $"{dupe.Key.Element} on '{dupe.Key.Component}' is measured more than once " +
                          $"(rows {string.Join(", ", dupe.Select(p => p.RowNumber))}). Keep only the " +
                          "current measurement — taking one of two silently risks taking the stale one.");

        var models = proposals.Select(p => p.DeviceModel)
            .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.Ordinal).ToList();
        if (models.Count > 1)
            return (null, $"the rows name more than one XRF device ({string.Join(", ", models)}). A " +
                          "project targets ONE deployment device — the floor is computed against the " +
                          "unit that must read the marker in the field.");

        var lodConflict = proposals
            .Where(p => p.DeviceLod is not null)
            .GroupBy(p => p.Element, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Select(p => p.DeviceLod!.Value).Distinct().Count() > 1);
        if (lodConflict is not null)
            return (null, $"{lodConflict.Key} is given more than one LOD " +
                          $"({string.Join(", ", lodConflict.Select(p => Num(p.DeviceLod!.Value)).Distinct())}). " +
                          "One element on one device has one LOD; keep the current one.");

        var pools = proposals
            .Where(p => p.Status is XrfProposal.Usable or XrfProposal.Conditional)
            .Select(p => new ElementPool(p.Component, p.Element, p.Line, p.Status, p.SignalNote))
            .ToList();

        var backgrounds = proposals
            .Where(p => p.BackgroundLevel is not null)
            .Select(p => new MeasuredBackground(
                p.Component, p.Element, p.BackgroundLevel!.Value, XrfTemplate.Ppm))
            .ToList();

        // The device is OPTIONAL, and deliberately so: the pools are what release Discovery, and the
        // device only matters at Dosing. Requiring it here would hold the whole pipeline for a number
        // the next stage does not read.
        XrfDevice? device = null;
        if (models.Count == 1)
        {
            var lods = proposals
                .Where(p => p.DeviceLod is not null)
                .GroupBy(p => p.Element, StringComparer.Ordinal)
                .Select(g => new DeviceLod(g.Key, g.First().DeviceLod!.Value, XrfTemplate.Ppm))
                .ToList();
            device = new XrfDevice(models[0]!, lods);
        }

        return (new XrfBuild(pools, backgrounds, device), null);
    }

    /// InvariantCulture, for the same reason DetectionFloor uses it: a number in a message the
    /// operator reads must mean one thing everywhere.
    private static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}

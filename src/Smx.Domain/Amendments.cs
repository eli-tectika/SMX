using Smx.Domain.Records;

namespace Smx.Domain;

/// Applying an operator's amendment to the CONSTRAINTS the agents actually read.
///
/// An amendment changes a REQUIREMENT — a fact about the customer's product — and requirements live in two
/// places: the project payload (verbatim, as intake received it) and <see cref="ConstraintsDoc"/> (what
/// every downstream agent is handed). Patching only the payload would amend a document nothing reads and
/// leave the analysis running on the old requirement, which is the quietest possible way to be wrong.
///
/// The amendable fields map 1:1 onto <see cref="ComponentSpec"/> and
/// <see cref="ConstraintsDoc.ClientRestrictedList"/>, which is not a coincidence — it is why
/// <see cref="IntakeAnswers"/>'s allow-list is exactly this set. The physicist's measured data
/// (element pools, measured backgrounds, device LODs) is absent from it by construction and stays that way
/// here: this function cannot express a write to it.
public static class Amendments
{
    /// Returns the patched constraints, or an error string. NEVER throws — the caller is an endpoint and,
    /// eventually, an LLM tool dispatcher, and the error text is the only thing that teaches either to
    /// correct itself.
    public static (ConstraintsDoc? Patched, string? Error) Apply(
        ConstraintsDoc constraints, string componentId, string field, string value)
    {
        var leaf = (field ?? "").Split('.')[^1];

        if (leaf == "clientRestrictedList")
        {
            var entries = ParseList(value);
            if (entries.Count == 0)
                return (null, "a client restricted list of nothing is not an amendment. To clear the list, " +
                              "say so explicitly and the operator's reason will record why.");
            constraints.ClientRestrictedList = entries;
            return (constraints, null);
        }

        var index = constraints.Components.FindIndex(c =>
            string.Equals(c.Id, componentId, StringComparison.Ordinal));
        if (index < 0)
            return (null, $"no component '{componentId}' on this project. Components are " +
                          $"{string.Join(", ", constraints.Components.Select(c => $"'{c.Id}'"))}.");

        var c0 = constraints.Components[index];
        ComponentSpec patched;

        switch (leaf)
        {
            case "material":
                if (Blank(value)) return (null, BlankValue(leaf));
                patched = c0 with { Material = value.Trim() };
                break;
            case "application":
                if (Blank(value)) return (null, BlankValue(leaf));
                patched = c0 with { Application = value.Trim() };
                break;
            case "objective":
                if (Blank(value)) return (null, BlankValue(leaf));
                patched = c0 with { Objective = value.Trim() };
                break;
            case "markets":
                var markets = ParseList(value);
                // Zero markets EMPTIES that component's regulatory screen — a false-pass mechanism, not an
                // innocuous omission. The same refusal POST /start makes, for the same reason.
                if (markets.Count == 0)
                    return (null, "a component with no target markets has an EMPTY regulatory screen, which " +
                                  "passes everything. Name the markets, or reject the component.");
                patched = c0 with { Markets = markets };
                break;
            case "batchMassKg":
                if (!double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var kg) || kg <= 0)
                    return (null, "batchMassKg is the batch MASS in KILOGRAMS as a positive number (ppm is " +
                                  "mg/kg). If the operator gave a volume, ask for the mass — never guess a " +
                                  "density.");
                patched = c0 with { BatchMassKg = kg };
                break;
            default:
                return (null, $"'{field}' is not an amendable requirement. Amendable fields are " +
                              $"{IntakeAnswers.AllowedFields}. The element pools, the measured background " +
                              $"and the device LODs are the physicist's measured data and are never " +
                              $"amendable — if they are wrong, the physicist must re-measure.");
        }

        constraints.Components[index] = patched;
        return (constraints, null);
    }

    /// What the record said before the amendment, for the log. Read BEFORE Apply mutates the document.
    public static string Previous(ConstraintsDoc constraints, string componentId, string field)
    {
        var leaf = (field ?? "").Split('.')[^1];
        if (leaf == "clientRestrictedList") return string.Join(", ", constraints.ClientRestrictedList);

        var c = constraints.Components.FirstOrDefault(x =>
            string.Equals(x.Id, componentId, StringComparison.Ordinal));
        return c is null ? "" : leaf switch
        {
            "material" => c.Material,
            "application" => c.Application,
            "objective" => c.Objective,
            "markets" => string.Join(", ", c.Markets),
            "batchMassKg" => c.BatchMassKg?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            _ => "",
        };
    }

    private static bool Blank(string? v) => string.IsNullOrWhiteSpace(v);

    private static string BlankValue(string field) =>
        $"'{field}' cannot be set to nothing. An amendment states what the requirement now IS.";

    private static List<string> ParseList(string? value) =>
        [.. (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

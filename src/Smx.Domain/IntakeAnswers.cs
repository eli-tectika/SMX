using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smx.Domain;

/// The allowlist for `record_answer` (design §5 — intake gap-fill).
///
/// A chat tool that could write ANY path into the project payload would be a mechanism by which a
/// language model can silently rewrite the physicist's MEASURED data — the ELEMENT POOLS (the measured XRF
/// background, on which every downstream candidate and verdict rests), the MEASURED BACKGROUND LEVELS and
/// the DEVICE LODs (from which the ppm detection floor is computed; a floor that reads low ships a marker
/// nobody can detect in the field). There is no undo and no reason anyone would look.
/// So this is an allowlist, not a denylist: only operator-known product facts are writable, and the
/// physicist's data and the eval seam (providedCandidates) are not writable at all, by construction.
///
/// Patch NEVER throws: the caller is an LLM tool dispatcher, an escaping exception fails the whole stage,
/// and the error text is the only thing that teaches the model to correct itself.
public static class IntakeAnswers
{
    private static readonly string[] ComponentFields = ["material", "application", "objective", "markets", "batchMassKg"];

    /// The COMPONENT fields whose value is a comma-separated list rather than a scalar. Only consulted on the
    /// component branch below — `clientRestrictedList` is a ROOT field and parses its own list before `parts`
    /// is ever destructured, so naming it here would be a dead entry that reads like a second code path.
    private static readonly string[] ListFields = ["markets"];

    /// The COMPONENT fields whose value is a NUMBER. Every other answerable field is text, so it can be stored
    /// verbatim; a number cannot. See ParseNumber for why this is a parse and not a copy.
    private static readonly string[] NumberFields = ["batchMassKg"];

    private static string AllowedFields =>
        $"components.{{componentId}}.{{{string.Join("|", ComponentFields)}}}, or clientRestrictedList";

    public static (JsonElement? Patched, string? Error) Patch(JsonElement payload, string field, string value)
    {
        field = (field ?? "").Trim();

        // The protected inputs are refused BY NAME and case-INsensitively, so the model learns the boundary
        // instead of retrying blindly and cannot mistake a mis-cased path for a mere typo. The ALLOW path
        // below stays exact-match — an allowlist is a list of exactly the strings you meant.
        if (field.StartsWith("elementPools", StringComparison.OrdinalIgnoreCase))
            return (null, "element pools are the physicist's measured XRF background and cannot be changed " +
                          "through chat. If they are wrong, the physicist must re-measure and the operator " +
                          "must re-enter them at intake.");
        if (field.StartsWith("providedCandidates", StringComparison.OrdinalIgnoreCase))
            return (null, "provided candidates are an input seam and cannot be changed through chat.");
        // Note what this refusal is and is not. It does not CLOSE a hole: the allowlist below already accepts
        // only `components.{id}.{field}` and `clientRestrictedList`, so these paths were refused already —
        // with a generic "not an answerable field". It exists because the generic message teaches the model
        // nothing, and a model that reads "not answerable" retries with a rephrasing. The boundary here is not
        // a typo to be worked around: it is measured data. Say so.
        if (field.StartsWith("measuredBackground", StringComparison.OrdinalIgnoreCase)
         || field.StartsWith("device", StringComparison.OrdinalIgnoreCase))
            return (null, "the measured background and the device LODs are the physicist's measured data — " +
                          "the ppm detection floor is computed from them, and a floor that reads low ships a " +
                          "marker nobody can detect. They cannot be changed through chat.");

        if (Root(payload) is not { } node)
            return (null, "this project's intake payload is not a JSON object, so no answer can be recorded " +
                          "into it. Tell the operator; do not retry.");

        var parts = field.Split('.');

        if (field == "clientRestrictedList")
        {
            // An absent key means "no restrictions recorded yet" (a project that predates the field), so
            // filling the gap is exactly this tool's job — create it.
            if (ParseList(value) is not { Length: > 0 } entries) return (null, BlankValue(field));
            node[field] = ToJsonArray(entries);
            return Rebuild(node);
        }

        if (parts is ["components", var componentId, var componentField])
        {
            if (!ComponentFields.Contains(componentField, StringComparer.Ordinal))
                return (null, $"'{componentField}' is not an answerable field. You may set only: " +
                              $"{string.Join(", ", ComponentFields)} on a component, or clientRestrictedList.");

            // `as JsonObject` throughout, never `AsObject()`/`AsArray()`: a payload whose shape we did not
            // expect must produce an error the model can act on, never an exception.
            var components = node["components"] as JsonArray;
            var component = components?
                .OfType<JsonObject>()
                .FirstOrDefault(c => c["id"] is JsonValue id && id.TryGetValue<string>(out var s) && s == componentId);
            if (component is null)
                return (null, $"there is no component '{componentId}' in this project. Its components are: " +
                              $"{KnownComponents(components)}.");

            if (ListFields.Contains(componentField, StringComparer.Ordinal))
            {
                if (ParseList(value) is not { Length: > 0 } entries) return (null, BlankValue(componentField));
                component[componentField] = ToJsonArray(entries);
            }
            else if (NumberFields.Contains(componentField, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(value)) return (null, BlankValue(componentField));
                if (ParseNumber(value) is not { } number) return (null, NotANumber(componentField, value));
                component[componentField] = JsonValue.Create(number);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(value)) return (null, BlankValue(componentField));
                component[componentField] = value.Trim();
            }
            return Rebuild(node);
        }

        return (null, $"'{field}' is not an answerable field. You may set only: {AllowedFields}.");
    }

    /// A blank answer fills no gap, so it is refused rather than written. The reason is stated PER FIELD:
    /// these strings exist to teach a model to self-correct, and a true rationale attached to the wrong
    /// field is a worse signal than a plain refusal.
    private static string BlankValue(string field) => field switch
    {
        // The one blank that is not merely useless but unsafe: zero target markets empties the component's
        // regulatory screen, which is a false-pass mechanism. Say so out loud.
        "markets" => "'markets' needs at least one target market. Recording none would leave this component " +
                     "with ZERO target markets, which empties its regulatory screen — ask the operator which " +
                     "markets the product ships to.",
        "clientRestrictedList" => "'clientRestrictedList' needs at least one entry (comma-separated). If the " +
                     "client imposes no restrictions, leave the list as it is rather than recording a blank.",
        _ => $"'{field}' needs a real value — an empty answer would blank this intake input rather than fill " +
             "it. Ask the operator for the value.",
    };

    /// A numeric answer is PARSED, not copied. Every other answerable field is text, so whatever the model
    /// says can be stored verbatim; `batchMassKg` is read back as a `double?` (ComponentSpec), and text that
    /// is not a number poisons the payload: IntakeAgent.Validate deserializes the payload on every run, so
    /// "a lot" throws a JsonException there — which the runner reports as the AGENT's reply being malformed,
    /// sending it to retry a reply that was fine while intake dies on a payload it has no tool to repair.
    ///
    /// InvariantCulture, and NO thousands separators: "1,000" is one thousand to one reader and 1.0 to
    /// another, and silently picking one mis-doses the batch by 1000×. It is refused and the operator is
    /// asked for a plain number.
    ///
    /// The finiteness check is not pedantry: "NaN" and "Infinity" PARSE as doubles, and STJ then refuses to
    /// WRITE them (they are not valid JSON), so Rebuild would throw — and Patch must never throw.
    private static double? ParseNumber(string value) =>
        double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
        && double.IsFinite(d) ? d : null;

    private static string NotANumber(string field, string value) =>
        $"'{field}' must be a plain number of KILOGRAMS (e.g. 250 or 12.5) — '{value.Trim()}' is not one. " +
        "Do not include units, thousands separators or words. ppm is mg/kg, so the batch is recorded as MASS: " +
        "if the operator gave you a VOLUME, ask them for the mass (volume × the material's density). Never " +
        "guess a density, and never treat litres as kilograms.";

    private static string KnownComponents(JsonArray? components)
    {
        var ids = components?.OfType<JsonObject>()
            .Select(c => (c["id"] as JsonValue)?.TryGetValue<string>(out var s) == true ? s : null)
            .Where(s => s is not null).ToArray() ?? [];
        return ids.Length == 0 ? "(none)" : string.Join(", ", ids);
    }

    private static string[] ParseList(string? value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonArray ToJsonArray(string[] entries) => new(entries.Select(s => (JsonNode)s!).ToArray());

    /// The caller's JsonElement is never mutated: we work on a parsed copy and hand back a fresh element.
    private static (JsonElement?, string?) Rebuild(JsonObject node) =>
        (JsonSerializer.Deserialize<JsonElement>(node.ToJsonString()), null);

    /// The payload as an object, or null for anything else — a default/Undefined element (whose ValueKind
    /// check catches it before GetRawText throws), a JSON `null`, an array, a scalar.
    ///
    /// The live exception here is ObjectDisposedException, NOT JsonException: a JsonElement is a view over a
    /// JsonDocument, and once that document is disposed every read — GetRawText included — throws
    /// ObjectDisposedException. ProjectDoc.Create defends against this by Clone()ing its payload; a payload
    /// reaching us from anywhere else may not have been. JsonException, by contrast, is effectively dead:
    /// ValueKind is already Object, so GetRawText hands JsonNode.Parse text that STJ itself just parsed. It
    /// is caught anyway because "never throws" must not rest on that reasoning staying true.
    private static JsonObject? Root(JsonElement payload)
    {
        try
        {
            if (payload.ValueKind is not JsonValueKind.Object) return null;
            return JsonNode.Parse(payload.GetRawText()) as JsonObject;
        }
        catch (Exception e) when (e is ObjectDisposedException or JsonException) { return null; }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Smx.Domain;

/// The allowlist for `record_answer` (design §5 — intake gap-fill).
///
/// A chat tool that could write ANY path into the project payload would be a mechanism by which a
/// language model can silently rewrite the ELEMENT POOLS — the physicist's measured XRF background, on
/// which every downstream candidate and verdict rests. There is no undo and no reason anyone would look.
/// So this is an allowlist, not a denylist: only operator-known product facts are writable, and the
/// physicist's data and the eval seam (providedCandidates) are not writable at all, by construction.
///
/// Patch NEVER throws: the caller is an LLM tool dispatcher, an escaping exception fails the whole stage,
/// and the error text is the only thing that teaches the model to correct itself.
public static class IntakeAnswers
{
    private static readonly string[] ComponentFields = ["material", "application", "objective", "markets"];

    /// The COMPONENT fields whose value is a comma-separated list rather than a scalar. Only consulted on the
    /// component branch below — `clientRestrictedList` is a ROOT field and parses its own list before `parts`
    /// is ever destructured, so naming it here would be a dead entry that reads like a second code path.
    private static readonly string[] ListFields = ["markets"];

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

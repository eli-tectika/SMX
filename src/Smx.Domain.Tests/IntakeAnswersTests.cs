using System.Text.Json;
using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class IntakeAnswersTests
{
    private static JsonElement Payload() => JsonSerializer.SerializeToElement(new
    {
        components = new[] { new { id = "bottle", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "" } },
        elementPools = new[] { new { component = "bottle", element = "Zr", line = "Ka", status = "V", signalNote = (string?)null } },
        providedCandidates = Array.Empty<object>(),
        clientRestrictedList = Array.Empty<string>(),
    }, Json.Options);

    [Fact]
    public void Patch_FillsAnAllowedComponentField()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "components.bottle.objective", "brand protection");
        Assert.Null(error);
        Assert.Equal("brand protection",
            patched!.Value.GetProperty("components")[0].GetProperty("objective").GetString());
    }

    [Fact]
    public void Patch_REFUSES_ToTouchTheElementPools()
    {
        // THE POINT OF THIS FILE. Element pools are the PHYSICIST'S MEASURED XRF BACKGROUND. Every
        // downstream verdict rests on them. A chat tool that can write them is a mechanism by which a
        // language model can silently alter measured data, and nobody would have a reason to look.
        var (patched, error) = IntakeAnswers.Patch(Payload(), "elementPools.0.status", "V");
        Assert.Null(patched);
        Assert.Contains("element pools", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("physicist", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patch_REFUSES_ToTouchProvidedCandidates()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "providedCandidates.0.cas", "7440-67-7");
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("measuredBackground.0.level")]
    [InlineData("device.lods.0.lodPpm")]
    [InlineData("device.model")]
    public void Patch_REFUSES_ToTouchTheMeasuredPhysicsInputs(string field)
    {
        // Same law as the element pools, and for the same reason: these are MEASURED values, and the ppm
        // detection floor is computed from them. A floor that reads low ships a marker nobody can detect in
        // the field, and there is no downstream check that catches it. A chat tool that can write them is a
        // mechanism by which a language model can silently alter measured data.
        //
        // NOTE the assertions. `Contains("measured")` alone would be VACUOUS: the generic off-allowlist
        // message ECHOES the field name back ("'measuredBackground.0.level' is not an answerable field"), so
        // that assertion passes with the refusal deleted. What must hold is that the model is told WHAT these
        // inputs are and WHY they are closed — sentences only the named refusal produces.
        var (patched, error) = IntakeAnswers.Patch(Payload(), field, "0.001");
        Assert.Null(patched);
        Assert.Contains("measured data", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("detection floor", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be changed through chat", error!);
    }

    [Fact]
    public void Patch_AllowsBatchMassKg_BecauseItIsAnOperatorKnownProductFact()
    {
        // Batch mass is not a measurement — it is a production fact the operator knows and may well supply
        // in conversation. It IS answerable. (Its VALIDITY is enforced by OrderAmount, not here.)
        var (patched, error) = IntakeAnswers.Patch(Payload(), "components.bottle.batchMassKg", "250");
        Assert.Null(error);
        Assert.Equal(250.0, patched!.Value.GetProperty("components")[0].GetProperty("batchMassKg").GetDouble());
    }

    [Fact]
    public void Patch_WritesBatchMassKg_AsANumberTheTypedRecordCanRead()
    {
        // The seam this field crosses: chat writes into the raw payload, and IntakeAgent.Validate then
        // deserializes that same payload into typed components (double? BatchMassKg). So the written value
        // must be READABLE as a number, which is why Patch parses it instead of storing the raw text.
        var (patched, _) = IntakeAnswers.Patch(Payload(), "components.bottle.batchMassKg", "250");
        var component = JsonSerializer.Deserialize<List<ComponentSpec>>(
            patched!.Value.GetProperty("components").GetRawText(), Json.Options)!.Single();
        Assert.Equal(250.0, component.BatchMassKg);
    }

    [Theory]
    [InlineData("a lot")]
    [InlineData("250 kg")]      // the unit is in the FIELD NAME; a value carrying its own unit is not a number
    [InlineData("1,000")]       // ambiguous: one thousand, or 1.0 to a decimal-comma reader?
    [InlineData("NaN")]         // parses as a double — and is not writable as JSON, so it would THROW
    [InlineData("Infinity")]
    public void Patch_RefusesABatchMassThatIsNotAPlainFiniteNumber(string value)
    {
        // batchMassKg is the first NUMERIC answerable field, and an unparseable one is not merely useless:
        // stored as text it deserializes into `double? BatchMassKg` only by luck. "a lot" throws
        // JsonException from inside IntakeAgent.Validate — which reports it as the AGENT's reply being
        // malformed, so the agent retries a reply that was fine and intake dies on a payload it cannot fix.
        // NaN/Infinity are worse: they parse, and then STJ refuses to WRITE them, so Patch itself would throw.
        var (patched, error) = IntakeAnswers.Patch(Payload(), "components.bottle.batchMassKg", value);
        Assert.Null(patched);
        Assert.Contains("kilograms", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Patch_RejectsAnUnknownField_WithAMessageThatNamesWhatIsAllowed()
    {
        // The error is read by a model, so it must teach: an unhelpful "invalid field" just gets retried.
        var (_, error) = IntakeAnswers.Patch(Payload(), "components.bottle.colour", "blue");
        Assert.Contains("objective", error!);   // names the allowed fields
    }

    [Fact]
    public void Patch_RejectsAnUnknownComponent()
    {
        var (_, error) = IntakeAnswers.Patch(Payload(), "components.lid.objective", "x");
        Assert.Contains("lid", error!);
    }

    [Fact]
    public void Patch_FillsTheClientRestrictedList_FromACommaSeparatedValue()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "clientRestrictedList", "Pb, Cd");
        Assert.Null(error);
        Assert.Equal(["Pb", "Cd"],
            patched!.Value.GetProperty("clientRestrictedList").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Patch_FillsMarkets_FromACommaSeparatedValue()
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), "components.bottle.markets", "EU, US");
        Assert.Null(error);
        Assert.Equal(["EU", "US"],
            patched!.Value.GetProperty("components")[0].GetProperty("markets").EnumerateArray().Select(e => e.GetString()));
    }

    // ---- the allowlist under attack -------------------------------------------------------------

    [Theory]
    [InlineData("components.bottle.objective.something")] // a 4-part path must not partially match
    [InlineData("components.bottle")]                     // too short
    [InlineData("components")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("derivedScope.0.listId")]
    [InlineData("COMPONENTS.bottle.objective")]           // the allow path is exact-match, by design
    [InlineData("components.bottle.Objective")]           // ...so a mis-cased field cannot write a stray key
    [InlineData("clientrestrictedlist")]
    public void Patch_RefusesAnythingOffTheAllowlist(string field)
    {
        var (patched, error) = IntakeAnswers.Patch(Payload(), field, "x");
        Assert.Null(patched);
        Assert.NotNull(error);
        Assert.Contains("clientRestrictedList", error);   // still teaches the shape it will accept
    }

    [Theory]
    [InlineData("ElementPools.0.status")]
    [InlineData("elementpools")]
    [InlineData("PROVIDEDCANDIDATES.0.cas")]
    [InlineData("MeasuredBackground.0.level")]
    [InlineData("DEVICE.model")]
    public void Patch_RefusalOfTheProtectedInputsIsCaseInsensitive(string field)
    {
        // The ALLOW path is exact-match (an allowlist is a list of exactly the strings you meant), but the
        // REFUSAL must not be dodgeable by casing — a mis-cased protected path has to hit its own named
        // message, not a generic one, so the model learns the boundary is real.
        var (patched, error) = IntakeAnswers.Patch(Payload(), field, "V");
        Assert.Null(patched);
        Assert.Contains("cannot be changed through chat", error);
    }

    [Fact]
    public void Patch_CannotAddressAComponentWhoseIdContainsADot()
    {
        // A dotted id would make the path ambiguous, so it is simply unaddressable — refused, never guessed.
        var payload = JsonSerializer.SerializeToElement(new
        {
            components = new[] { new { id = "bottle.inner", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "" } },
        }, Json.Options);
        var (patched, error) = IntakeAnswers.Patch(payload, "components.bottle.inner.objective", "x");
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Fact]
    public void Patch_TreatsAWeirdComponentIdAsJustAnId()
    {
        // JsonObject has no prototype, so "__proto__" is an ordinary key; assert it is addressed by id
        // equality (as data) and pollutes nothing.
        var payload = JsonSerializer.SerializeToElement(new
        {
            components = new[] { new { id = "__proto__", material = "HDPE", application = "packaging", markets = new[] { "EU" }, objective = "" } },
        }, Json.Options);
        var (patched, error) = IntakeAnswers.Patch(payload, "components.__proto__.objective", "brand protection");
        Assert.Null(error);
        Assert.Equal("brand protection", patched!.Value.GetProperty("components")[0].GetProperty("objective").GetString());
        Assert.Equal(1, patched.Value.GetProperty("components").GetArrayLength());
    }

    [Fact]
    public void Patch_CreatesTheClientRestrictedList_WhenTheProjectPredatesIt()
    {
        // An absent list means "no restrictions recorded yet" — filling the gap is exactly this tool's job.
        var payload = JsonSerializer.SerializeToElement(new { components = Array.Empty<object>() }, Json.Options);
        var (patched, error) = IntakeAnswers.Patch(payload, "clientRestrictedList", "Pb");
        Assert.Null(error);
        Assert.Equal(["Pb"], patched!.Value.GetProperty("clientRestrictedList").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("""{"components":{"bottle":{}}}""")]                      // components is not an array
    [InlineData("""{"components":["bottle"]}""")]                         // elements are not objects
    [InlineData("""{"components":[{"material":"HDPE"}]}""")]              // a component with no id
    [InlineData("""{"components":[{"id":7}]}""")]                         // a non-string id
    [InlineData("""{}""")]                                                // no components at all
    [InlineData("""[]""")]                                                // payload is not even an object
    [InlineData("""null""")]
    public void Patch_NeverThrowsOnAMalformedPayload(string rawPayload)
    {
        // An unhandled exception in an LLM tool call escapes into the dispatcher and fails the whole stage.
        var payload = JsonDocument.Parse(rawPayload).RootElement;
        var (patched, error) = IntakeAnswers.Patch(payload, "components.bottle.objective", "x");
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Fact]
    public void Patch_NeverThrowsOnAnUninitializedPayload()
    {
        var (patched, error) = IntakeAnswers.Patch(default, "clientRestrictedList", "Pb");
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Fact]
    public void Patch_NeverThrowsOnAPayloadWhoseDocumentWasDisposed()
    {
        // A JsonElement is a view over a JsonDocument: once that document is disposed, every read throws
        // ObjectDisposedException — which is NOT a JsonException. Unhandled, it escapes the tool call and
        // fails the whole stage.
        JsonElement payload;
        using (var doc = JsonDocument.Parse("""{"components":[{"id":"bottle","objective":""}]}"""))
            payload = doc.RootElement;   // the document is disposed on the way out; the element outlives it

        var (patched, error) = IntakeAnswers.Patch(payload, "components.bottle.objective", "brand protection");
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("components.bottle.markets", "")]
    [InlineData("components.bottle.markets", " , ")]   // parses to zero markets
    [InlineData("components.bottle.material", "  ")]
    [InlineData("components.bottle.batchMassKg", "  ")]
    [InlineData("clientRestrictedList", "")]
    public void Patch_RefusesToRecordABlankAnswer(string field, string value)
    {
        // A blank answer fills no gap, so it is refused rather than written.
        var (patched, error) = IntakeAnswers.Patch(Payload(), field, value);
        Assert.Null(patched);
        Assert.NotNull(error);
    }

    [Fact]
    public void Patch_SaysWhyABlankMarketsIsUnsafe()
    {
        // The one blank that is not merely useless: zero markets empties the regulatory screen.
        var (_, error) = IntakeAnswers.Patch(Payload(), "components.bottle.markets", "");
        Assert.Contains("regulatory screen", error!);
    }

    [Fact]
    public void Patch_DoesNotAttachTheMarketsRationaleToOtherFields()
    {
        // These strings teach a model to self-correct. Telling it a blank `material` "would empty the
        // regulatory screen" is a true sentence about the wrong field — a worse signal than a plain refusal.
        var (_, error) = IntakeAnswers.Patch(Payload(), "components.bottle.material", " ");
        Assert.Contains("material", error!);
        Assert.DoesNotContain("regulatory screen", error!);
        Assert.DoesNotContain("markets", error!);
    }

    [Fact]
    public void Patch_DoesNotMutateTheCallersPayload()
    {
        var payload = Payload();
        var (patched, _) = IntakeAnswers.Patch(payload, "components.bottle.objective", "brand protection");
        Assert.Equal("", payload.GetProperty("components")[0].GetProperty("objective").GetString());
        Assert.Equal("brand protection", patched!.Value.GetProperty("components")[0].GetProperty("objective").GetString());
    }

    [Fact]
    public void Patch_LeavesTheElementPoolsByteForByteIdentical()
    {
        // The whole file exists for this: a successful, allowed patch must not so much as re-order the
        // physicist's data.
        var payload = Payload();
        var (patched, _) = IntakeAnswers.Patch(payload, "components.bottle.objective", "brand protection");
        Assert.Equal(payload.GetProperty("elementPools").GetRawText(),
                     patched!.Value.GetProperty("elementPools").GetRawText());
    }
}

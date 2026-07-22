using Smx.Domain.Intake;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class IntakeGateTests
{
    private static List<DossierEntry> FullDossier() =>
        IntakeQuestions.All
            .Select(q => new DossierEntry
            {
                QuestionId = q.Id, State = DossierState.Answered,
                Answer = "answered", Provenance = "operator",
            })
            .ToList();

    // ComponentSpec is a positional record (Id, Material, Application, Markets, Objective, BatchMassKg = null)
    // with init-only properties — there is no parameterless constructor and no property setters, so a
    // component is built positionally and mutated (below) via `with`, not property assignment.
    private static List<ComponentSpec> OneGoodComponent() =>
    [
        new("bottle", "PET", "food contact", ["EU"], "brand"),
    ];

    [Fact]
    public void Passes_WhenEverythingIsPresent()
    {
        Assert.Null(IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), FullDossier()));
    }

    [Fact]
    public void UnknownAndNotApplicable_Pass()
    {
        // The operator genuinely may not know. "unknown" is DATA — it travels downstream as a stated
        // gap. What must never pass is a question nobody reached.
        var dossier = FullDossier();
        dossier[0] = dossier[0] with { State = DossierState.Unknown, Answer = "client didn't say" };
        dossier[1] = dossier[1] with { State = DossierState.NotApplicable, Answer = "no reactions" };
        Assert.Null(IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), dossier));
    }

    [Fact]
    public void Refuses_AndNamesEveryUntouchedQuestion()
    {
        var dossier = FullDossier().Where(e => e.QuestionId != "detection-challenges").ToList();
        var error = IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), dossier);
        Assert.NotNull(error);
        // Naming the gap is the whole point — a bare "not ready" teaches the model nothing and it
        // will simply retry the same call.
        Assert.Contains("detection-challenges", error);
    }

    [Fact]
    public void Refuses_AComponentWithNoMarkets_AndSaysWhy()
    {
        var components = OneGoodComponent();
        components[0] = components[0] with { Markets = [] };
        var error = IntakeGate.Check("Acme", "MUFE", "a summary", components, FullDossier());
        Assert.NotNull(error);
        // Same rationale already written into IntakeAnswers.BlankValue: zero markets EMPTIES the
        // component's regulatory screen. The message must say so, not just "markets required".
        Assert.Contains("regulatory screen", error);
    }

    [Theory]
    [InlineData("", "MUFE")]
    [InlineData("Acme", " ")]
    public void Refuses_BlankClientOrProduct(string client, string product) =>
        Assert.NotNull(IntakeGate.Check(client, product, "a summary", OneGoodComponent(), FullDossier()));

    [Fact]
    public void Refuses_WithNoComponents() =>
        Assert.NotNull(IntakeGate.Check("Acme", "MUFE", "a summary", [], FullDossier()));

    [Fact]
    public void Refuses_WithNoSummary() =>
        Assert.NotNull(IntakeGate.Check("Acme", "MUFE", "  ", OneGoodComponent(), FullDossier()));

    [Fact]
    public void Refuses_AgentProposedWithoutConfidence()
    {
        // An agent inference with no confidence is indistinguishable from an operator statement once
        // it is in the record. That is exactly the provenance collapse the dossier exists to prevent.
        var dossier = FullDossier();
        dossier[0] = dossier[0] with
        {
            State = DossierState.AgentProposed, Provenance = "agent", Confidence = null,
        };
        Assert.NotNull(IntakeGate.Check("Acme", "MUFE", "a summary", OneGoodComponent(), dossier));
    }
}

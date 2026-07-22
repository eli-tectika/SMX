using Smx.Domain.Intake;

namespace Smx.Domain.Tests;

public class IntakeQuestionsTests
{
    [Fact]
    public void Ids_AreUniqueAndIdSafe()
    {
        // The id is written into a dossier entry and rendered into the tool description. A duplicate
        // silently merges two questions into one; a stray character breaks the model's ability to
        // name it back to us reliably.
        var ids = IntakeQuestions.All.Select(q => q.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9-]+$", id));
    }

    [Fact]
    public void EveryQuestion_CarriesAPromptAndAWhy()
    {
        // `Why` names the downstream stage that consumes the answer. It is in the AGENT's context, not
        // just the source: it is how the agent judges an answer sufficient rather than merely present.
        Assert.All(IntakeQuestions.All, q =>
        {
            Assert.False(string.IsNullOrWhiteSpace(q.Prompt), $"{q.Id} has no prompt");
            Assert.False(string.IsNullOrWhiteSpace(q.Why), $"{q.Id} has no why");
        });
    }

    [Fact]
    public void Covers_TheStructurallyRequiredQuestions()
    {
        // These are not "nice to have" — the pipeline cannot run without them. A component with no
        // markets has an EMPTY regulatory screen, which is a false-pass mechanism; an objective flips
        // the meaning of a conditional XRF verdict at Background.
        string[] required =
        [
            "component-breakdown", "component-material", "component-application",
            "component-markets", "component-objective", "client-restrictions", "sample-status",
        ];
        Assert.All(required, id => Assert.Contains(IntakeQuestions.All, q => q.Id == id));
    }

    [Fact]
    public void Description_ListsEveryQuestionId()
    {
        // The record_finding tool description is DERIVED from this list, never hand-written beside it.
        // A question the catalogue accepts but the description omits is a question the model never
        // offers to record — it reads the list as exhaustive — so the operator's answer is silently
        // lost. That drift has already happened once in this codebase, to `batchMassKg`.
        foreach (var q in IntakeQuestions.All)
            Assert.Contains(q.Id, IntakeQuestions.Description, StringComparison.Ordinal);
    }
}

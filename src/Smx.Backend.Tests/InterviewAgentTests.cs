using Smx.Domain.Intake;
using Smx.Domain.Records;
using Xunit;

namespace Smx.Backend.Tests;

public class InterviewAgentTests
{
    [Fact]
    public void Instructions_ForbidAssertingFactsAndClaimingToStart()
    {
        // These are load-bearing sentences, not prose. Deleting one silently changes what the agent
        // will do at the least-reviewed point in a project.
        var i = Agents.InterviewAgent.Instructions;
        Assert.Contains("never", i, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot start", i, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unknown", i, StringComparison.OrdinalIgnoreCase);
    }

    /// The question list is INTERPOLATED into the instructions from IntakeQuestions.All, never
    /// hand-listed beside it — the same discipline InterviewTools' record_finding description follows.
    /// A question the agent is never told about is a question it never asks, and the operator's answer
    /// to it is silently lost. Pinning every id here is what makes that regression fail loudly.
    [Fact]
    public void Instructions_NamesEveryCatalogueQuestionId()
    {
        var i = Agents.InterviewAgent.Instructions;
        foreach (var q in IntakeQuestions.All)
            Assert.Contains(q.Id, i, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderThread_PutsTheTurnsInOrderAndLabelsTheSpeakers()
    {
        // The MAF session is fresh every turn and cannot be rehydrated, so this rendering IS the
        // agent's entire memory of the interview.
        var turns = new List<InterviewTurn>
        {
            new() { Role = "operator", Text = "Acme, PET bottles.", CreatedAt = "2026-07-21T10:00:00.0000000Z" },
            new() { Role = "agent",    Text = "How many components?", CreatedAt = "2026-07-21T10:00:05.0000000Z" },
        };
        var rendered = Agents.InterviewAgent.RenderThread(turns);

        Assert.Contains("Acme, PET bottles.", rendered);
        Assert.True(rendered.IndexOf("Acme", StringComparison.Ordinal)
                  < rendered.IndexOf("How many", StringComparison.Ordinal));
    }

    [Fact]
    public void RenderThread_SaysSoWhenTheInterviewHasNotStarted() =>
        Assert.Contains("no messages", Agents.InterviewAgent.RenderThread([]), StringComparison.OrdinalIgnoreCase);
}

using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class MatrixAssemblerTests
{
    private static CandidatesDoc Candidates() => new()
    {
        Id = RecordIds.Candidates("p1"), ProjectId = "p1",
        Substances =
        [
            new("bottle", "Y", "2-EH", "136-25-4", null, null, true, "A", "strong", []),
            new("bottle", "Zr", "neodec", "39049-04-2", null, null, false, "C", "excluded", []), // C: not screened
        ],
    };

    private static VerdictDoc Verdict(string cas, string comp, VerdictStatus s) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1", Cas = cas, ComponentId = comp,
        Element = "Y", Form = "2-EH",
        Dimensions = [new("ElementGate", s, [new Citation("regulatory", "x", "t")], 0.9, "r")],
    };

    [Fact]
    public void Assemble_CarriesTheAgentsProposalAndTheOperatorsSignature_AsSEPARATEFields()
    {
        // The matrix is the ONLY surface the operator reads verdicts on, so a proposal that never reaches a
        // cell is a proposal they cannot confirm — and they go back to authoring every determination by hand,
        // which is the authoring burden the proposal exists to remove.
        //
        // They travel as two fields and they must stay two fields. Collapse them into one and the agent's
        // proposal becomes indistinguishable from the operator's signature: the agent would be signing the
        // regulatory gate, which is the single thing Law 9 exists to prevent.
        var v = Verdict("136-25-4", "bottle", VerdictStatus.Pass);
        v.ProposedDetermination = Determinations.Recommended;
        v.ProposedReason = "clean on all three dimensions";
        // The operator has NOT spoken yet — which is exactly the state the proposal is rendered in.

        var cell = Assert.Single(MatrixAssembler.Assemble(Candidates(), ["bottle"], [v], "t").Cells);

        Assert.Equal(Determinations.Recommended, cell.ProposedDetermination);
        Assert.Equal("clean on all three dimensions", cell.ProposedReason);
        Assert.Null(cell.Determination);            // the signature line is still blank
        Assert.False(cell.EvidenceReviewed);
    }

    [Fact]
    public void Assemble_CarriesTheOperatorsDetermination_OnceTheyHaveSigned()
    {
        var v = Verdict("136-25-4", "bottle", VerdictStatus.Fail);
        v.ProposedDetermination = Determinations.Rejected;
        v.ProposedReason = "listed in REACH Annex XVII";
        v.Determination = Determinations.Recommended;      // the R.E. OVERRODE the agent — that is her right
        v.DeterminationReason = "the listing was superseded in the March amendment";
        v.EvidenceReviewed = true;

        var cell = Assert.Single(MatrixAssembler.Assemble(Candidates(), ["bottle"], [v], "t").Cells);

        // Both survive, and they disagree. The operator's stands; the agent's is still visible beside it, so
        // the override is legible to the next reader rather than silently replacing what was proposed.
        Assert.Equal(Determinations.Rejected, cell.ProposedDetermination);
        Assert.Equal(Determinations.Recommended, cell.Determination);
        Assert.Contains("superseded", cell.DeterminationReason);
        Assert.True(cell.EvidenceReviewed);
    }

    [Fact]
    public void Cells_ExcludesCTier()
    {
        var cells = MatrixAssembler.Cells(Candidates()).ToList();
        Assert.Single(cells);
        Assert.Equal(("136-25-4", "bottle"), cells[0]);
    }

    [Fact]
    public void IsComplete_TrueOnlyWhenEveryNonCCellHasVerdict()
    {
        var c = Candidates();
        Assert.False(MatrixAssembler.IsComplete(c, []));
        Assert.True(MatrixAssembler.IsComplete(c, [Verdict("136-25-4", "bottle", VerdictStatus.Pass)]));
    }

    [Fact]
    public void Assemble_BuildsRowsColumnsCells()
    {
        var c = Candidates();
        var m = MatrixAssembler.Assemble(c, ["bottle"], [Verdict("136-25-4", "bottle", VerdictStatus.Pass)], "t");
        Assert.Equal(["bottle"], m.Columns);
        Assert.Single(m.Rows);
        Assert.Equal("136-25-4", m.Rows[0].Cas);
        Assert.Single(m.Cells);
        Assert.Equal(VerdictStatus.Pass, m.Cells[0].Overall);
    }

    [Fact]
    public void Assemble_ThrowsWhenIncomplete()
    {
        Assert.Throws<InvalidOperationException>(() => MatrixAssembler.Assemble(Candidates(), ["bottle"], [], "t"));
    }
}

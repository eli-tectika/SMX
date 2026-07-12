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

using Smx.Domain;
using Smx.Domain.Records;

namespace Smx.Domain.Tests;

public class MatrixAssemblerTests
{
    private static ConstraintsDoc Constraints() => new()
    {
        Id = RecordIds.Constraints("p1"), ProjectId = "p1",
        Components = [new("bottle", "HDPE", "packaging", ["EU"], "brand"), new("liquid", "aqueous", "cosmetic", ["EU"], "brand")],
        Substances = [new("Zr", "neodecanoate", "cas-zr"), new("Cd", "sulfide", "cas-cd")],
    };

    private static VerdictDoc V(string cas, string comp, VerdictStatus s) => new()
    {
        Id = RecordIds.Verdict("p1", cas, comp), ProjectId = "p1", Cas = cas, ComponentId = comp,
        Element = cas == "cas-zr" ? "Zr" : "Cd", Form = "f",
        Dimensions = [new("ElementGate", s, [new Citation("reg-index", "r", "t")], 0.9, "r")],
    };

    [Fact]
    public void IsComplete_FalseUntilEveryCellHasAVerdict()
    {
        var c = Constraints();
        Assert.False(MatrixAssembler.IsComplete(c, [V("cas-zr", "bottle", VerdictStatus.Pass)]));
        VerdictDoc[] all = [V("cas-zr", "bottle", VerdictStatus.Pass), V("cas-zr", "liquid", VerdictStatus.Pass),
                            V("cas-cd", "bottle", VerdictStatus.Fail), V("cas-cd", "liquid", VerdictStatus.Fail)];
        Assert.True(MatrixAssembler.IsComplete(c, all));
    }

    [Fact]
    public void Assemble_ProducesRowPerSubstance_ColumnPerComponent_CellPerPair()
    {
        var c = Constraints();
        VerdictDoc[] all = [V("cas-zr", "bottle", VerdictStatus.Pass), V("cas-zr", "liquid", VerdictStatus.Conditional),
                            V("cas-cd", "bottle", VerdictStatus.Fail), V("cas-cd", "liquid", VerdictStatus.Fail)];
        var m = MatrixAssembler.Assemble(c, all, "2026-07-08T00:00:00Z");
        Assert.Equal("p1|matrix", m.Id);
        Assert.Equal(2, m.Rows.Count);
        Assert.Equal(["bottle", "liquid"], m.Columns);
        Assert.Equal(4, m.Cells.Count);
        Assert.Equal(VerdictStatus.Conditional, m.Cells.Single(x => x.Cas == "cas-zr" && x.ComponentId == "liquid").Overall);
    }

    [Fact]
    public void Assemble_Throws_WhenIncomplete()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MatrixAssembler.Assemble(Constraints(), [V("cas-zr", "bottle", VerdictStatus.Pass)], "t"));
    }
}

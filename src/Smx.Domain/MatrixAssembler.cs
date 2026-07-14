using Smx.Domain.Records;

namespace Smx.Domain;

public static class MatrixAssembler
{
    /// The screened cells: every non-C candidate paired with its component.
    public static IEnumerable<(string Cas, string ComponentId)> Cells(CandidatesDoc c) =>
        c.Substances.Where(s => s.Tier != "C").Select(s => (s.Cas, s.ComponentId));

    public static bool IsComplete(CandidatesDoc c, IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var have = verdicts.Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        return Cells(c).All(have.Contains);
    }

    public static MatrixDoc Assemble(
        CandidatesDoc c, IReadOnlyList<string> componentIds,
        IReadOnlyCollection<VerdictDoc> verdicts, string generatedAt)
    {
        if (!IsComplete(c, verdicts))
            throw new InvalidOperationException("matrix assembly requires a verdict for every non-excluded candidate×component cell");
        var byCell = verdicts.ToDictionary(v => (v.Cas, v.ComponentId));
        var rows = c.Substances.Where(s => s.Tier != "C")
            .GroupBy(s => s.Cas).Select(g => g.First())
            .Select(s => new SubstanceSpec(s.Element, s.Form, s.Cas)).ToList();
        return new MatrixDoc
        {
            Id = RecordIds.Matrix(c.ProjectId), ProjectId = c.ProjectId,
            Rows = rows,
            Columns = [.. componentIds],
            Cells = [.. Cells(c).Select(cell =>
            {
                var v = byCell[cell];
                // NAMED, and it must stay named. The cell carries two adjacent (string?, string?) pairs —
                // the agent's proposal and the operator's signature — so a positional swap of those four
                // arguments COMPILES CLEAN and silently republishes the agent's proposal as the operator's
                // determination: the agent signing the regulatory gate, which is the one thing Law 9 exists
                // to prevent. Named arguments make that transposition a compile error instead.
                return new MatrixCell(
                    Cas: v.Cas, ComponentId: v.ComponentId, Overall: v.Overall, Dimensions: v.Dimensions,
                    ProposedDetermination: v.ProposedDetermination, ProposedReason: v.ProposedReason,
                    Determination: v.Determination, DeterminationReason: v.DeterminationReason,
                    EvidenceReviewed: v.EvidenceReviewed);
            })],
            GeneratedAt = generatedAt,
        };
    }
}

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
                return new MatrixCell(v.Cas, v.ComponentId, v.Overall, v.Dimensions,
                    v.ProposedDetermination, v.ProposedReason,
                    v.Determination, v.DeterminationReason, v.EvidenceReviewed);
            })],
            GeneratedAt = generatedAt,
        };
    }
}

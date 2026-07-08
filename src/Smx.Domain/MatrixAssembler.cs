using Smx.Domain.Records;

namespace Smx.Domain;

public static class MatrixAssembler
{
    public static IEnumerable<(string Cas, string ComponentId)> Cells(ConstraintsDoc c) =>
        c.Substances.SelectMany(s => c.Components.Select(k => (s.Cas, k.Id)));

    public static bool IsComplete(ConstraintsDoc c, IReadOnlyCollection<VerdictDoc> verdicts)
    {
        var have = verdicts.Select(v => (v.Cas, v.ComponentId)).ToHashSet();
        return Cells(c).All(have.Contains);
    }

    public static MatrixDoc Assemble(ConstraintsDoc c, IReadOnlyCollection<VerdictDoc> verdicts, string generatedAt)
    {
        if (!IsComplete(c, verdicts))
            throw new InvalidOperationException("matrix assembly requires a verdict for every substance×component cell");
        var byCell = verdicts.ToDictionary(v => (v.Cas, v.ComponentId));
        return new MatrixDoc
        {
            Id = RecordIds.Matrix(c.ProjectId), ProjectId = c.ProjectId,
            Rows = [.. c.Substances],
            Columns = [.. c.Components.Select(k => k.Id)],
            Cells = [.. Cells(c).Select(cell =>
            {
                var v = byCell[cell];
                return new MatrixCell(v.Cas, v.ComponentId, v.Overall, v.Dimensions);
            })],
            GeneratedAt = generatedAt,
        };
    }
}

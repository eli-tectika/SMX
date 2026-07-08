namespace Smx.Domain.Records;

public sealed record MatrixCell(string Cas, string ComponentId, VerdictStatus Overall, List<DimensionVerdict> Dimensions);

public sealed class MatrixDoc
{
    public required string Id { get; set; }
    public required string ProjectId { get; set; }
    public string Type { get; set; } = RecordTypes.Matrix;
    public List<SubstanceSpec> Rows { get; set; } = [];      // substances
    public List<string> Columns { get; set; } = [];          // component ids
    public List<MatrixCell> Cells { get; set; } = [];
    public string GeneratedAt { get; set; } = "";
}

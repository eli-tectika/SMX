using System.Text.Json;
using Smx.Domain.Records;

namespace Smx.Eval;

/// One golden case = one project payload + expected overall verdict per cell.
public sealed class GoldenCase
{
    public required string Name { get; set; }
    public required JsonElement ProjectPayload { get; set; } // the exact POST /projects body
    public List<ExpectedCell> Expected { get; set; } = [];
}

/// track: "plumbing" (answerable via ref-compatibility lookup) or "reasoning" (requires retrieval+judgment)
public sealed record ExpectedCell(string Cas, string ComponentId, VerdictStatus Expected, string Track);

public sealed class TrackScore
{
    public int Total { get; set; }
    public int Agreed { get; set; }
    public double Agreement => Total == 0 ? 1.0 : (double)Agreed / Total;
}

public sealed class EvalReport
{
    public Dictionary<string, TrackScore> Tracks { get; } = new(); // collection expressions don't cover Dictionary
    public int FalsePassCount { get; set; }
    public int UncitedCount { get; set; }
    public int MissingCount { get; set; }
    public List<string> Failures { get; } = [];
}

namespace Smx.Domain.Records;

/// An approved final code, reusable across projects (design §6.2). Structured store; PK /id.
public sealed class MarkerLibraryDoc
{
    public required string Id { get; set; }
    public string Type { get; set; } = KnowledgeTypes.MarkerLibrary;
    public required MarkerComposition Composition { get; set; }
    public required ValidatedFor ValidatedFor { get; set; }
    public required string SourceProject { get; set; }
    public string Status { get; set; } = MarkerStatus.Approved;
    public int ReuseCount { get; set; }
    public required string CreatedAt { get; set; }
}

public sealed record MarkerComposition(IReadOnlyList<string> Markers, double Ppm, string Ratio);
public sealed record ValidatedFor(string Application, string Material, string Objective);

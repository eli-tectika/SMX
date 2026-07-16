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

    /// Every project that has confirmed this code — the source history, and the pin that makes reuse
    /// counting idempotent under the at-least-once change feed: the close handler increments ReuseCount
    /// only when the closing project is NOT already listed here, so a redelivered VP gate can re-run the
    /// write without double-counting. A bare counter cannot distinguish "a second project reused this"
    /// from "the same gate was delivered twice"; the list can.
    public List<string> LinkedProjects { get; set; } = [];
    public required string CreatedAt { get; set; }
}

public sealed record MarkerComposition(IReadOnlyList<string> Markers, double Ppm, string Ratio);
public sealed record ValidatedFor(string Application, string Material, string Objective);

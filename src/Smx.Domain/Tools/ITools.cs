namespace Smx.Domain.Tools;

/// A retrieved chunk with everything needed to build a Citation.
public sealed record RetrievedChunk(string Source, string Reference, string Content, double Score);

/// Exact tabulated verdict from ref-compatibility; null when the pair is not tabulated.
public sealed record CompatibilityCard(string Element, string Substrate, string Verdict, string? Notes, string RefId);

public interface ICompatibilityLookup
{
    Task<CompatibilityCard?> LookupAsync(string element, string substrate, CancellationToken ct = default);
}

public interface IRegulatorySearch { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }
public interface ISdsSearch        { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }
public interface IReferenceSearch  { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }

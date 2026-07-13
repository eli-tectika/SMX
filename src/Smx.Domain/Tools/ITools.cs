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
public interface ILearnedConclusionsSearch { Task<IReadOnlyList<RetrievedChunk>> SearchAsync(string query, int top = 5, CancellationToken ct = default); }

/// One catalog product listing from ref-catalog (docType "product").
public sealed record CatalogCard(string Element, string Molecule, string Compound, string Cas, string? Purity, string Supplier, string RefId);

public interface ICatalogLookup
{
    /// All catalog products for an element (single-partition read of ref-catalog by /element).
    Task<IReadOnlyList<CatalogCard>> LookupAsync(string element, CancellationToken ct = default);
}

/// Text → vector. Backs BOTH the learned-conclusions index push and its hybrid retrieval — the same
/// model must embed both sides or the vectors are not comparable. text-embedding-3-large, 3072 dims,
/// on the same Foundry account (and the same Entra credential) as the chat model.
public interface IEmbedder
{
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

/// Write side of the `learned-conclusions` AI Search index; ILearnedConclusionsSearch is the read side.
/// AI Search indexes have no ARM/Bicep resource type, so the index is created in code on first push
/// (data-plane; the workload identity holds Search Index Data Contributor).
public interface ILearnedConclusionsIndex
{
    Task EnsureIndexAsync(CancellationToken ct = default);
    Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default);
}

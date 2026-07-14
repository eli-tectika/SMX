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
/// AI Search indexes have no ARM/Bicep resource type, so the index is created in code.
///
/// Two calls, two roles — they are not interchangeable:
///   • EnsureIndexAsync creates/updates the index DEFINITION (control plane) → Search Service Contributor.
///     Callers must call it before their first PushAsync; PushAsync does not call it. Implementations latch
///     it, so calling it before every push costs nothing after the first.
///   • PushAsync writes DOCUMENTS (data plane) → Search Index Data Contributor.
/// Search Index Data Contributor cannot modify object definitions, so it alone cannot create the index.
/// infra/modules/ai.bicep grants the workload identity both.
public interface ILearnedConclusionsIndex
{
    Task EnsureIndexAsync(CancellationToken ct = default);
    Task PushAsync(IReadOnlyList<LearnedConclusionChunk> chunks, CancellationToken ct = default);
}

/// One web result, as the agent sees it. Deliberately NOT a RetrievedChunk: a web hit is not a retrieved
/// corpus chunk, it has no index reference, and the difference must survive all the way to the citation.
public sealed record WebHit(string Title, string Url, string Snippet, string Host);

/// Anonymized external search, via the Search Proxy. The ONLY tool in this system that reaches the public
/// internet at agent time, and it is exposed to Discovery ALONE — never to Regulatory, whose verdicts may
/// rest only on the curated, sync-dated, R.E.-gated corpus (spec §2 D4).
///
/// A failure is not an empty list. `WebSearchResult.Note` carries the reason (blocked, quota, provider
/// down) so the agent can tell "I searched and found nothing" from "I never got an answer" — the second is
/// not evidence of absence, and an agent that confuses them will confidently exclude a good marker.
public sealed record WebSearchResult(IReadOnlyList<WebHit> Hits, string? Note);

public interface IWebSearch
{
    Task<WebSearchResult> SearchAsync(string query, string intent, CancellationToken ct = default);
}

/// The client/product/project names of the project currently being worked on. This type exists so the terms
/// are passed EXPLICITLY into the tool that must reject them: a tool constructed without them would be a
/// tool that cannot protect the project, and the compiler now says so.
public sealed record SensitiveTerms(IReadOnlyList<string> Terms)
{
    public static SensitiveTerms None => new([]);
}

# File Viewer — Plan 1: the document access layer

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give `Smx.Backend` the ability to list, resolve, and serve the documents already sitting in the
Bronze container — safety data sheets and regulatory source documents — plus the indexed chunks an agent
actually read.

**Architecture:** A `DocumentId` value type encodes `{kind}_{base64url(payload)}`; decoding yields a Cosmos
partition key, so every resolution is a point read. An `IDocumentCatalog` composes two providers (SDS, Reg)
over narrow domain ports, following the existing `ISdsCorpusReader` pattern exactly — port in `Smx.Domain`,
Cosmos implementation in `Smx.Infrastructure`, in-memory fake in `Smx.Domain.Tests/Fakes`. Four minimal-API
endpoints expose list / detail / content / text. **Nothing in this plan writes anything.**

**Tech Stack:** .NET 8 (`Smx.Backend.Tests` targets net10.0), xUnit, `WebApplicationFactory<Program>`,
Microsoft.Azure.Cosmos, Azure.Search.Documents, Azure.Storage.Files.DataLake 12.20.0.

**Spec:** `docs/superpowers/specs/2026-07-22-file-viewer-design.md`. Section references below point there.

**Branch:** `worktree-feat-file-viewer` in `.claude/worktrees/feat-file-viewer`, based on `feat/sds-first-sync`.

---

## File structure

| File | Responsibility |
|---|---|
| `src/Smx.Domain/Documents/DocumentId.cs` | Encode/decode/validate the opaque id. Pure, no I/O. |
| `src/Smx.Domain/Documents/DocumentModels.cs` | `DocumentSummary`, `DocumentDetail`, `ProvenanceField`, `DocumentChunk`, `DocumentFilter`, `DocumentKinds`, `DocumentStates`. |
| `src/Smx.Domain/Documents/IDocumentSources.cs` | `ISdsDocumentSource`, `IRegDocumentSource` + their row records. The Cosmos seam. |
| `src/Smx.Domain/Documents/IDocumentContentStore.cs` | Blob seam: `OpenAsync`, `ReadAsync`. |
| `src/Smx.Domain/Documents/IDocumentTextReader.cs` | Chunk seam. |
| `src/Smx.Domain/Documents/IDocumentCatalog.cs` | `ListAsync`, `GetAsync`. |
| `src/Smx.Domain/Documents/SdsDocumentProvider.cs` | Sheets ∪ gap rows; no double-emit. Pure logic over the port. |
| `src/Smx.Domain/Documents/RegDocumentProvider.cs` | `reg` vs `seed` classification; blob path construction. Pure logic over the port. |
| `src/Smx.Domain/Documents/DocumentCatalog.cs` | Composes the two providers; applies filters. |
| `src/Smx.Domain.Tests/Fakes/InMemoryDocumentSources.cs` | Fakes for both ports. |
| `src/Smx.Domain.Tests/Fakes/InMemoryDocumentContentStore.cs` | Fake blob store; **throws on any write**. |
| `src/Smx.Domain.Tests/Fakes/InMemoryDocumentTextReader.cs` | Fake chunk source. |
| `src/Smx.Infrastructure/CosmosSdsDocumentSource.cs` | `ISdsDocumentSource` over `sds-registry` + `sds-master-list`. |
| `src/Smx.Infrastructure/CosmosRegDocumentSource.cs` | `IRegDocumentSource` over `reg-registry` + `reg-state`. |
| `src/Smx.Infrastructure/BronzeDocumentStore.cs` | ADLS implementation. |
| `src/Smx.Infrastructure/LocalBronzeDocumentStore.cs` | Local-directory implementation for dev. |
| `src/Smx.Infrastructure/CosmosRegSilverTextReader.cs` | Regulatory chunks from `reg-silver`. |
| `src/Smx.Infrastructure/SdsIndexTextReader.cs` | SDS chunks from the `sds-index` AI Search index. |
| `src/Smx.Backend/Api/DocumentEndpoints.cs` | The four routes. |
| `src/Smx.Backend.Tests/DocumentIdTests.cs` | Task 1. |
| `src/Smx.Backend.Tests/DocumentCatalogTests.cs` | Tasks 4–6. |
| `src/Smx.Backend.Tests/DocumentEndpointsTests.cs` | Tasks 10–11. |

**Tests live in `Smx.Backend.Tests`** even for `Smx.Domain` types, matching how `MatrixXlsxWriterTests` and
the domain-logic tests already sit there. `Smx.Domain.Tests` is the *fakes* project consumed by both.

**Build/test commands used throughout:**

```bash
dotnet build src/Smx.Backend.sln
dotnet test  src/Smx.Backend.sln
dotnet test  src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentIdTests"
```

---

## Task 1: `DocumentId` — encode, decode, refuse

**Why first:** every other task depends on it, and it is the security boundary (spec §3 invariants 1–2).

> **Implemented and hardened.** Code review found three real gaps in the reference implementation
> below, all verified by execution and all fixed in `c7a6689`. Later tasks depend on the fixed shape:
> decoding uses a **strict** UTF-8 encoder (`new UTF8Encoding(false, true)`) because the default one
> uses replacement fallback and silently turned invalid bytes into a `U+FFFD` partition key; the
> whitespace guard is `char.IsWhiteSpace` rather than `== ' '` because NBSP, U+2028/29 and U+3000 all
> slipped through a slug payload; and the base64url primitive is called `EncodePayload`, not
> `EncodePayloadForTest`, because production `Encode` calls it. `PartitionKeyOf`/`SegmentsOf` now
> throw `ArgumentException` on an unknown kind instead of `KeyNotFoundException`. Read
> `src/Smx.Domain/Documents/DocumentId.cs` for the authoritative version.

**Files:**
- Create: `src/Smx.Domain/Documents/DocumentId.cs`
- Test: `src/Smx.Backend.Tests/DocumentIdTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/DocumentIdTests.cs`:

```csharp
using Smx.Domain.Documents;

namespace Smx.Backend.Tests;

public class DocumentIdTests
{
    // The four kinds and the payload each carries. Decoding must recover the payload EXACTLY —
    // the first segment is a Cosmos partition key, so a lossy round-trip is a cross-partition scan
    // at best and a wrong-document read at worst.
    [Theory]
    [InlineData("sds", "7440-22-4|sigma-aldrich|2024-03-11")]
    [InlineData("reg", "echa-svhc/candidate-list")]
    [InlineData("seed", "eu/clp-annex-vi")]
    [InlineData("sdsgap", "Nd_oxide")]
    public void RoundTrips(string kind, string payload)
    {
        var id = DocumentId.Encode(kind, payload);
        Assert.True(DocumentId.TryDecode(id, out var decodedKind, out var decodedPayload));
        Assert.Equal(kind, decodedKind);
        Assert.Equal(payload, decodedPayload);
    }

    // base64url only: '+' and '/' would be re-encoded by a URL layer and '=' padding is legal but
    // must survive. This is the same constraint DedupKey.ForChunk documents for AI Search keys.
    [Fact]
    public void EncodedFormIsUrlSafe()
    {
        var id = DocumentId.Encode("sds", "7440-22-4|sigma-aldrich|2024-03-11");
        Assert.DoesNotContain('+', id);
        Assert.DoesNotContain('/', id);
        Assert.StartsWith("sds_", id);
    }

    // Spec §3 invariant 2. Every one of these must fail BEFORE any store is touched.
    [Theory]
    [InlineData("")]
    [InlineData("sds")]                       // no separator
    [InlineData("sds_")]                      // empty payload
    [InlineData("nope_YWJj")]                 // unknown kind
    [InlineData("sds_!!!!")]                  // not base64
    [InlineData("sds_" + "Li4vLi4vc2VjcmV0")] // decodes to "../../secret"
    public void RejectsMalformed(string id)
    {
        Assert.False(DocumentId.TryDecode(id, out _, out _));
    }

    // Traversal and control characters are refused even when they arrive base64-clean, because the
    // payload's first segment becomes a partition key and the rest becomes part of a blob path.
    [Theory]
    [InlineData("reg", "../etc/passwd")]
    [InlineData("reg", "echa/../../bronze")]
    [InlineData("reg", "echa/doc id")]   // a space is a slug violation for reg/seed/sdsgap
    [InlineData("reg", "echa/doc\nid")]
    public void RejectsDangerousPayloads(string kind, string payload)
    {
        var id = kind + "_" + DocumentId.EncodePayload(payload);
        Assert.False(DocumentId.TryDecode(id, out _, out _));
    }

    /// A SPACE is not dangerous, and refusing one would be a real bug.
    ///
    /// DedupKey.Norm lowercases and COLLAPSES whitespace — it does not strip it — so a registry id
    /// legitimately contains single spaces whenever the supplier name does. "Alfa Aesar", "Sigma
    /// Aldrich" and "Fisher Scientific" all yield ids with a space in them, and a validator that
    /// rejected those would make every multi-word supplier's safety sheet unopenable.
    [Theory]
    [InlineData("7440-22-4|alfa aesar|2024-01-01")]
    [InlineData("1313-97-9|fisher scientific|2025-06-30")]
    public void AcceptsSpacesInsidePayloadSegments(string payload)
    {
        var id = DocumentId.Encode(DocumentId.Sds, payload);
        Assert.True(DocumentId.TryDecode(id, out _, out var decoded));
        Assert.Equal(payload, decoded);
    }

    // Segment counts are fixed per kind: a 'reg' payload is exactly sourceId/docId. Extra segments
    // would let a caller append path components onto the constructed blob path.
    [Theory]
    [InlineData("reg", "onlyonesegment")]
    [InlineData("reg", "a/b/c")]
    [InlineData("seed", "a/b/c")]
    [InlineData("sds", "cas|supplier")]        // needs three
    [InlineData("sds", "a|b|c|d")]
    public void RejectsWrongSegmentCount(string kind, string payload)
    {
        var id = kind + "_" + DocumentId.EncodePayload(payload);
        Assert.False(DocumentId.TryDecode(id, out _, out _));
    }

    // The partition key is the first segment. Getting this wrong is silent: the read just returns null.
    [Theory]
    [InlineData("sds_", "7440-22-4|sigma|2024-01-01", "7440-22-4")]
    [InlineData("reg_", "echa-svhc/candidate-list", "echa-svhc")]
    [InlineData("seed_", "eu/clp-annex-vi", "eu")]
    [InlineData("sdsgap_", "Nd_oxide", "Nd")]
    public void PartitionKeyIsTheFirstSegment(string prefix, string payload, string expected)
    {
        var id = prefix + DocumentId.EncodePayload(payload);
        Assert.True(DocumentId.TryDecode(id, out var kind, out var decoded));
        Assert.Equal(expected, DocumentId.PartitionKeyOf(kind, decoded));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentIdTests"
```

Expected: **build error** — `The type or namespace name 'Documents' does not exist in the namespace 'Smx.Domain'`.

- [ ] **Step 3: Implement `DocumentId`**

Create `src/Smx.Domain/Documents/DocumentId.cs`:

```csharp
using System.Text;

namespace Smx.Domain.Documents;

/// The only thing the document API accepts. `{kind}_{base64url(payload)}`.
///
/// Two jobs, both load-bearing. It keeps blob paths out of the API surface — a `?path=` parameter
/// against the bronze container is an arbitrary-read primitive over the entire regulatory corpus
/// (design D2). And it makes every lookup a point read, because the payload's first segment is the
/// Cosmos partition key of the row that owns the document.
///
/// base64url rather than the raw payload because the natural ids contain '|' and spaces — the same
/// constraint, and the same fix, that DedupKey.ForChunk records for AI Search keys.
public static class DocumentId
{
    public const string Sds = "sds";
    public const string Reg = "reg";
    public const string Seed = "seed";
    public const string SdsGap = "sdsgap";

    // kind -> (separator, exact segment count). Fixed counts matter: an extra segment on a `reg`
    // payload becomes an extra component of the constructed blob path.
    // SpacesAllowed is per-kind and load-bearing in BOTH directions. An `sds` payload carries a
    // supplier name, and DedupKey.Norm collapses whitespace rather than stripping it — "alfa aesar"
    // is a real registry id, so rejecting spaces would make every multi-word supplier's sheet
    // unopenable. The other three carry slugs (DedupKey.Slug emits [a-z0-9-] only, sourceIds look
    // like `echa-svhc`, regions like `eu`), where a space is never legitimate.
    private static readonly Dictionary<string, (char Sep, int Segments, bool SpacesAllowed)> Shapes = new()
    {
        [Sds] = ('|', 3, true),      // cas | supplier | revisionDate
        [Reg] = ('/', 2, false),     // sourceId / docId
        [Seed] = ('/', 2, false),    // region / docId
        [SdsGap] = ('_', 2, false),  // element _ form-slug  (DedupKey.ForMasterList)
    };

    public static string Encode(string kind, string payload)
    {
        if (!Shapes.ContainsKey(kind)) throw new ArgumentException($"unknown document kind '{kind}'", nameof(kind));
        return kind + "_" + EncodePayload(payload);
    }

    /// base64url of a raw payload. Public only so tests can build deliberately-invalid ids;
    /// production callers use Encode, which validates the kind.
    public static string EncodePayload(string payload) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).Replace('+', '-').Replace('/', '_');

    public static bool TryDecode(string? id, out string kind, out string payload)
    {
        kind = ""; payload = "";
        if (string.IsNullOrEmpty(id)) return false;

        var split = id.IndexOf('_');
        if (split <= 0 || split == id.Length - 1) return false;

        var k = id[..split];
        if (!Shapes.TryGetValue(k, out var shape)) return false;

        string decoded;
        try
        {
            var b64 = id[(split + 1)..].Replace('-', '+').Replace('_', '/');
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(b64)));
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }

        if (decoded.Length == 0) return false;
        // Traversal and control characters, refused even when base64-clean: the payload becomes both
        // a partition key and part of a blob path.
        if (decoded.Contains("..", StringComparison.Ordinal)) return false;
        if (decoded.Contains('\\')) return false;
        if (decoded.Any(char.IsControl)) return false;
        if (!shape.SpacesAllowed && decoded.Contains(' ')) return false;

        var segments = decoded.Split(shape.Sep);
        if (segments.Length != shape.Segments) return false;
        if (segments.Any(string.IsNullOrWhiteSpace)) return false;
        // A '/' inside a non-'/'-separated payload would still reach the path builder.
        if (shape.Sep != '/' && decoded.Contains('/')) return false;

        kind = k; payload = decoded;
        return true;
    }

    /// The Cosmos partition key for a decoded payload: always its first segment.
    /// sds -> cas, reg/seed -> sourceId/region, sdsgap -> element.
    public static string PartitionKeyOf(string kind, string payload) =>
        payload.Split(Shapes[kind].Sep)[0];

    /// The payload's segments, in order. Callers know their own kind's shape.
    public static string[] SegmentsOf(string kind, string payload) =>
        payload.Split(Shapes[kind].Sep);

    private static string Pad(string b64) => (b64.Length % 4) switch
    {
        2 => b64 + "==",
        3 => b64 + "=",
        0 => b64,
        _ => b64 + "===",   // length%4==1 is invalid base64; let FromBase64String reject it
    };
}
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentIdTests"
```

Expected: **Passed! — Failed: 0**, 26 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Documents/DocumentId.cs src/Smx.Backend.Tests/DocumentIdTests.cs
git commit -m "feat(documents): the document id, which is also the security boundary

The API accepts this and never a blob path — a ?path= parameter against the
bronze container is an arbitrary-read primitive over the whole regulatory
corpus. Decoding yields the partition key of the row that owns the document,
so every resolution is a point read rather than a scan.

Traversal, control characters and wrong segment counts are refused before any
store is touched, because the payload becomes both a partition key and part of
a constructed blob path."
```

---

## Task 2: the document models and the ports

**Files:**
- Create: `src/Smx.Domain/Documents/DocumentModels.cs`, `IDocumentSources.cs`, `IDocumentContentStore.cs`, `IDocumentTextReader.cs`, `IDocumentCatalog.cs`

Pure type declarations with no behaviour — nothing to test in isolation; Tasks 4–6 exercise them.

- [ ] **Step 1: Create `src/Smx.Domain/Documents/DocumentModels.cs`**

```csharp
namespace Smx.Domain.Documents;

public static class DocumentKinds
{
    public const string Sds = "sds";           // a safety data sheet, present or missing
    public const string Reg = "reg";           // a synced regulatory source document
    public const string Seed = "seed";         // a seed-imported regulatory document
    public const string All = "all";
}

public static class DocumentStates
{
    public const string Available = "available";
    public const string Missing = "missing";        // catalogued, no file — the gap rows
    public const string Superseded = "superseded";
    public const string All = "all";
}

public static class UnavailableReasons
{
    public const string NeverFetched = "never-fetched";   // gap row: no sheet was ever obtained
    public const string BlobMissing = "blob-missing";     // registry says yes, storage says no — real drift
}

/// One catalog row. `Kind` is the FACET (sds/reg/seed) — note a gap row reports `sds`, because it is a
/// safety data sheet that is missing, not a fourth category. Only DocumentId carries the `sdsgap`
/// distinction, and only because resolution targets a different container.
public sealed record DocumentSummary(
    string Id,
    string Kind,
    string Title,
    string Subtitle,
    bool Available,
    string State,
    string? ContentType,
    string? OfficialDate,
    string? IngestedUtc);

/// A labelled provenance line. A LIST rather than a fixed record because SDS and regulatory provenance
/// genuinely differ in shape, and the rail renders whatever it is handed.
public sealed record ProvenanceField(string Label, string Value, string Kind = "text");

public static class ProvenanceKinds
{
    public const string Text = "text";
    public const string Url = "url";
    public const string Hash = "hash";
}

public sealed record DocumentDetail(
    DocumentSummary Summary,
    IReadOnlyList<ProvenanceField> Provenance,
    string? UnavailableReason,
    string? UnavailableDetail,
    string? SupersededById,
    string? BlobPath);   // never serialised to the client; the endpoints use it to fetch

public sealed record DocumentChunk(int Ordinal, string Text, string? EntryId, string? Section);

public sealed record DocumentFilter(string Kind = DocumentKinds.All, string? Q = null, string State = DocumentStates.All);
```

- [ ] **Step 2: Create `src/Smx.Domain/Documents/IDocumentSources.cs`**

```csharp
namespace Smx.Domain.Documents;

/// A row of `sds-registry` (PK /cas) — a sheet that exists. Mirrors RegistryPointer, minus the
/// index-doc-id list the viewer has no use for.
public sealed record SdsSheetRow(
    string Id, string Cas, string Supplier, string ProductName, string RevisionDate,
    string Region, string Language, string SourceUrl, string BlobPath, bool Indexed,
    string IngestedUtc, string? SupersededBy, string? MasterListId);

/// A row of `sds-master-list` (PK /element) — a substance the system knows it needs a sheet for.
/// Status is pending | fetched | failed | awaiting_operator (SdsStatus in the Functions app).
public sealed record SdsMasterRow(
    string Id, string Element, string Form, string Cas, string Status,
    string? LastAttemptUtc, int AttemptCount);

/// A row of `reg-state` (PK /sourceId, id = docId) — per-document change-detection state.
public sealed record RegDocRow(
    string DocId, string SourceId, string Sha256, string OfficialDate, string SyncRunId, string LastFetchTs);

/// A curated source from `reg-registry`. Membership here is what distinguishes a synced document
/// (`regulatory/` prefix) from a seed-imported one (`seed/` prefix).
public sealed record RegSourceRow(
    string SourceId, string Regulation, string Authority, IReadOnlyList<RegDocTitleRow> Documents);

public sealed record RegDocTitleRow(string DocId, string Url, string? Title);

public interface ISdsDocumentSource
{
    Task<IReadOnlyList<SdsSheetRow>> ListSheetsAsync(CancellationToken ct = default);
    Task<SdsSheetRow?> GetSheetAsync(string registryId, string cas, CancellationToken ct = default);
    Task<IReadOnlyList<SdsMasterRow>> ListMasterAsync(CancellationToken ct = default);
    Task<SdsMasterRow?> GetMasterAsync(string masterId, string element, CancellationToken ct = default);
}

public interface IRegDocumentSource
{
    Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default);
    Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default);
}
```

- [ ] **Step 3: Create `src/Smx.Domain/Documents/IDocumentContentStore.cs`**

```csharp
namespace Smx.Domain.Documents;

public sealed record DocumentBytes(Stream Stream, long Length);

/// Read-only access to the bronze filesystem. There is deliberately no write method: this feature
/// writes nothing, and an interface with no Put cannot grow one by accident (spec §3 invariant 7).
public interface IDocumentContentStore
{
    Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default);

    /// Whole-blob read, for the small `meta.json` sidecars.
    Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create `src/Smx.Domain/Documents/IDocumentTextReader.cs`**

```csharp
namespace Smx.Domain.Documents;

/// The chunks an agent actually retrieved — returned VERBATIM. No re-extraction, no cleanup, no
/// re-chunking (spec §3 invariant 4). If the index holds garbage, the operator must see the garbage;
/// that is the entire reason this surface exists.
public interface IDocumentTextReader
{
    Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default);
}
```

- [ ] **Step 5: Create `src/Smx.Domain/Documents/IDocumentCatalog.cs`**

```csharp
namespace Smx.Domain.Documents;

public interface IDocumentCatalog
{
    Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentFilter filter, CancellationToken ct = default);
    Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default);
}
```

- [ ] **Step 6: Verify it builds**

```bash
dotnet build src/Smx.Backend.sln
```

Expected: **Build succeeded**, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Domain/Documents/
git commit -m "feat(documents): the models and the four ports

Ports live in Smx.Domain with Cosmos/ADLS implementations in
Smx.Infrastructure, matching how ISdsCorpusReader is already split.

Two shapes carry decisions. Provenance is a list of labelled fields rather
than a fixed record, because SDS and regulatory provenance genuinely differ
and the rail should render what it is handed. IDocumentContentStore has no
write method at all — this feature writes nothing, and an interface without a
Put cannot grow one by accident."
```

---

## Task 3: the in-memory fakes

**Files:**
- Create: `src/Smx.Domain.Tests/Fakes/InMemoryDocumentSources.cs`, `InMemoryDocumentContentStore.cs`, `InMemoryDocumentTextReader.cs`

- [ ] **Step 1: Create `src/Smx.Domain.Tests/Fakes/InMemoryDocumentSources.cs`**

```csharp
using Smx.Domain.Documents;

namespace Smx.Domain.Tests.Fakes;

/// In-memory ISdsDocumentSource: `Sheets` is sds-registry, `Master` is sds-master-list.
public sealed class InMemorySdsDocumentSource : ISdsDocumentSource
{
    public List<SdsSheetRow> Sheets { get; } = [];
    public List<SdsMasterRow> Master { get; } = [];

    public Task<IReadOnlyList<SdsSheetRow>> ListSheetsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SdsSheetRow>>(Sheets.ToList());

    public Task<SdsSheetRow?> GetSheetAsync(string registryId, string cas, CancellationToken ct = default) =>
        Task.FromResult(Sheets.FirstOrDefault(s =>
            s.Id == registryId && string.Equals(s.Cas, cas, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<SdsMasterRow>> ListMasterAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SdsMasterRow>>(Master.ToList());

    public Task<SdsMasterRow?> GetMasterAsync(string masterId, string element, CancellationToken ct = default) =>
        Task.FromResult(Master.FirstOrDefault(m =>
            m.Id == masterId && string.Equals(m.Element, element, StringComparison.OrdinalIgnoreCase)));
}

/// In-memory IRegDocumentSource: `Sources` is reg-registry, `Docs` is reg-state.
public sealed class InMemoryRegDocumentSource : IRegDocumentSource
{
    public List<RegSourceRow> Sources { get; } = [];
    public List<RegDocRow> Docs { get; } = [];

    public Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegSourceRow>>(Sources.ToList());

    public Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RegDocRow>>(Docs.ToList());

    public Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default) =>
        Task.FromResult(Docs.FirstOrDefault(d => d.DocId == docId && d.SourceId == sourceId));
}
```

- [ ] **Step 2: Create `src/Smx.Domain.Tests/Fakes/InMemoryDocumentContentStore.cs`**

```csharp
using System.Text;
using Smx.Domain.Documents;

namespace Smx.Domain.Tests.Fakes;

/// In-memory bronze. `Blobs` is keyed by blob path.
///
/// `PathsRead` records every path this store was asked for, so tests can assert that a rejected
/// document id never reached storage at all (spec §3 invariant 2) — a 404 is not by itself proof
/// that no read was attempted.
public sealed class InMemoryDocumentContentStore : IDocumentContentStore
{
    public Dictionary<string, byte[]> Blobs { get; } = [];
    public List<string> PathsRead { get; } = [];

    public void Put(string path, string content) => Blobs[path] = Encoding.UTF8.GetBytes(content);

    public Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default)
    {
        PathsRead.Add(blobPath);
        if (!Blobs.TryGetValue(blobPath, out var bytes)) return Task.FromResult<DocumentBytes?>(null);
        return Task.FromResult<DocumentBytes?>(new DocumentBytes(new MemoryStream(bytes), bytes.Length));
    }

    public Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default)
    {
        PathsRead.Add(blobPath);
        return Task.FromResult(Blobs.TryGetValue(blobPath, out var bytes) ? bytes : null);
    }
}
```

- [ ] **Step 3: Create `src/Smx.Domain.Tests/Fakes/InMemoryDocumentTextReader.cs`**

```csharp
using Smx.Domain.Documents;

namespace Smx.Domain.Tests.Fakes;

/// In-memory chunk source, keyed by document id. A document absent from `Chunks` returns an empty
/// list — which is a real, meaningful state (in bronze, never indexed), not a failure.
public sealed class InMemoryDocumentTextReader : IDocumentTextReader
{
    public Dictionary<string, List<DocumentChunk>> Chunks { get; } = [];

    public Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DocumentChunk>>(
            Chunks.TryGetValue(document.Summary.Id, out var c) ? c.ToList() : []);
}
```

- [ ] **Step 4: Verify it builds**

```bash
dotnet build src/Smx.Backend.sln
```

Expected: **Build succeeded**, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain.Tests/Fakes/
git commit -m "test(documents): in-memory fakes for the four ports

The content store records every path it was asked for. A test that only
asserts 404 cannot tell a rejected id from one that was resolved and missed;
PathsRead makes 'storage was never touched' directly assertable."
```

---

## Task 4: `SdsDocumentProvider` — sheets and the gaps between them

**Files:**
- Create: `src/Smx.Domain/Documents/SdsDocumentProvider.cs`
- Test: `src/Smx.Backend.Tests/DocumentCatalogTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/DocumentCatalogTests.cs`:

```csharp
using Smx.Domain.Documents;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class SdsDocumentProviderTests
{
    private readonly InMemorySdsDocumentSource _source = new();
    private SdsDocumentProvider Provider => new(_source);

    private static SdsSheetRow Sheet(string cas, string supplier, string rev, string? superseded = null,
        string? masterId = null) =>
        new($"{cas}|{supplier}|{rev}", cas, supplier, $"{supplier} {cas}", rev, "EU", "en",
            $"https://example.test/{cas}.pdf", $"sds/{cas}/{supplier}/{rev}.pdf", true,
            "2026-07-16T00:00:00Z", superseded, masterId);

    private static SdsMasterRow Master(string element, string form, string cas, string status,
        int attempts = 0, string? masterId = null) =>
        new(masterId ?? $"{element}_{form}", element, form, cas, status, attempts > 0 ? "2026-07-18T00:00:00Z" : null, attempts);

    [Fact]
    public async Task ListsSheetsAsAvailable()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2024-03-11"));
        var rows = await Provider.ListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(DocumentKinds.Sds, row.Kind);
        Assert.True(row.Available);
        Assert.Equal(DocumentStates.Available, row.State);
        Assert.Equal("application/pdf", row.ContentType);
        Assert.Contains("7761-88-8", row.Subtitle);
    }

    // Design D9: a missing MSDS is the thing that blocks an order. Listing only files that exist
    // would let absence read as coverage.
    [Fact]
    public async Task EmitsGapRowsForSubstancesWithNoSheet()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 3));
        var rows = await Provider.ListAsync();
        var row = Assert.Single(rows);
        Assert.False(row.Available);
        Assert.Equal(DocumentStates.Missing, row.State);
        Assert.Equal(DocumentKinds.Sds, row.Kind);          // facet is sds — a MISSING sheet is still a sheet
        Assert.StartsWith("sdsgap_", row.Id);                // but the id resolves against a different container
    }

    // A substance that HAS a sheet must not also appear as a gap; otherwise every fetched substance
    // is listed twice and the "missing" count is meaningless.
    [Fact]
    public async Task DoesNotEmitAGapForASubstanceThatHasASheet()
    {
        _source.Sheets.Add(Sheet("1313-97-9", "alfa", "2025-02-02", masterId: "Nd_oxide"));
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "fetched"));
        var rows = await Provider.ListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].Available);
    }

    // The link may be by masterListId OR, for older rows that predate it, by CAS.
    [Fact]
    public async Task SuppressesTheGapWhenOnlyTheCasMatches()
    {
        _source.Sheets.Add(Sheet("1313-97-9", "alfa", "2025-02-02", masterId: null));
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "pending"));
        var rows = await Provider.ListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].Available);
    }

    [Fact]
    public async Task MarksSupersededSheets()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2023-01-01", superseded: "7761-88-8|sigma|2024-03-11"));
        var rows = await Provider.ListAsync();
        Assert.Equal(DocumentStates.Superseded, rows[0].State);
        Assert.True(rows[0].Available);      // superseded still opens — it is history, not absence
    }

    [Fact]
    public async Task ResolvesASheetToItsBlobPathAndProvenance()
    {
        _source.Sheets.Add(Sheet("7761-88-8", "sigma", "2024-03-11"));
        var id = DocumentId.Encode(DocumentId.Sds, "7761-88-8|sigma|2024-03-11");
        var detail = await Provider.GetAsync(id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("sds/7761-88-8/sigma/2024-03-11.pdf", detail!.BlobPath);
        Assert.Contains(detail.Provenance, p => p.Label == "Source URL" && p.Kind == ProvenanceKinds.Url);
        Assert.Contains(detail.Provenance, p => p.Label == "Supplier" && p.Value == "sigma");
        Assert.Contains(detail.Provenance, p => p.Label == "Revision date" && p.Value == "2024-03-11");
    }

    // Spec §8: a gap row is not a lookup failure. It resolves, reports why there is no file, and
    // carries the attempt count that tells the operator whether retrying is worth anything.
    [Fact]
    public async Task ResolvesAGapRowToAStatedAbsence()
    {
        _source.Master.Add(Master("Nd", "oxide", "1313-97-9", "failed", attempts: 3));
        var id = DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide");
        var detail = await Provider.GetAsync(id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.False(detail!.Summary.Available);
        Assert.Equal(UnavailableReasons.NeverFetched, detail.UnavailableReason);
        Assert.Null(detail.BlobPath);
        Assert.Contains("3", detail.UnavailableDetail);
    }

    [Fact]
    public async Task ReturnsNullForAnUnknownSheet()
    {
        var id = DocumentId.Encode(DocumentId.Sds, "0000-00-0|nobody|1999-01-01");
        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~SdsDocumentProviderTests"
```

Expected: **build error** — `SdsDocumentProvider` does not exist.

- [ ] **Step 3: Implement `SdsDocumentProvider`**

Create `src/Smx.Domain/Documents/SdsDocumentProvider.cs`:

```csharp
namespace Smx.Domain.Documents;

/// The safety-data-sheet half of the catalog: `sds-registry` (sheets that exist) unioned with the
/// non-fetched rows of `sds-master-list` (substances the system knows it needs a sheet for and does
/// not have).
///
/// Emitting the gaps is deliberate (design D9). A missing MSDS is exactly what blocks an order, so a
/// library that listed only files would let absence read as coverage.
public sealed class SdsDocumentProvider(ISdsDocumentSource source)
{
    public const string SheetContentType = "application/pdf";

    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        var sheets = await source.ListSheetsAsync(ct);
        var master = await source.ListMasterAsync(ct);

        var rows = sheets.Select(ToSummary).ToList();

        // Suppress the gap for anything already covered by a sheet. The link is masterListId where
        // the ingest recorded one, and CAS otherwise — older registry rows predate masterListId.
        var coveredMasterIds = sheets.Where(s => s.MasterListId is { Length: > 0 })
                                     .Select(s => s.MasterListId!)
                                     .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var coveredCas = sheets.Select(s => s.Cas).ToHashSet(StringComparer.OrdinalIgnoreCase);

        rows.AddRange(master
            .Where(m => !coveredMasterIds.Contains(m.Id) && !coveredCas.Contains(m.Cas))
            .Select(ToGapSummary));

        return rows;
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out var payload)) return null;

        if (kind == DocumentId.Sds)
        {
            var sheet = await source.GetSheetAsync(payload, DocumentId.PartitionKeyOf(kind, payload), ct);
            return sheet is null ? null : new DocumentDetail(
                ToSummary(sheet),
                Provenance(sheet),
                UnavailableReason: null,
                UnavailableDetail: null,
                SupersededById: sheet.SupersededBy is { Length: > 0 }
                    ? DocumentId.Encode(DocumentId.Sds, sheet.SupersededBy) : null,
                BlobPath: sheet.BlobPath);
        }

        if (kind == DocumentId.SdsGap)
        {
            var m = await source.GetMasterAsync(payload, DocumentId.PartitionKeyOf(kind, payload), ct);
            return m is null ? null : new DocumentDetail(
                ToGapSummary(m),
                [
                    new("CAS", m.Cas),
                    new("Element", m.Element),
                    new("Form", m.Form),
                    new("Status", m.Status),
                    new("Fetch attempts", m.AttemptCount.ToString()),
                    new("Last attempt", m.LastAttemptUtc ?? "not recorded"),
                ],
                UnavailableReason: UnavailableReasons.NeverFetched,
                UnavailableDetail: Explain(m),
                SupersededById: null,
                BlobPath: null);
        }

        return null;
    }

    private static DocumentSummary ToSummary(SdsSheetRow s) => new(
        Id: DocumentId.Encode(DocumentId.Sds, s.Id),
        Kind: DocumentKinds.Sds,
        Title: string.IsNullOrWhiteSpace(s.ProductName) ? s.Cas : s.ProductName,
        Subtitle: $"CAS {s.Cas} · {s.Supplier} · rev {s.RevisionDate} · {s.Region} / {s.Language}",
        Available: true,
        State: s.SupersededBy is { Length: > 0 } ? DocumentStates.Superseded : DocumentStates.Available,
        ContentType: SheetContentType,
        OfficialDate: s.RevisionDate,
        IngestedUtc: s.IngestedUtc);

    private static DocumentSummary ToGapSummary(SdsMasterRow m) => new(
        Id: DocumentId.Encode(DocumentId.SdsGap, m.Id),
        Kind: DocumentKinds.Sds,          // facet: a missing sheet is still a sheet
        Title: $"{m.Element} {m.Form} — no safety sheet",
        Subtitle: $"CAS {m.Cas} · {Explain(m)}",
        Available: false,
        State: DocumentStates.Missing,
        ContentType: null,
        OfficialDate: null,
        IngestedUtc: null);

    private static IReadOnlyList<ProvenanceField> Provenance(SdsSheetRow s) =>
    [
        new("Source URL", s.SourceUrl, ProvenanceKinds.Url),
        new("Supplier", s.Supplier),
        new("Product name", string.IsNullOrWhiteSpace(s.ProductName) ? "not recorded" : s.ProductName),
        new("CAS", s.Cas),
        new("Revision date", s.RevisionDate),
        new("Region / language", $"{s.Region} / {s.Language}"),
        new("Ingested", s.IngestedUtc),
        new("Indexed", s.Indexed ? "yes" : "no"),
        new("Superseded by", s.SupersededBy is { Length: > 0 } ? s.SupersededBy : "—"),
    ];

    private static string Explain(SdsMasterRow m) => m.Status switch
    {
        "failed" => $"{m.AttemptCount} fetch attempt(s) failed · last {m.LastAttemptUtc ?? "not recorded"}",
        "awaiting_operator" => "awaiting operator upload — no automated source",
        "pending" => "queued for fetch",
        _ => m.Status,
    };
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~SdsDocumentProviderTests"
```

Expected: **Passed! — Failed: 0**, 8 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Documents/SdsDocumentProvider.cs src/Smx.Backend.Tests/DocumentCatalogTests.cs
git commit -m "feat(documents): safety sheets, and the substances that have none

The library unions sds-registry with the non-fetched rows of sds-master-list.
A missing MSDS is precisely what blocks an order, so listing only files that
exist would let absence read as coverage.

Suppression of a gap row checks masterListId first and CAS second, because
older registry rows predate masterListId and would otherwise appear both as a
sheet and as a hole."
```

---

## Task 5: `RegDocumentProvider` — synced versus seeded

**Files:**
- Create: `src/Smx.Domain/Documents/RegDocumentProvider.cs`
- Modify: `src/Smx.Backend.Tests/DocumentCatalogTests.cs` (append a second test class)

- [ ] **Step 1: Append the failing tests to `src/Smx.Backend.Tests/DocumentCatalogTests.cs`**

```csharp
public class RegDocumentProviderTests
{
    private readonly InMemoryRegDocumentSource _source = new();
    private readonly InMemoryDocumentContentStore _bronze = new();
    private RegDocumentProvider Provider => new(_source, _bronze);

    private const string Meta = """
        {"sourceId":"echa-svhc","docId":"candidate-list","sourceUrl":"https://echa.europa.eu/candidate-list",
         "officialDate":"2025-11-20","fetchTs":"20260701T031400Z","sha256":"9f2c1ae4",
         "contentType":"text/html","httpStatus":200,"syncRunId":"sync-2026-07-01-a3f"}
        """;

    private void GivenSyncedSource()
    {
        _source.Sources.Add(new RegSourceRow("echa-svhc", "REACH SVHC", "ECHA",
            [new RegDocTitleRow("candidate-list", "https://echa.europa.eu/candidate-list", "SVHC candidate list")]));
        _source.Docs.Add(new RegDocRow("candidate-list", "echa-svhc", "9f2c1ae4", "2025-11-20",
            "sync-2026-07-01-a3f", "20260701T031400Z"));
    }

    // A reg-state row whose sourceId matches a curated source is a SYNCED document; the path carries
    // the fetch timestamp as a folder. Classification happens here, at catalog time — never by
    // probing storage for which prefix happens to exist.
    [Fact]
    public async Task SyncedDocumentsResolveUnderTheRegulatoryPrefix()
    {
        GivenSyncedSource();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json", Meta);
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("regulatory/echa-svhc/candidate-list/20260701T031400Z/raw.html", detail!.BlobPath);
        Assert.Equal("text/html", detail.Summary.ContentType);
    }

    // A reg-state row whose sourceId is NOT a curated source came from the seed importer, where
    // sourceId is the region and the path has no fetchTs segment at all (SeedImporter.cs:96,109).
    [Fact]
    public async Task SeededDocumentsResolveUnderTheSeedPrefixWithNoTimestampSegment()
    {
        _source.Docs.Add(new RegDocRow("clp-annex-vi", "eu", "abc123", "2024-08-14", "seed-run", "2026-07-05"));
        _bronze.Put("seed/eu/clp-annex-vi/meta.json",
            """{"sourceId":"eu","docId":"clp-annex-vi","sourceUrl":"https://eur-lex.europa.eu/clp",
                "officialDate":"2024-08-14","fetchTs":"2026-07-05","sha256":"abc123",
                "contentType":"text/plain","httpStatus":0,"syncRunId":"seed-run"}""");
        var id = DocumentId.Encode(DocumentId.Seed, "eu/clp-annex-vi");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("seed/eu/clp-annex-vi/raw.txt", detail!.BlobPath);
    }

    [Fact]
    public async Task ListClassifiesEachDocByRegistryMembership()
    {
        GivenSyncedSource();
        _source.Docs.Add(new RegDocRow("clp-annex-vi", "eu", "abc123", "2024-08-14", "seed-run", "2026-07-05"));

        var rows = await Provider.ListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Kind == DocumentKinds.Reg && r.Id.StartsWith("reg_"));
        Assert.Contains(rows, r => r.Kind == DocumentKinds.Seed && r.Id.StartsWith("seed_"));
    }

    // The title comes from the curated registry when there is one; a seeded doc has only its id.
    [Fact]
    public async Task UsesTheCuratedTitleWhenTheRegistryHasOne()
    {
        GivenSyncedSource();
        var rows = await Provider.ListAsync();
        Assert.Equal("SVHC candidate list", rows[0].Title);
    }

    // The extension is not stored anywhere — it is derived at ingest from the content type and then
    // discarded. meta.json is where it comes back from, and the same read populates the rail.
    [Theory]
    [InlineData("text/html", "raw.html")]
    [InlineData("application/pdf", "raw.pdf")]
    [InlineData("text/csv", "raw.csv")]
    [InlineData("application/json", "raw.json")]
    [InlineData("application/xml", "raw.xml")]
    [InlineData("application/octet-stream", "raw.bin")]
    public async Task DerivesTheExtensionFromTheStoredContentType(string contentType, string expectedFile)
    {
        GivenSyncedSource();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json",
            $$"""{"sourceId":"echa-svhc","docId":"candidate-list","sourceUrl":"https://x.test",
                 "officialDate":"2025-11-20","fetchTs":"20260701T031400Z","sha256":"9f",
                 "contentType":"{{contentType}}","httpStatus":200,"syncRunId":"r"}""");
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.EndsWith(expectedFile, detail!.BlobPath);
    }

    // Spec §3 invariant 6: a missing sidecar yields "not recorded", never an invented value.
    [Fact]
    public async Task StatesNotRecordedWhenTheSidecarIsAbsent()
    {
        GivenSyncedSource();
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Contains(detail!.Provenance, p => p.Label == "SHA-256" && p.Value == "not recorded");
        Assert.All(detail.Provenance, p => Assert.NotEqual("", p.Value));
    }

    [Fact]
    public async Task PopulatesTheRailFromTheSidecar()
    {
        GivenSyncedSource();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json", Meta);
        var id = DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list");

        var detail = await Provider.GetAsync(id, CancellationToken.None);

        Assert.Contains(detail!.Provenance, p => p.Label == "SHA-256" && p.Value == "9f2c1ae4");
        Assert.Contains(detail.Provenance, p => p.Label == "Sync run" && p.Value == "sync-2026-07-01-a3f");
        Assert.Contains(detail.Provenance, p => p.Label == "Authority" && p.Value == "ECHA");
    }

    [Fact]
    public async Task ReturnsNullForAnUnknownDoc()
    {
        var id = DocumentId.Encode(DocumentId.Reg, "nope/nothing");
        Assert.Null(await Provider.GetAsync(id, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegDocumentProviderTests"
```

Expected: **build error** — `RegDocumentProvider` does not exist.

- [ ] **Step 3: Implement `RegDocumentProvider`**

Create `src/Smx.Domain/Documents/RegDocumentProvider.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Smx.Domain.Documents;

/// The regulatory half of the catalog, over `reg-registry` (curated sources) and `reg-state`
/// (per-document change-detection state).
///
/// The load-bearing subtlety is that two DIFFERENT bronze layouts live behind one Cosmos container.
/// A synced document is written to `regulatory/{sourceId}/{docId}/{fetchTs}/raw.{ext}`; a
/// seed-imported one to `seed/{region}/{docId}/raw.txt`, with no timestamp segment, and its
/// reg-state LastFetchTs holds the sync date, which never appeared in the path. So the two cannot be
/// told apart from a path — they are told apart by whether the sourceId is a curated source, decided
/// here at catalog time rather than by probing storage.
public sealed class RegDocumentProvider(IRegDocumentSource source, IDocumentContentStore bronze)
{
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(CancellationToken ct = default)
    {
        var sources = await source.ListSourcesAsync(ct);
        var docs = await source.ListDocsAsync(ct);
        var byId = sources.ToDictionary(s => s.SourceId, StringComparer.OrdinalIgnoreCase);

        return docs.Select(d =>
        {
            var curated = byId.GetValueOrDefault(d.SourceId);
            var title = curated?.Documents.FirstOrDefault(x => x.DocId == d.DocId)?.Title;
            return new DocumentSummary(
                Id: DocumentId.Encode(curated is null ? DocumentId.Seed : DocumentId.Reg, $"{d.SourceId}/{d.DocId}"),
                Kind: curated is null ? DocumentKinds.Seed : DocumentKinds.Reg,
                Title: title is { Length: > 0 } ? title : d.DocId,
                Subtitle: curated is null
                    ? $"seed / {d.SourceId} · official {d.OfficialDate}"
                    : $"{curated.Authority} · {curated.Regulation} · official {d.OfficialDate}",
                Available: true,
                State: DocumentStates.Available,
                ContentType: null,        // known only from the sidecar; the detail read fills it in
                OfficialDate: d.OfficialDate,
                IngestedUtc: d.LastFetchTs);
        }).ToList();
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out var payload)) return null;
        if (kind != DocumentId.Reg && kind != DocumentId.Seed) return null;

        var segments = DocumentId.SegmentsOf(kind, payload);
        var (sourceId, docId) = (segments[0], segments[1]);

        var doc = await source.GetDocAsync(docId, sourceId, ct);
        if (doc is null) return null;

        var sources = await source.ListSourcesAsync(ct);
        var curated = sources.FirstOrDefault(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));

        // Kind and layout must agree. A `seed`-kinded id whose sourceId IS curated (or vice versa)
        // is a hand-edited id pointing at a path that does not exist; refuse rather than guess.
        if (kind == DocumentId.Reg && curated is null) return null;
        if (kind == DocumentId.Seed && curated is not null) return null;

        var folder = kind == DocumentId.Reg
            ? $"regulatory/{sourceId}/{docId}/{doc.LastFetchTs}"
            : $"seed/{sourceId}/{docId}";

        var meta = await ReadMetaAsync($"{folder}/meta.json", ct);
        var contentType = meta?.ContentType is { Length: > 0 } ? meta.ContentType
            : kind == DocumentId.Seed ? "text/plain" : "application/octet-stream";
        var blobPath = $"{folder}/raw.{ExtensionFor(contentType)}";

        var title = curated?.Documents.FirstOrDefault(x => x.DocId == docId)?.Title;
        var summary = new DocumentSummary(
            Id: documentId,
            Kind: kind == DocumentId.Reg ? DocumentKinds.Reg : DocumentKinds.Seed,
            Title: title is { Length: > 0 } ? title : docId,
            Subtitle: curated is null
                ? $"seed / {sourceId} · official {doc.OfficialDate}"
                : $"{curated.Authority} · {curated.Regulation} · official {doc.OfficialDate}",
            Available: true,
            State: DocumentStates.Available,
            ContentType: contentType,
            OfficialDate: doc.OfficialDate,
            IngestedUtc: doc.LastFetchTs);

        // "not recorded" rather than a plausible substitute — spec §3 invariant 6. For a seeded doc
        // the sidecar's httpStatus is 0 and contentType is hardcoded text/plain (SeedImporter.cs:111),
        // so the rail names it an import instead of implying an HTTP fetch that never happened.
        var provenance = new List<ProvenanceField>
        {
            new("Source URL", meta?.SourceUrl is { Length: > 0 } ? meta.SourceUrl : "not recorded", ProvenanceKinds.Url),
            new("Authority", curated?.Authority ?? "seed import"),
            new("Regulation", curated?.Regulation ?? sourceId),
            new("Official date", doc.OfficialDate),
            new("Origin", kind == DocumentId.Reg ? "monthly sync" : "seed import"),
            new(kind == DocumentId.Reg ? "Fetched" : "Imported", meta?.FetchTs is { Length: > 0 } ? meta.FetchTs : doc.LastFetchTs),
            new("Sync run", meta?.SyncRunId is { Length: > 0 } ? meta.SyncRunId : doc.SyncRunId),
            new("SHA-256", meta?.Sha256 is { Length: > 0 } ? meta.Sha256 : (doc.Sha256 is { Length: > 0 } ? doc.Sha256 : "not recorded"), ProvenanceKinds.Hash),
            new("Content type", contentType),
        };
        if (kind == DocumentId.Reg)
            provenance.Add(new("HTTP status", meta is null ? "not recorded" : meta.HttpStatus.ToString()));

        return new DocumentDetail(summary, provenance, null, null, null, blobPath);
    }

    private async Task<BronzeMetaView?> ReadMetaAsync(string path, CancellationToken ct)
    {
        var bytes = await bronze.ReadAsync(path, ct);
        if (bytes is null) return null;
        try { return JsonSerializer.Deserialize<BronzeMetaView>(bytes, MetaJson); }
        catch (JsonException) { return null; }   // a corrupt sidecar reads as "not recorded", not a 500
    }

    private static readonly JsonSerializerOptions MetaJson = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// Mirrors BronzeIngestor.ExtensionFor — the mapping that produced these files in the first place.
    private static string ExtensionFor(string contentType) => contentType switch
    {
        var c when c.Contains("html", StringComparison.OrdinalIgnoreCase) => "html",
        var c when c.Contains("pdf", StringComparison.OrdinalIgnoreCase) => "pdf",
        var c when c.Contains("csv", StringComparison.OrdinalIgnoreCase) => "csv",
        var c when c.Contains("json", StringComparison.OrdinalIgnoreCase) => "json",
        var c when c.Contains("xml", StringComparison.OrdinalIgnoreCase) => "xml",
        var c when c.Contains("text/plain", StringComparison.OrdinalIgnoreCase) => "txt",
        _ => "bin",
    };

    private sealed record BronzeMetaView(
        string? SourceId, string? DocId, string? SourceUrl, string? OfficialDate, string? FetchTs,
        string? Sha256, string? ContentType, int HttpStatus, string? SyncRunId);
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~RegDocumentProviderTests"
```

Expected: **Passed! — Failed: 0**, 13 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Documents/RegDocumentProvider.cs src/Smx.Backend.Tests/DocumentCatalogTests.cs
git commit -m "feat(documents): regulatory documents, synced and seeded

Two bronze layouts hide behind one Cosmos container. A synced document lives
at regulatory/{sourceId}/{docId}/{fetchTs}/, a seeded one at
seed/{region}/{docId}/ with no timestamp segment — and the seeded row's
LastFetchTs holds a sync date that never appeared in its path. They cannot be
told apart from a path, so they are told apart by curated-source membership,
decided at catalog time rather than by probing storage.

The file extension is derived at ingest and then discarded; meta.json is where
it comes back from, and that same read fills the provenance rail. A missing or
corrupt sidecar reads as 'not recorded' rather than a 500 or an invention."
```

---

## Task 6: `DocumentCatalog` — composition and filters

**Files:**
- Create: `src/Smx.Domain/Documents/DocumentCatalog.cs`
- Modify: `src/Smx.Backend.Tests/DocumentCatalogTests.cs` (append a third test class)

- [ ] **Step 1: Append the failing tests**

```csharp
public class DocumentCatalogTests
{
    private readonly InMemorySdsDocumentSource _sds = new();
    private readonly InMemoryRegDocumentSource _reg = new();
    private readonly InMemoryDocumentContentStore _bronze = new();

    private DocumentCatalog Catalog => new(new SdsDocumentProvider(_sds), new RegDocumentProvider(_reg, _bronze));

    private void Given()
    {
        _sds.Sheets.Add(new SdsSheetRow("7761-88-8|sigma|2024-03-11", "7761-88-8", "sigma", "Silver nitrate",
            "2024-03-11", "EU", "en", "https://x.test/a.pdf", "sds/7761-88-8/sigma/2024-03-11.pdf", true,
            "2026-07-16T00:00:00Z", null, null));
        _sds.Master.Add(new SdsMasterRow("Nd_oxide", "Nd", "oxide", "1313-97-9", "failed", "2026-07-18T00:00:00Z", 3));
        _reg.Sources.Add(new RegSourceRow("echa-svhc", "REACH SVHC", "ECHA",
            [new RegDocTitleRow("candidate-list", "https://echa.europa.eu/cl", "SVHC candidate list")]));
        _reg.Docs.Add(new RegDocRow("candidate-list", "echa-svhc", "9f", "2025-11-20", "run", "20260701T031400Z"));
    }

    [Fact]
    public async Task ListsBothHalves()
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter());
        Assert.Equal(3, rows.Count);   // 1 sheet + 1 gap + 1 regulation
    }

    [Theory]
    [InlineData(DocumentKinds.Sds, 2)]    // the sheet and the gap
    [InlineData(DocumentKinds.Reg, 1)]
    [InlineData(DocumentKinds.Seed, 0)]
    [InlineData(DocumentKinds.All, 3)]
    public async Task FiltersByKind(string kind, int expected)
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter(Kind: kind));
        Assert.Equal(expected, rows.Count);
    }

    [Theory]
    [InlineData(DocumentStates.Available, 2)]
    [InlineData(DocumentStates.Missing, 1)]
    [InlineData(DocumentStates.All, 3)]
    public async Task FiltersByState(string state, int expected)
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter(State: state));
        Assert.Equal(expected, rows.Count);
    }

    [Theory]
    [InlineData("silver", 1)]
    [InlineData("7761", 1)]
    [InlineData("svhc", 1)]
    [InlineData("Nd", 1)]
    [InlineData("nothing-matches-this", 0)]
    public async Task FiltersBySearchAcrossTitleAndSubtitle(string q, int expected)
    {
        Given();
        var rows = await Catalog.ListAsync(new DocumentFilter(Q: q));
        Assert.Equal(expected, rows.Count);
    }

    [Fact]
    public async Task RoutesGetToTheOwningProvider()
    {
        Given();
        _bronze.Put("regulatory/echa-svhc/candidate-list/20260701T031400Z/meta.json",
            """{"contentType":"text/html","sha256":"9f","sourceUrl":"https://echa.europa.eu/cl",
                "fetchTs":"20260701T031400Z","syncRunId":"run","httpStatus":200}""");

        Assert.NotNull(await Catalog.GetAsync(DocumentId.Encode(DocumentId.Sds, "7761-88-8|sigma|2024-03-11")));
        Assert.NotNull(await Catalog.GetAsync(DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide")));
        Assert.NotNull(await Catalog.GetAsync(DocumentId.Encode(DocumentId.Reg, "echa-svhc/candidate-list")));
    }

    // Spec §3 invariant 2: a malformed id must not reach storage at all. Asserting 'null' alone
    // would not distinguish "rejected" from "resolved and missed".
    [Fact]
    public async Task AMalformedIdNeverTouchesStorage()
    {
        Given();
        Assert.Null(await Catalog.GetAsync("sds_!!!!"));
        Assert.Null(await Catalog.GetAsync("../../etc/passwd"));
        Assert.Null(await Catalog.GetAsync(DocumentId.EncodePayload("reg/../../x")));
        Assert.Empty(_bronze.PathsRead);
    }

    // Ordering must be stable, or the library reshuffles on every poll.
    [Fact]
    public async Task OrdersDeterministically()
    {
        Given();
        var first = await Catalog.ListAsync(new DocumentFilter());
        var second = await Catalog.ListAsync(new DocumentFilter());
        Assert.Equal(first.Select(r => r.Id), second.Select(r => r.Id));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentCatalogTests"
```

Expected: **build error** — `DocumentCatalog` does not exist.

- [ ] **Step 3: Implement `DocumentCatalog`**

Create `src/Smx.Domain/Documents/DocumentCatalog.cs`:

```csharp
namespace Smx.Domain.Documents;

/// Composes the two providers and applies the list filters.
///
/// Assembled on read, never stored (design D3). Both registries are small — nine curated regulatory
/// sources and a per-substance sheet list — and a stored projection would mean touching both ingest
/// pipelines, backfilling everything already written, and keeping a second copy of state that can
/// disagree with the blob store. When the corpus outgrows this, IDocumentCatalog is the seam: the
/// implementation changes and neither the API nor the UI notices.
public sealed class DocumentCatalog(SdsDocumentProvider sds, RegDocumentProvider reg) : IDocumentCatalog
{
    public async Task<IReadOnlyList<DocumentSummary>> ListAsync(DocumentFilter filter, CancellationToken ct = default)
    {
        var rows = new List<DocumentSummary>();
        if (filter.Kind is DocumentKinds.All or DocumentKinds.Sds)
            rows.AddRange(await sds.ListAsync(ct));
        if (filter.Kind is DocumentKinds.All or DocumentKinds.Reg or DocumentKinds.Seed)
            rows.AddRange(await reg.ListAsync(ct));

        IEnumerable<DocumentSummary> q = rows;

        if (filter.Kind != DocumentKinds.All)
            q = q.Where(r => r.Kind == filter.Kind);

        if (filter.State != DocumentStates.All)
            q = q.Where(r => r.State == filter.State);

        if (!string.IsNullOrWhiteSpace(filter.Q))
            q = q.Where(r =>
                r.Title.Contains(filter.Q, StringComparison.OrdinalIgnoreCase) ||
                r.Subtitle.Contains(filter.Q, StringComparison.OrdinalIgnoreCase));

        // Stable order, or the library reshuffles under the operator on every refresh. Missing rows
        // sort first inside their kind: the gaps are the actionable ones.
        return q.OrderBy(r => r.Kind, StringComparer.Ordinal)
                .ThenBy(r => r.Available ? 1 : 0)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
    }

    public async Task<DocumentDetail?> GetAsync(string documentId, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(documentId, out var kind, out _)) return null;
        return kind switch
        {
            DocumentId.Sds or DocumentId.SdsGap => await sds.GetAsync(documentId, ct),
            DocumentId.Reg or DocumentId.Seed => await reg.GetAsync(documentId, ct),
            _ => null,
        };
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentCatalogTests"
```

Expected: **Passed! — Failed: 0**, 16 tests.

- [ ] **Step 5: Commit**

```bash
git add src/Smx.Domain/Documents/DocumentCatalog.cs src/Smx.Backend.Tests/DocumentCatalogTests.cs
git commit -m "feat(documents): the catalog, assembled on read

No projection is stored. Both registries are small, and a stored copy would
mean touching both ingest pipelines, backfilling what is already written, and
maintaining state that can silently disagree with the blob store.
IDocumentCatalog is the seam for when that trade stops holding.

Ordering is stable and puts missing rows first within their kind, because the
gaps are the actionable ones."
```

---

## Task 7: the content stores — local and ADLS

**Files:**
- Create: `src/Smx.Infrastructure/LocalBronzeDocumentStore.cs`, `src/Smx.Infrastructure/BronzeDocumentStore.cs`
- Modify: `src/Smx.Infrastructure/BackendOptions.cs`, `src/Smx.Infrastructure/Smx.Infrastructure.csproj`
- Test: `src/Smx.Backend.Tests/LocalBronzeDocumentStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/LocalBronzeDocumentStoreTests.cs`:

```csharp
using System.Text;
using Smx.Infrastructure;

namespace Smx.Backend.Tests;

public class LocalBronzeDocumentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "smx-bronze-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task ReadsAStoredBlob()
    {
        Write("sds/7761-88-8/sigma/2024-03-11.pdf", "%PDF-1.4 hello");
        var store = new LocalBronzeDocumentStore(_root);

        var bytes = await store.ReadAsync("sds/7761-88-8/sigma/2024-03-11.pdf");

        Assert.Equal("%PDF-1.4 hello", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public async Task OpenReportsTheLength()
    {
        Write("regulatory/a/b/ts/raw.html", "<html></html>");
        var store = new LocalBronzeDocumentStore(_root);

        var opened = await store.OpenAsync("regulatory/a/b/ts/raw.html");

        Assert.NotNull(opened);
        Assert.Equal(13, opened!.Length);
        opened.Stream.Dispose();
    }

    [Fact]
    public async Task ReturnsNullForAMissingBlob()
    {
        var store = new LocalBronzeDocumentStore(_root);
        Assert.Null(await store.ReadAsync("nope/missing.pdf"));
        Assert.Null(await store.OpenAsync("nope/missing.pdf"));
    }

    // Defence in depth. DocumentId already refuses traversal, but this store takes a raw string and
    // must not be the one component that trusts it — the root is a containment boundary, and a
    // future caller may not be DocumentId.
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("sds/../../secrets.txt")]
    [InlineData("/etc/passwd")]
    public async Task RefusesToEscapeTheRoot(string path)
    {
        Write("secrets.txt", "top secret");
        var store = new LocalBronzeDocumentStore(_root);
        Assert.Null(await store.ReadAsync(path));
        Assert.Null(await store.OpenAsync(path));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~LocalBronzeDocumentStoreTests"
```

Expected: **build error** — `LocalBronzeDocumentStore` does not exist.

- [ ] **Step 3: Implement `LocalBronzeDocumentStore`**

Create `src/Smx.Infrastructure/LocalBronzeDocumentStore.cs`:

```csharp
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IDocumentContentStore over a local directory, selected by BRONZE_LOCAL_PATH. Mirrors the repo's
/// existing *_DRY_RUN convention so the viewer is runnable without Azure.
///
/// The root containment check is deliberate duplication: DocumentId already refuses traversal, but
/// this store accepts a raw string and must not be the single component that trusts its caller.
public sealed class LocalBronzeDocumentStore(string root) : IDocumentContentStore
{
    private readonly string _root = Path.GetFullPath(root);

    public Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        if (full is null || !File.Exists(full)) return Task.FromResult<DocumentBytes?>(null);
        var info = new FileInfo(full);
        Stream stream = File.OpenRead(full);
        return Task.FromResult<DocumentBytes?>(new DocumentBytes(stream, info.Length));
    }

    public async Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default)
    {
        var full = Resolve(blobPath);
        if (full is null || !File.Exists(full)) return null;
        return await File.ReadAllBytesAsync(full, ct);
    }

    private string? Resolve(string blobPath)
    {
        if (string.IsNullOrWhiteSpace(blobPath) || Path.IsPathRooted(blobPath)) return null;
        var combined = Path.GetFullPath(Path.Combine(_root, blobPath.Replace('/', Path.DirectorySeparatorChar)));
        // Ordinal, with the separator appended, so "/bronze-evil" cannot pass as inside "/bronze".
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
        return combined.StartsWith(prefix, StringComparison.Ordinal) ? combined : null;
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~LocalBronzeDocumentStoreTests"
```

Expected: **Passed! — Failed: 0**, 6 tests.

- [ ] **Step 5: Add the DataLake package**

In `src/Smx.Infrastructure/Smx.Infrastructure.csproj`, add to the existing `<ItemGroup>` of
`PackageReference` entries (matching the version `Smx.Functions` already pins):

```xml
    <PackageReference Include="Azure.Storage.Files.DataLake" Version="12.20.0" />
```

- [ ] **Step 6: Implement `BronzeDocumentStore`**

Create `src/Smx.Infrastructure/BronzeDocumentStore.cs`:

```csharp
using Azure;
using Azure.Storage.Files.DataLake;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IDocumentContentStore over the ADLS Gen2 `bronze` filesystem, read-only.
///
/// The backend's UAMI already holds Storage Blob Data Contributor at account scope
/// (infra/modules/data.bicep) — this class is the code that was missing, not the permission.
/// It reads and never writes: the interface has no Put, and this feature adds no state.
public sealed class BronzeDocumentStore(DataLakeFileSystemClient filesystem) : IDocumentContentStore
{
    public async Task<DocumentBytes?> OpenAsync(string blobPath, CancellationToken ct = default)
    {
        var file = filesystem.GetFileClient(blobPath);
        try
        {
            var props = await file.GetPropertiesAsync(cancellationToken: ct);
            var response = await file.ReadAsync(ct);
            return new DocumentBytes(response.Value.Content, props.Value.ContentLength);
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    public async Task<byte[]?> ReadAsync(string blobPath, CancellationToken ct = default)
    {
        var file = filesystem.GetFileClient(blobPath);
        try
        {
            var response = await file.ReadAsync(ct);
            using var buffer = new MemoryStream();
            await response.Value.Content.CopyToAsync(buffer, ct);
            return buffer.ToArray();
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }
}
```

- [ ] **Step 7: Add the options**

In `src/Smx.Infrastructure/BackendOptions.cs`, add three properties to the `BackendOptions` record's
init-property section (alongside `IntakeSessionContainer`):

```csharp
    /// The ADLS Gen2 account holding the `bronze` filesystem — the SDS PDFs and regulatory source
    /// documents. Empty in local dev, where BronzeLocalPath takes over.
    public string BronzeAccountName { get; init; } = "";

    public string BronzeFilesystem { get; init; } = "bronze";

    /// Local directory standing in for bronze. Set only in dev; when set it wins over the account.
    public string BronzeLocalPath { get; init; } = "";
```

`From(IConfiguration c)` builds the record positionally and sets init-properties in a **trailing
initializer block** — currently `{ IntakeSessionContainer = c["INTAKE_SESSION_CONTAINER"] ?? "intake-sessions" }`
at the very end of the file. Extend that block:

```csharp
    {
        IntakeSessionContainer = c["INTAKE_SESSION_CONTAINER"] ?? "intake-sessions",
        BronzeAccountName = c["BRONZE_ACCOUNT_NAME"] ?? "",
        BronzeFilesystem = c["BRONZE_FILESYSTEM"] ?? "bronze",
        BronzeLocalPath = c["BRONZE_LOCAL_PATH"] ?? "",
    };
```

- [ ] **Step 7b: Assert the new defaults**

`src/Smx.Orchestrator.Tests/BackendOptionsTests.cs` already pins every default. Add to the same test,
alongside the existing assertions:

```csharp
        Assert.Equal("bronze", o.BronzeFilesystem);                     // default
        Assert.Equal("", o.BronzeAccountName);                          // unset until bicep provides it
        Assert.Equal("", o.BronzeLocalPath);                            // local dev only
```

- [ ] **Step 8: Verify the whole solution still builds and passes**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```

Expected: **Build succeeded**; all tests pass (previous total + 6).

- [ ] **Step 9: Commit**

```bash
git add src/Smx.Infrastructure/ src/Smx.Backend.Tests/LocalBronzeDocumentStoreTests.cs
git commit -m "feat(documents): read bronze from the backend, at last

The backend's identity has held Storage Blob Data Contributor at account scope
since the infra landed; what was missing was a client, an env var and any code
at all. This adds the first two-thirds.

Both stores are read-only by construction. The local store re-checks root
containment even though DocumentId already refuses traversal — it takes a raw
string, and it must not be the one component that trusts its caller."
```

---

## Task 8: the text readers

**Files:**
- Create: `src/Smx.Infrastructure/CosmosRegSilverTextReader.cs`, `src/Smx.Infrastructure/SdsIndexTextReader.cs`, `src/Smx.Domain/Documents/CompositeDocumentTextReader.cs`, `src/Smx.Domain/Documents/SdsChunkOrdinal.cs`
- Test: `src/Smx.Backend.Tests/SdsChunkOrdinalTests.cs`

The two Cosmos/Search readers are thin query wrappers exercised end to end in Task 11 through fakes; the
one piece with real logic — recovering a chunk's ordinal from its AI Search key — is unit-tested here.

- [ ] **Step 1: Write the failing test**

Create `src/Smx.Backend.Tests/SdsChunkOrdinalTests.cs`:

```csharp
using System.Text;
using Smx.Domain.Documents;

namespace Smx.Backend.Tests;

public class SdsChunkOrdinalTests
{
    // Mirrors DedupKey.ForChunk: base64url(registryId) + "-" + ordinal.
    private static string Key(string registryId, int ordinal) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(registryId)).Replace('+', '-').Replace('/', '_')
        + "-" + ordinal;

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(148)]
    public void RecoversTheOrdinal(int ordinal)
    {
        var key = Key("7761-88-8|sigma|2024-03-11", ordinal);
        Assert.Equal(ordinal, SdsChunkOrdinal.From(key));
    }

    // base64url uses '-' as a character, so the ordinal is after the LAST '-', not the first.
    [Fact]
    public void SplitsOnTheLastDashNotTheFirst()
    {
        var key = Key("7761-88-8|sigma|2024-03-11", 12);
        Assert.Contains('-', key[..key.LastIndexOf('-')]);   // guard: the prefix really does contain a dash
        Assert.Equal(12, SdsChunkOrdinal.From(key));
    }

    // An unparseable key must sort last rather than throw or collide at 0 — one malformed key should
    // not silently reorder an entire safety data sheet.
    [Theory]
    [InlineData("nodash")]
    [InlineData("abc-notanumber")]
    [InlineData("")]
    public void UnparseableKeysSortLast(string key)
    {
        Assert.Equal(int.MaxValue, SdsChunkOrdinal.From(key));
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~SdsChunkOrdinalTests"
```

Expected: **build error** — `SdsChunkOrdinal` does not exist.

- [ ] **Step 3: Implement `SdsChunkOrdinal`**

Create `src/Smx.Domain/Documents/SdsChunkOrdinal.cs`:

```csharp
namespace Smx.Domain.Documents;

/// Recovers a chunk's position from its AI Search key.
///
/// `sds-index` has no ordinal field, so ordering must be reconstructed from the key that
/// DedupKey.ForChunk builds: base64url(registryId) + "-" + ordinal. base64url uses '-' as a
/// character, so the split is on the LAST '-'.
///
/// This is a known fragility (spec §11): if DedupKey.ForChunk ever changes shape, SDS chunk order
/// breaks silently. The durable fix is an ordinal field on the index, which needs a re-ingest.
public static class SdsChunkOrdinal
{
    public static int From(string? chunkKey)
    {
        if (string.IsNullOrEmpty(chunkKey)) return int.MaxValue;
        var dash = chunkKey.LastIndexOf('-');
        if (dash < 0 || dash == chunkKey.Length - 1) return int.MaxValue;
        // Unparseable sorts LAST, never 0 — one bad key must not silently reorder a whole sheet.
        return int.TryParse(chunkKey[(dash + 1)..], out var ordinal) ? ordinal : int.MaxValue;
    }
}
```

- [ ] **Step 4: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~SdsChunkOrdinalTests"
```

Expected: **Passed! — Failed: 0**, 7 tests.

- [ ] **Step 5: Implement the composite router**

Create `src/Smx.Domain/Documents/CompositeDocumentTextReader.cs`:

```csharp
namespace Smx.Domain.Documents;

/// Routes a document to the store that holds its chunks: regulatory and seeded documents to
/// reg-silver, safety sheets to the sds-index. Gap rows have no chunks by definition.
public sealed class CompositeDocumentTextReader(IDocumentTextReader regulatory, IDocumentTextReader sds)
    : IDocumentTextReader
{
    public Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!document.Summary.Available) return Task.FromResult<IReadOnlyList<DocumentChunk>>([]);
        return document.Summary.Kind switch
        {
            DocumentKinds.Reg or DocumentKinds.Seed => regulatory.ReadChunksAsync(document, ct),
            DocumentKinds.Sds => sds.ReadChunksAsync(document, ct),
            _ => Task.FromResult<IReadOnlyList<DocumentChunk>>([]),
        };
    }
}
```

- [ ] **Step 6: Implement `CosmosRegSilverTextReader`**

Create `src/Smx.Infrastructure/CosmosRegSilverTextReader.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// Regulatory chunks from `reg-silver` (PK /docId). A point-partition query — the whole document's
/// chunks live in one partition — ordered by chunkIndex.
///
/// Returned VERBATIM (spec §3 invariant 4). Each chunk carries its own citation.entryId, which is
/// what the viewer anchors a citation to.
///
/// camelCase property names in the SELECT: these documents are written by the regsync Functions app
/// with camelCase serialisation, and a PascalCase projection silently returns nulls.
public sealed class CosmosRegSilverTextReader(Container regSilver) : IDocumentTextReader
{
    private sealed record Row(int ChunkIndex, string Text, CitationRow? Citation);
    private sealed record CitationRow(string? EntryId, string? ArticleOrAnnex);

    public async Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(document.Summary.Id, out var kind, out var payload)) return [];
        if (kind != DocumentId.Reg && kind != DocumentId.Seed) return [];
        var docId = DocumentId.SegmentsOf(kind, payload)[1];

        var q = new QueryDefinition(
                "SELECT c.chunkIndex, c.text, c.citation FROM c WHERE c.docId = @docId")
            .WithParameter("@docId", docId);

        var rows = new List<Row>();
        using var it = regSilver.GetItemQueryIterator<Row>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(docId) });
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));

        return rows.OrderBy(r => r.ChunkIndex)
                   .Select(r => new DocumentChunk(r.ChunkIndex, r.Text, r.Citation?.EntryId, r.Citation?.ArticleOrAnnex))
                   .ToList();
    }
}
```

- [ ] **Step 7: Implement `SdsIndexTextReader`**

Create `src/Smx.Infrastructure/SdsIndexTextReader.cs`:

```csharp
using Azure.Search.Documents;
using Azure.Search.Documents.Models;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// SDS chunks from the `sds-index` AI Search index.
///
/// blobPath is NOT filterable on that index (SdsSearchClient.cs:31), so the filter is the dedup
/// triple — cas + supplier + revisionDate — which is exactly DedupKey.ForRegistry and therefore
/// identifies one sheet. Ordering comes from the ordinal encoded in each chunk key, since the index
/// carries no ordinal field.
public sealed class SdsIndexTextReader(SearchClient sdsIndex) : IDocumentTextReader
{
    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string? Content { get; set; }
        public string? GhsSection { get; set; }
    }

    public async Task<IReadOnlyList<DocumentChunk>> ReadChunksAsync(DocumentDetail document, CancellationToken ct = default)
    {
        if (!DocumentId.TryDecode(document.Summary.Id, out var kind, out var payload)) return [];
        if (kind != DocumentId.Sds) return [];

        var parts = DocumentId.SegmentsOf(kind, payload);
        var (cas, supplier, revisionDate) = (parts[0], parts[1], parts[2]);

        var options = new SearchOptions
        {
            Filter = $"cas eq '{Escape(cas)}' and supplier eq '{Escape(supplier)}' and revisionDate eq '{Escape(revisionDate)}'",
            Size = 1000,
        };
        options.Select.Add("id");
        options.Select.Add("content");
        options.Select.Add("ghsSection");

        var response = await sdsIndex.SearchAsync<Row>("*", options, ct);
        var rows = new List<Row>();
        await foreach (var hit in response.Value.GetResultsAsync().WithCancellation(ct))
            if (hit.Document is not null) rows.Add(hit.Document);

        return rows.OrderBy(r => SdsChunkOrdinal.From(r.Id))
                   .Select(r => new DocumentChunk(SdsChunkOrdinal.From(r.Id), r.Content ?? "", null, r.GhsSection))
                   .ToList();
    }

    /// OData string literals escape a single quote by doubling it.
    private static string Escape(string value) => value.Replace("'", "''");
}
```

- [ ] **Step 8: Build and run the full suite**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```

Expected: **Build succeeded**; all tests pass.

- [ ] **Step 9: Commit**

```bash
git add src/Smx.Domain/Documents/SdsChunkOrdinal.cs src/Smx.Domain/Documents/CompositeDocumentTextReader.cs src/Smx.Infrastructure/CosmosRegSilverTextReader.cs src/Smx.Infrastructure/SdsIndexTextReader.cs src/Smx.Backend.Tests/SdsChunkOrdinalTests.cs
git commit -m "feat(documents): what the agent actually read

Chunks are returned verbatim — no re-extraction, no cleanup. The point of this
surface is that a PDF which chunked to garbage stops being invisible, and
cleaning the text up on the way out would destroy exactly that.

Regulatory chunks are a point-partition query on reg-silver. SDS chunks are
filtered by the dedup triple, because blobPath is not filterable on the index,
and ordered by an ordinal decoded from the chunk key, because the index has no
ordinal field. That decode is a known fragility, recorded in the spec."
```

---

## Task 9: `GET /api/documents` and `GET /api/documents/{id}`

**Files:**
- Create: `src/Smx.Backend/Api/DocumentEndpoints.cs`
- Modify: `src/Smx.Backend/Program.cs`
- Test: `src/Smx.Backend.Tests/DocumentEndpointsTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `src/Smx.Backend.Tests/DocumentEndpointsTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Smx.Domain.Documents;
using Smx.Domain.Tests.Fakes;

namespace Smx.Backend.Tests;

public class DocumentEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly InMemorySdsDocumentSource _sds = new();
    private readonly InMemoryRegDocumentSource _reg = new();
    private readonly InMemoryDocumentContentStore _bronze = new();
    private readonly InMemoryDocumentTextReader _text = new();
    private readonly HttpClient _client;

    public DocumentEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.AddSingleton<ISdsDocumentSource>(_sds);
                s.AddSingleton<IRegDocumentSource>(_reg);
                s.AddSingleton<IDocumentContentStore>(_bronze);
                s.AddSingleton<IDocumentTextReader>(_text);
                s.AddSingleton<IDocumentCatalog>(sp => new DocumentCatalog(
                    new SdsDocumentProvider(sp.GetRequiredService<ISdsDocumentSource>()),
                    new RegDocumentProvider(sp.GetRequiredService<IRegDocumentSource>(),
                                            sp.GetRequiredService<IDocumentContentStore>())));
            })).CreateClient();
    }

    private const string SheetId = "7761-88-8|sigma|2024-03-11";

    private void GivenASheet() => _sds.Sheets.Add(new SdsSheetRow(
        SheetId, "7761-88-8", "sigma", "Silver nitrate", "2024-03-11", "EU", "en",
        "https://x.test/a.pdf", "sds/7761-88-8/sigma/2024-03-11.pdf", true, "2026-07-16T00:00:00Z", null, null));

    private void GivenAGap() => _sds.Master.Add(new SdsMasterRow(
        "Nd_oxide", "Nd", "oxide", "1313-97-9", "failed", "2026-07-18T00:00:00Z", 3));

    [Fact]
    public async Task List_ReturnsEmptyArrayOnColdStart()
    {
        var json = await _client.GetFromJsonAsync<JsonElement>("/documents");
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task List_ReturnsSheetsAndGaps()
    {
        GivenASheet(); GivenAGap();
        var json = await _client.GetFromJsonAsync<JsonElement>("/documents");
        Assert.Equal(2, json.GetArrayLength());
    }

    [Fact]
    public async Task List_FiltersByStateAndQuery()
    {
        GivenASheet(); GivenAGap();
        var missing = await _client.GetFromJsonAsync<JsonElement>("/documents?state=missing");
        Assert.Equal(1, missing.GetArrayLength());
        var q = await _client.GetFromJsonAsync<JsonElement>("/documents?q=silver");
        Assert.Equal(1, q.GetArrayLength());
    }

    [Fact]
    public async Task Detail_ReturnsProvenance()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");
        Assert.True(json.GetProperty("summary").GetProperty("available").GetBoolean());
        Assert.True(json.GetProperty("provenance").GetArrayLength() > 0);
    }

    // The blob path is an internal detail. Leaking it to the client would hand back exactly the
    // string the id scheme exists to keep out of the API surface.
    [Fact]
    public async Task Detail_NeverLeaksTheBlobPath()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        var body = await _client.GetStringAsync($"/documents/{id}");
        Assert.DoesNotContain("blobPath", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sds/7761-88-8/sigma", body, StringComparison.Ordinal);
    }

    // Spec §8: a gap row is 200-with-a-reason, not 404. It is a known absence, not a lookup failure.
    [Fact]
    public async Task Detail_OfAGapRow_Is200WithAStatedReason()
    {
        GivenAGap();
        var id = DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide");
        var res = await _client.GetAsync($"/documents/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
        Assert.False(json.GetProperty("summary").GetProperty("available").GetBoolean());
        Assert.Equal("never-fetched", json.GetProperty("unavailableReason").GetString());
    }

    [Fact]
    public async Task Detail_404sForUnknownAndMalformedIds()
    {
        foreach (var id in new[] { DocumentId.Encode(DocumentId.Sds, "0-0|nobody|1999-01-01"), "sds_!!!!", "garbage" })
            Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/documents/{id}")).StatusCode);
    }

    // Spec §3 invariant 2: rejection happens before storage is consulted.
    [Fact]
    public async Task AMalformedIdNeverReachesStorage()
    {
        await _client.GetAsync("/documents/sds_!!!!");
        await _client.GetAsync("/documents/nope_YWJj");
        Assert.Empty(_bronze.PathsRead);
    }
}
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentEndpointsTests"
```

Expected: all tests **fail with 404**, because the routes do not exist yet.

- [ ] **Step 3: Implement the two endpoints**

Create `src/Smx.Backend/Api/DocumentEndpoints.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Smx.Domain;
using Smx.Domain.Documents;

namespace Smx.Backend.Api;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        // [FromServices] is required, not decorative — see the comment in KnowledgeEndpoints: without
        // it, minimal APIs may infer the service as a request body, which is illegal on GET and
        // breaks routing for the ENTIRE app while building the composite endpoint data source.
        app.MapGet("/documents", async (string? kind, string? q, string? state,
            [FromServices] IDocumentCatalog catalog, CancellationToken ct) =>
        {
            var filter = new DocumentFilter(
                Kind: kind is { Length: > 0 } ? kind : DocumentKinds.All,
                Q: q,
                State: state is { Length: > 0 } ? state : DocumentStates.All);
            return Results.Json(await catalog.ListAsync(filter, ct), Json.Options);
        });

        app.MapGet("/documents/{id}", async (string id, [FromServices] IDocumentCatalog catalog,
            CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            return Results.Json(ToWire(detail), Json.Options);
        });
    }

    /// The wire shape drops BlobPath. Returning it would hand the client the exact string the id
    /// scheme exists to keep out of the API surface.
    internal static object ToWire(DocumentDetail d) => new
    {
        summary = d.Summary,
        provenance = d.Provenance,
        unavailableReason = d.UnavailableReason,
        unavailableDetail = d.UnavailableDetail,
        supersededById = d.SupersededById,
    };
}
```

- [ ] **Step 4: Register in `src/Smx.Backend/Program.cs`**

Add to the endpoint-mapping block, after `app.MapKnowledgeEndpoints();`:

```csharp
app.MapDocumentEndpoints();
```

- [ ] **Step 5: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentEndpointsTests"
```

Expected: **Passed! — Failed: 0**, 8 tests.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Api/DocumentEndpoints.cs src/Smx.Backend/Program.cs src/Smx.Backend.Tests/DocumentEndpointsTests.cs
git commit -m "feat(documents): list and detail

The wire shape deliberately drops blobPath — returning it would hand back the
exact string the id scheme exists to keep out of the API.

A gap row answers 200 with a stated reason rather than 404. It is a known
absence, not a lookup failure, and the difference is what lets the library
show a missing MSDS as an actionable row instead of an error."
```

---

## Task 10: `GET /documents/{id}/content` and `/text`

**Files:**
- Modify: `src/Smx.Backend/Api/DocumentEndpoints.cs`, `src/Smx.Backend.Tests/DocumentEndpointsTests.cs`

- [ ] **Step 1: Append the failing tests to `DocumentEndpointsTests`**

```csharp
    [Fact]
    public async Task Content_StreamsTheStoredBytesWithSafetyHeaders()
    {
        GivenASheet();
        _bronze.Put("sds/7761-88-8/sigma/2024-03-11.pdf", "%PDF-1.4 hello");
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var res = await _client.GetAsync($"/documents/{id}/content");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF-1.4 hello", await res.Content.ReadAsStringAsync());
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        // The CSP sandbox directive makes the browser treat the response as sandboxed however it is
        // framed — belt to the frontend's braces, which never same-origins this content anyway.
        Assert.Contains("sandbox", res.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("inline", res.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Content_DownloadFlagSwitchesToAttachment()
    {
        GivenASheet();
        _bronze.Put("sds/7761-88-8/sigma/2024-03-11.pdf", "%PDF");
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var res = await _client.GetAsync($"/documents/{id}/content?download=1");

        Assert.Equal("attachment", res.Content.Headers.ContentDisposition!.DispositionType);
        Assert.False(string.IsNullOrEmpty(res.Content.Headers.ContentDisposition.FileNameStar
                                          ?? res.Content.Headers.ContentDisposition.FileName));
    }

    // Registry says the sheet exists, storage disagrees. That is real drift and it gets its own
    // reason on the detail endpoint rather than a bare error.
    [Fact]
    public async Task Content_404sWhenTheBlobIsMissing()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);

        var res = await _client.GetAsync($"/documents/{id}/content");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}");
        Assert.Equal("blob-missing", detail.GetProperty("unavailableReason").GetString());
    }

    // A gap id is VALID and its document is knowably absent — neither 404 nor 500. 409 says
    // "this exists as a record and has no file", which is exactly the state.
    [Fact]
    public async Task Content_409sForAGapRow()
    {
        GivenAGap();
        var id = DocumentId.Encode(DocumentId.SdsGap, "Nd_oxide");
        var content = await _client.GetAsync($"/documents/{id}/content");
        var text = await _client.GetAsync($"/documents/{id}/text");
        Assert.Equal(HttpStatusCode.Conflict, content.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, text.StatusCode);
    }

    [Fact]
    public async Task Text_ReturnsChunksInOrder()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        _text.Chunks[id] =
        [
            new DocumentChunk(0, "Section 1 identification", null, "1"),
            new DocumentChunk(1, "Section 2 hazards", null, "2"),
        ];

        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}/text");

        Assert.Equal(2, json.GetArrayLength());
        Assert.Equal("Section 1 identification", json[0].GetProperty("text").GetString());
    }

    // Spec §8: a document in bronze that never reached the index is a real and important state.
    // An empty array is the honest answer; the viewer says what it means.
    [Fact]
    public async Task Text_ReturnsEmptyArrayWhenNothingWasIndexed()
    {
        GivenASheet();
        var id = DocumentId.Encode(DocumentId.Sds, SheetId);
        var json = await _client.GetFromJsonAsync<JsonElement>($"/documents/{id}/text");
        Assert.Equal(0, json.GetArrayLength());
    }

    [Fact]
    public async Task ContentAndText_404ForMalformedIds()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/documents/sds_!!!!/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/documents/sds_!!!!/text")).StatusCode);
        Assert.Empty(_bronze.PathsRead);
    }
```

Also add this using to the top of the file if not already present:

```csharp
using System.Net.Http.Json;
```

- [ ] **Step 2: Run to verify failure**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentEndpointsTests"
```

Expected: the seven new tests fail with **404** (routes missing).

- [ ] **Step 3: Add `MaxInlineBytes` and the two routes**

In `src/Smx.Backend/Api/DocumentEndpoints.cs`, add inside `MapDocumentEndpoints`, after the detail route:

```csharp
        app.MapGet("/documents/{id}/content", async (string id, bool? download,
            [FromServices] IDocumentCatalog catalog, [FromServices] IDocumentContentStore store,
            CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            // Valid id, knowably absent document. Not 404 (the record exists) and not 500 (nothing
            // failed) — 409 is the state itself.
            if (detail.BlobPath is null) return Results.Conflict(new { reason = detail.UnavailableReason });

            var opened = await store.OpenAsync(detail.BlobPath, ct);
            if (opened is null) return Results.NotFound();

            var contentType = detail.Summary.ContentType ?? "application/octet-stream";
            var wantsDownload = download is true;

            var response = Results.Stream(opened.Stream, contentType,
                fileDownloadName: wantsDownload ? FileNameFor(detail) : null,
                enableRangeProcessing: true);
            return response;
        }).AddEndpointFilter(async (ctx, next) =>
        {
            // Applied as a filter so the headers are set even on the streaming path, where the result
            // writes the body itself.
            ctx.HttpContext.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.HttpContext.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            return await next(ctx);
        });

        app.MapGet("/documents/{id}/text", async (string id, [FromServices] IDocumentCatalog catalog,
            [FromServices] IDocumentTextReader reader, CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();
            if (detail.BlobPath is null) return Results.Conflict(new { reason = detail.UnavailableReason });

            // An empty list is the honest answer for a document that reached bronze but never the
            // index — it means no agent has ever read it, and the viewer says so.
            return Results.Json(await reader.ReadChunksAsync(detail, ct), Json.Options);
        });
```

And add this helper to the class:

```csharp
    /// A download filename derived from the document, never from client input.
    private static string FileNameFor(DocumentDetail d)
    {
        var ext = (d.Summary.ContentType ?? "") switch
        {
            var c when c.Contains("pdf", StringComparison.OrdinalIgnoreCase) => "pdf",
            var c when c.Contains("html", StringComparison.OrdinalIgnoreCase) => "html",
            var c when c.Contains("csv", StringComparison.OrdinalIgnoreCase) => "csv",
            var c when c.Contains("json", StringComparison.OrdinalIgnoreCase) => "json",
            var c when c.Contains("xml", StringComparison.OrdinalIgnoreCase) => "xml",
            _ => "txt",
        };
        var stem = new string(d.Summary.Title.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray())
            .Trim('-');
        if (stem.Length == 0) stem = "document";
        if (stem.Length > 80) stem = stem[..80];
        return $"{stem}.{ext}";
    }
```

- [ ] **Step 4: Make `blob-missing` reachable on the detail endpoint**

The detail endpoint must report `blob-missing` when the registry row resolves but the blob is gone.
In `DocumentEndpoints.MapDocumentEndpoints`, replace the detail route body with:

```csharp
        app.MapGet("/documents/{id}", async (string id, [FromServices] IDocumentCatalog catalog,
            [FromServices] IDocumentContentStore store, CancellationToken ct) =>
        {
            var detail = await catalog.GetAsync(id, ct);
            if (detail is null) return Results.NotFound();

            // Drift check: the registry says this document exists. If storage disagrees, say so
            // here rather than letting the viewer discover it as a blank pane.
            if (detail.BlobPath is not null && await store.ReadAsync(detail.BlobPath, ct) is null)
                detail = detail with
                {
                    Summary = detail.Summary with { Available = false, State = DocumentStates.Missing },
                    UnavailableReason = UnavailableReasons.BlobMissing,
                    UnavailableDetail = "The registry lists this document but it is not in storage.",
                };

            return Results.Json(ToWire(detail), Json.Options);
        });
```

> Note: this reads the whole blob to check existence. Acceptable for SDS PDFs and regulatory
> documents at current sizes; if it becomes a problem, add an `ExistsAsync` to `IDocumentContentStore`.

- [ ] **Step 5: Run to verify pass**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~DocumentEndpointsTests"
```

Expected: **Passed! — Failed: 0**, 15 tests.

- [ ] **Step 6: Run the whole suite**

```bash
dotnet test src/Smx.Backend.sln
```

Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/Smx.Backend/Api/DocumentEndpoints.cs src/Smx.Backend.Tests/DocumentEndpointsTests.cs
git commit -m "feat(documents): serve the bytes, and the chunks

Content carries nosniff and a CSP sandbox directive, set through an endpoint
filter so they survive the streaming path. Those are belt to the frontend's
braces: it never renders this content same-origin either.

Three distinct failures get three distinct answers. A malformed id is 404. A
gap row is 409 — valid id, knowably absent file. A registry row whose blob has
vanished is 404 on content and 'blob-missing' on detail, because that is drift
and it should be named rather than discovered as a blank pane."
```

---

## Task 11: production wiring and infra

**Files:**
- Modify: `src/Smx.Backend/Program.cs`, `infra/modules/compute.bicep`, `infra/single-rg/modules/compute.bicep`

- [ ] **Step 1: Wire the services in `src/Smx.Backend/Program.cs`**

Inside the `if (builder.Configuration["COSMOS_ACCOUNT_ENDPOINT"] is { Length: > 0 })` block, after the
existing `ISdsCorpusReader` registration, add:

```csharp
    // Document access layer. The UAMI already holds Storage Blob Data Contributor at account scope;
    // BRONZE_LOCAL_PATH lets local dev stand in a directory for the filesystem.
    builder.Services.AddSingleton<IDocumentContentStore>(_ =>
        opts.BronzeLocalPath is { Length: > 0 }
            ? new LocalBronzeDocumentStore(opts.BronzeLocalPath)
            : new BronzeDocumentStore(new DataLakeServiceClient(
                new Uri($"https://{opts.BronzeAccountName}.dfs.core.windows.net"), credential)
                .GetFileSystemClient(opts.BronzeFilesystem)));

    builder.Services.AddSingleton<ISdsDocumentSource>(sp => new CosmosSdsDocumentSource(
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, opts.SdsRegistryContainer),
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, "sds-master-list")));

    builder.Services.AddSingleton<IRegDocumentSource>(sp => new CosmosRegDocumentSource(
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, "reg-registry"),
        sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, "reg-state")));

    builder.Services.AddSingleton<IDocumentCatalog>(sp => new DocumentCatalog(
        new SdsDocumentProvider(sp.GetRequiredService<ISdsDocumentSource>()),
        new RegDocumentProvider(sp.GetRequiredService<IRegDocumentSource>(),
                                sp.GetRequiredService<IDocumentContentStore>())));

    builder.Services.AddSingleton<IDocumentTextReader>(sp => new CompositeDocumentTextReader(
        new CosmosRegSilverTextReader(
            sp.GetRequiredService<CosmosClient>().GetContainer(opts.CosmosDatabase, "reg-silver")),
        new SdsIndexTextReader(new SearchClient(
            new Uri(opts.SearchEndpoint), opts.SdsIndex, credential))));
```

Add the needed usings at the top of `Program.cs`:

```csharp
using Azure.Search.Documents;
using Azure.Storage.Files.DataLake;
using Smx.Domain.Documents;
```

> The container names `sds-master-list`, `reg-registry`, `reg-state` and `reg-silver` are literals
> here because they belong to the Functions app's estate, not the backend's configuration surface.
> If a later change makes them configurable, add them to `BackendOptions` alongside `SdsRegistryContainer`.

- [ ] **Step 2: Write the two Cosmos sources**

Create `src/Smx.Infrastructure/CosmosSdsDocumentSource.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// ISdsDocumentSource over the SDS library subsystem's containers. Read-only: this side never
/// writes the corpus.
///
/// camelCase projections throughout — these documents are written by the regsync Functions app with
/// camelCase serialisation, and a PascalCase SELECT silently yields nulls.
public sealed class CosmosSdsDocumentSource(Container sdsRegistry, Container sdsMasterList) : ISdsDocumentSource
{
    private const string SheetSelect =
        "SELECT c.id, c.cas, c.supplier, c.productName, c.revisionDate, c.region, c.language," +
        " c.sourceUrl, c.blobPath, c.indexed, c.ingestedUtc, c.supersededBy, c.masterListId FROM c";

    private const string MasterSelect =
        "SELECT c.id, c.element, c.form, c.cas, c.status, c.lastAttemptUtc, c.attemptCount FROM c";

    public async Task<IReadOnlyList<SdsSheetRow>> ListSheetsAsync(CancellationToken ct = default)
    {
        var rows = new List<SdsSheetRow>();
        using var it = sdsRegistry.GetItemQueryIterator<SdsSheetRow>(new QueryDefinition(SheetSelect));
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    public async Task<SdsSheetRow?> GetSheetAsync(string registryId, string cas, CancellationToken ct = default)
    {
        try
        {
            var resp = await sdsRegistry.ReadItemAsync<SdsSheetRow>(registryId, new PartitionKey(cas), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }

    public async Task<IReadOnlyList<SdsMasterRow>> ListMasterAsync(CancellationToken ct = default)
    {
        var rows = new List<SdsMasterRow>();
        using var it = sdsMasterList.GetItemQueryIterator<SdsMasterRow>(new QueryDefinition(MasterSelect));
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    public async Task<SdsMasterRow?> GetMasterAsync(string masterId, string element, CancellationToken ct = default)
    {
        try
        {
            var resp = await sdsMasterList.ReadItemAsync<SdsMasterRow>(masterId, new PartitionKey(element), cancellationToken: ct);
            return resp.Resource;
        }
        catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound) { return null; }
    }
}
```

Create `src/Smx.Infrastructure/CosmosRegDocumentSource.cs`:

```csharp
using Microsoft.Azure.Cosmos;
using Smx.Domain.Documents;

namespace Smx.Infrastructure;

/// IRegDocumentSource over `reg-registry` (curated sources, PK /sourceId) and `reg-state`
/// (per-document change-detection state, PK /sourceId, id = docId). Read-only.
public sealed class CosmosRegDocumentSource(Container regRegistry, Container regState) : IRegDocumentSource
{
    public async Task<IReadOnlyList<RegSourceRow>> ListSourcesAsync(CancellationToken ct = default)
    {
        var rows = new List<RegSourceRow>();
        var q = new QueryDefinition("SELECT c.sourceId, c.regulation, c.authority, c.documents FROM c");
        using var it = regRegistry.GetItemQueryIterator<RegSourceRow>(q);
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    public async Task<IReadOnlyList<RegDocRow>> ListDocsAsync(CancellationToken ct = default)
    {
        var rows = new List<RegDocRow>();
        // reg-state's `id` IS the docId (SyncPipeline.cs / SeedImporter.cs both upsert it that way).
        var q = new QueryDefinition(
            "SELECT c.id AS docId, c.sourceId, c.sha256, c.officialDate, c.syncRunId, c.lastFetchTs FROM c");
        using var it = regState.GetItemQueryIterator<RegDocRow>(q);
        while (it.HasMoreResults) rows.AddRange(await it.ReadNextAsync(ct));
        return rows;
    }

    public async Task<RegDocRow?> GetDocAsync(string docId, string sourceId, CancellationToken ct = default)
    {
        var q = new QueryDefinition(
                "SELECT c.id AS docId, c.sourceId, c.sha256, c.officialDate, c.syncRunId, c.lastFetchTs" +
                " FROM c WHERE c.id = @docId")
            .WithParameter("@docId", docId);
        using var it = regState.GetItemQueryIterator<RegDocRow>(q,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(sourceId) });
        while (it.HasMoreResults)
        {
            var page = await it.ReadNextAsync(ct);
            foreach (var row in page) return row;
        }
        return null;
    }
}
```

- [ ] **Step 3: Add the env vars to both bicep twins**

In **both** `infra/modules/compute.bicep` and `infra/single-rg/modules/compute.bicep`, add to the shared
container-app env list (near `SDS_REGISTRY_CONTAINER`):

```bicep
  // The bronze filesystem holds the SDS PDFs and the regulatory source documents. The workload UAMI
  // already has Storage Blob Data Contributor at account scope (data.bicep) — this only tells the
  // backend where to look. Read-only from this side; the regsync Functions app is the only writer.
  { name: 'BRONZE_ACCOUNT_NAME', value: bronzeAccountName }
  { name: 'BRONZE_FILESYSTEM', value: 'bronze' }
```

Add the parameter near the other storage-related params in both files:

```bicep
@description('Storage account holding the bronze filesystem (SDS PDFs + regulatory source documents).')
param bronzeAccountName string
```

Then pass it from each caller. In `infra/main.bicep`, find the `module compute` block and add:

```bicep
    bronzeAccountName: data.outputs.storageName
```

Do the same in `infra/single-rg/main.bicep`.

> The output is `storageName` (`infra/modules/data.bicep:256`), **not** `storageAccountName` — both
> twins expose it at the same line. `data.bicep` also already outputs `bronzeFilesystem` (`:259`),
> so if you prefer to thread that through rather than hardcode `'bronze'`, the output is there.

- [ ] **Step 4: Verify both bicep variants compile**

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null && echo "main OK"
az bicep build --file infra/single-rg/main.bicep --stdout > /dev/null && echo "single-rg OK"
```

Expected: both print OK. (A pre-existing `BCP037` warning about `modelProviderData` in `ai.bicep` is
unrelated and expected.)

- [ ] **Step 5: Build and run the full suite**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```

Expected: **Build succeeded**; all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Smx.Backend/Program.cs src/Smx.Infrastructure/CosmosSdsDocumentSource.cs src/Smx.Infrastructure/CosmosRegDocumentSource.cs infra/
git commit -m "feat(documents): production wiring and the bronze env vars

Every Cosmos projection is camelCase, because these containers are written by
the regsync Functions app and a PascalCase SELECT returns nulls silently
rather than failing.

No RBAC changes: the workload UAMI has held Storage Blob Data Contributor at
account scope since the infra landed. Both bicep twins get the env var, as
they must — they are twins, not alternatives."
```

---

## Task 12: verify the whole layer end to end

- [ ] **Step 1: Full build and test**

```bash
dotnet build src/Smx.Backend.sln && dotnet test src/Smx.Backend.sln
```

Expected: **Build succeeded**, **Failed: 0** across `Smx.Backend.Tests`, `Smx.Orchestrator.Tests`, `Smx.Eval.Tests`.

- [ ] **Step 2: Confirm the invariants are actually covered**

```bash
dotnet test src/Smx.Backend.sln --filter "FullyQualifiedName~Document" --logger "console;verbosity=normal" 2>&1 | grep -c "Passed"
```

Then confirm by eye that these named tests exist and pass:
- `RejectsDangerousPayloads`, `RejectsWrongSegmentCount` (invariants 1–2)
- `AMalformedIdNeverTouchesStorage`, `AMalformedIdNeverReachesStorage` (invariant 2)
- `StatesNotRecordedWhenTheSidecarIsAbsent` (invariant 6)
- `Text_ReturnsEmptyArrayWhenNothingWasIndexed` (spec §8)
- `Detail_NeverLeaksTheBlobPath` (D2)

- [ ] **Step 3: Confirm nothing writes**

```bash
grep -rn "PutAsync\|UpsertAsync\|CreateItem\|DeleteItem\|MergeOrUpload" src/Smx.Domain/Documents/ src/Smx.Backend/Api/DocumentEndpoints.cs
```

Expected: **no output**. Spec §3 invariant 7 — this layer reads and never writes.

- [ ] **Step 4: Commit any fixes, then tag the milestone**

```bash
git add -A && git commit -m "test(documents): plan 1 complete — the access layer holds"
```

---

## Plan 1 self-review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §2 D1 (stream through backend) | 10 |
| §2 D2 (id, never a path) | 1, 9 |
| §2 D3 (read-time catalog) | 6 |
| §2 D4 (hosted in backend) | 9, 11 |
| §2 D9 (gap rows first-class) | 4 |
| §2 D10 (writes nothing) | 12 step 3 |
| §3 invariants 1–2 | 1, 6, 9 |
| §3 invariant 4 (verbatim chunks) | 8 |
| §3 invariant 5 (says why) | 9, 10 |
| §3 invariant 6 (never inferred) | 5 |
| §3 invariant 7 (no writes) | 3, 12 |
| §5 identity table (4 kinds) | 1, 4, 5 |
| §6 four endpoints | 9, 10 |
| §8 failure modes | 9, 10 |
| §9 configuration | 7, 11 |
| §10 backend testing | 1, 4, 5, 6, 7, 8, 9, 10 |

**Not covered here, by design:** §7 (the frontend) and the 25 MB inline cap, which is a rendering
decision and lives in Plan 2. §3 invariant 3 (HTML never same-origin) is likewise a frontend
invariant — the backend's contribution is the CSP sandbox header, covered in Task 10.

**Type consistency check:** `DocumentSummary`, `DocumentDetail`, `ProvenanceField`, `DocumentChunk`,
`DocumentFilter` are defined once in Task 2 and used unchanged in Tasks 4–10. `SdsSheetRow`,
`SdsMasterRow`, `RegDocRow`, `RegSourceRow`, `RegDocTitleRow` likewise. `DocumentId.Sds/Reg/Seed/SdsGap`
(id kinds) and `DocumentKinds.Sds/Reg/Seed/All` (facets) are deliberately different sets — Task 4's
`ToGapSummary` is where the two meet, mapping an `sdsgap` id onto an `sds` facet.

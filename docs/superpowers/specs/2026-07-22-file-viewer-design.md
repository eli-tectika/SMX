# File Viewer — the document access layer and its reader

**Status:** design, 2026-07-22
**Scope:** a new `IDocumentCatalog` + document endpoints in `Smx.Backend`, an ADLS reader in
`Smx.Infrastructure`, and two new frontend surfaces (`/docs` library, `/docs/:id` viewer) plus a reusable
overlay mount.
**Base branch:** `feat/sds-first-sync` (the corpus-backed `GET /msds-registry` this design attaches to lives
only there; `origin/main` has diverged and lacks it).
**Depends on:** nothing unbuilt. Every byte this feature renders is already in Bronze today.

---

## 1. Why this exists

SMX stores documents and cannot show them to anyone.

Three Bronze prefixes are live and populated: `sds/{cas}/{supplier}/{rev}.pdf`
(`Sds/Ingestion/IngestionPipeline.cs:27-28`), `regulatory/{sourceId}/{docId}/{fetchTs}/raw.{ext}` plus a
`meta.json` sidecar (`Reg/Ingestion/BronzeIngestor.cs:47-49`), and `seed/{region}/{docId}/raw.txt`
(`Reg/Seeding/SeedImporter.cs:109-116`). The regulatory ingestor deliberately preserves the original
content type — `ExtensionFor` maps to `csv | json | xml | html | pdf | bin` (`BronzeIngestor.cs:58-68`) —
so original PDFs and HTML are retained verbatim, immutably, one folder per fetch.

**Nothing can read any of it.** `IBronzeStore.GetAsync` (`Sds/Data/IBronzeStore.cs`) has no caller outside
tests. There is no SAS or user-delegation-key code anywhere in `src/` or `tools/`. `Smx.Backend` has no
`Azure.Storage.*` package reference at all — though its managed identity already holds **Storage Blob Data
Contributor at account scope** (`infra/modules/data.bicep:28-29, 58-66`), and the backend and orchestrator
container apps run under that same UAMI (`infra/modules/compute.bicep:145, 196-200`). The permission exists;
the code does not.

The consequences are concrete, not theoretical:

- **`GET sds/substance` returns `blobPath` as a bare string** (`Sds/Triggers/GetSdsForSubstance.cs:27`) that
  no consumer can resolve into content.
- **The MSDS Registry gates procurement.** An order stays blocked until its MSDS is "current and reviewed"
  (`UX_Spec.md:139`, `:129`). The operator signs that review on `/msds-registry` — a screen that cannot
  display the safety data sheet being signed off. The gate is real; the reading is imaginary.
- **`CitationChip` has no href.** It renders `source · reference · date` as inert text
  (`smx-web/src/components/ui/Primitives.tsx:175-196`). Every regulatory verdict claims a traceable source
  and none of them can be opened. Meanwhile `domain/blocking.ts:82-87` blocks on an uncited verdict with the
  words *"Untraceable, therefore unusable"* — a standard the system applies to the agent and not to itself.
- **Extraction failures are invisible.** Agents never read these files; they read chunks (`SdsChunk`,
  `SilverChunk`). A PDF that renders perfectly to a human but chunked to garbage silently poisons every
  verdict citing it, and there is no surface on which the two could be compared.

### What this is not

Not an upload feature. Not a project-attachment feature. Not an archive of past experiments — no such data
exists anywhere in the repo, and the chemistry design records that explicitly
(`2026-07-12-chemistry-backend-end-to-end-design.md:37`: *"No historical projects to seed from."*). Those
categories were considered and cut in §12.

---

## 2. Decisions

| # | Decision | Rationale |
|---|---|---|
| **D1** | Bytes stream **through the backend API**; no SAS URLs, ever. | The storage account is `networkAcls.defaultAction: 'Deny'` behind private endpoints (`data.bicep:36-66`) and the browser reaches the app through App Gateway. A SAS URL would be unreachable from the browser *and* would put a time-boxed public hole in a private-by-default architecture. |
| **D2** | The API accepts a **document id**, never a blob path. | `/api/documents?path=…` is an arbitrary-blob-read primitive against a container holding the entire regulatory corpus. Ids resolve through a registry point read, so only catalogued documents are reachable. |
| **D3** | The catalog is **assembled on read**, never stored. | Both registries are small (9 regulatory sources; the SDS list is per-substance). Writing a projection means touching both ingest pipelines, backfilling everything already stored, and maintaining a second copy of state that can silently disagree with the blob store. Deferred to §12, behind an interface that makes it a drop-in. |
| **D4** | Hosted in **`Smx.Backend`**, not `Smx.Functions`. | The frontend speaks only to the backend; App Gateway's `apiPathRule` routes `/api/*` to the backend container. The Functions HTTP triggers are `AuthorizationLevel.Anonymous`. Adding a second origin means a second gateway route and a second auth surface for a read path that must be authenticated. |
| **D5** | The viewer has **two faces**: the original bytes, and the indexed chunks. | The agent reasons over chunks. Showing only the PDF hides the artifact that actually drives verdicts; showing only the chunks hides whether extraction was faithful. Both, side by side, is the only honest presentation. |
| **D6** | Rendered at a **permanent route** `/docs/:id`, with an overlay as a second mount of the same component. | Gates are operator-signed records and Learned Conclusions carry provenance. "The document I signed off against" must be a durable reference, not a modal that vanished. The overlay serves quick peeks without losing stage scroll position. |
| **D7** | HTML originals render in `<iframe sandbox="" srcdoc>` — **never** a `blob:` URL. | A `blob:` URL inherits the creating document's origin. Regulatory HTML fetched from the open web, rendered in an origin-inheriting frame, is stored XSS against the operator's session. `sandbox=""` grants neither `allow-scripts` nor `allow-same-origin`. |
| **D8** | Citation chips become clickable **only on real-endpoint screens**. | Discovery, Dosing, Cost and Decision render fixtures behind a `MockBadge`. Wiring a fabricated citation to a real document viewer launders mock data into something that looks agent-produced — precisely what the badge exists to prevent (`CLAUDE.md`, frontend section). |
| **D9** | Gap rows — substances with **no** sheet — are first-class entries in the library. | `MasterListEntry.Status` already tracks `pending \| failed \| awaiting_operator` (`Sds/Domain/Models.cs:3-9`). A missing MSDS is the thing that blocks an order. Listing only files that exist would make absence read as coverage. |
| **D10** | This feature **writes nothing**. Read-only against Cosmos, AI Search, and Bronze. | No new state, therefore no new drift, no migration, and no way for the viewer to corrupt what it displays. |

---

## 3. Hard invariants (each has a test)

1. **No endpoint accepts a blob path or any path fragment.** The only input is an opaque document id.
2. **A decoded id that does not resolve to a registry row yields 404** — never a blob read attempt.
3. **HTML content never reaches a `blob:` URL or a same-origin frame.** Asserted directly in the frontend
   test suite, because this is the kind of invariant a later "optimization" quietly removes.
4. **The text view returns stored chunks verbatim.** No re-extraction, no cleanup, no re-chunking. If the
   index holds garbage, the operator sees garbage.
5. **A document that cannot be displayed states why.** No blank pane, no silent empty list.
6. **Provenance fields are never inferred.** A missing `meta.json` yields *"not recorded"*, not a plausible
   substitute.
7. **The feature performs no writes.** Asserted by fake stores that throw on any write call.

---

## 4. Architecture

```
smx-web
  routes/Documents.tsx ────────┐
  routes/DocumentView.tsx ─────┼──> components/FileViewer.tsx ──> api/client.ts
  components/FileViewerOverlay.tsx ┘                                    │
                                                                       │ /api/documents*
Smx.Backend                                                            ▼
  Api/DocumentEndpoints.cs
        │
        ├── IDocumentCatalog ─┬─ SdsDocumentProvider  ──> sds-registry, sds-master-list (Cosmos)
        │   (Smx.Domain)      └─ RegDocumentProvider  ──> reg-registry, reg-state       (Cosmos)
        │
        ├── IDocumentTextReader ─┬─ reg-silver (Cosmos, pk /docId)
        │                        └─ sds-index  (AI Search)
        │
        └── IDocumentContentStore ──> BronzeDocumentStore (Smx.Infrastructure)
                                        └─ DataLakeServiceClient, workload UAMI, filesystem `bronze`
```

### Files

**New — `Smx.Domain`**

| File | Contents |
|---|---|
| `Documents/DocumentId.cs` | `Encode(kind, payload)` / `TryDecode(id, out kind, out payload)`. base64url, matching `DedupKey.ForChunk`'s alphabet choice and its stated reason (`Sds/Domain/DedupKey.cs:14-18`). Rejects unknown kinds, malformed base64, `..`, control chars, and payloads whose separator count is wrong for their kind. |
| `Documents/DocumentSummary.cs` | Catalog row: `Id, Kind, Title, Subtitle, Available, State, ContentType, SizeBytes?, OfficialDate?, IngestedUtc?, ChunkCount?`. |
| `Documents/DocumentDetail.cs` | `Summary + Provenance + Unavailable?` where `Provenance` is an ordered `IReadOnlyList<ProvenanceField>` of `(Label, Value, Kind)` — a list, not a fixed record, because SDS and regulatory provenance differ in shape and the rail renders whatever it is given. |
| `Documents/DocumentChunk.cs` | `Ordinal, Text, EntryId?, Section?` — the unit the text view renders and anchors to. |
| `Documents/IDocumentCatalog.cs` | `ListAsync(filter, ct)`, `GetAsync(id, ct)`. |
| `Documents/IDocumentContentStore.cs` | `OpenAsync(blobPath, ct) -> (Stream, long)?`, `ReadMetaAsync(path, ct)`. |
| `Documents/IDocumentTextReader.cs` | `ReadChunksAsync(DocumentDetail, ct)`. |

**New — `Smx.Infrastructure`**

| File | Contents |
|---|---|
| `BronzeDocumentStore.cs` | `IDocumentContentStore` over `DataLakeServiceClient`. `Azure.Storage.Files.DataLake` matching the version `Smx.Functions` already pins (12.20.0). |
| `LocalBronzeDocumentStore.cs` | Same interface over a local directory, selected by `BRONZE_LOCAL_PATH`. Mirrors the repo's existing `*_DRY_RUN` convention so the feature is runnable with `func`-less local dev. |
| `SdsDocumentProvider.cs`, `RegDocumentProvider.cs` | The two catalog halves. |
| `CosmosRegSilverTextReader.cs`, `SdsIndexTextReader.cs` | The two text sources. |

**New — `Smx.Backend`**

| File | Contents |
|---|---|
| `Api/DocumentEndpoints.cs` | The four routes in §6. |

**Modified**

| File | Change |
|---|---|
| `Smx.Infrastructure/BackendOptions.cs` | `+ BronzeAccountName`, `+ BronzeFilesystem` (default `bronze`), `+ BronzeLocalPath`. `SdsIndex` already exists (`:23`, `:85`, default `sds-index`) — no change needed. |
| `Smx.Backend/Program.cs` | Register the store, providers, catalog, text readers; map the endpoints. |
| `infra/modules/compute.bicep` **and** `infra/single-rg/modules/compute.bicep` | `+ BRONZE_ACCOUNT_NAME`, `+ BRONZE_FILESYSTEM` in the shared container-app env list (`:103-139`). No role assignments change — the UAMI is already Blob Data Contributor at account scope. |
| `smx-web/src/api/client.ts`, `types.ts` | Document fetchers and types. |
| `smx-web/src/App.tsx` | `/docs` and `/docs/:id` routes. |
| `smx-web/src/components/ui/Primitives.tsx` | `CitationChip` gains an **optional** `documentId`; absent → renders exactly as today. |
| `smx-web/src/components/Finder.tsx` | A fourth hit kind, `document`. |
| `smx-web/src/components/EvidencePanel.tsx`, `routes/stages/Regulatory.tsx` | Pass `documentId` where the citation is real. |
| `smx-web/src/routes/MsdsRegistry.tsx` | Rows open the sheet. |
| `CLAUDE.md` | The frontend section's "only three screens are backed by real endpoints" count changes. |

---

## 5. Document identity

`{kind}_{base64url(payload)}`. base64url because the natural ids contain `|` and spaces, which is the same
constraint — and the same fix — that `DedupKey.ForChunk` already documents.

| Kind | Payload | Resolution | Blob |
|---|---|---|---|
| `sds` | `RegistryPointer.Id`, i.e. `{cas}\|{supplier}\|{revisionDate}` | point read `sds-registry`, id = payload, **pk = cas** (the first segment) | `BlobPath` on the pointer |
| `reg` | `{sourceId}/{docId}` | point read `reg-state`, id = docId, **pk = sourceId** | `regulatory/{sourceId}/{docId}/{LastFetchTs}/raw.{ext}` |
| `seed` | `{region}/{docId}` | point read `reg-state`, id = docId, **pk = region** | `seed/{region}/{docId}/raw.txt` |
| `sdsgap` | `MasterListEntry.Id`, i.e. `{element}_{form-slug}` | point read `sds-master-list`, pk = element | none — this row exists to state an absence |

Decoding yields the partition key, so **every resolution is a point read**, never a cross-partition scan.

`sdsgap` is an *id* kind, not a filter facet. In `DocumentSummary.Kind` and in the `kind` query parameter a
gap row reports `sds` — it is a safety data sheet, one that is missing. It is selected by `state=missing`,
not by a separate kind. Keeping the id prefix distinct is what makes resolution unambiguous, since a gap row
resolves against `sds-master-list` while a real sheet resolves against `sds-registry`.

**`reg` vs `seed` is decided when the catalog is built, not by probing blobs.** A `reg-state` entry whose
`SourceId` matches a source in `reg-registry` is `reg`; one that does not is a seed-imported region
(`SeedImporter.cs:96` — *"sourceId = region"*). The distinction must be made at catalog time because the two
prefixes have different shapes: the regulatory path carries a `{fetchTs}` segment and the seed path does not,
and for seeded docs `RegDocState.LastFetchTs` holds the sync date, which never appeared in the path
(`SeedImporter.cs:138`).

**The extension is not stored anywhere.** It is derived at ingest from the content type
(`BronzeIngestor.ExtensionFor`) and then discarded. It is recovered by reading the `meta.json` sidecar, which
carries `ContentType` — the same read that populates the provenance rail. One blob read, two purposes.

---

## 6. The API

### `GET /api/documents`

Query: `kind` (`sds|reg|seed|all`), `q` (substring over title/subtitle/CAS/supplier/regulation),
`state` (`available|missing|superseded|all`). Returns `DocumentSummary[]`.

The SDS half unions `sds-registry` (sheets that exist) with `sds-master-list` entries in a non-`fetched`
status (gap rows, D9). A master-list entry whose registry pointer exists is not emitted twice.

### `GET /api/documents/{id}`

`DocumentDetail`. `404` for an unknown or malformed id. A gap row returns **`200` with `available: false`**
and its reason — its absence is a fact the catalog knows, not a lookup failure.

Provenance by kind:

| `sds` (from `RegistryPointer`) | `reg` / `seed` (from `BronzeMeta`) |
|---|---|
| Source URL, supplier, product name, revision date, region, language, ingested, superseded-by, master-list id | Source URL, authority, official date, fetched, sync run, SHA-256, content type, HTTP status |

### `GET /api/documents/{id}/content`

Streams bytes. Headers: the stored content type, `X-Content-Type-Options: nosniff`,
`Content-Security-Policy: sandbox; default-src 'none'`, and `Content-Disposition: inline` (or `attachment`
when `?download=1`). `404` when the registry row exists but the blob does not — reported as the structured
`blob-missing` reason on the detail endpoint rather than a bare error, since it means real drift.

### `GET /api/documents/{id}/text`

`DocumentChunk[]`, verbatim from the index.

- **Regulatory / seed:** point-partition query on `reg-silver` (pk `/docId`), ordered by `ChunkIndex`. Each
  chunk carries its own `Citation.EntryId` (`Reg/Domain/RegModels.cs:30-32`) — that is the anchor target.
- **SDS:** `sds-index` filtered `cas eq … and supplier eq … and revisionDate eq …` — all three are filterable
  (`Sds/Ingestion/SdsSearchClient.cs:23-28`) and together they are exactly `DedupKey.ForRegistry`. `content`
  is a `SearchableField` (`:30`) so the text is retrievable. `blobPath` is **not** filterable, which is why
  the dedup triple is the filter. Ordering comes from the ordinal suffix decoded from each chunk key
  (`DedupKey.ForChunk`), since the index has no ordinal field.

An empty result is not an error — see §8.

---

## 7. The frontend

**`components/FileViewer.tsx`** is the single real component: title, download, tab strip
(`Original` / `What the agent read · N chunks`), content pane, provenance rail. `routes/DocumentView.tsx`
mounts it at `/docs/:id` with a breadcrumb; `components/FileViewerOverlay.tsx` mounts it dimmed with focus
trap and Esc-to-close; `routes/Documents.tsx` is the library.

**Rendering.** MSAL bearer tokens mean `<iframe src="/api/…">` cannot authenticate — the browser will not
attach the header. So the viewer fetches through the existing `authorizedFetch` (`api/client.ts:36-41`) and
renders from memory:

| Content type | Renderer |
|---|---|
| `application/pdf` | `URL.createObjectURL` → `<iframe>` (revoked on unmount) |
| `text/html` | `<iframe sandbox="" srcdoc={text}>` — **D7**, never an object URL |
| `text/*`, `application/json`, `application/xml`, csv | `<pre>` |
| anything else, or > 25 MB | not rendered; download offered. 25 MB matches the intake spec's per-file cap |

**Anchoring** arrives as `?entry=27` or `?chunk=148`. The viewer opens on the text tab, scrolls the matching
chunk into view, and marks it *cited*. `?entry=` matching more than one chunk anchors the first and reports
the count; matching none falls back to the top of the list with a stated reason. Anchoring is a text-view
feature only — mapping a chunk to a coordinate in a rendered PDF is not tractable without a full PDF text
layer, and §12 records why that is deferred rather than faked.

**Entry points.** `/docs` in app nav; MSDS Registry rows; Finder (⌘K) `document` hits; `EvidencePanel` and
the Regulatory stage citation chips; the Matrix "traces to no source" blocker. Per D8, fixture-backed screens
pass no `documentId` and their chips stay inert.

**Neither new screen carries a `MockBadge`.** Both read real endpoints end to end.

---

## 8. Failure modes

The governing rule: **a document that cannot be shown says why.** A blank pane, in a system where the
operator signs off against documents, is worse than an error.

| Situation | Response |
|---|---|
| Unknown or malformed id | `404`. Payload shape validated before any store call |
| Registry row exists, blob does not | Detail returns `available: false`, `reason: blob-missing`. Real drift — a failed sync, a deleted blob — and the viewer names it |
| Gap row (`pending` / `failed` / `awaiting_operator`) | Not an error. Shows attempt count and last attempt (`MasterListEntry.AttemptCount`, `LastAttemptUtc`) and links the existing `POST sds/upload` path. `/content` and `/text` on a gap id return `409` with the same reason — the id is valid and the document is knowably absent, which is neither "not found" nor a server fault |
| Superseded sheet (`SupersededBy` set) | Opens normally, with a banner linking to its replacement |
| **Zero indexed chunks** | *"No agent has read this document — it is in Bronze but not in the index."* A document that never reached Silver/Gold is invisible to every verdict, and that deserves to be loud |
| `meta.json` absent | Provenance fields read *"not recorded"*. Invariant 6 |
| Over 25 MB | Not rendered inline; download offered |
| Bronze unconfigured | `503` with a plain message, surfaced by the viewer rather than swallowed |

---

## 9. Configuration

| Setting | Default | Notes |
|---|---|---|
| `BRONZE_ACCOUNT_NAME` | — | Required in Azure; added to both `compute.bicep` variants |
| `BRONZE_FILESYSTEM` | `bronze` | Matches `functions.bicep:340` |
| `BRONZE_LOCAL_PATH` | unset | Local dev; when set, the local store replaces the ADLS one |
| `SDS_SEARCH_INDEX` | `sds-index` | Already present (`BackendOptions.cs:85`); unset in bicep but the default matches `SdsOptions.SearchIndex` |

---

## 10. Testing

TDD throughout, per the repo's practice.

**`Smx.Backend.Tests`** — `DocumentId` encode/decode round-trip across all four kinds; traversal and
malformed payloads rejected without a store call; catalog assembly from fakes, including the union of
registry pointers with master-list gap rows and the no-double-emit rule; `reg` vs `seed` classification
driven by `reg-registry` membership; blob-path construction per kind, including seed's missing `{fetchTs}`
segment; `available: false` on blob-missing; response headers (`nosniff`, CSP sandbox, disposition);
chunk ordering by `ChunkIndex` and by decoded ordinal; a write-throwing fake store proving invariant 7.

**`smx-web` (vitest)** — each content type routes to its renderer, with an explicit regression test that
**HTML goes to `srcdoc` + `sandbox` and never to a `blob:` URL** (invariant 3); anchoring by `entry` and by
`chunk`, including the multi-match and no-match paths; gap rows render the upload affordance; zero-chunk
documents render the "no agent has read this" state; library filters; `CitationChip` without `documentId`
renders exactly as before (guarding D8).

**Infra** — `az bicep build` on both `infra/main.bicep` and `infra/single-rg/main.bicep`.

---

## 11. Residual risks

- **Listing cost grows with the corpus.** Read-time assembly is right at 9 regulatory sources and a
  per-substance SDS list. At a few thousand sheets, listing becomes a cross-partition scan per page load.
  `IDocumentCatalog` exists so the projection in §12 replaces the implementation without touching the API or
  the UI.
- **Chunk ordinals are decoded from a key, not read from a field.** Should `DedupKey.ForChunk` ever change
  shape, SDS text ordering breaks silently. A test pins the current shape; an `ordinal` field on `sds-index`
  is the durable fix and needs a re-ingest.
- **The provenance rail is only as honest as `meta.json`.** For seeded documents, `BronzeMeta.ContentType` is
  hardcoded `text/plain` and `HttpStatus` is `0` (`SeedImporter.cs:111-112`) — accurate, but the rail should
  not imply an HTTP fetch that never happened. Seed documents label their origin as an import.
- **`BackendOptions.RegulatoryIndex` defaults to `regulatory-index` while the writer and both bicep variants
  use `regulatory-corpus`** (`BackendOptions.cs:87` vs `RegOptions.cs:28`, `compute.bicep:117`). It does not
  affect this feature — regulatory text comes from `reg-silver`, not the index — but it is a live trap for
  local dev and should be fixed separately.

---

## 12. Out of scope (and why)

- **Intake attachments.** `SessionAttachment` is fully modelled with `BlobPath` and `TextBlobPath`
  (`Smx.Domain/Records/IntakeDocs.cs:32-42`), and the extractors landed on this branch — but the upload
  endpoint does not exist, so there is nothing in `intake/` to view. When it lands, a fourth provider and an
  `intake` kind slot into the existing catalog; the viewer needs no change.
- **Experiment history / past-project archives.** No data model, no storage, no ingest, and the chemistry
  design states plainly that no historical project data exists. A viewer for it would be a viewer for nothing.
- **Persisting the generated matrix XLSX.** It is produced in memory and streamed
  (`Api/MatrixXlsxWriter.cs`, `ProjectEndpoints.cs:45-56`). Making it a catalogued document means deciding
  when a snapshot is authoritative, which is a records question, not a viewer question.
- **A materialized `documents` container.** §11 states the trigger condition.
- **Anchoring inside rendered PDFs.** Requires a PDF text layer (pdf.js, ~1 MB) and coordinate mapping from
  chunk text back to page position. The text view gives the same auditable result today; a highlight that is
  approximately right on a safety data sheet is worse than no highlight.
- **In-viewer find.** Browser find works on the text view. Deferred until it is asked for.
- **Editing, annotating, or re-uploading from the viewer.** The operator never hand-mutates agent output
  (`CLAUDE.md`); a document is evidence, and evidence is read-only here.

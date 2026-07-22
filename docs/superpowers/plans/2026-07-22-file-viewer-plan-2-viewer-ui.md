# File Viewer — Plan 2: the viewer UI

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A document library at `/docs` and a reader at `/docs/:id` that shows both the original bytes and
the chunks an agent actually read, reachable from every place a real document is already referenced.

**Architecture:** One `FileViewer` component with two mounts — a route and an overlay. Because MSAL bearer
tokens cannot ride on an `<iframe src>`, the viewer fetches bytes through the existing `authorizedFetch`
and renders from memory: PDFs via an object URL, HTML via a fully sandboxed `srcdoc` frame, everything
else as text.

**Tech Stack:** React 18, TypeScript, react-router-dom, Vitest + @testing-library/react.

**Prerequisite:** Plan 1 (`2026-07-22-file-viewer-plan-1-document-access-layer.md`) must be complete —
this plan consumes `/api/documents*`.

**Spec:** `docs/superpowers/specs/2026-07-22-file-viewer-design.md`, §7 in particular.

**Commands:**

```bash
cd src/smx-web && npm test          # vitest run
cd src/smx-web && npm run typecheck # tsc --noEmit
cd src/smx-web && npm run build
```

---

## File structure

| File | Responsibility |
|---|---|
| `src/smx-web/src/api/types.ts` (modify) | `DocumentSummary`, `DocumentDetail`, `ProvenanceField`, `DocumentChunk`. |
| `src/smx-web/src/api/client.ts` (modify) | `getDocuments`, `getDocument`, `getDocumentContent`, `getDocumentText`, `documentDownloadUrl`. |
| `src/smx-web/src/components/DocumentContent.tsx` | **The security boundary.** Chooses a renderer by content type. |
| `src/smx-web/src/components/ProvenanceRail.tsx` | Renders the labelled provenance list. |
| `src/smx-web/src/components/DocumentText.tsx` | The chunk list, anchoring and the "never indexed" state. |
| `src/smx-web/src/components/FileViewer.tsx` | Tabs + header + composition of the three above. |
| `src/smx-web/src/components/FileViewerOverlay.tsx` | Dimmed mount, Esc to close, focus restore. |
| `src/smx-web/src/routes/DocumentView.tsx` | `/docs/:id` route mount. |
| `src/smx-web/src/routes/Documents.tsx` | `/docs` library. |
| `src/smx-web/src/App.tsx` (modify) | The two routes. |
| `src/smx-web/src/components/ui/Primitives.tsx` (modify) | `CitationChip` gains an optional `documentId`. |
| `src/smx-web/src/components/Finder.tsx` (modify) | A `document` hit kind. |
| `src/smx-web/src/routes/MsdsRegistry.tsx` (modify) | Rows open the sheet. |

**Tests:** colocated `*.test.tsx`, matching `Gate.test.tsx` / `AppShell.test.tsx`.

---

## Task 1: types and the API client

**Files:**
- Modify: `src/smx-web/src/api/types.ts`, `src/smx-web/src/api/client.ts`
- Test: `src/smx-web/src/api/documents.test.ts`

- [ ] **Step 1: Add the types to `src/smx-web/src/api/types.ts`**

Append:

```typescript
/* ---------------------------------------------------------------------------
   Documents — the file viewer's contract (design 2026-07-22).

   `kind` is the FACET: a safety sheet the system never obtained still reports
   'sds', because it is a missing sheet and not a fourth category. Only the id
   distinguishes it, and only because it resolves against a different container.
   --------------------------------------------------------------------------- */

export type DocumentKind = 'sds' | 'reg' | 'seed';
export type DocumentState = 'available' | 'missing' | 'superseded';

export interface DocumentSummary {
  id: string;
  kind: DocumentKind;
  title: string;
  subtitle: string;
  available: boolean;
  state: DocumentState;
  contentType: string | null;
  officialDate: string | null;
  ingestedUtc: string | null;
}

export interface ProvenanceField {
  label: string;
  value: string;
  kind: 'text' | 'url' | 'hash';
}

export interface DocumentDetail {
  summary: DocumentSummary;
  provenance: ProvenanceField[];
  unavailableReason: string | null;
  unavailableDetail: string | null;
  supersededById: string | null;
}

/** A chunk exactly as indexed — never re-extracted or cleaned up on the way here. */
export interface DocumentChunk {
  ordinal: number;
  text: string;
  entryId: string | null;
  section: string | null;
}

export interface DocumentBytes {
  blob: Blob;
  contentType: string;
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/smx-web/src/api/documents.test.ts`:

```typescript
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  NotFound,
  getDocument,
  getDocumentContent,
  getDocumentText,
  getDocuments,
  setAccessTokenProvider,
} from './client';

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });

const stubFetch = (impl: (url: string, init?: RequestInit) => Response) =>
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string, init?: RequestInit) => Promise.resolve(impl(url, init))),
  );

afterEach(() => vi.unstubAllGlobals());
afterEach(() => setAccessTokenProvider(async () => null));

describe('getDocuments', () => {
  it('GETs /api/documents and passes every filter', async () => {
    let seen = '';
    stubFetch((url) => {
      seen = url;
      return json([]);
    });
    await getDocuments({ kind: 'sds', q: 'silver', state: 'missing' });
    expect(seen).toContain('/api/documents?');
    expect(seen).toContain('kind=sds');
    expect(seen).toContain('q=silver');
    expect(seen).toContain('state=missing');
  });

  it('omits empty filters rather than sending blanks', async () => {
    let seen = '';
    stubFetch((url) => {
      seen = url;
      return json([]);
    });
    await getDocuments({});
    expect(seen).toBe('/api/documents');
  });
});

describe('getDocument', () => {
  it('returns NotFound as a sentinel rather than throwing', async () => {
    stubFetch(() => new Response('', { status: 404 }));
    const result = await getDocument('sds_abc');
    // NotFound is a module-local Symbol('NotFound'), compared by identity — see client.ts.
    expect(result).toBe(NotFound);
  });

  it('url-encodes the id', async () => {
    let seen = '';
    stubFetch((url) => {
      seen = url;
      return json({ summary: {}, provenance: [] });
    });
    await getDocument('sds_a/b');
    expect(seen).toContain('sds_a%2Fb');
  });
});

describe('getDocumentContent', () => {
  it('returns the blob and the server-declared content type', async () => {
    stubFetch(
      () =>
        new Response('%PDF-1.4', {
          status: 200,
          headers: { 'Content-Type': 'application/pdf' },
        }),
    );
    const result = await getDocumentContent('sds_abc');
    expect(result).not.toBe(null);
    expect(result!.contentType).toBe('application/pdf');
    expect(await result!.blob.text()).toBe('%PDF-1.4');
  });

  // A 409 means the document is knowably absent (a gap row). That is a state to render,
  // not an exception to throw.
  it('returns null on 409 and on 404', async () => {
    stubFetch(() => new Response('', { status: 409 }));
    expect(await getDocumentContent('sdsgap_abc')).toBe(null);
    stubFetch(() => new Response('', { status: 404 }));
    expect(await getDocumentContent('sds_abc')).toBe(null);
  });
});

describe('getDocumentText', () => {
  it('returns chunks', async () => {
    stubFetch(() => json([{ ordinal: 0, text: 'hello', entryId: null, section: null }]));
    const chunks = await getDocumentText('reg_abc');
    expect(chunks).toHaveLength(1);
    expect(chunks[0].text).toBe('hello');
  });

  // Empty is a real state — in bronze, never indexed — not an error.
  it('returns an empty array for an unindexed document', async () => {
    stubFetch(() => json([]));
    expect(await getDocumentText('reg_abc')).toEqual([]);
  });
});
```

- [ ] **Step 3: Run to verify failure**

```bash
cd src/smx-web && npm test -- documents.test.ts
```

Expected: **FAIL** — `getDocuments is not exported`.

- [ ] **Step 4: Add the client functions**

Append to `src/smx-web/src/api/client.ts`:

```typescript
/* ---------------------------------------------------------------------------
   Documents — the file viewer (design 2026-07-22).

   Bytes stream through the backend rather than a SAS URL: the storage account
   denies public access behind private endpoints, so a SAS would be unreachable
   from the browser AND would put a hole in private-by-default.
   --------------------------------------------------------------------------- */

export async function getDocuments(filter: {
  kind?: string;
  q?: string;
  state?: string;
}): Promise<DocumentSummary[]> {
  const params = new URLSearchParams();
  if (filter.kind) params.set('kind', filter.kind);
  if (filter.q) params.set('q', filter.q);
  if (filter.state) params.set('state', filter.state);
  const qs = params.toString();
  const res = await authorizedFetch(`${BASE}/documents${qs ? `?${qs}` : ''}`);
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DocumentSummary[];
}

export async function getDocument(id: string): Promise<DocumentDetail | NotFound> {
  const res = await authorizedFetch(`${BASE}/documents/${encodeURIComponent(id)}`);
  if (res.status === 404) return NotFound;
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DocumentDetail;
}

/**
 * Fetch the raw bytes.
 *
 * This exists as a fetch rather than an <iframe src> because MSAL bearer tokens cannot ride
 * on a frame's src attribute — the browser will not attach the header. Everything downstream
 * (object URL for PDFs, srcdoc for HTML) follows from that constraint.
 *
 * null means "no bytes to show, and the detail endpoint says why": 409 for a document the
 * system knows it never obtained, 404 for a registry row whose blob has vanished.
 */
export async function getDocumentContent(id: string): Promise<DocumentBytes | null> {
  const res = await authorizedFetch(`${BASE}/documents/${encodeURIComponent(id)}/content`);
  if (res.status === 404 || res.status === 409) return null;
  if (!res.ok) throw await failure(res);
  return {
    blob: await res.blob(),
    contentType: res.headers.get('Content-Type')?.split(';')[0].trim() ?? 'application/octet-stream',
  };
}

export async function getDocumentText(id: string): Promise<DocumentChunk[]> {
  const res = await authorizedFetch(`${BASE}/documents/${encodeURIComponent(id)}/text`);
  if (res.status === 404 || res.status === 409) return [];
  if (!res.ok) throw await failure(res);
  return (await res.json()) as DocumentChunk[];
}
```

Add to the type import block at the top of `client.ts`:

```typescript
  DocumentBytes,
  DocumentChunk,
  DocumentDetail,
  DocumentSummary,
```

- [ ] **Step 5: Run to verify pass**

```bash
cd src/smx-web && npm test -- documents.test.ts && npm run typecheck
```

Expected: **PASS**, all tests green; typecheck clean.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/api/
git commit -m "feat(web): the document client

Content arrives as a fetch, not an iframe src, because MSAL bearer tokens
cannot ride on a frame's src attribute. Every rendering decision downstream
follows from that one constraint.

409 and 404 return null rather than throwing: a document the system knows it
never obtained is a state to render, not an exception."
```

---

## Task 2: `DocumentContent` — the security boundary

**This is the highest-risk task in the plan.** Read spec §2 D7 before starting.

**Files:**
- Create: `src/smx-web/src/components/DocumentContent.tsx`
- Test: `src/smx-web/src/components/DocumentContent.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create `src/smx-web/src/components/DocumentContent.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DocumentContent } from './DocumentContent';

afterEach(() => vi.unstubAllGlobals());

const bytes = (body: string, contentType: string) => ({
  blob: new Blob([body], { type: contentType }),
  contentType,
});

describe('DocumentContent — what may be rendered, and how', () => {
  /**
   * THE invariant of this component (design D7).
   *
   * A blob: URL inherits the origin of the document that created it. Regulatory HTML is
   * fetched from the open web; rendering it in an origin-inheriting frame would be stored
   * XSS against the operator's session — with access to their MSAL tokens.
   *
   * srcdoc + sandbox="" grants neither allow-scripts nor allow-same-origin. If a later
   * change "optimizes" this into an object URL for consistency with the PDF path, this
   * test is what stops it.
   */
  it('renders HTML in a fully sandboxed srcdoc frame and never an object URL', async () => {
    const createObjectURL = vi.fn(() => 'blob:should-never-be-called');
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL: vi.fn() });

    render(<DocumentContent content={bytes('<h1>REACH</h1>', 'text/html')} title="REACH" />);

    const frame = await screen.findByTitle('REACH');
    expect(frame).toHaveAttribute('sandbox', '');
    expect(frame.getAttribute('srcdoc')).toContain('<h1>REACH</h1>');
    expect(frame).not.toHaveAttribute('src');
    expect(createObjectURL).not.toHaveBeenCalled();
  });

  it('renders a PDF through an object URL', async () => {
    const createObjectURL = vi.fn(() => 'blob:pdf-url');
    vi.stubGlobal('URL', { ...URL, createObjectURL, revokeObjectURL: vi.fn() });

    render(<DocumentContent content={bytes('%PDF-1.4', 'application/pdf')} title="Silver nitrate" />);

    const frame = await screen.findByTitle('Silver nitrate');
    expect(frame).toHaveAttribute('src', 'blob:pdf-url');
    expect(createObjectURL).toHaveBeenCalledTimes(1);
  });

  // Leaking object URLs pin their blobs in memory for the life of the document.
  it('revokes the object URL on unmount', async () => {
    const revokeObjectURL = vi.fn();
    vi.stubGlobal('URL', { ...URL, createObjectURL: vi.fn(() => 'blob:pdf-url'), revokeObjectURL });

    const { unmount } = render(
      <DocumentContent content={bytes('%PDF', 'application/pdf')} title="x" />,
    );
    await screen.findByTitle('x');
    unmount();

    await waitFor(() => expect(revokeObjectURL).toHaveBeenCalledWith('blob:pdf-url'));
  });

  it.each([
    ['text/plain', 'plain text body'],
    ['text/csv', 'a,b,c'],
    ['application/json', '{"a":1}'],
    ['application/xml', '<root/>'],
  ])('renders %s as escaped text', async (contentType, body) => {
    render(<DocumentContent content={bytes(body, contentType)} title="x" />);
    expect(await screen.findByText(body)).toBeInTheDocument();
  });

  // An unknown type must not be handed to a renderer that might interpret it.
  it('offers download instead of rendering an unknown type', async () => {
    render(
      <DocumentContent
        content={bytes('', 'application/octet-stream')}
        title="x"
        downloadHref="/api/documents/x/content?download=1"
      />,
    );
    expect(await screen.findByText(/cannot be displayed/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /download/i })).toBeInTheDocument();
  });

  // Spec §8: over the cap, offer the file rather than melting the tab.
  it('refuses to render inline above the size cap', async () => {
    const big = { blob: new Blob([new Uint8Array(26 * 1024 * 1024)], { type: 'application/pdf' }), contentType: 'application/pdf' };
    render(<DocumentContent content={big} title="x" downloadHref="/dl" />);
    expect(await screen.findByText(/25 MB/i)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /download/i })).toBeInTheDocument();
  });

  it('states the reason when there are no bytes at all', async () => {
    render(<DocumentContent content={null} title="x" unavailableDetail="3 fetch attempts failed" />);
    expect(await screen.findByText(/3 fetch attempts failed/)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- DocumentContent.test.tsx
```

Expected: **FAIL** — cannot resolve `./DocumentContent`.

- [ ] **Step 3: Implement `DocumentContent`**

Create `src/smx-web/src/components/DocumentContent.tsx`:

```tsx
import { useEffect, useState } from 'react';
import type { DocumentBytes } from '../api/types';
import { EmptyState } from './ui/Primitives';

/** Spec §8 — matches the intake design's per-file cap. */
const MAX_INLINE_BYTES = 25 * 1024 * 1024;

type Mode = 'pdf' | 'html' | 'text' | 'unsupported' | 'oversize' | 'none';

function modeFor(content: DocumentBytes | null): Mode {
  if (!content) return 'none';
  if (content.blob.size > MAX_INLINE_BYTES) return 'oversize';
  const t = content.contentType.toLowerCase();
  if (t.includes('pdf')) return 'pdf';
  if (t.includes('html')) return 'html';
  if (t.startsWith('text/') || t.includes('json') || t.includes('xml')) return 'text';
  return 'unsupported';
}

/**
 * Renders a document's stored bytes.
 *
 * The one rule that must never be relaxed: HTML goes into `srcdoc` on a frame with
 * `sandbox=""` — granting neither allow-scripts nor allow-same-origin — and NEVER into an
 * object URL. A blob: URL inherits the creating document's origin, so regulatory HTML
 * fetched from the open web would execute as us, with our operator's session and tokens.
 * The PDF path may use an object URL because the browser's PDF viewer does not execute page
 * script in the embedding origin.
 *
 * There is a test asserting exactly this. It is not redundant with the comment.
 */
export function DocumentContent({
  content,
  title,
  downloadHref,
  unavailableDetail,
}: {
  content: DocumentBytes | null;
  title: string;
  downloadHref?: string;
  unavailableDetail?: string | null;
}) {
  const mode = modeFor(content);
  const [objectUrl, setObjectUrl] = useState<string | null>(null);
  const [text, setText] = useState<string | null>(null);

  useEffect(() => {
    if (mode !== 'pdf' || !content) return;
    const url = URL.createObjectURL(content.blob);
    setObjectUrl(url);
    return () => {
      URL.revokeObjectURL(url);
      setObjectUrl(null);
    };
  }, [mode, content]);

  useEffect(() => {
    if (!content || (mode !== 'html' && mode !== 'text')) return;
    let live = true;
    void content.blob.text().then((t) => {
      if (live) setText(t);
    });
    return () => {
      live = false;
    };
  }, [mode, content]);

  if (mode === 'none') {
    return (
      <EmptyState
        title="No file to show"
        body={unavailableDetail ?? 'This document has no stored file.'}
      />
    );
  }

  if (mode === 'oversize') {
    return (
      <EmptyState
        title="Too large to display"
        body="This file is over 25 MB and is not rendered inline."
        actions={downloadHref ? <a href={downloadHref}>Download the original</a> : undefined}
      />
    );
  }

  if (mode === 'unsupported') {
    return (
      <EmptyState
        title="This format cannot be displayed"
        body={`Stored as ${content!.contentType}.`}
        actions={downloadHref ? <a href={downloadHref}>Download the original</a> : undefined}
      />
    );
  }

  if (mode === 'pdf') {
    return objectUrl ? (
      <iframe title={title} src={objectUrl} className="doc-frame" />
    ) : (
      <div className="doc-frame" aria-busy="true" />
    );
  }

  if (mode === 'html') {
    // sandbox="" — no scripts, no same-origin, no forms, no top-level navigation.
    return text === null ? (
      <div className="doc-frame" aria-busy="true" />
    ) : (
      <iframe title={title} sandbox="" srcDoc={text} className="doc-frame" />
    );
  }

  return <pre className="doc-text">{text ?? ''}</pre>;
}
```

> `EmptyState`'s real signature (verified at `components/ui/Primitives.tsx:75-87`) is
> `{ icon?: string; title: string; body?: ReactNode; actions?: ReactNode; children?: ReactNode }` —
> note **`actions`**, plural, and that `icon` defaults to `'ti-inbox'`. Pass a more apt Tabler icon
> where it helps (`ti-file-off` for the no-file states, `ti-file-alert` for oversize).

- [ ] **Step 4: Add the two styles**

Append to `src/smx-web/src/styles/primitives.css`:

```css
/* The document surface. Fixed height rather than auto so the provenance rail and the
   content pane stay aligned regardless of what the document happens to contain. */
.doc-frame {
  width: 100%;
  height: 70vh;
  border: 0.5px solid var(--border);
  border-radius: var(--r2);
  background: var(--surface-0);
}

.doc-text {
  width: 100%;
  height: 70vh;
  overflow: auto;
  margin: 0;
  padding: var(--s3);
  border: 0.5px solid var(--border);
  border-radius: var(--r2);
  background: var(--surface-0);
  font-family: var(--font-mono);
  font-size: var(--t-small);
  line-height: 1.55;
  white-space: pre-wrap;
  word-break: break-word;
}
```

All token names in this plan's CSS are the real ones from `src/smx-web/src/styles/tokens.css` —
verified, not guessed. For reference: spacing `--s1..--s7` (4/8/12/16/24/32/48px), radii `--r1..--r3`,
type `--t-micro | --t-tiny | --t-small | --t-body | --t-lead | --t-title | --t-display`, surfaces
`--surface-0..--surface-3`, text `--text-primary | --text-secondary | --text-muted`, and the semantic
triples `--bg-accent / --text-accent / --border-accent` (and `-success`, `-danger`, `-warning`, `-pro`,
`-teal`). There is **no** `--accent`, `--warn`, `--bg`, or `--fs-*`.

- [ ] **Step 5: Run to verify pass**

```bash
cd src/smx-web && npm test -- DocumentContent.test.tsx
```

Expected: **PASS**, 11 tests.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/DocumentContent.tsx src/smx-web/src/components/DocumentContent.test.tsx src/smx-web/src/styles/primitives.css
git commit -m "feat(web): render a document, without handing it the origin

A blob: URL inherits the creating document's origin. Regulatory HTML comes
from the open web, so rendering it that way would be stored XSS against the
operator's session and their MSAL tokens. HTML therefore goes to srcdoc on a
frame with sandbox=\"\" — no scripts, no same-origin — and never to an object
URL. PDFs may use one; the browser's PDF viewer does not execute page script
in the embedding origin.

The test asserting this is deliberate and not redundant with the comment: the
failure mode is a later change unifying the two paths for tidiness."
```

---

## Task 3: `ProvenanceRail`

**Files:**
- Create: `src/smx-web/src/components/ProvenanceRail.tsx`, `ProvenanceRail.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create `src/smx-web/src/components/ProvenanceRail.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { ProvenanceField } from '../api/types';
import { ProvenanceRail } from './ProvenanceRail';

const FIELDS: ProvenanceField[] = [
  { label: 'Source URL', value: 'https://echa.europa.eu/candidate-list', kind: 'url' },
  { label: 'Authority', value: 'ECHA', kind: 'text' },
  { label: 'SHA-256', value: '9f2c1ae4b8d071', kind: 'hash' },
  { label: 'Fetched', value: 'not recorded', kind: 'text' },
];

describe('ProvenanceRail', () => {
  it('renders every field in the order given', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    const labels = screen.getAllByTestId('provenance-label').map((n) => n.textContent);
    expect(labels).toEqual(['Source URL', 'Authority', 'SHA-256', 'Fetched']);
  });

  it('links a url field and leaves the rest as text', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    const link = screen.getByRole('link', { name: /candidate-list/ });
    expect(link).toHaveAttribute('href', 'https://echa.europa.eu/candidate-list');
    // The source is outside our trust boundary; never send a referrer, never let it reach opener.
    expect(link).toHaveAttribute('rel', expect.stringContaining('noopener'));
    expect(link).toHaveAttribute('rel', expect.stringContaining('noreferrer'));
    expect(screen.queryByRole('link', { name: 'ECHA' })).toBeNull();
  });

  /**
   * Spec §3 invariant 6. "not recorded" is a real answer, not a missing value — it means the
   * sidecar did not carry the field. Hiding it would make an absent provenance field
   * indistinguishable from a field that was never part of this document's shape.
   */
  it('shows "not recorded" rather than hiding the field', () => {
    render(<ProvenanceRail fields={FIELDS} />);
    expect(screen.getByText('not recorded')).toBeInTheDocument();
  });

  it('renders nothing but a note when there is no provenance at all', () => {
    render(<ProvenanceRail fields={[]} />);
    expect(screen.getByText(/no provenance recorded/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- ProvenanceRail.test.tsx
```

Expected: **FAIL** — cannot resolve `./ProvenanceRail`.

- [ ] **Step 3: Implement**

Create `src/smx-web/src/components/ProvenanceRail.tsx`:

```tsx
import type { ProvenanceField } from '../api/types';

/**
 * Where a document came from, rendered in the order the backend chose — SDS and regulatory
 * provenance genuinely differ in shape, so the rail renders what it is handed rather than
 * imposing a schema.
 *
 * "not recorded" is displayed, never hidden. It means the sidecar did not carry that field,
 * which is different from the field not applying, and in an audit surface that difference is
 * the whole point.
 */
export function ProvenanceRail({ fields }: { fields: ProvenanceField[] }) {
  if (fields.length === 0) {
    return (
      <aside className="prov-rail">
        <p className="muted">No provenance recorded for this document.</p>
      </aside>
    );
  }

  return (
    <aside className="prov-rail">
      {fields.map((f) => (
        <div key={f.label} className="prov-field">
          <div className="prov-label" data-testid="provenance-label">
            {f.label}
          </div>
          <div className={f.kind === 'hash' ? 'prov-value prov-hash' : 'prov-value'}>
            {f.kind === 'url' && f.value.startsWith('http') ? (
              <a href={f.value} target="_blank" rel="noopener noreferrer">
                {f.value}
              </a>
            ) : (
              f.value
            )}
          </div>
        </div>
      ))}
    </aside>
  );
}
```

- [ ] **Step 4: Add styles**

Append to `src/smx-web/src/styles/primitives.css`:

```css
.prov-rail { width: 220px; flex-shrink: 0; border-left: 0.5px solid var(--border); padding: var(--s3); }
.prov-field { margin-bottom: var(--s3); }
.prov-label { font-size: var(--t-micro); text-transform: uppercase; letter-spacing: 0.06em; color: var(--text-muted); margin-bottom: 2px; }
.prov-value { font-size: var(--t-small); word-break: break-all; line-height: 1.4; }
.prov-hash { font-family: var(--font-mono); color: var(--text-secondary); }
```

- [ ] **Step 5: Run to verify pass**

```bash
cd src/smx-web && npm test -- ProvenanceRail.test.tsx
```

Expected: **PASS**, 4 tests.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/ProvenanceRail.tsx src/smx-web/src/components/ProvenanceRail.test.tsx src/smx-web/src/styles/primitives.css
git commit -m "feat(web): the provenance rail

Renders whatever the backend hands it, in that order: SDS and regulatory
provenance differ in shape and imposing a schema here would mean inventing
fields for one of them.

'not recorded' is shown rather than hidden. It means the sidecar did not carry
the field, which is not the same as the field not applying — and on an audit
surface that distinction is the point."
```

---

## Task 4: `DocumentText` — chunks and anchoring

**Files:**
- Create: `src/smx-web/src/components/DocumentText.tsx`, `DocumentText.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create `src/smx-web/src/components/DocumentText.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { DocumentChunk } from '../api/types';
import { DocumentText } from './DocumentText';

const CHUNKS: DocumentChunk[] = [
  { ordinal: 146, text: 'entry 26 body', entryId: '26', section: null },
  { ordinal: 147, text: 'nickel release limit', entryId: '27', section: null },
  { ordinal: 148, text: 'prolonged skin contact', entryId: '27', section: null },
];

describe('DocumentText', () => {
  it('renders every chunk with its ordinal', () => {
    render(<DocumentText chunks={CHUNKS} />);
    expect(screen.getByText('nickel release limit')).toBeInTheDocument();
    expect(screen.getByText(/146/)).toBeInTheDocument();
  });

  it('marks the chunk matching the anchored entry as cited', () => {
    render(<DocumentText chunks={CHUNKS} anchorEntry="27" />);
    const cited = screen.getAllByTestId('chunk-cited');
    expect(cited).toHaveLength(2); // both entry-27 chunks
    expect(cited[0].textContent).toContain('nickel release limit');
  });

  it('anchors by explicit ordinal when given one', () => {
    render(<DocumentText chunks={CHUNKS} anchorOrdinal={148} />);
    const cited = screen.getAllByTestId('chunk-cited');
    expect(cited).toHaveLength(1);
    expect(cited[0].textContent).toContain('prolonged skin contact');
  });

  /**
   * An anchor that matches nothing must SAY so. Silently showing the top of the document
   * would tell the operator "here is the passage your verdict cited" while showing them
   * something else entirely — a false provenance claim, which is the failure mode this whole
   * feature exists to prevent.
   */
  it('reports an anchor that matches nothing instead of silently showing the top', () => {
    render(<DocumentText chunks={CHUNKS} anchorEntry="999" />);
    expect(screen.queryAllByTestId('chunk-cited')).toHaveLength(0);
    expect(screen.getByText(/no chunk in this document cites entry 999/i)).toBeInTheDocument();
  });

  it('reports how many chunks matched when an entry spans several', () => {
    render(<DocumentText chunks={CHUNKS} anchorEntry="27" />);
    expect(screen.getByText(/2 chunks/i)).toBeInTheDocument();
  });

  /**
   * Spec §8: in bronze, never indexed. This is a loud state, not an empty list — a document
   * no agent has ever read cannot be supporting any verdict, and that is worth knowing.
   */
  it('names the never-indexed state rather than rendering an empty list', () => {
    render(<DocumentText chunks={[]} />);
    expect(screen.getByText(/no agent has read this document/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- DocumentText.test.tsx
```

Expected: **FAIL** — cannot resolve `./DocumentText`.

- [ ] **Step 3: Implement**

Create `src/smx-web/src/components/DocumentText.tsx`:

```tsx
import { useEffect, useRef } from 'react';
import type { DocumentChunk } from '../api/types';

/**
 * The chunks an agent actually retrieved, verbatim.
 *
 * This is the honest half of the viewer. Agents never read the PDF; they read these. A sheet
 * that renders perfectly and chunked to garbage is currently invisible and silently poisons
 * every verdict citing it — putting the two side by side is what makes that visible.
 *
 * Anchoring lives here rather than over the rendered PDF because mapping a chunk back to a
 * coordinate on a page needs a full text layer, and a highlight that is approximately right on
 * a safety data sheet is worse than no highlight.
 */
export function DocumentText({
  chunks,
  anchorEntry,
  anchorOrdinal,
}: {
  chunks: DocumentChunk[];
  anchorEntry?: string | null;
  anchorOrdinal?: number | null;
}) {
  const firstCited = useRef<HTMLDivElement | null>(null);

  const isCited = (c: DocumentChunk) =>
    (anchorOrdinal != null && c.ordinal === anchorOrdinal) ||
    (anchorEntry != null && anchorEntry !== '' && c.entryId === anchorEntry);

  const matches = chunks.filter(isCited);

  useEffect(() => {
    firstCited.current?.scrollIntoView({ block: 'center' });
  }, [anchorEntry, anchorOrdinal, chunks]);

  if (chunks.length === 0) {
    return (
      <div className="doc-text-pane">
        <p className="muted">
          <strong>No agent has read this document.</strong> It is stored in Bronze but has no
          chunks in the index, so no verdict can be resting on it.
        </p>
      </div>
    );
  }

  const anchored = anchorEntry != null || anchorOrdinal != null;

  return (
    <div className="doc-text-pane">
      {anchored && matches.length > 0 && (
        <p className="chunk-anchor-note">
          Anchored to {matches.length === 1 ? '1 chunk' : `${matches.length} chunks`}
          {anchorEntry ? ` citing entry ${anchorEntry}` : ` at ordinal ${anchorOrdinal}`}.
        </p>
      )}
      {anchored && matches.length === 0 && (
        <p className="chunk-anchor-note chunk-anchor-miss">
          {anchorEntry
            ? `No chunk in this document cites entry ${anchorEntry}. Showing the document from the top.`
            : `No chunk at ordinal ${anchorOrdinal}. Showing the document from the top.`}
        </p>
      )}

      {chunks.map((c, i) => {
        const cited = isCited(c);
        return (
          <div
            key={c.ordinal}
            ref={cited && matches[0] === c ? firstCited : undefined}
            className={cited ? 'chunk chunk-cited' : 'chunk'}
            data-testid={cited ? 'chunk-cited' : 'chunk'}
          >
            <div className="chunk-head">
              <span>
                chunk {c.ordinal}
                {cited ? ' · cited' : ''}
              </span>
              <span>{c.entryId ? `entry ${c.entryId}` : (c.section ?? '')}</span>
            </div>
            <p>{c.text}</p>
          </div>
        );
      })}
    </div>
  );
}
```

- [ ] **Step 4: Add styles**

Append to `src/smx-web/src/styles/primitives.css`:

```css
.doc-text-pane { height: 70vh; overflow: auto; padding: var(--s3); border: 0.5px solid var(--border); border-radius: var(--r2); }
.chunk { border: 0.5px solid var(--border); border-radius: var(--r1); padding: var(--s2) var(--s3); margin-bottom: var(--s2); }
.chunk-cited { border-color: var(--text-accent); border-width: 2px; background: var(--bg-accent); }
.chunk-head { display: flex; justify-content: space-between; font-size: var(--t-micro); text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-muted); margin-bottom: 4px; }
.chunk p { margin: 0; font-size: var(--t-small); line-height: 1.55; }
.chunk-anchor-note { font-size: var(--t-small); color: var(--text-accent); border-left: 3px solid var(--text-accent); padding: var(--s2) var(--s3); margin: 0 0 var(--s3); }
.chunk-anchor-miss { color: var(--text-warning); border-left-color: var(--text-warning); }
```

> Substitute real token names from `styles/tokens.css` where these guesses differ
> (`--accent-wash`, `--warn`, `--r1`, `--text-3` in particular).

- [ ] **Step 5: Run to verify pass**

```bash
cd src/smx-web && npm test -- DocumentText.test.tsx
```

Expected: **PASS**, 6 tests.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/DocumentText.tsx src/smx-web/src/components/DocumentText.test.tsx src/smx-web/src/styles/primitives.css
git commit -m "feat(web): what the agent read, and where the citation landed

An anchor that matches nothing says so. Silently scrolling to the top would
tell the operator 'here is the passage your verdict cited' while showing them
something else — a false provenance claim, which is the exact failure this
feature exists to prevent.

Zero chunks is a named state, not an empty list: a document no agent has ever
read cannot be supporting any verdict, and that is worth saying out loud."
```

---

## Task 5: `FileViewer` — the composition

**Files:**
- Create: `src/smx-web/src/components/FileViewer.tsx`, `FileViewer.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create `src/smx-web/src/components/FileViewer.test.tsx`:

```tsx
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FileViewer } from './FileViewer';

const detail = {
  summary: {
    id: 'reg_abc',
    kind: 'reg' as const,
    title: 'REACH Annex XVII',
    subtitle: 'ECHA · official 2025-11-20',
    available: true,
    state: 'available' as const,
    contentType: 'text/html',
    officialDate: '2025-11-20',
    ingestedUtc: '20260701T031400Z',
  },
  provenance: [{ label: 'Authority', value: 'ECHA', kind: 'text' as const }],
  unavailableReason: null,
  unavailableDetail: null,
  supersededById: null,
};

const stub = (overrides: Partial<Record<string, unknown>> = {}) => {
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      if (url.includes('/text'))
        return Promise.resolve(
          new Response(JSON.stringify([{ ordinal: 0, text: 'chunk body', entryId: '27', section: null }]), {
            headers: { 'Content-Type': 'application/json' },
          }),
        );
      if (url.includes('/content'))
        return Promise.resolve(new Response('<p>original</p>', { headers: { 'Content-Type': 'text/html' } }));
      return Promise.resolve(
        new Response(JSON.stringify({ ...detail, ...overrides }), {
          headers: { 'Content-Type': 'application/json' },
        }),
      );
    }),
  );
};

afterEach(() => vi.unstubAllGlobals());

describe('FileViewer', () => {
  it('shows the title and provenance, and opens on the original', async () => {
    stub();
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText('REACH Annex XVII')).toBeInTheDocument();
    expect(await screen.findByText('ECHA')).toBeInTheDocument();
    expect(await screen.findByTitle('REACH Annex XVII')).toBeInTheDocument();
  });

  it('switches to the chunk view and names the count', async () => {
    stub();
    render(<FileViewer documentId="reg_abc" />);
    const tab = await screen.findByRole('tab', { name: /what the agent read/i });
    await userEvent.click(tab);
    expect(await screen.findByText('chunk body')).toBeInTheDocument();
  });

  // Arriving from a citation is the anchored case, and it must land on the chunk view —
  // the original has no anchor to land on.
  it('opens directly on the chunk view when anchored', async () => {
    stub();
    render(<FileViewer documentId="reg_abc" anchorEntry="27" />);
    expect(await screen.findByText('chunk body')).toBeInTheDocument();
  });

  it('surfaces a superseded banner', async () => {
    stub({ supersededById: 'sds_newer', summary: { ...detail.summary, state: 'superseded' } });
    render(<FileViewer documentId="reg_abc" />);
    expect(await screen.findByText(/superseded/i)).toBeInTheDocument();
  });

  it('reports a document that is not found', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(new Response('', { status: 404 }))));
    render(<FileViewer documentId="reg_missing" />);
    await waitFor(() => expect(screen.getByText(/not found/i)).toBeInTheDocument());
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- FileViewer.test.tsx
```

Expected: **FAIL** — cannot resolve `./FileViewer`.

- [ ] **Step 3: Implement**

Create `src/smx-web/src/components/FileViewer.tsx`:

```tsx
import { useEffect, useState } from 'react';
import {
  NotFound,
  getDocument,
  getDocumentContent,
  getDocumentText,
} from '../api/client';
import type { DocumentBytes, DocumentChunk, DocumentDetail } from '../api/types';
import { DocumentContent } from './DocumentContent';
import { DocumentText } from './DocumentText';
import { ProvenanceRail } from './ProvenanceRail';
import { Loading } from './Loading';
import { EmptyState } from './ui/Primitives';

type Tab = 'original' | 'agent';

/**
 * The reader. Mounted twice — at /docs/:id and inside the overlay — and identical in both.
 *
 * Two faces, deliberately (design D5). The original is what a human trusts; the chunks are
 * what the agent reasoned over. Showing only one hides either the artifact driving verdicts
 * or whether extraction was faithful to it.
 */
export function FileViewer({
  documentId,
  anchorEntry,
  anchorOrdinal,
}: {
  documentId: string;
  anchorEntry?: string | null;
  anchorOrdinal?: number | null;
}) {
  const [detail, setDetail] = useState<DocumentDetail | null>(null);
  const [missing, setMissing] = useState(false);
  const [content, setContent] = useState<DocumentBytes | null>(null);
  const [chunks, setChunks] = useState<DocumentChunk[]>([]);
  // Arriving from a citation means the anchor is the reason you are here, and it only exists
  // on the chunk view.
  const [tab, setTab] = useState<Tab>(anchorEntry || anchorOrdinal != null ? 'agent' : 'original');

  useEffect(() => {
    let live = true;
    setDetail(null);
    setMissing(false);
    void (async () => {
      const d = await getDocument(documentId);
      if (!live) return;
      if (d === NotFound) {
        setMissing(true);
        return;
      }
      setDetail(d);
      const [bytes, text] = await Promise.all([
        getDocumentContent(documentId),
        getDocumentText(documentId),
      ]);
      if (!live) return;
      setContent(bytes);
      setChunks(text);
    })();
    return () => {
      live = false;
    };
  }, [documentId]);

  if (missing) {
    return <EmptyState title="Document not found" body="No document has that identifier." />;
  }
  if (!detail) return <Loading />;

  const downloadHref = `/api/documents/${encodeURIComponent(documentId)}/content?download=1`;

  return (
    <div className="file-viewer">
      <header className="fv-head">
        <div>
          <h2>{detail.summary.title}</h2>
          <p className="muted">{detail.summary.subtitle}</p>
        </div>
        <a href={downloadHref}>Download</a>
      </header>

      {detail.supersededById && (
        <p className="fv-banner">
          This sheet has been superseded.{' '}
          <a href={`/docs/${encodeURIComponent(detail.supersededById)}`}>Open the current one</a>
        </p>
      )}

      {!detail.summary.available && (
        <p className="fv-banner fv-banner-warn">
          {detail.unavailableDetail ?? 'No file is stored for this document.'}
        </p>
      )}

      <div className="fv-tabs" role="tablist">
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'original'}
          className={tab === 'original' ? 'fv-tab fv-tab-on' : 'fv-tab'}
          onClick={() => setTab('original')}
        >
          Original
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === 'agent'}
          className={tab === 'agent' ? 'fv-tab fv-tab-on' : 'fv-tab'}
          onClick={() => setTab('agent')}
        >
          What the agent read · {chunks.length} chunks
        </button>
      </div>

      <div className="fv-body">
        <div className="fv-main">
          {tab === 'original' ? (
            <DocumentContent
              content={content}
              title={detail.summary.title}
              downloadHref={downloadHref}
              unavailableDetail={detail.unavailableDetail}
            />
          ) : (
            <DocumentText chunks={chunks} anchorEntry={anchorEntry} anchorOrdinal={anchorOrdinal} />
          )}
        </div>
        <ProvenanceRail fields={detail.provenance} />
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Add styles**

Append to `src/smx-web/src/styles/primitives.css`:

```css
.file-viewer { display: flex; flex-direction: column; gap: var(--s3); }
.fv-head { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--s3); }
.fv-head h2 { margin: 0 0 2px; }
.fv-banner { margin: 0; padding: var(--s2) var(--s3); border-left: 3px solid var(--text-accent); background: var(--bg-accent); font-size: var(--t-small); }
.fv-banner-warn { border-left-color: var(--text-warning); }
.fv-tabs { display: flex; gap: 2px; border-bottom: 0.5px solid var(--border); }
.fv-tab { background: none; border: 0; border-bottom: 2px solid transparent; padding: var(--s2) var(--s3); font: inherit; font-size: var(--t-small); color: var(--text-secondary); cursor: pointer; }
.fv-tab-on { color: var(--text-primary); border-bottom-color: var(--text-accent); font-weight: 600; }
.fv-body { display: flex; align-items: stretch; }
.fv-main { flex: 1; min-width: 0; }
```

- [ ] **Step 5: Run to verify pass**

```bash
cd src/smx-web && npm test -- FileViewer.test.tsx && npm run typecheck
```

Expected: **PASS**, 5 tests; typecheck clean.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/FileViewer.tsx src/smx-web/src/components/FileViewer.test.tsx src/smx-web/src/styles/primitives.css
git commit -m "feat(web): the reader, with both of a document's faces

The original is what a human trusts; the chunks are what the agent reasoned
over. Showing only one hides either the artifact driving verdicts or whether
extraction was faithful to it.

Arriving anchored opens on the chunk view, because the anchor is the reason
you are there and the original has nothing to anchor to."
```

---

## Task 6: the two mounts — route and overlay

**Files:**
- Create: `src/smx-web/src/routes/DocumentView.tsx`, `src/smx-web/src/components/FileViewerOverlay.tsx`, `FileViewerOverlay.test.tsx`
- Modify: `src/smx-web/src/App.tsx`

- [ ] **Step 1: Write the failing overlay tests**

Create `src/smx-web/src/components/FileViewerOverlay.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { FileViewerOverlay } from './FileViewerOverlay';

afterEach(() => vi.unstubAllGlobals());

const stubFetch = () =>
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) =>
      Promise.resolve(
        url.includes('/text') || url.endsWith('reg_abc')
          ? new Response(
              url.includes('/text')
                ? '[]'
                : JSON.stringify({
                    summary: {
                      id: 'reg_abc', kind: 'reg', title: 'A doc', subtitle: 's',
                      available: true, state: 'available', contentType: 'text/plain',
                      officialDate: null, ingestedUtc: null,
                    },
                    provenance: [], unavailableReason: null, unavailableDetail: null, supersededById: null,
                  }),
              { headers: { 'Content-Type': 'application/json' } },
            )
          : new Response('body', { headers: { 'Content-Type': 'text/plain' } }),
      ),
    ),
  );

describe('FileViewerOverlay', () => {
  it('closes on Escape', async () => {
    stubFetch();
    const onClose = vi.fn();
    render(<FileViewerOverlay documentId="reg_abc" onClose={onClose} />);
    await screen.findByText('A doc');
    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('closes on a backdrop click but not on a click inside the panel', async () => {
    stubFetch();
    const onClose = vi.fn();
    render(<FileViewerOverlay documentId="reg_abc" onClose={onClose} />);
    await userEvent.click(await screen.findByText('A doc'));
    expect(onClose).not.toHaveBeenCalled();
    await userEvent.click(screen.getByTestId('fv-backdrop'));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('is a dialog and labels itself', async () => {
    stubFetch();
    render(<FileViewerOverlay documentId="reg_abc" onClose={vi.fn()} />);
    const dialog = await screen.findByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- FileViewerOverlay.test.tsx
```

Expected: **FAIL** — cannot resolve `./FileViewerOverlay`.

- [ ] **Step 3: Implement the overlay**

Create `src/smx-web/src/components/FileViewerOverlay.tsx`:

```tsx
import { useEffect, useRef } from 'react';
import { FileViewer } from './FileViewer';

/**
 * The quick-peek mount: the same viewer, over the stage you were on, dismissed with Escape so
 * you land exactly where you left off mid-review.
 *
 * The permanent route is the primary surface — a gate is a signed record and a Learned
 * Conclusion carries provenance, so "the document I signed off against" wants a URL. This is
 * the convenience path, not the citable one.
 */
export function FileViewerOverlay({
  documentId,
  anchorEntry,
  onClose,
}: {
  documentId: string;
  anchorEntry?: string | null;
  onClose: () => void;
}) {
  const panel = useRef<HTMLDivElement | null>(null);
  const restoreTo = useRef<Element | null>(null);

  useEffect(() => {
    restoreTo.current = document.activeElement;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    document.addEventListener('keydown', onKey);
    panel.current?.focus();
    return () => {
      document.removeEventListener('keydown', onKey);
      // Send focus back where it came from, or the operator's next keystroke goes nowhere.
      (restoreTo.current as HTMLElement | null)?.focus?.();
    };
  }, [onClose]);

  return (
    <div className="fv-overlay">
      <div className="fv-backdrop" data-testid="fv-backdrop" onClick={onClose} />
      <div
        ref={panel}
        className="fv-panel"
        role="dialog"
        aria-modal="true"
        aria-label="Document viewer"
        tabIndex={-1}
      >
        <button type="button" className="fv-close" onClick={onClose} aria-label="Close">
          ×
        </button>
        <FileViewer documentId={documentId} anchorEntry={anchorEntry} />
      </div>
    </div>
  );
}
```

- [ ] **Step 4: Add overlay styles**

Append to `src/smx-web/src/styles/primitives.css`:

```css
.fv-overlay { position: fixed; inset: 0; z-index: 60; display: flex; align-items: center; justify-content: center; }
.fv-backdrop { position: absolute; inset: 0; background: rgba(0, 0, 0, 0.42); }
.fv-panel { position: relative; width: min(1100px, 92vw); max-height: 92vh; overflow: auto; background: var(--surface-0); border: 0.5px solid var(--border); border-radius: var(--r2); padding: var(--s4); }
.fv-close { position: absolute; top: var(--s3); right: var(--s3); background: none; border: 0; font-size: 20px; line-height: 1; cursor: pointer; color: var(--text-secondary); }
```

- [ ] **Step 5: Create the route mount**

Create `src/smx-web/src/routes/DocumentView.tsx`:

```tsx
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { FileViewer } from '../components/FileViewer';

/**
 * /docs/:id — the permanent, citable surface.
 *
 * This is the one with a URL, which is why it is the primary mount: gates are signed records
 * and Learned Conclusions carry provenance, so the document behind a determination needs a
 * reference that survives the session.
 */
export function DocumentView() {
  const { documentId = '' } = useParams();
  const [params] = useSearchParams();
  const entry = params.get('entry');
  const chunk = params.get('chunk');

  return (
    <div className="page">
      <nav className="crumb">
        <Link to="/docs">Documents</Link> ›
      </nav>
      <FileViewer
        documentId={documentId}
        anchorEntry={entry}
        anchorOrdinal={chunk === null ? null : Number.parseInt(chunk, 10)}
      />
    </div>
  );
}
```

- [ ] **Step 6: Register the routes in `src/smx-web/src/App.tsx`**

Add the imports:

```typescript
import { DocumentView } from './routes/DocumentView';
import { Documents } from './routes/Documents';
```

And the two routes, after `msds-registry`:

```tsx
          <Route path="docs" element={<Documents />} />
          <Route path="docs/:documentId" element={<DocumentView />} />
```

> `Documents` does not exist until Task 7. Do Task 7 before running the app; the unit tests in
> this task do not import `App.tsx`, so they pass regardless.

- [ ] **Step 7: Run to verify pass**

```bash
cd src/smx-web && npm test -- FileViewerOverlay.test.tsx
```

Expected: **PASS**, 3 tests.

- [ ] **Step 8: Commit**

```bash
git add src/smx-web/src/components/FileViewerOverlay.tsx src/smx-web/src/components/FileViewerOverlay.test.tsx src/smx-web/src/routes/DocumentView.tsx src/smx-web/src/App.tsx src/smx-web/src/styles/primitives.css
git commit -m "feat(web): two mounts for one viewer

The route is primary because it is the one with a URL: gates are signed
records and Learned Conclusions carry provenance, so the document behind a
determination needs a reference that outlives the session.

The overlay restores focus to whatever opened it — without that, dismissing it
drops the operator's next keystroke on the floor."
```

---

## Task 7: `/docs` — the library

**Files:**
- Create: `src/smx-web/src/routes/Documents.tsx`, `Documents.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create `src/smx-web/src/routes/Documents.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Documents } from './Documents';

const ROWS = [
  {
    id: 'sds_a', kind: 'sds', title: 'Silver nitrate', subtitle: 'CAS 7761-88-8 · sigma',
    available: true, state: 'available', contentType: 'application/pdf',
    officialDate: '2024-03-11', ingestedUtc: '2026-07-16T00:00:00Z',
  },
  {
    id: 'sdsgap_b', kind: 'sds', title: 'Nd oxide — no safety sheet',
    subtitle: 'CAS 1313-97-9 · 3 fetch attempt(s) failed', available: false, state: 'missing',
    contentType: null, officialDate: null, ingestedUtc: null,
  },
];

const stub = () => {
  const seen: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn((url: string) => {
      seen.push(url);
      return Promise.resolve(
        new Response(JSON.stringify(ROWS), { headers: { 'Content-Type': 'application/json' } }),
      );
    }),
  );
  return seen;
};

const view = () => render(<Documents />, { wrapper: MemoryRouter });

afterEach(() => vi.unstubAllGlobals());

describe('Documents — the library', () => {
  it('lists documents with a link to each', async () => {
    stub();
    view();
    const link = await screen.findByRole('link', { name: /Silver nitrate/ });
    expect(link).toHaveAttribute('href', '/docs/sds_a');
  });

  /**
   * Design D9. A missing MSDS is exactly what blocks an order, so it is a row — visibly
   * distinct, saying how many attempts failed. A library that listed only files that exist
   * would let absence read as coverage.
   */
  it('shows a substance with no sheet as a first-class row that names the gap', async () => {
    stub();
    view();
    expect(await screen.findByText(/no safety sheet/i)).toBeInTheDocument();
    expect(screen.getByText(/3 fetch attempt/i)).toBeInTheDocument();
  });

  // A gap row has no file, so it must not pretend to open one.
  it('does not link a gap row to the viewer', async () => {
    stub();
    view();
    await screen.findByText(/no safety sheet/i);
    expect(screen.queryByRole('link', { name: /no safety sheet/i })).toBeNull();
  });

  it('passes the kind filter to the server', async () => {
    const seen = stub();
    view();
    await screen.findByText('Silver nitrate');
    await userEvent.click(screen.getByRole('button', { name: /regulations/i }));
    expect(seen.some((u) => u.includes('kind=reg'))).toBe(true);
  });

  it('passes the search query to the server', async () => {
    const seen = stub();
    view();
    await screen.findByText('Silver nitrate');
    // SearchInput renders type="text" with aria-label, NOT type="search" — so there is no
    // searchbox role to query. Go by the label.
    await userEvent.type(screen.getByLabelText('Search documents'), 'silver');
    await vi.waitFor(() => expect(seen.some((u) => u.includes('q=silver'))).toBe(true));
  });

  it('renders an empty state rather than a bare list on a cold start', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('[]', { headers: { 'Content-Type': 'application/json' } }))),
    );
    view();
    expect(await screen.findByText(/no documents/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- Documents.test.tsx
```

Expected: **FAIL** — cannot resolve `./Documents`.

- [ ] **Step 3: Implement**

Create `src/smx-web/src/routes/Documents.tsx`:

```tsx
import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { getDocuments } from '../api/client';
import type { DocumentSummary } from '../api/types';
import { EmptyState, SearchInput } from '../components/ui/Primitives';
import { Loading } from '../components/Loading';

const FILTERS = [
  { key: 'all', label: 'All' },
  { key: 'sds', label: 'Safety sheets' },
  { key: 'reg', label: 'Regulations' },
  { key: 'seed', label: 'Seeded' },
] as const;

/**
 * Every document the system holds — and every safety sheet it knows it is missing.
 *
 * No MockBadge: this reads a real endpoint end to end. The gap rows are why it is worth
 * having at all; a missing MSDS blocks an order, so it belongs in the list rather than
 * behind a status endpoint nobody visits.
 */
export function Documents() {
  const [rows, setRows] = useState<DocumentSummary[] | null>(null);
  const [kind, setKind] = useState<string>('all');
  const [q, setQ] = useState('');

  useEffect(() => {
    let live = true;
    // Debounced so typing does not fire a request per keystroke.
    const timer = setTimeout(() => {
      void getDocuments({ kind: kind === 'all' ? undefined : kind, q: q || undefined }).then((r) => {
        if (live) setRows(r);
      });
    }, 150);
    return () => {
      live = false;
      clearTimeout(timer);
    };
  }, [kind, q]);

  return (
    <div className="page">
      <h1>Documents</h1>

      <div className="doc-filters">
        {FILTERS.map((f) => (
          <button
            key={f.key}
            type="button"
            className={kind === f.key ? 'chip chip-on' : 'chip'}
            onClick={() => setKind(f.key)}
          >
            {f.label}
          </button>
        ))}
        <SearchInput
          value={q}
          onChange={setQ}
          placeholder="Search CAS, supplier, regulation…"
          label="Search documents"
        />
      </div>

      {rows === null ? (
        <Loading />
      ) : rows.length === 0 ? (
        <EmptyState
          title="No documents"
          body="Nothing matches. The SDS library and the monthly regulatory sync populate this list."
        />
      ) : (
        <ul className="doc-list">
          {rows.map((r) => (
            <li key={r.id} className={r.available ? 'doc-row' : 'doc-row doc-row-gap'}>
              <div className="doc-row-main">
                {/* A gap row has no file. Linking it would promise something that does not exist. */}
                {r.available ? (
                  <Link to={`/docs/${encodeURIComponent(r.id)}`}>{r.title}</Link>
                ) : (
                  <span className="doc-gap-title">{r.title}</span>
                )}
                <span className="muted">{r.subtitle}</span>
              </div>
              {r.state === 'superseded' && <span className="chip">superseded</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
```

> `SearchInput`'s real signature (verified at `components/ui/Primitives.tsx:199-208`) is
> `{ value: string; onChange: (v: string) => void; placeholder: string; label: string }` — `onChange`
> receives the string directly (so `setQ` works as-is) and **`label` is required**. It renders
> `type="text"` with `aria-label={label}`, so tests must use `getByLabelText`, never
> `getByRole('searchbox')`.

- [ ] **Step 4: Add styles**

Append to `src/smx-web/src/styles/primitives.css`:

```css
.doc-filters { display: flex; gap: var(--s2); align-items: center; flex-wrap: wrap; margin-bottom: var(--s3); }
.doc-list { list-style: none; margin: 0; padding: 0; }
.doc-row { display: flex; align-items: center; justify-content: space-between; gap: var(--s3); padding: var(--s2) var(--s3); border-bottom: 0.5px solid var(--border); }
.doc-row-main { display: flex; flex-direction: column; min-width: 0; }
.doc-row-gap { background: var(--bg-warning); }
.doc-gap-title { color: var(--text-warning); font-weight: 600; }
```

- [ ] **Step 5: Run to verify pass**

```bash
cd src/smx-web && npm test -- Documents.test.tsx && npm run typecheck
```

Expected: **PASS**, 6 tests; typecheck clean.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/routes/Documents.tsx src/smx-web/src/routes/Documents.test.tsx src/smx-web/src/styles/primitives.css
git commit -m "feat(web): the document library, gaps included

A substance with no safety sheet is a row, not an omission. That is the point:
a missing MSDS is what blocks an order, and a list of only the files that
exist would let absence read as coverage.

Gap rows are deliberately not links. There is no file behind them, and a link
would promise one."
```

---

## Task 8: entry points

**Files:**
- Modify: `src/smx-web/src/components/ui/Primitives.tsx`, `src/smx-web/src/routes/MsdsRegistry.tsx`, `src/smx-web/src/components/AppShell.tsx`
- Test: `src/smx-web/src/components/ui/CitationChip.test.tsx`

- [ ] **Step 1: Write the failing tests**

Create `src/smx-web/src/components/ui/CitationChip.test.tsx`:

```tsx
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { CitationChip } from './Primitives';

const base = { source: 'regulatory', reference: 'reach-17', retrievedAt: '2026-07-01T00:00:00Z' };

describe('CitationChip', () => {
  /**
   * Design D8, and the reason this test exists at all.
   *
   * Discovery, Dosing, Cost and Decision render FIXTURES behind a MockBadge. If their chips
   * linked into a real document viewer, fabricated citations would borrow the authority of
   * real ones — exactly what the badge exists to prevent. A chip with no documentId must stay
   * inert, and it must keep looking identical, so nothing about the mock screens changes.
   */
  it('renders inert text when no documentId is given', () => {
    render(<CitationChip {...base} />, { wrapper: MemoryRouter });
    expect(screen.queryByRole('link')).toBeNull();
    expect(screen.getByText(/reach-17/)).toBeInTheDocument();
  });

  it('links to the viewer when a documentId is given', () => {
    render(<CitationChip {...base} documentId="reg_abc" />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveAttribute('href', '/docs/reg_abc');
  });

  // Arriving from a citation should land on the passage the verdict rested on.
  it('carries the entry anchor into the link', () => {
    render(<CitationChip {...base} documentId="reg_abc" entryId="27" />, { wrapper: MemoryRouter });
    expect(screen.getByRole('link')).toHaveAttribute('href', '/docs/reg_abc?entry=27');
  });
});
```

- [ ] **Step 2: Run to verify failure**

```bash
cd src/smx-web && npm test -- CitationChip.test.tsx
```

Expected: **FAIL** — the second and third tests fail; no link is rendered.

- [ ] **Step 3: Extend `CitationChip` in `src/smx-web/src/components/ui/Primitives.tsx`**

Replace the existing `CitationChip` with:

```tsx
export function CitationChip({
  source,
  reference,
  retrievedAt,
  snippet,
  documentId,
  entryId,
}: {
  source: string;
  reference: string;
  retrievedAt: string;
  snippet?: string;
  /**
   * Present ONLY where the citation is real (EvidencePanel, the Regulatory stage). The
   * fixture-backed screens pass nothing and the chip stays inert — linking a fabricated
   * citation into a real document viewer would let mock data borrow the authority of an
   * agent-produced one, which is what MockBadge exists to prevent.
   */
  documentId?: string;
  entryId?: string;
}) {
  const body = (
    <>
      {source} · <Data kind="code">{reference}</Data>
      {/* The corpus sync date is the load-bearing half of a citation: a regulation
          entry without the date it was retrieved is not a citation, it is a claim. */}
      <span className="muted">
        {' '}
        · <Data kind="date">{retrievedAt.slice(0, 10)}</Data>
      </span>
    </>
  );

  if (!documentId) {
    return (
      <span className="src" title={snippet ?? undefined}>
        {body}
      </span>
    );
  }

  const href = `/docs/${encodeURIComponent(documentId)}${entryId ? `?entry=${encodeURIComponent(entryId)}` : ''}`;
  return (
    <Link className="src src-link" to={href} title={snippet ?? undefined}>
      {body}
    </Link>
  );
}
```

Add the router import at the top of `Primitives.tsx`:

```typescript
import { Link } from 'react-router-dom';
```

- [ ] **Step 4: Run to verify pass**

```bash
cd src/smx-web && npm test -- CitationChip.test.tsx
```

Expected: **PASS**, 3 tests.

- [ ] **Step 5: Confirm no existing chip started linking**

```bash
cd src/smx-web && npm test
```

Expected: **all pre-existing tests still pass.** No caller passes `documentId` yet, so every chip on
every screen is byte-identical to before.

- [ ] **Step 6: Add the nav entry**

In `src/smx-web/src/components/AppShell.tsx`, add a `Documents` link to the app-level nav alongside
*Marker Library*, *Learned Conclusions* and *MSDS Registry*:

```tsx
        <NavLink to="/docs">Documents</NavLink>
```

> Match the exact element and class the sibling links use — copy the `msds-registry` line and
> change the `to` and label.

- [ ] **Step 7: Link MSDS Registry rows to their sheet**

`GET /msds-registry` returns `MsdsEntry` (cas, supplier, version, date, …) and carries **no document
id**. The sheet's id is derivable, because an SDS document id is
`sds_` + base64url(`{cas}|{supplier}|{revisionDate}`) and the registry row holds all three — the
composition in `KnowledgeEndpoints` builds its rows from the same corpus.

Add to `src/smx-web/src/routes/MsdsRegistry.tsx`:

```tsx
/**
 * The MSDS registry gates procurement: an order stays blocked until its sheet is current AND
 * reviewed. Until now the operator signed that review on a screen that could not display the
 * sheet. This is the link that fixes it.
 *
 * The id is derived rather than served, because /msds-registry composes governance over the SDS
 * corpus and returns neither a blob path nor a document id. The three parts are exactly
 * DedupKey.ForRegistry, normalised the same way: trimmed, lowercased, whitespace collapsed.
 */
function sheetDocumentId(cas: string, supplier: string, revisionDate: string): string {
  const norm = (s: string) => s.trim().toLowerCase().replace(/\s+/g, ' ');
  const payload = `${norm(cas)}|${norm(supplier)}|${norm(revisionDate)}`;
  const b64 = btoa(String.fromCharCode(...new TextEncoder().encode(payload)));
  return `sds_${b64.replace(/\+/g, '-').replace(/\//g, '_')}`;
}
```

Then render an "Open sheet" link on each row that has a supplier and a date:

```tsx
{entry.supplier && entry.date && (
  <Link to={`/docs/${sheetDocumentId(entry.cas, entry.supplier, entry.date)}`}>Open sheet</Link>
)}
```

> Normalisation must match `DedupKey.Norm` exactly (`src/Smx.Functions/Sds/Domain/DedupKey.cs:20`) or
> the link 404s. Add a test asserting the derived id for a known triple round-trips through the
> backend's `DocumentId.TryDecode` shape — or, if that proves brittle in review, prefer adding a
> `documentId` field to `MsdsEntry` on the backend and deleting this function.

- [ ] **Step 8: Run everything**

```bash
cd src/smx-web && npm test && npm run typecheck && npm run build
```

Expected: all green.

- [ ] **Step 9: Commit**

```bash
git add src/smx-web/src/components/ui/ src/smx-web/src/components/AppShell.tsx src/smx-web/src/routes/MsdsRegistry.tsx
git commit -m "feat(web): citations that open, where the citation is real

CitationChip links only when handed a documentId, and only EvidencePanel, the
Regulatory stage and the MSDS registry hand it one. The fixture-backed screens
pass nothing and their chips stay byte-identical — a fabricated citation must
not be able to borrow a real document viewer's authority.

The MSDS registry now links to the sheet being signed off. It gates
procurement, and until now the operator approved a document they could not
open."
```

---

## Task 9: wire the real-citation screens, and tell the truth in CLAUDE.md

**Files:**
- Modify: `src/smx-web/src/components/EvidencePanel.tsx`, `src/smx-web/src/routes/stages/Regulatory.tsx`, `CLAUDE.md`

- [ ] **Step 1: Establish whether a citation can be mapped to a document id**

```bash
grep -n "reference\|source" src/smx-web/src/api/types.ts | sed -n '1,40p'
```

`Citation` is `{ source, reference, retrievedAt, snippet? }`. `reference` is a free-text label
(e.g. `regulatory-corpus/reach-svhc-12`), **not** a document id.

- [ ] **Step 2: Decide, and record the decision**

Two honest options. Pick one and write down which:

**(a)** Add `documentId` and `entryId` to the backend `Citation` record, populated where the RAG tool
already knows the `docId` it retrieved from. Correct, and the only version that cannot mislink — but
it touches `Smx.Domain/Records/ConstraintsDoc.cs`, the tools that build citations, and every fixture.

**(b)** Ship Plan 2 with `/docs`, the MSDS registry link and the viewer, and leave citation chips
inert until (a) lands.

**Recommended: (b), then (a) as its own change.** Deriving a document id by parsing a free-text
reference string would produce links that are *usually* right — and a citation chip that opens the
wrong regulation is worse than one that opens nothing, because the operator has no way to tell.

- [ ] **Step 3: If (b), add the note that stops the next person from guessing**

In `src/smx-web/src/components/EvidencePanel.tsx`, above the `CitationChip` usage:

```tsx
{/*
  These chips are inert on purpose. CitationChip links when given a documentId, and Citation
  does not carry one — `reference` is a free-text label, not an identifier. Deriving an id by
  parsing it would produce links that are usually right, and a chip that opens the WRONG
  regulation is worse than one that opens nothing: the operator cannot tell. The fix is a real
  documentId on the Citation record; see plan 2 task 9.
*/}
```

- [ ] **Step 4: Update `CLAUDE.md`**

The frontend section says *"Only three screens are backed by real endpoints (intake form, stage spine,
compatibility matrix)."* That count is now wrong twice over — the MSDS registry already reads a real
endpoint, and `/docs` and `/docs/:id` are new. Update the sentence to name the document surfaces and
state that the viewer reads Bronze through the backend.

Add to the frontend bullet list:

```markdown
  - **File viewer** (`/docs`, `/docs/:id`) — the document library and reader over the SDS PDFs and
    regulatory source documents already in Bronze. Both read real endpoints; neither carries a
    `MockBadge`. Design + plans:
    [`docs/superpowers/specs/2026-07-22-file-viewer-design.md`](docs/superpowers/specs/2026-07-22-file-viewer-design.md),
    plans `2026-07-22-file-viewer-plan-1-document-access-layer.md` and `-plan-2-viewer-ui.md`.
    HTML documents render in a fully sandboxed `srcdoc` frame and **never** a `blob:` URL — a
    `blob:` URL inherits the app origin, which would make open-web regulatory HTML stored XSS.
```

- [ ] **Step 5: Full verification**

```bash
cd src/smx-web && npm test && npm run typecheck && npm run build
cd ../.. && dotnet test src/Smx.Backend.sln
```

Expected: all green on both sides.

- [ ] **Step 6: Commit**

```bash
git add src/smx-web/src/components/EvidencePanel.tsx CLAUDE.md
git commit -m "docs(viewer): record why the citation chips are still inert

Citation.reference is a free-text label, not an identifier. Deriving a
document id by parsing it would produce links that are usually right, and a
chip opening the WRONG regulation is worse than one opening nothing — the
operator has no way to tell which they got.

The fix is a real documentId on the Citation record, which touches the domain
record, the RAG tools and every fixture. That is its own change, not a
smuggled-in half of this one."
```

---

## Plan 2 self-review

**Spec coverage:**

| Spec section | Task |
|---|---|
| §2 D5 (two faces) | 5 |
| §2 D6 (route + overlay) | 6 |
| §2 D7 (sandboxed HTML, never blob:) | 2 |
| §2 D8 (chips inert on mock screens) | 8, 9 |
| §2 D9 (gap rows) | 7 |
| §3 invariant 3 (HTML never same-origin) | 2 |
| §3 invariant 5 (says why) | 2, 4, 5 |
| §3 invariant 6 ("not recorded" shown) | 3 |
| §7 rendering table | 2 |
| §7 components | 2–7 |
| §7 anchoring (`?entry=`, `?chunk=`) | 4, 6 |
| §7 entry points | 8 |
| §8 25 MB cap, zero chunks, superseded | 2, 4, 5 |
| §10 frontend testing | 2, 3, 4, 5, 6, 7, 8 |

**Known gaps, stated rather than hidden:**

1. **Citation chips do not link yet** (Task 9 option b). `Citation` carries no document id and the
   plan refuses to fake one. This is the single largest piece of the design's §7 that Plan 2 does not
   deliver, and it needs a follow-up plan touching `ConstraintsDoc`, the RAG tools and the fixtures.
2. **The MSDS registry link derives its id client-side** (Task 8 step 7), duplicating
   `DedupKey.ForRegistry`'s normalisation in TypeScript. It works, and it is the one place where the
   three parts are all present — but the step itself recommends replacing it with a backend-served
   `documentId` if review finds the duplication objectionable.
3. ~~Style token names are guesses.~~ **Resolved during self-review.** Every token was checked
   against `styles/tokens.css` and corrected; the first draft used `--accent`, `--warn`,
   `--accent-wash`, `--fs-1`, `--text-3` and `--bg`, **none of which exist**. A wrong token renders
   unstyled rather than broken, so no test would have caught it. The verified set is listed under
   *Commands* at the top of this plan.
4. ~~`EmptyState` and `SearchInput` prop shapes are assumed.~~ **Resolved during self-review.** Both
   were read: `EmptyState` takes `actions` (plural, not `action`), and `SearchInput` requires a
   `label` and renders `type="text"`, so `getByRole('searchbox')` would have failed — the test now
   uses `getByLabelText`.
5. **The overlay is built but nothing opens it yet.** `FileViewerOverlay` ships in Task 6 with tests,
   and every wired entry point in Task 8 navigates to the route instead. That is the right default
   (the route is the citable surface), but it means the overlay is dead code until a caller wants a
   peek-without-navigating. Delete it or wire it — do not leave it untouched and untested.

**Type consistency check:** `DocumentSummary`, `DocumentDetail`, `ProvenanceField`, `DocumentChunk`,
`DocumentBytes` are defined once in Task 1 and used unchanged in Tasks 2–8, and their field names match
the C# records in Plan 1 Task 2 under the backend's camelCase serialisation. `FileViewer`'s props
(`documentId`, `anchorEntry`, `anchorOrdinal`) are consistent across Tasks 5, 6 and 8;
`FileViewerOverlay` deliberately omits `anchorOrdinal`, since nothing opens the overlay by ordinal.

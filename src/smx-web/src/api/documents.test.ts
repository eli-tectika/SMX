import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
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

  /**
   * The backend 400s an unrecognised kind or state on purpose — a typo'd filter answering
   * `200 []` would read as "no such documents", which is the exact confusion this whole
   * feature exists to prevent. The filter's type keeps a typo from compiling; this asserts
   * that if one ever reaches the wire anyway, it arrives as an error and not as emptiness.
   */
  it('raises the backend 400 for an unknown filter rather than yielding an empty list', async () => {
    stubFetch(() => json({ error: "unknown kind 'sdss'", allowed: ['all', 'sds', 'reg', 'seed'] }, 400));
    await expect(getDocuments({ kind: 'sdss' as never })).rejects.toThrow("unknown kind 'sdss'");
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

  /**
   * A 503 is NOT null. null means "this document has no bytes, and the detail endpoint says
   * why" — a claim about the document. A 503 is a claim about the deployment: the library is
   * unconfigured, so nothing is known about this document at all. Collapsing the two would
   * have the viewer tell the operator "no file is stored for this document" on the strength
   * of a missing environment variable, and a document that cannot be shown must say why.
   */
  it('throws on a 503 rather than reporting the document as fileless', async () => {
    stubFetch(() =>
      new Response(
        JSON.stringify({
          title: 'Document library unavailable',
          detail: 'The document library is not configured on this deployment.',
          status: 503,
        }),
        { status: 503, headers: { 'Content-Type': 'application/problem+json' } },
      ),
    );
    await expect(getDocumentContent('sds_abc')).rejects.toBeInstanceOf(ApiError);
    await expect(getDocumentContent('sds_abc')).rejects.toThrow(/not configured on this deployment/);
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
